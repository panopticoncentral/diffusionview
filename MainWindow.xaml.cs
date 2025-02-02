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
using WinRT.Interop;

namespace DiffusionView;

public sealed partial class MainWindow : INotifyPropertyChanged
{
    private readonly PhotoService _photoService;

    private string _currentFolder;

    public readonly ObservableCollection<PhotoItem> Photos = [];
    public readonly ObservableCollection<NavigationViewItem> Folders = [];
    public readonly ObservableCollection<NavigationViewItem> Models = [];

    private PhotoItem _selectedItem;
    private PhotoItem _focusedItem;

    public PhotoItem SelectedItem
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedItem)));

            if (_selectedItem == null) return;
            _selectedItem.IsSelected = true;
            var button = GetButtonForItem(_selectedItem);
            ScrollIntoView(button);
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
            UpdateSinglePhotoView(_focusedItem);
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
        _ = _photoService.InitializeAsync();
    }

    private async void MainGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            switch (e.Key)
            {
                case VirtualKey.Left:
                {
                    if (Photos.Count == 0) return;

                    if (SinglePhotoView.Visibility == Visibility.Visible)
                    {
                        PreviousButton_Click(null, null);
                    }
                    else if (SelectedItem == null)
                    {
                        SelectedItem = Photos[0];
                    }
                    else
                    {
                        var currentIndex = Photos.IndexOf(SelectedItem);
                        var newIndex = currentIndex > 0 ? currentIndex - 1 : Photos.Count - 1;
                        SelectedItem = Photos[newIndex];
                    }

                    e.Handled = true;
                    break;
                }

                case VirtualKey.Right:
                {
                    if (Photos.Count == 0) return;

                    if (SinglePhotoView.Visibility == Visibility.Visible)
                    {
                        NextButton_Click(null, null);
                    }
                    else if (SelectedItem == null)
                    {
                        SelectedItem = Photos[0];
                    }
                    else
                    {
                        var currentIndex = Photos.IndexOf(SelectedItem);
                        var newIndex = (currentIndex + 1) % Photos.Count;
                        SelectedItem = Photos[newIndex];
                    }

                    e.Handled = true;
                    break;
                }

                case VirtualKey.Delete:
                    e.Handled = await DeleteSelectedItemAsync();
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

    private void PhotoItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PhotoItem photo }) return;

        SelectedItem = photo;
        ((Grid)Content).Focus(FocusState.Programmatic);
    }

    private void PhotoService_FolderAdded(object _, FolderChangedEventArgs e)
    {
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(async void () =>
        {
            try
            {
                var children = new ObservableCollection<NavigationViewItem>();
                var rootItem = new NavigationViewItem
                {
                    Content = e.Name,
                    Icon = new SymbolIcon(Symbol.Folder),
                    MenuItemsSource = children,
                    Tag = e.Path
                };

                Folders.Add(rootItem);

                var folder = await StorageFolder.GetFolderFromPathAsync(e.Path);
                var subFolders = await folder.GetFoldersAsync();

                await AddSubFolderItemsAsync(children, subFolders);
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
                Photos.Clear();
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

                int index;
                for (index = 0; index < Models.Count; index++)
                {
                    var item = Models[index];
                    if (string.Compare((string)item.Content, e.Name, StringComparison.InvariantCultureIgnoreCase) > 0) break;
                }

                Models.Insert(index, modelItem);
            }
            else
            {
                versions = (ObservableCollection<NavigationViewItem>)modelItem.MenuItemsSource;
            }

            int versionIndex;
            for (versionIndex = 0; versionIndex < versions.Count; versionIndex++)
            {
                var version = versions[versionIndex];
                if (string.Compare((string)version.Content, e.Version, StringComparison.InvariantCultureIgnoreCase) > 0) break;
            }

            versions.Insert(versionIndex,
                new NavigationViewItem
                {
                    Content = e.Version,
                    Icon = new SymbolIcon(Symbol.Contact),
                    Tag = e.Hash
                });
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
            if (versions.Count > 1)
            {
                versions.RemoveAt(versionIndex);
                return;
            }

            Models.Remove(modelItem);
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

    private void PhotoItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PhotoItem photo }) return;

        FocusedItem = photo;
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

    private Button GetButtonForItem(PhotoItem photo)
    {
        if (GridView.Content is not ItemsRepeater repeater) return null;

        for (var i = 0; i < Photos.Count; i++)
        {
            var element = repeater.TryGetElement(i) as Button;
            if (element?.DataContext == photo) return element;
        }

        return new Button
        {
            DataContext = photo
        };
    }

    private void ScrollIntoView(FrameworkElement element)
    {
        if (GridView?.Content is not ItemsRepeater repeater) return;

        var transform = element.TransformToVisual(repeater);
        var elementBounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

        var scrollViewer = GridView;
        var viewportHeight = scrollViewer.ViewportHeight;
        var verticalOffset = scrollViewer.VerticalOffset;

        if (elementBounds.Top < verticalOffset)
        {
            scrollViewer.ChangeView(null, elementBounds.Top, null);
        }
        else if (elementBounds.Bottom > verticalOffset + viewportHeight)
        {
            scrollViewer.ChangeView(null, elementBounds.Bottom - viewportHeight, null);
        }
    }

    private void UpdateSinglePhotoView(PhotoItem photo)
    {
        try
        {
            var bitmap = new BitmapImage(new Uri(photo.FilePath));
            SinglePhotoImage.Source = bitmap;

            UpdateNavigationButtonStates();
        }
        catch (Exception)
        {
            SinglePhotoImage.Source = photo.Thumbnail;
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

        UpdateSinglePhotoView(SelectedItem);

        GridView.Visibility = Visibility.Collapsed;
        SinglePhotoView.Visibility = Visibility.Visible;
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
            await DeleteSelectedItemAsync();
        }
        catch (Exception)
        {
            // For the moment, ignore exceptions
        }
    }

    private static async Task AddSubFolderItemsAsync(ObservableCollection<NavigationViewItem> children, IReadOnlyList<StorageFolder> folders)
    {
        foreach (var folder in folders)
        {
            try
            {
                var subChildren = new ObservableCollection<NavigationViewItem>();
                var subItem = new NavigationViewItem
                {
                    Content = folder.Name,
                    Icon = new SymbolIcon(Symbol.Folder),
                    MenuItemsSource = subChildren,
                    Tag = folder.Path
                };

                children.Add(subItem);

                var subFolders = await folder.GetFoldersAsync();
                if (subFolders.Count > 0)
                {
                    await AddSubFolderItemsAsync(subChildren, subFolders);
                }
            }
            catch (Exception)
            {
                // For now, ignore exceptions
            }
        }
    }

    private async Task<bool> DeleteSelectedItemAsync()
    {
        if (SelectedItem == null) return false;

        var dialog = new ContentDialog
        {
            Title = "Delete Photo",
            Content = $"Are you sure you want to delete {SelectedItem.FileName}?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return false;

        var file = await StorageFile.GetFileFromPathAsync(SelectedItem.FilePath);
        await file.DeleteAsync();
        return true;
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

    private async Task SelectFolder(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return;

        // Clear the current selection and folder state
        SelectedItem = null;
        FocusedItem = null;
        _currentFolder = folderPath;
        Photos.Clear();

        // Load all photos for the selected folder
        var photos = (await PhotoService.GetPhotosForFolderAsync(folderPath))
            .Select(p => new PhotoItem(p));

        // Add each photo to the observable collection
        foreach (var photo in photos)
        {
            Photos.Add(photo);
        }

        // Reset the view state
        SelectedItem = null;
        FocusedItem = null;
        SinglePhotoImage.Source = null;

        // Ensure we're in grid view mode
        GridView.Visibility = Visibility.Visible;
        SinglePhotoView.Visibility = Visibility.Collapsed;

        GridView.ChangeView(null, 0, null);
    }

    private async Task SelectModel(long modelHash)
    {
        var photos =
            (await PhotoService.GetPhotosByModelAsync(modelHash))
            .Select(p => new PhotoItem(p));

        SelectedItem = null;
        FocusedItem = null;
        _currentFolder = null;
        Photos.Clear();

        foreach (var photo in photos)
        {
            Photos.Add(photo);
        }

        SelectedItem = null;
        FocusedItem = null;
        SinglePhotoImage.Source = null;

        GridView.Visibility = Visibility.Visible;
        SinglePhotoView.Visibility = Visibility.Collapsed;

        GridView.ChangeView(null, 0, null);
    }
}