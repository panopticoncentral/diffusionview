using DiffusionView.Service;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using DiffusionView.Database;
using WinRT.Interop;

namespace DiffusionView;

public sealed partial class MainWindow : INotifyPropertyChanged
{
    private readonly PhotoService _photoService;

    public readonly ObservableCollection<PhotoItem> Photos = [];

    public readonly ObservableCollection<NavigationViewItem> Folders = [];
    public readonly ObservableCollection<NavigationViewItem> Models = [];

    public readonly ObservableCollection<PhotoItem> SelectedItems = [];
    private PhotoItem _focusedItem;

    private bool _isMultiSelectMode;

    public bool IsMultiSelectMode
    {
        get => _isMultiSelectMode;
        set
        {
            if (_isMultiSelectMode == value) return;
            _isMultiSelectMode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMultiSelectMode)));
        }
    }

    public PhotoItem FocusedItem
    {
        get => _focusedItem;
        set
        {
            if (_focusedItem == value)
            {
                return;
            }

            _focusedItem = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FocusedItem)));

            if (_focusedItem == null)
            {
                GridView.Visibility = Visibility.Visible;
                SinglePhotoView.Visibility = Visibility.Collapsed;
                return;
            }

            SwitchToSinglePhotoView();
            UpdateSinglePhotoView();
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;

        _photoService = new PhotoService();
        _photoService.FolderAdded += PhotoService_FolderAdded;
        _photoService.FolderRemoved += PhotoService_FolderRemoved;
        _photoService.PhotoAdded += PhotoService_PhotoAdded;
        _photoService.PhotoRemoved += PhotoService_PhotoRemoved;
        _photoService.ModelAdded += PhotoService_ModelAdded;
        _photoService.ModelRemoved += PhotoService_ModelRemoved;
        _photoService.ScanProgress += PhotoService_ScanProgress;
        _ = _photoService.InitializeAsync();
    }

    private static string GetNavigationItemText(NavigationViewItem item)
    {
        return item.Content switch
        {
            string text => text,
            StackPanel panel => (panel.Children[0] as TextBlock)?.Text ?? string.Empty,
            _ => string.Empty
        };
    }

    private static void InsertInCollectionInOrder(ObservableCollection<NavigationViewItem> items, NavigationViewItem item)
    {
        var newItemText = GetNavigationItemText(item);

        int index;
        for (index = 0; index < items.Count; index++)
        {
            var collectionItemText = GetNavigationItemText(items[index]);
            if (string.Compare(collectionItemText, newItemText, StringComparison.InvariantCultureIgnoreCase) > 0)
                break;
        }

        items.Insert(index, item);
    }

    private async void MainGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            switch (e.Key)
            {
                case VirtualKey.Space when SinglePhotoView.Visibility == Visibility.Visible:
                    if (FocusedItem != null)
                    {
                        FocusedItem.IsSelected = !FocusedItem.IsSelected;
                        if (FocusedItem.IsSelected)
                        {
                            if (!SelectedItems.Contains(FocusedItem))
                            {
                                SelectedItems.Add(FocusedItem);
                            }
                        }
                        else
                        {
                            SelectedItems.Remove(FocusedItem);
                        }
                        e.Handled = true;
                    }
                    break;

                case VirtualKey.Control:
                    IsMultiSelectMode = true;
                    break;

                case VirtualKey.Left:
                {
                    if (Photos.Count == 0) return;

                    if (SinglePhotoView.Visibility == Visibility.Visible)
                    {
                        PreviousButton_Click(null, null);
                        e.Handled = true;
                    }

                    break;
                }

                case VirtualKey.Right:
                {
                    if (Photos.Count == 0) return;

                    if (SinglePhotoView.Visibility == Visibility.Visible)
                    {
                        NextButton_Click(null, null);
                        e.Handled = true;
                    }

                    break;
                }

                case VirtualKey.Delete:
                    await DeleteSelectedItemsAsync();
                    e.Handled = true;
                    break;

                case VirtualKey.Escape:
                    if (SinglePhotoView.Visibility == Visibility.Visible)
                    {
                        BackToGridButton_Click(null, null);
                        e.Handled = true;
                    }
                    break;
            }
        }
        catch (Exception)
        {
            // For now, ignore exceptions
        }
    }

    private void MainGrid_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Control)
        {
            IsMultiSelectMode = false;
        }
    }

    private void SelectionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (FocusedItem == null) return;

        if (FocusedItem.IsSelected)
        {
            if (!SelectedItems.Contains(FocusedItem))
            {
                SelectedItems.Add(FocusedItem);
            }
        }
        else
        {
            SelectedItems.Remove(FocusedItem);
        }
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

            if (item.Icon is SymbolIcon { Symbol: Symbol.Folder } && item.Tag is string folderPath)
            {
                await SelectFolder(folderPath);
            }

            if (item.Icon is SymbolIcon { Symbol: Symbol.Contact } && item.Tag is long modelHash)
            {
                await SelectModel(modelHash);
            }
        }
        catch (Exception)
        {
            // Don't do anything for the moment...
        }
    }

    private void PhotoItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PhotoItem photo }) return;

        if (IsMultiSelectMode)
        {
            if (photo.IsSelected)
            {
                photo.IsSelected = false;
                SelectedItems.Remove(photo);
            }
            else
            {
                photo.IsSelected = true;
                SelectedItems.Add(photo);
            }
        }
        else
        {
            foreach (var item in SelectedItems.ToList())
            {
                item.IsSelected = false;
            }
            SelectedItems.Clear();

            photo.IsSelected = true;
            SelectedItems.Add(photo);
        }

        ((Grid)Content).Focus(FocusState.Programmatic);
    }
    
    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (FocusedItem == null) return;

        var currentIndex = Photos.IndexOf(FocusedItem);

        var previousIndex = currentIndex - 1;
        if (previousIndex < 0)
        {
            previousIndex = Photos.Count - 1;
        }

        FocusedItem = Photos[previousIndex];
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (FocusedItem == null) return;

        var currentIndex = Photos.IndexOf(FocusedItem);

        var nextIndex = (currentIndex + 1) % Photos.Count;

        FocusedItem = Photos[nextIndex];
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
            if (FocusedItem == null) return;

            var file = await StorageFile.GetFileFromPathAsync(FocusedItem.FilePath);
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
            await DeleteSelectedItemsAsync();
        }
        catch (Exception)
        {
            // For the moment, ignore exceptions
        }
    }

    private void PhotoService_FolderAdded(object _, FolderChangedEventArgs e)
    {
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(async void () =>
        {
            try
            {
                await AddFolderItemAsync(e.Path, Folders);
            }
            catch (Exception)
            {
                // For now, ignore exceptions
            }
        });
    }

    private async Task AddFolderItemAsync(string path, ObservableCollection<NavigationViewItem> collection)
    {
        foreach (var collectionItem in collection)
        {
            var itemPath = collectionItem.Tag?.ToString();
            
            if (string.IsNullOrEmpty(itemPath) || !path.StartsWith(itemPath)) continue;

            if (itemPath == path) return;

            await AddFolderItemAsync(
                path, 
                collectionItem.MenuItemsSource as ObservableCollection<NavigationViewItem>);
            return;
        }

        object content;

        if (collection == Folders)
        {
            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 4
            };

            var textBlock = new TextBlock { Text = path };
            stackPanel.Children.Add(textBlock);

            var progressBar = new ProgressBar
            {
                Maximum = 100,
                Value = 0,
                Height = 2,
                Margin = new Thickness(0, 0, 12, 0),
                Style = MainGrid.Resources["ScanProgressBarStyle"] as Style
            };
            stackPanel.Children.Add(progressBar);
            content = stackPanel;
        }
        else
        {
            content = Path.GetFileName(path);
        }

        var children = new ObservableCollection<NavigationViewItem>();
        var item = new NavigationViewItem
        {
            Content = content,
            Icon = new SymbolIcon(Symbol.Folder),
            MenuItemsSource = children,
            Tag = path
        };

        InsertInCollectionInOrder(collection, item);
    }

    private void PhotoService_FolderRemoved(object _, FolderChangedEventArgs e)
    {
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (NavView.SelectedItem is NavigationViewItem
                {
                    Icon: SymbolIcon { Symbol: Symbol.Folder }, 
                    Tag: string folderPath
                } 
                && folderPath == e.Path)
            {
                Photos.Clear();
            }

            RemoveFolderItem(e.Path, Folders);
        });
    }

    private static void RemoveFolderItem(string path, ObservableCollection<NavigationViewItem> collection)
    {
        foreach (var collectionItem in collection)
        {
            var itemPath = collectionItem.Tag?.ToString();
            if (string.IsNullOrEmpty(itemPath) || !path.StartsWith(itemPath)) continue;

            if (itemPath == path)
            {
                collection.Remove(collectionItem);
            }
            else
            {
                RemoveFolderItem(path, collectionItem.MenuItemsSource as ObservableCollection<NavigationViewItem>);
            }

            break;
        }
    }

    private void PhotoService_PhotoRemoved(object _, PhotoChangedEventArgs e)
    {
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            var photo = Photos.FirstOrDefault(p => p.FilePath == e.Photo.Path);
            if (photo == null) return;

            if (FocusedItem == photo)
            {
                var currentIndex = Photos.IndexOf(photo);
                Photos.Remove(photo);

                if (Photos.Count > 0)
                {
                    var nextIndex = currentIndex >= Photos.Count ? 0 : currentIndex;
                    FocusedItem = Photos[nextIndex];
                }
                else
                {
                    FocusedItem = null;
                }
            }
            else
            {
                Photos.Remove(photo);
            }
        });
    }

    private void PhotoService_PhotoAdded(object _, PhotoChangedEventArgs e)
    {
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            var item = NavView.SelectedItem as NavigationViewItem;
            if (item == null) return;

            switch (item.Icon)
            {
                case SymbolIcon { Symbol: Symbol.Folder }
                    when item.Tag is string folderPath
                         && Path.GetDirectoryName(e.Photo.Path) == folderPath:
                case SymbolIcon { Symbol: Symbol.Contact }
                    when item.Tag is long modelHash
                         && e.Photo.ModelHash == modelHash:
                {
                    var photo = new PhotoItem(e.Photo);
                    Photos.Add(photo);
                    break;
                }
            }
        });
    }

    private void PhotoService_ModelAdded(object sender, ModelChangedEventArgs e)
    {
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            ObservableCollection<NavigationViewItem> versions;
            var modelItem = Models.SingleOrDefault(m => (string)m.Content == e.Name);
            if (modelItem == null)
            {
                modelItem = new NavigationViewItem
                {
                    Content = e.Name,
                    Icon = new SymbolIcon(Symbol.Contact)
                };

                versions = [];
                modelItem.MenuItemsSource = versions;

                InsertInCollectionInOrder(Models, modelItem);
            }
            else
            {
                versions = (ObservableCollection<NavigationViewItem>)modelItem.MenuItemsSource;
            }

            var version = new NavigationViewItem
            {
                Content = e.Version,
                Icon = new SymbolIcon(Symbol.Contact),
                Tag = e.Hash
            };

            InsertInCollectionInOrder(versions, version);
        });
    }

    private void PhotoService_ModelRemoved(object sender, ModelChangedEventArgs e)
    {
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            var modelItem = Models.SingleOrDefault(m => (string)m.Content == e.Name);
            if (modelItem == null) return;

            var versions = (ObservableCollection<NavigationViewItem>)modelItem.MenuItemsSource;

            int versionIndex;
            for (versionIndex = 0; versionIndex < versions.Count; versionIndex++)
            {
                var version = versions[versionIndex];
                if (string.Compare((string)version.Content, e.Version, StringComparison.InvariantCultureIgnoreCase) > 0) break;
            }

            if (versionIndex == versions.Count) return;

            if (NavView.SelectedItem is NavigationViewItem
                {
                    Icon: SymbolIcon { Symbol: Symbol.Contact },
                    Tag: long modelHash
                }
                && modelHash == e.Hash)
            {
                Photos.Clear();
            }
            
            if (versions.Count > 1)
            {
                versions.RemoveAt(versionIndex);
                return;
            }

            Models.Remove(modelItem);
        });
    }

    private void PhotoService_ScanProgress(object sender, ScanProgressEventArgs e)
    {
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            var folderItem = Folders.FirstOrDefault(item => (string)item.Tag == e.Path);
            if (folderItem?.Content is not StackPanel stackPanel) return;

            var progressBar = stackPanel.Children.OfType<ProgressBar>().FirstOrDefault();
            if (progressBar == null) return;

            progressBar.Value = e.Progress * 100;
            if (Math.Abs(e.Progress - 1.0) < double.Epsilon)
            {
                progressBar.Visibility = Visibility.Collapsed;
            }
        });
    }


    private void PhotoItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PhotoItem photo }) return;

        FocusedItem = photo;
    }

    private void UpdateSinglePhotoView()
    {
        try
        {
            var bitmap = new BitmapImage(new Uri(FocusedItem.FilePath));
            SinglePhotoImage.Source = bitmap;

            SelectionToggle.IsChecked = FocusedItem.IsSelected;

            UpdateNavigationButtonStates();
        }
        catch (Exception)
        {
            SinglePhotoImage.Source = FocusedItem.Thumbnail;
        }
    }

    private void UpdateNavigationButtonStates()
    {
        if (FocusedItem == null) return;

        PreviousButton.IsEnabled = Photos.Count > 1;
        NextButton.IsEnabled = Photos.Count > 1;
    }

    private void SwitchToSinglePhotoView()
    {
        if (FocusedItem == null) return;

        UpdateSinglePhotoView();

        GridView.Visibility = Visibility.Collapsed;
        SinglePhotoView.Visibility = Visibility.Visible;
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

    private async Task NavigateTo(Func<Task<List<Photo>>> provider)
    {
        var photos =
            (await provider())
            .Select(p => new PhotoItem(p));

        foreach (var item in SelectedItems.ToList())
        {
            item.IsSelected = false;
        }
        SelectedItems.Clear();

        FocusedItem = null;
        Photos.Clear();

        foreach (var photo in photos)
        {
            Photos.Add(photo);
        }

        GridView.Visibility = Visibility.Visible;
        SinglePhotoView.Visibility = Visibility.Collapsed;

        GridView.ChangeView(null, 0, null);
    }

    private async Task SelectFolder(string folderPath)
    {
        await NavigateTo(() =>
            string.IsNullOrEmpty(folderPath)
                ? Task.FromResult(new List<Photo>())
                : PhotoService.GetPhotosForFolderAsync(folderPath));
    }

    private async Task SelectModel(long modelHash)
    {
        await NavigateTo(() => PhotoService.GetPhotosByModelAsync(modelHash));
    }
    
    private async Task DeleteSelectedItemsAsync()
    {
        if (SelectedItems.Count == 0) return;

        var message = SelectedItems.Count == 1
            ? $"Are you sure you want to delete {SelectedItems[0].FileName}?"
            : $"Are you sure you want to delete {SelectedItems.Count} items?";

        var dialog = new ContentDialog
        {
            Title = "Delete Photo",
            Content = message,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        foreach (var item in SelectedItems.ToList())
        {
            var file = await StorageFile.GetFileFromPathAsync(item.FilePath);
            await file.DeleteAsync();
        }
    }
}