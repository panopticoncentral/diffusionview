using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace DiffusionView;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, FolderInfo> _folders = [];

    public ObservableCollection<PhotoItem> PhotoCollection { get; } = [];

    private readonly ThumbnailCache _thumbnailCache = new();
    private readonly MetadataCache _metadataCache = new();
    private readonly ImageLoader _imageLoader= new();
    private readonly SemaphoreSlim _loadingSemaphore = new(1, 1);
    private CancellationTokenSource _loadingCancellation;

    private const int PageSize = 50;
    private bool _isLoading;
    private string _currentFolderPath;

    private PhotoItem _selectedItem;

    private PhotoItem SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem == value)
            {
                return;
            }
                
            if (_selectedItem != null)
            {
                _selectedItem.IsSelected = false;
            }

            _selectedItem = value;

            if (_selectedItem == null)
            {
                return;
            }
                
            _selectedItem.IsSelected = true;
            UpdatePreviewPane(_selectedItem);
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
    }

    private async Task<NavigationViewItem> AddFolderRecursive(StorageFolder folder)
    {
        var subfolders = new List<NavigationViewItem>();

        var childFolders = await folder.GetFoldersAsync();
        foreach (var childFolder in childFolders)
        {
            var subItem = await AddFolderRecursive(childFolder);
            if (subItem != null)
            {
                subfolders.Add(subItem);
            }
        }

        var folderInfo = new FolderInfo
        {
            Name = folder.Name,
            FolderPath = folder.Path
        };

        _folders[folderInfo.FolderPath] = folderInfo;

        var navItem = new NavigationViewItem
        {
            Content = folderInfo.Name,
            Icon = new SymbolIcon(Symbol.Folder),
            Tag = folderInfo.FolderPath
        };

        foreach (var subfolder in subfolders)
        {
            navItem.MenuItems.Add(subfolder);
        }

        return navItem;
    }

    private async Task<NavigationViewItem> AddFolder()
    {
        var folderPicker = new FolderPicker();
        folderPicker.FileTypeFilter.Add("*");

        var windowHandle = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(folderPicker, windowHandle);

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder == null)
        {
            return null;
        }

        return await AddFolderRecursive(folder);
    }

    private async Task LoadMetadataAsync(PhotoItem photoItem, StorageFile file)
    {
        try
        {
            var properties = await file.GetBasicPropertiesAsync();
            var imageProperties = await file.Properties.GetImagePropertiesAsync();

            var metadata = new PhotoMetadata
            {
                DateTaken = imageProperties.DateTaken.LocalDateTime,
                FileSize = properties.Size,
                Width = (int)imageProperties.Width,
                Height = (int)imageProperties.Height,
            };

            _metadataCache.AddMetadata(file.Path, metadata);
            UpdatePhotoItemFromMetadata(photoItem, metadata);
        }
        finally
        {
            photoItem.IsLoading = false;
        }
    }

    private static void UpdatePhotoItemFromMetadata(PhotoItem photoItem, PhotoMetadata metadata)
    {
        photoItem.DateTaken = metadata.DateTaken;
        photoItem.FileSize = metadata.FileSize;
        photoItem.Width = metadata.Width;
        photoItem.Height = metadata.Height;
    }

    private async Task<PhotoItem> CreatePhotoItemAsync(StorageFile file)
    {
        var photoItem = new PhotoItem
        {
            FileName = file.Name,
            FilePath = file.Path,
            IsLoading = true
        };

        if (_metadataCache.TryGetMetadata(file.Path, out var metadata))
        {
            UpdatePhotoItemFromMetadata(photoItem, metadata);
        }
        else
        {
            _ = LoadMetadataAsync(photoItem, file);
        }

        photoItem.Thumbnail = await _thumbnailCache.GetThumbnailAsync(file.Path);

        return photoItem;
    }

    private async Task LoadPhotosFromFolder(string folderPath)
    {
        PhotoCollection.Clear();

        try
        {
            if (_loadingCancellation != null)
            {
                await _loadingCancellation.CancelAsync();
            }
            _loadingCancellation = new CancellationTokenSource();
            var cancellationToken = _loadingCancellation.Token;

            var queryOptions = new QueryOptions
            {
                FolderDepth = FolderDepth.Deep,
                IndexerOption = IndexerOption.UseIndexerWhenAvailable
            };

            queryOptions.SetPropertyPrefetch(
                PropertyPrefetchOptions.BasicProperties | PropertyPrefetchOptions.ImageProperties,
                []);
            queryOptions.FileTypeFilter.Add(".png");

            _isLoading = true;

            var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
            var query = folder.CreateFileQueryWithOptions(queryOptions);

            var files = await query.GetFilesAsync(0, PageSize);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var photoItem = await CreatePhotoItemAsync(file);
                PhotoCollection.Add(photoItem);

                _ = _imageLoader.PreloadImageAsync(photoItem.FilePath);
            }

            SelectedItem = null;
            PreviewImage.Source = null;
        }
        catch (OperationCanceledException)
        {
            // Loading was cancelled, ignore
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Error Loading Photos",
                Content = $"An error occurred while loading photos from the folder: {ex.Message}",
                CloseButtonText = "OK"
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task SelectFolder(NavigationViewItem folderViewItem)
    {
        if (!_folders.TryGetValue((string)folderViewItem.Tag, out var folderItem))
        {
            return;
        }

        _currentFolderPath = folderItem.FolderPath;
        await LoadPhotosFromFolder(folderItem.FolderPath);
    }

    private async void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item)
        {
            return;
        }

        if (item == AddFolderButton)
        {
            item = await AddFolder();
            if (item == null)
            {
                return;
            }

            NavView.MenuItems.Add(item);
            NavView.SelectedItem = item;
        }

        await SelectFolder(item);
    }

    private static string FormatFileSize(ulong bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        var order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }

    private void UpdatePreviewPane(PhotoItem photo)
    {
        if (photo == null)
        {
            PreviewImage.Source = null;
            return;
        }

        try
        {
            var bitmap = new BitmapImage(new Uri(photo.FilePath));
            PreviewImage.Source = bitmap;

            FileNameText.Text = photo.FileName;
            DateTakenText.Text = photo.DateTaken?.ToString("MMMM dd, yyyy") ?? "Unknown";
            SizeText.Text = FormatFileSize(photo.FileSize);
            ResolutionText.Text = $"{photo.Width} x {photo.Height}";
        }
        catch (Exception)
        {
            PreviewImage.Source = null;
        }
    }

    private void PhotoItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PhotoItem photo })
        {
            return;
        }

        SelectedItem = photo;
    }

    private async Task LoadMorePhotosAsync()
    {
        if (_isLoading) return;

        await _loadingSemaphore.WaitAsync();
        try
        {
            _isLoading = true;

            if (_loadingCancellation != null)
            {
                await _loadingCancellation.CancelAsync();
            }
            _loadingCancellation = new CancellationTokenSource();
            var cancellationToken = _loadingCancellation.Token;

            var queryOptions = new QueryOptions
            {
                FolderDepth = FolderDepth.Deep,
                IndexerOption = IndexerOption.UseIndexerWhenAvailable
            };

            queryOptions.SetPropertyPrefetch(PropertyPrefetchOptions.BasicProperties |
                                             PropertyPrefetchOptions.ImageProperties,
                []);

            var folder = await StorageFolder.GetFolderFromPathAsync(_currentFolderPath);
            var query = folder.CreateFileQueryWithOptions(queryOptions);

            var files = (await query.GetFilesAsync())
                .Skip(PhotoCollection.Count)
                .Take(PageSize);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var photoItem = await CreatePhotoItemAsync(file);
                PhotoCollection.Add(photoItem);
                _ = _imageLoader.PreloadImageAsync(photoItem.FilePath);
            }
        }
        catch (OperationCanceledException)
        {
            // Loading was cancelled, ignore
        }
        finally
        {
            _isLoading = false;
            _loadingSemaphore.Release();
        }
    }

    private async void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_isLoading) return;

        var scrollViewer = (ScrollViewer)sender;
        var verticalOffset = scrollViewer.VerticalOffset;
        var maxVerticalOffset = scrollViewer.ScrollableHeight;

        if (maxVerticalOffset - verticalOffset <= 200)
        {
            await LoadMorePhotosAsync();
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not { } selectedPhoto)
        {
            return;
        }
        var file = await StorageFile.GetFileFromPathAsync(selectedPhoto.FilePath);
        await Launcher.LaunchFileAsync(file);
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not { } selectedPhoto)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete Photo",
            Content = $"Are you sure you want to delete {selectedPhoto.FileName}?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }
        var file = await StorageFile.GetFileFromPathAsync(selectedPhoto.FilePath);
        await file.DeleteAsync();
        PhotoCollection.Remove(selectedPhoto);
        _thumbnailCache.RemoveThumbnail(selectedPhoto.FilePath);
        _metadataCache.RemoveMetadata(selectedPhoto.FilePath);
    }
}