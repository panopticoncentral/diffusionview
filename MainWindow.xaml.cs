using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;
using Windows.Storage.Pickers;
using Windows.System;
using DiffusionView.PhotoService;
using WinRT.Interop;

namespace DiffusionView;

public sealed partial class MainWindow : Window
{
    private readonly PhotoService.PhotoService _photoService = new();
    public ObservableCollection<PhotoItem> PhotoCollection { get; } = [];
    private string _currentFolderPath;
    private PhotoItem _selectedItem;
    private ContentDialog _syncDialog;
    private TextBlock _syncStatusText;
    private ProgressBar _syncProgressBar;

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

        _photoService.PhotoAdded += (s, e) =>
        {
            if (e.Photo.FilePath.StartsWith(_currentFolderPath))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var photo = new PhotoItem
                    {
                        FileName = e.Photo.FileName,
                        FilePath = e.Photo.FilePath,
                        DateTaken = e.Photo.DateTaken,
                        FileSize = e.Photo.FileSize,
                        Width = e.Photo.Width,
                        Height = e.Photo.Height,
                        Thumbnail = CreateBitmapImage(e.Photo.ThumbnailData)
                    };
                    PhotoCollection.Add(photo);
                });
            }
        };

        _photoService.PhotoRemoved += (s, e) =>
        {
            if (e.Photo.FilePath.StartsWith(_currentFolderPath))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var photo = PhotoCollection.FirstOrDefault(p => p.FilePath == e.Photo.FilePath);
                    if (photo != null)
                    {
                        PhotoCollection.Remove(photo);
                    }
                });
            }
        };

        _photoService.FolderRemoved += (s, e) =>
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                // Remove the folder from navigation
                var itemToRemove = FindNavViewItemByPath(NavView, e.FolderPath);
                if (itemToRemove != null)
                {
                    NavView.MenuItems.Remove(itemToRemove);
                }

                // Show notification to user
                var dialog = new ContentDialog
                {
                    Title = "Folder Removed",
                    Content = $"The folder at {e.FolderPath} was removed because: {e.Reason}",
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                };
                await dialog.ShowAsync();

                // If the removed folder was selected, clear the photo collection
                if (_currentFolderPath == e.FolderPath)
                {
                    PhotoCollection.Clear();
                    _currentFolderPath = null;
                }
            });
        };

        _photoService.SyncProgress += (s, e) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (e.ProcessedFolders == 1) // First folder
                {
                    ShowSyncProgressDialog(e);
                }
                UpdateSyncProgress(e);
            });
        };

        InitializePhotoService();
    }

    private async void InitializePhotoService()
    {
        try
        {
            await _photoService.InitializeAsync();
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Initialization Error",
                Content = $"Failed to initialize photo library: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    private void ShowSyncProgressDialog(SyncProgressEventArgs e)
    {
        var content = new StackPanel { Spacing = 10 };
        _syncStatusText = new TextBlock { Text = "Synchronizing folders..." };
        _syncProgressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = e.TotalFolders,
            Value = e.ProcessedFolders
        };

        content.Children.Add(_syncStatusText);
        content.Children.Add(_syncProgressBar);

        _syncDialog = new ContentDialog
        {
            Title = "Initializing Photo Library",
            Content = content,
            CloseButtonText = "Hide",
            XamlRoot = Content.XamlRoot
        };

        _ = _syncDialog.ShowAsync();
    }

    private void UpdateSyncProgress(SyncProgressEventArgs e)
    {
        if (_syncProgressBar != null)
        {
            _syncProgressBar.Value = e.ProcessedFolders;
            _syncStatusText.Text = $"Scanning: {e.CurrentFolder}\n{e.ProcessedFolders} of {e.TotalFolders} folders processed";

            if (e.ProcessedFolders == e.TotalFolders)
            {
                _syncDialog?.Hide();
                _syncDialog = null;
            }
        }
    }

    private async Task<NavigationViewItem> AddFolderRecursive(StorageFolder folder)
    {
        await _photoService.AddFolderAsync(folder);

        var navItem = new NavigationViewItem
        {
            Content = folder.Name,
            Icon = new SymbolIcon(Symbol.Folder),
            Tag = folder.Path
        };

        var childFolders = await folder.GetFoldersAsync();
        foreach (var childFolder in childFolders)
        {
            var subItem = await AddFolderRecursive(childFolder);
            if (subItem != null)
            {
                navItem.MenuItems.Add(subItem);
            }
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

    private async Task SelectFolder(NavigationViewItem folderViewItem)
    {
        var folderPath = (string)folderViewItem.Tag;
        if (string.IsNullOrEmpty(folderPath))
        {
            return;
        }

        _currentFolderPath = folderPath;
        PhotoCollection.Clear();
        var photos = await _photoService.GetPhotosForFolderAsync(folderPath);
        foreach (var photo in photos)
        {
            PhotoCollection.Add(photo);
        }

        SelectedItem = null;
        PreviewImage.Source = null;
    }

    private NavigationViewItem FindNavViewItemByPath(NavigationView navView, string path)
    {
        foreach (var item in navView.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag?.ToString() == path)
            {
                return item;
            }

            var found = FindNavViewItemByPathRecursive(item, path);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }

    private NavigationViewItem FindNavViewItemByPathRecursive(NavigationViewItem parent, string path)
    {
        foreach (var item in parent.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag?.ToString() == path)
            {
                return item;
            }

            var found = FindNavViewItemByPathRecursive(item, path);
            if (found != null)
            {
                return found;
            }
        }
        return null;
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

    private void PhotoItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PhotoItem photo })
        {
            return;
        }

        SelectedItem = photo;
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
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }
        var file = await StorageFile.GetFileFromPathAsync(selectedPhoto.FilePath);
        await file.DeleteAsync();
        PhotoCollection.Remove(selectedPhoto);
    }
}