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
    private readonly ObservableCollection<PhotoItem> _currentPhotos = [];

    public readonly ObservableCollection<NavigationViewItem> Folders = [];
    public readonly ObservableCollection<NavigationViewItem> Models = [];

    private PhotoItem _selectedItem;

    private bool _fileInfoExpanded = true;
    private bool _promptsExpanded = true;
    private bool _parametersExpanded = true;
    private bool _extraParametersExpanded = true;
    private bool _rawExpanded = true;

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
                _selectedItem.UpdateVisualState(GetButtonForItem(_selectedItem));
                UpdatePreviewPane(null);
            }

            _selectedItem = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedItem)));

            if (_selectedItem == null) return;

            _selectedItem.IsSelected = true;
            var button = GetButtonForItem(_selectedItem);
            _selectedItem.UpdateVisualState(button);
            ScrollIntoView(button);
            UpdatePreviewPane(_selectedItem);
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;

        FileInfoExpander.Expanding += (s, e) => _fileInfoExpanded = true;
        FileInfoExpander.Collapsed += (s, e) => _fileInfoExpanded = false;

        PromptsExpander.Expanding += (s, e) => _promptsExpanded = true;
        PromptsExpander.Collapsed += (s, e) => _promptsExpanded = false;

        ParametersExpander.Expanding += (s, e) => _parametersExpanded = true;
        ParametersExpander.Collapsed += (s, e) => _parametersExpanded = false;

        ExtraParametersExpander.Expanding += (s, e) => _extraParametersExpanded = true;
        ExtraParametersExpander.Collapsed += (s, e) => _extraParametersExpanded = false;

        RawExpander.Expanding += (s, e) => _rawExpanded = true;
        RawExpander.Collapsed += (s, e) => _rawExpanded = false;

        _photoService = new PhotoService();
        _photoService.FolderAdded += PhotoService_FolderAdded;
        _photoService.FolderRemoved += PhotoService_FolderRemoved;
        _photoService.PhotoAdded += PhotoService_PhotoAdded;
        _photoService.PhotoRemoved += PhotoService_PhotoRemoved;
        _photoService.ModelAdded += PhotoService_ModelAdded;
        _photoService.ModelRemoved += PhotoService_ModelRemoved;
        _ = _photoService.InitializeAsync();
    }

    private void MainGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Left:
            {
                if (_currentPhotos.Count == 0) return;

                if (SinglePhotoView.Visibility == Visibility.Visible)
                {
                    PreviousButton_Click(null, null);
                } 
                else if (SelectedItem == null)
                {
                    SelectedItem = _currentPhotos[0];
                }
                else
                {
                    var currentIndex = _currentPhotos.IndexOf(SelectedItem);
                    var newIndex = currentIndex > 0 ? currentIndex - 1 : _currentPhotos.Count - 1;
                    SelectedItem = _currentPhotos[newIndex];
                }

                e.Handled = true;
                break;
            }

            case VirtualKey.Right:
            {
                if (_currentPhotos.Count == 0) return;

                if (SinglePhotoView.Visibility == Visibility.Visible)
                {
                    NextButton_Click(null, null);
                }
                else if (SelectedItem == null)
                {
                    SelectedItem = _currentPhotos[0];
                }
                else
                {
                    var currentIndex = _currentPhotos.IndexOf(SelectedItem);
                    var newIndex = (currentIndex + 1) % _currentPhotos.Count;
                    SelectedItem = _currentPhotos[newIndex];
                }

                e.Handled = true;
                break;
            }
        }
    }

    private void PhotoItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PhotoItem photo }) return;

        SelectedItem = photo;
        ((Grid)Content).Focus(FocusState.Programmatic);
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
            if (SelectedItem == photo)
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
            var photo = new PhotoItem(e.Photo);
            _currentPhotos.Add(photo);
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
        throw new NotImplementedException();
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

    private void PhotoItem_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PhotoItem photo }) return;

        SelectedItem = photo;
        SwitchToSinglePhotoView();
    }

    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem == null) return;

        var currentIndex = _currentPhotos.IndexOf(SelectedItem);

        var previousIndex = currentIndex - 1;
        if (previousIndex < 0)
        {
            previousIndex = _currentPhotos.Count - 1;
        }

        SelectedItem = _currentPhotos[previousIndex];
        UpdateSinglePhotoView(_currentPhotos[previousIndex]);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem == null) return;

        var currentIndex = _currentPhotos.IndexOf(SelectedItem);

        var nextIndex = (currentIndex + 1) % _currentPhotos.Count;

        SelectedItem = _currentPhotos[nextIndex];
        UpdateSinglePhotoView(_currentPhotos[nextIndex]);
    }

    private Button GetButtonForItem(PhotoItem photo)
    {
        if (GridView.Content is not ItemsRepeater repeater) return null;

        for (var i = 0; i < _currentPhotos.Count; i++)
        {
            var element = repeater.TryGetElement(i) as Button;
            if (element?.DataContext == photo)
            {
                return element;
            }
        }

        return new Button
        {
            DataContext = photo
        };
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
        if (SelectedItem == null) return;

        PreviousButton.IsEnabled = _currentPhotos.Count > 1;
        NextButton.IsEnabled = _currentPhotos.Count > 1;
    }

    private void SwitchToSinglePhotoView()
    {
        if (SelectedItem == null) return;

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
            if (SelectedItem == null) return;
    
            var file = await StorageFile.GetFileFromPathAsync(SelectedItem.FilePath);
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
            if (SelectedItem == null) return;

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
            if (result != ContentDialogResult.Primary) return;

            var file = await StorageFile.GetFileFromPathAsync(SelectedItem.FilePath);
            await file.DeleteAsync();
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

    private void RestoreExpanderStates()
    {
        FileInfoExpander.IsExpanded = _fileInfoExpanded;
        PromptsExpander.IsExpanded = _promptsExpanded;
        ParametersExpander.IsExpanded = _parametersExpanded;
        ExtraParametersExpander.IsExpanded = _extraParametersExpanded;
        RawExpander.IsExpanded = _rawExpanded;
    }

    private void UpdatePreviewPane(PhotoItem photo)
    {
        if (photo == null)
        {
            return;
        }

        try
        {
            // Preserve expander states before updating content
            var previousStates = (_fileInfoExpanded, _promptsExpanded, _parametersExpanded, _extraParametersExpanded, _rawExpanded);

            // Restore previous expander states
            (_fileInfoExpanded, _promptsExpanded, _parametersExpanded, _extraParametersExpanded, _rawExpanded) = previousStates;
            RestoreExpanderStates();
        }
        catch (Exception)
        {
            // If there's an error updating the UI, clear everything
            UpdatePreviewPane(null);
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

    private async Task SelectFolder(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return;

        // Clear the current selection and folder state
        SelectedItem = null;
        _currentFolder = folderPath;
        _currentPhotos.Clear();

        // Load all photos for the selected folder
        var photos = (await PhotoService.GetPhotosForFolderAsync(folderPath))
            .Select(p => new PhotoItem(p));

        // Add each photo to the observable collection
        foreach (var photo in photos)
        {
            _currentPhotos.Add(photo);
        }

        // Reset the view state
        SelectedItem = null;
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
        _currentFolder = null;
        _currentPhotos.Clear();

        foreach (var photo in photos)
        {
            _currentPhotos.Add(photo);
        }

        SelectedItem = null;
        SinglePhotoImage.Source = null;

        GridView.Visibility = Visibility.Visible;
        SinglePhotoView.Visibility = Visibility.Collapsed;

        GridView.ChangeView(null, 0, null);
    }
}