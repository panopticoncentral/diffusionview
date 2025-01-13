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
using Microsoft.UI.Xaml.Input;

namespace DiffusionView;

public sealed partial class MainWindow
{
    private readonly PhotoService _photoService;

    private string _currentFolder;
    private readonly ObservableCollection<PhotoItem> _currentPhotos = [];

    private Button _selectedItem;

    private bool _fileInfoExpanded = true;
    private bool _promptsExpanded = true;
    private bool _parametersExpanded = true;
    private bool _extraParametersExpanded = true;
    private bool _rawExpanded = true;

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
        _photoService.ThumbnailLoaded += PhotoService_ThumbnailLoaded;
        _photoService.Initialize();
    }

    private void MainGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Only handle left/right arrow keys
        if (e.Key != Windows.System.VirtualKey.Left && e.Key != Windows.System.VirtualKey.Right)
            return;

        // If we have no photos, there's nothing to navigate
        if (_currentPhotos.Count == 0)
            return;

        if (SinglePhotoView.Visibility == Visibility.Visible)
        {
            // In single photo view, use the existing navigation buttons
            if (e.Key == Windows.System.VirtualKey.Left)
                PreviousButton_Click(null, null);
            else
                NextButton_Click(null, null);

            e.Handled = true;
            return;
        }

        // In grid view, navigate the selection
        if (SelectedItem?.DataContext is not PhotoItem currentPhoto)
        {
            // If nothing is selected, select the first item
            var firstButton = GetButtonForItem(_currentPhotos[0]);
            if (firstButton != null)
            {
                SelectedItem = firstButton;
                ScrollIntoView(firstButton);
            }
            e.Handled = true;
            return;
        }

        // Find the current index
        var currentIndex = _currentPhotos.IndexOf(currentPhoto);
        if (currentIndex == -1)
            return;

        // Calculate the new index
        var newIndex = currentIndex;
        if (e.Key == Windows.System.VirtualKey.Left)
        {
            newIndex = currentIndex > 0 ? currentIndex - 1 : _currentPhotos.Count - 1;
        }
        else
        {
            newIndex = (currentIndex + 1) % _currentPhotos.Count;
        }

        // Update selection
        var nextButton = GetButtonForItem(_currentPhotos[newIndex]);
        if (nextButton != null)
        {
            // Update the selection state of the current photo
            if (currentPhoto != null)
            {
                currentPhoto.IsSelected = false;
                currentPhoto.UpdateVisualState(SelectedItem);
            }

            // Update the selection state of the new photo
            var nextPhoto = _currentPhotos[newIndex];
            nextPhoto.IsSelected = true;
            nextPhoto.UpdateVisualState(nextButton);

            // Update selected item and scroll
            SelectedItem = nextButton;
            UpdatePreviewPane(nextPhoto);
            ScrollIntoView(nextButton);
        }

        e.Handled = true;
    }

    // Update the PhotoItem_Click method to ensure the Grid gets focus
    private void PhotoItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PhotoItem photo } button) return;

        SelectedItem = button;
        // Ensure the main grid has focus to receive keyboard input
        ((Grid)Content).Focus(FocusState.Programmatic);
    }

    private void ScrollIntoView(FrameworkElement element)
    {
        if (GridView?.Content is not ItemsRepeater repeater)
            return;

        // Get the element bounds within the ItemsRepeater
        var transform = element.TransformToVisual(repeater);
        var elementBounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

        // Get the visible bounds of the ScrollViewer
        var scrollViewer = GridView;
        var viewportHeight = scrollViewer.ViewportHeight;
        var verticalOffset = scrollViewer.VerticalOffset;

        // If the element is above the visible area, scroll up
        if (elementBounds.Top < verticalOffset)
        {
            scrollViewer.ChangeView(null, elementBounds.Top, null);
        }
        // If the element is below the visible area, scroll down
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
                // Basic file properties
                FileName = e.Photo.Name,
                FilePath = e.Photo.Path,
                DateTaken = e.Photo.DateTaken,
                FileSize = e.Photo.FileSize,
                Width = e.Photo.Width,
                Height = e.Photo.Height,
                LastModified = e.Photo.LastModified,
                Thumbnail = e.Photo.ThumbnailData != null ? CreateBitmapImage(e.Photo.ThumbnailData) : null,

                // Stable Diffusion metadata
                Prompt = e.Photo.Prompt,
                NegativePrompt = e.Photo.NegativePrompt,
                Steps = e.Photo.Steps,
                GeneratedWidth = e.Photo.GeneratedWidth,
                GeneratedHeight = e.Photo.GeneratedHeight,
                Sampler = e.Photo.Sampler,
                CfgScale = e.Photo.CfgScale,
                Seed = e.Photo.Seed,
                Model = e.Photo.Model,
                ModelHash = e.Photo.ModelHash,
                Version = e.Photo.Version,
                OtherParameters = new Dictionary<string, string>(e.Photo.OtherParameters),

                Raw = e.Photo.Raw
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

    private void PhotoItem_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PhotoItem photo } button) return;

        SelectedItem = button;
        SwitchToSinglePhotoView();
    }

    // Handles the previous button click in single photo view
    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem?.DataContext is not PhotoItem currentPhoto) return;

        // Find the current index in the photo collection
        var currentIndex = _currentPhotos.IndexOf(currentPhoto);

        // Calculate the previous index, wrapping around to the end if necessary
        var previousIndex = currentIndex - 1;
        if (previousIndex < 0)
        {
            previousIndex = _currentPhotos.Count - 1;
        }

        // Find the button associated with the previous photo
        var photoButton = GetButtonForItem(_currentPhotos[previousIndex]);
        if (photoButton != null)
        {
            // Update selection and view
            SelectedItem = photoButton;
            UpdateSinglePhotoView(_currentPhotos[previousIndex]);
        }
    }

    // Handles the next button click in single photo view
    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem?.DataContext is not PhotoItem currentPhoto) return;

        // Find the current index in the photo collection
        var currentIndex = _currentPhotos.IndexOf(currentPhoto);

        // Calculate the next index, wrapping around to the start if necessary
        var nextIndex = (currentIndex + 1) % _currentPhotos.Count;

        // Find the button associated with the next photo
        var photoButton = GetButtonForItem(_currentPhotos[nextIndex]);
        if (photoButton != null)
        {
            // Update selection and view
            SelectedItem = photoButton;
            UpdateSinglePhotoView(_currentPhotos[nextIndex]);
        }
    }

    // Helper method to find the Button associated with a PhotoItem
    private Button GetButtonForItem(PhotoItem photo)
    {
        // If we have an ItemsRepeater, we can search through its realized elements
        if (GridView.Content is not ItemsRepeater repeater) return null;

        // Look through all realized elements to find the one matching our photo
        for (var i = 0; i < _currentPhotos.Count; i++)
        {
            var element = repeater.TryGetElement(i) as Button;
            if (element?.DataContext == photo)
            {
                return element;
            }
        }

        // If we couldn't find the button (item might not be realized), create a new one
        return new Button
        {
            DataContext = photo
        };
    }

    // Helper method to update the single photo view with a new photo
    private void UpdateSinglePhotoView(PhotoItem photo)
    {
        try
        {
            // Load and display the full-resolution image
            var bitmap = new BitmapImage(new Uri(photo.FilePath));
            SinglePhotoImage.Source = bitmap;

            // Update navigation button states
            UpdateNavigationButtonStates();
        }
        catch (Exception)
        {
            // If loading full resolution fails, fall back to thumbnail
            SinglePhotoImage.Source = photo.Thumbnail;
        }
    }

    // Updates the enabled state of navigation buttons
    private void UpdateNavigationButtonStates()
    {
        if (SelectedItem?.DataContext is not PhotoItem currentPhoto) return;

        var currentIndex = _currentPhotos.IndexOf(currentPhoto);

        // Enable/disable navigation buttons based on photo position and collection size
        PreviousButton.IsEnabled = _currentPhotos.Count > 1;
        NextButton.IsEnabled = _currentPhotos.Count > 1;
    }

    // Updated version of SwitchToSinglePhotoView to use the new helper method
    private void SwitchToSinglePhotoView()
    {
        if (SelectedItem?.DataContext is not PhotoItem photo) return;

        UpdateSinglePhotoView(photo);

        // Switch visibility
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
            // Clear all fields when no photo is selected
            FileNameText.Text = "";
            FilePathText.Text = "";
            LastModifiedText.Text = "";
            SizeText.Text = "";
            ResolutionText.Text = "";
            ModelText.Text = "";
            ModelHashText.Text = "";
            StepsText.Text = "";
            CfgScaleText.Text = "";
            SamplerText.Text = "";
            SeedText.Text = "";
            VersionText.Text = "";
            PromptText.Text = "";
            NegativePromptText.Text = "";
            return;
        }

        try
        {
            // Preserve expander states before updating content
            var previousStates = (_fileInfoExpanded, _promptsExpanded, _parametersExpanded, _extraParametersExpanded, _rawExpanded);

            // Update all the fields as before...
            FileNameText.Text = photo.FileName;
            FilePathText.Text = photo.FilePath;
            LastModifiedText.Text = photo.LastModified.ToString("g");
            SizeText.Text = FormatFileSize(photo.FileSize);
            ResolutionText.Text = $"{photo.Width} x {photo.Height}";

            ModelText.Text = photo.Model ?? "Unknown";
            ModelHashText.Text = photo.ModelHash != 0 ? photo.ModelHash.ToString("X") : "Unknown";
            StepsText.Text = photo.Steps != 0 ? photo.Steps.ToString() : "Unknown";
            GeneratedResolutionText.Text = $"{photo.GeneratedWidth} x {photo.GeneratedHeight}";
            CfgScaleText.Text = photo.CfgScale != 0 ? photo.CfgScale.ToString("F1") : "Unknown";
            SamplerText.Text = photo.Sampler ?? "Unknown";
            SeedText.Text = photo.Seed != 0 ? photo.Seed.ToString("X") : "Unknown";
            VersionText.Text = photo.Version ?? "Unknown";

            PromptText.Text = photo.Prompt ?? "No prompt available";
            NegativePromptText.Text = photo.NegativePrompt ?? "No negative prompt";

            if (photo.OtherParameters?.Count > 0)
            {
                var parameters = photo.OtherParameters
                    .Select(kvp => new KeyValuePair(kvp.Key, kvp.Value))
                    .OrderBy(kvp => kvp.Key)
                    .ToList();
                ExtraParametersRepeater.ItemsSource = parameters;
                ExtraParametersExpander.Visibility = Visibility.Visible;
            }
            else
            {
                ExtraParametersExpander.Visibility = Visibility.Collapsed;
            }

            RawText.Text = photo.Raw ?? "No raw data available";

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

    private async Task SelectFolder(NavigationViewItem folderViewItem)
    {
        // Get the folder path from the navigation item's Tag property
        var folderPath = (string)folderViewItem.Tag;
        if (string.IsNullOrEmpty(folderPath)) return;

        // Clear the current selection and folder state
        SelectedItem = null;
        _currentFolder = folderPath;
        _currentPhotos.Clear();

        // Load all photos for the selected folder
        var photos = (await _photoService.GetPhotosForFolderAsync(folderPath))
            .Select(p => new PhotoItem
            {
                // Basic file properties
                FileName = p.Name,
                FilePath = p.Path,
                DateTaken = p.DateTaken,
                FileSize = p.FileSize,
                Width = p.Width,
                Height = p.Height,
                LastModified = p.LastModified,
                Thumbnail = p.ThumbnailData != null ? CreateBitmapImage(p.ThumbnailData) : null,

                // Stable Diffusion metadata
                Prompt = p.Prompt,
                NegativePrompt = p.NegativePrompt,
                Steps = p.Steps,
                GeneratedWidth = p.GeneratedWidth,
                GeneratedHeight = p.GeneratedHeight,
                Sampler = p.Sampler,
                CfgScale = p.CfgScale,
                Seed = p.Seed,
                Model = p.Model,
                ModelHash = p.ModelHash,
                Version = p.Version,
                OtherParameters = new Dictionary<string, string>(p.OtherParameters),

                Raw = p.Raw
            });

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
    }
}