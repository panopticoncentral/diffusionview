using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using DiffusionView.Service;
using WinRT.Interop;
using System.Collections.Generic;
using System.IO;

namespace DiffusionView;

public sealed partial class MainWindow
{
    private readonly PhotoService _photoService;

    private string _currentFolder;
    private readonly ObservableCollection<PhotoItem> _currentPhotos = [];

    private Button _selectedItem;

    private Button SelectedItem
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
                var previousPhoto = (PhotoItem)_selectedItem.DataContext;
                if (previousPhoto != null)
                {
                    previousPhoto.IsSelected = false;
                    previousPhoto.UpdateVisualState(_selectedItem);
                    UpdatePreviewPane(null);
                }
            }

            _selectedItem = value;

            if (_selectedItem == null) return;

            var photo = (PhotoItem)_selectedItem.DataContext;
            photo.IsSelected = true;
            photo.UpdateVisualState(_selectedItem);
            UpdatePreviewPane(photo);
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;

        _photoService = new PhotoService();
        _photoService.FolderAdded += PhotoService_FolderAdded;
        _photoService.FolderRemoved += PhotoService_FolderRemoved;
        _photoService.PhotoAdded += PhotoService_PhotoAdded;
        _photoService.PhotoRemoved += PhotoService_PhotoRemoved;
        _photoService.ThumbnailLoaded += PhotoService_ThumbnailLoaded;
        _photoService.Initialize();
    }

    private void PhotoService_FolderAdded(object _, FolderChangedEventArgs e)
    {
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(async void () =>
        {
            try
            {
                var rootItem = new NavigationViewItem
                {
                    Content = e.Name,
                    Icon = new SymbolIcon(Symbol.Folder),
                    Tag = e.Path
                };

                var folderHeaderIndex = -1;
                for (var i = 0; i < NavView.MenuItems.Count; i++)
                {
                    if (NavView.MenuItems[i] is not NavigationViewItemHeader header ||
                        header.Content?.ToString() != "Folders") continue;
                    folderHeaderIndex = i;
                    break;
                }

                if (folderHeaderIndex != -1)
                {
                    NavView.MenuItems.Insert(folderHeaderIndex + 1, rootItem);
                }
                else
                {
                    NavView.MenuItems.Add(rootItem);
                }

                var folder = await StorageFolder.GetFolderFromPathAsync(e.Path);
                var subFolders = await folder.GetFoldersAsync();

                await AddSubFolderItemsAsync(rootItem, subFolders);
            }
            catch (Exception)
            {
                // For now, ignore exceptions
            }
        });
    }

    private void PhotoService_FolderRemoved(object _, FolderChangedEventArgs e)
    {
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_currentFolder == e.Path)
            {
                _currentPhotos.Clear();
                _currentFolder = null;
            }

            foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
            {
                if (item.Tag?.ToString() != e.Path) continue;
                NavView.MenuItems.Remove(item);
                break;
            }
        });
    }

    private void PhotoService_PhotoRemoved(object _, PhotoChangedEventArgs e)
    {
        if (_currentFolder == null || Path.GetDirectoryName(e.Photo.Path) != _currentFolder) return;
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_currentFolder == null || Path.GetDirectoryName(e.Photo.Path) != _currentFolder) return;
            var photo = _currentPhotos.FirstOrDefault(p => p.FilePath == e.Photo.Path);
            if (photo == null) return;
            if (SelectedItem.DataContext == photo)
            {
                SelectedItem = null;
            }
            _currentPhotos.Remove(photo);
        });
    }

    private void PhotoService_PhotoAdded(object _, PhotoChangedEventArgs e)
    {
        if (_currentFolder == null || Path.GetDirectoryName(e.Photo.Path) != _currentFolder) return;
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_currentFolder == null || Path.GetDirectoryName(e.Photo.Path) != _currentFolder) return;
            var photo = new PhotoItem
            {
                FileName = e.Photo.Name,
                FilePath = e.Photo.Path,
                DateTaken = e.Photo.DateTaken,
                FileSize = e.Photo.FileSize,
                Width = e.Photo.Width,
                Height = e.Photo.Height,
                Thumbnail = e.Photo.ThumbnailData != null ? CreateBitmapImage(e.Photo.ThumbnailData) : null
            };
            _currentPhotos.Add(photo);
        });
    }

    private void PhotoService_ThumbnailLoaded(object _, PhotoChangedEventArgs e)
    {
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            var photo = _currentPhotos.FirstOrDefault(p => p.FilePath == e.Photo.Path);
            if (photo == null) return;

            photo.Thumbnail = CreateBitmapImage(e.Photo.ThumbnailData);
        });
    }

    private async void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        try
        {
            if (args.InvokedItemContainer is not NavigationViewItem item) return;
 
            if (item == AddFolderButton)
            {
                await AddFolder();
            }

            await SelectFolder(item);
        }
        catch (Exception)
        {
            // Don't do anything for the moment...
        }
    }

    private void PhotoItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PhotoItem photo } button) return;

        SelectedItem = button;
    }

    private void PhotoItem_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PhotoItem photo } button) return;

        SelectedItem = button;
        SwitchToSinglePhotoView();
    }

    private void SwitchToSinglePhotoView()
    {
        if (SelectedItem?.DataContext is not PhotoItem photo) return;

        try
        {
            var bitmap = new BitmapImage(new Uri(photo.FilePath));
            SinglePhotoImage.Source = bitmap;

            GridView.Visibility = Visibility.Collapsed;
            SinglePhotoView.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            SinglePhotoImage.Source = photo.Thumbnail;
        }
    }

    private void BackToGridButton_Click(object sender, RoutedEventArgs e)
    {
        GridView.Visibility = Visibility.Visible;
        SinglePhotoView.Visibility = Visibility.Collapsed;
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedItem is not { } selectedPhoto) return;
    
            var file = await StorageFile.GetFileFromPathAsync(((PhotoItem)selectedPhoto.DataContext).FilePath);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception)
        {
            // Don't do anything at this point
        }
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SelectedItem is not { } selectedPhoto) return;

            var dialog = new ContentDialog
            {
                Title = "Delete Photo",
                Content = $"Are you sure you want to delete {((PhotoItem)selectedPhoto.DataContext).FileName}?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            var file = await StorageFile.GetFileFromPathAsync(((PhotoItem)selectedPhoto.DataContext).FilePath);
            await file.DeleteAsync();
        }
        catch (Exception)
        {
            // For the moment, ignore exceptions
        }
    }
    
    private static async Task AddSubFolderItemsAsync(NavigationViewItem parentItem, IReadOnlyList<StorageFolder> folders)
    {
        foreach (var folder in folders)
        {
            try
            {
                // Create navigation item for this subfolder
                var subItem = new NavigationViewItem
                {
                    Content = folder.Name,
                    Icon = new SymbolIcon(Symbol.Folder),
                    Tag = folder.Path
                };

                parentItem.MenuItems.Add(subItem);

                var subFolders = await folder.GetFoldersAsync();
                if (subFolders.Count > 0)
                {
                    await AddSubFolderItemsAsync(subItem, subFolders);
                }
            }
            catch (Exception)
            {
                // For now, ignore exceptions
            }
        }
    }

    private static BitmapImage CreateBitmapImage(byte[] data)
    {
        if (data == null)
            return null;

        var image = new BitmapImage();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        stream.WriteAsync(data.AsBuffer()).GetResults();
        stream.Seek(0);
        image.SetSource(stream);
        return image;
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
            FileNameText.Text = "";
            DateTakenText.Text = "";
            SizeText.Text = "";
            ResolutionText.Text = "";
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

    private async Task AddFolder()
    {
        var folderPicker = new FolderPicker();
        folderPicker.FileTypeFilter.Add("*");

        var windowHandle = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(folderPicker, windowHandle);

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder == null)
        {
            return;
        }

        await _photoService.AddFolderAsync(folder);
    }

    private async Task SelectFolder(NavigationViewItem folderViewItem)
    {
        var folderPath = (string)folderViewItem.Tag;
        if (string.IsNullOrEmpty(folderPath)) return;

        SelectedItem = null;
        _currentFolder = folderPath;
        _currentPhotos.Clear();
        var photos = (await _photoService.GetPhotosForFolderAsync(folderPath))
            .Select(p => new PhotoItem
            {
                FileName = p.Name,
                FilePath = p.Path,
                DateTaken = p.DateTaken,
                FileSize = p.FileSize,
                Width = p.Width,
                Height = p.Height,
                Thumbnail = p.ThumbnailData != null ? CreateBitmapImage(p.ThumbnailData) : null
            });

        foreach (var photo in photos)
        {
            _currentPhotos.Add(photo);
        }

        SelectedItem = null;
        PreviewImage.Source = null;
    }
}