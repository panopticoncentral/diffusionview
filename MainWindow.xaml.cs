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
using Microsoft.EntityFrameworkCore;

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
            UpdateParameterExpanders();
        }
    }

    private void UpdateParameterExpanders()
    {
        if (FocusedItem == null) return;

        UpdateGenerationParametersExpander();
        UpdateLorasInversionsExpander();
        UpdateTextualInversionsExpander();
        UpdateADetailerExpander();
        UpdateHiresParametersExpander();
        UpdateAdditionalParametersExpander();
    }

    private void AddRow(Grid grid, int row, string name, string value)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (!string.IsNullOrWhiteSpace(name))
        {
            var textBlock = new TextBlock
            {
                Text = name + ":",
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(textBlock, row);
            Grid.SetColumn(textBlock, 0);
            grid.Children.Add(textBlock);
        }

        var textBoxStyle = PropertiesPane.Resources["SelectableTextStyle"] as Style;
        var textBox = new TextBox
        {
            Text = value,
            Style = textBoxStyle,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(textBox, row);
        Grid.SetColumn(textBox, 1);
        grid.Children.Add(textBox);
    }

    private void UpdateParametersExpander(Expander expander, Grid grid, List<(string, string)> properties)
    {
        grid.RowDefinitions.Clear();
        grid.Children.Clear();

        var row = 0;

        foreach (var (name, property) in properties)
        {
            if (string.IsNullOrWhiteSpace(property)) continue;
            AddRow(grid, row, name, property);
            row++;
        }

        expander.Visibility = row == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateGenerationParametersExpander()
    {
        var generationProperties = new List<(string, string)>
        {
            ("Model", $"{FocusedItem.ModelName} [{FocusedItem.ModelVersionName}]"),
            ("Generated Resolution", FocusedItem.GeneratedResolution),
            ("Steps", FocusedItem.Steps),
            ("CFG scale", FocusedItem.CfgScale),
            ("Sampler", FocusedItem.Sampler),
            ("Seed", FocusedItem.Seed),
            ("Version", FocusedItem.Version),
            ("Clip Skip", FocusedItem.ClipSkip),
            ("VAE", FocusedItem.Vae),
            ("Denoising Strength", FocusedItem.DenoisingStrength),
            ("Variation Seed", FocusedItem.VariationSeed),
            ("Variation Seed Strength", FocusedItem.VariationSeedStrength),
            ("Schedule Type", FocusedItem.ScheduleType),
            ("Don't Normalize Emphasis", FocusedItem.NoEmphasisNorm),
            ("Rng", FocusedItem.Rng),
            ("Remix Of", FocusedItem.RemixOf)
        };

        UpdateParametersExpander(GenerationParametersExpander, GenerationParametersGrid, generationProperties);
    }

    private void UpdateLorasInversionsExpander()
    {
        UpdateParametersExpander(
            LorasExpander,
            LorasGrid,
            FocusedItem
                .Loras
                .Select(lora => ((string)null, $"{lora.name} [{lora.versionName}]{(lora.weight != null ? $" ({lora.weight})" : string.Empty)}"))
                .ToList());
    }

    private void UpdateTextualInversionsExpander()
    {
        UpdateParametersExpander(
            TextualInversionsExpander, 
            TextualInversionsGrid, 
            FocusedItem
                .TextualInversions
                .Select(ti => ((string)null, $"{ti.name} [{ti.versionName}]"))
                .ToList());
    }

    private void UpdateADetailerExpander()
    {
        var aDetailerProperties = new List<(string, string)>
        {
            ("Model", FocusedItem.ADetailerModel),
            ("Confidence", FocusedItem.ADetailerConfidence),
            ("Dilate/Erode", FocusedItem.ADetailerDilateErode),
            ("Mask Blur", FocusedItem.ADetailerMaskBlur),
            ("Denoising Strength", FocusedItem.ADetailerDenoisingStrength),
            ("Inpaint Only Masked", FocusedItem.ADetailerInpaintOnlyMasked),
            ("Inpaint Padding", FocusedItem.ADetailerInpaintPadding),
            ("Version", FocusedItem.ADetailerVersion)
        };

        UpdateParametersExpander(ADetailerParametersExpander, ADetailerParametersGrid, aDetailerProperties);
    }

    private void UpdateHiresParametersExpander()
    {
        var hiresDetailerProperties = new List<(string, string)>
        {
            ("Prompt", FocusedItem.HiresPrompt),
            ("Steps", FocusedItem.HiresSteps),
            ("Cfg Scale", FocusedItem.HiresCfgScale),
            ("Upscale", FocusedItem.HiresUpscale),
            ("Upscaler", FocusedItem.HiresUpscaler)
        };

        UpdateParametersExpander(HiresParametersExpander, HiresParametersGrid, hiresDetailerProperties);
    }

    private void UpdateAdditionalParametersExpander()
    {
        AdditionalParametersGrid.RowDefinitions.Clear();
        AdditionalParametersGrid.Children.Clear();

        if (FocusedItem.ParametersList.Any())
        {
            var row = 0;

            foreach (var (key, value) in FocusedItem.ParametersList)
            {
                AddRow(AdditionalParametersGrid, row, key, value);
                row++;
            }
            AdditionalParametersExpander.Visibility = Visibility.Visible;
        }
        else
        {
            AdditionalParametersExpander.Visibility = Visibility.Collapsed;
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

            if (item.Icon is SymbolIcon { Symbol: Symbol.Contact } && item.Tag is long modelVersionId)
            {
                await SelectModel(modelVersionId);
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

    private void PhotoService_FolderAdded(object _, PhotoService.FolderChangedEventArgs e)
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

    private async Task DeleteFolderAsync(string folderPath)
    {
        try
        {
            var dialogContent = $"Are you sure you want to delete the folder '{folderPath}' from your computer?";

            var dialog = new ContentDialog
            {
                Title = "Delete Folder",
                Content = dialogContent,
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            if (Directory.Exists(folderPath))
            {
                Directory.Delete(folderPath, true);
            }
        }
        catch (Exception ex)
        {
            var errorDialog = new ContentDialog
            {
                Title = "Error",
                Content = $"An error occurred when trying to delete the folder: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };

            await errorDialog.ShowAsync();
        }
    }

    private MenuFlyout CreateFolderContextMenu(string folderPath)
    {
        var contextMenu = new MenuFlyout();

        var deleteItem = new MenuFlyoutItem
        {
            Text = "Remove Folder",
            Icon = new SymbolIcon(Symbol.Delete)
        };

        deleteItem.Click += async (_, _) => await DeleteFolderAsync(folderPath);

        contextMenu.Items.Add(deleteItem);
        return contextMenu;
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
            Tag = path,
            ContextFlyout = CreateFolderContextMenu(path)
        };

        InsertInCollectionInOrder(collection, item);
    }

    private void PhotoService_FolderRemoved(object _, PhotoService.FolderChangedEventArgs e)
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

    private void PhotoService_PhotoRemoved(object _, PhotoService.PhotoChangedEventArgs e)
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

    private void PhotoService_PhotoAdded(object _, PhotoService.PhotoChangedEventArgs e)
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
                    when item.Tag is long modelVersionId
                         && e.Photo.Models.FirstOrDefault(m => m.Model.Kind == "Checkpoint")?.ModelVersionId == modelVersionId:
                {
                    var photo = new PhotoItem(e.Photo);
                    Photos.Add(photo);
                    break;
                }
            }
        });
    }

    private void PhotoService_ModelAdded(object sender, PhotoService.ModelChangedEventArgs e)
    {
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            ObservableCollection<NavigationViewItem> versions;
            var modelItem = Models.SingleOrDefault(m => (string)m.Content == e.ModelName);
            if (modelItem == null)
            {
                modelItem = new NavigationViewItem
                {
                    Content = e.ModelName,
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
                Content = e.ModelVersionName,
                Icon = new SymbolIcon(Symbol.Contact),
                Tag = e.ModelVersionId
            };

            InsertInCollectionInOrder(versions, version);
        });
    }

    private void PhotoService_ModelRemoved(object sender, PhotoService.ModelChangedEventArgs e)
    {
        if (DispatcherQueue == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            var modelItem = Models.SingleOrDefault(m => (string)m.Content == e.ModelName);
            if (modelItem == null) return;

            var versions = (ObservableCollection<NavigationViewItem>)modelItem.MenuItemsSource;

            int versionIndex;
            for (versionIndex = 0; versionIndex < versions.Count; versionIndex++)
            {
                var version = versions[versionIndex];
                if (string.Compare((string)version.Content, e.ModelVersionName, StringComparison.InvariantCultureIgnoreCase) == 0) break;
            }

            if (versionIndex == versions.Count) return;

            if (NavView.SelectedItem is NavigationViewItem
                {
                    Icon: SymbolIcon { Symbol: Symbol.Contact },
                    Tag: long modelVersionId
                }
                && modelVersionId == e.ModelVersionId)
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

    private void PhotoService_ScanProgress(object sender, PhotoService.ScanProgressEventArgs e)
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

    private async Task SelectModel(long modelVersionId)
    {
        await NavigateTo(() => PhotoService.GetPhotosByModelVersionIdAsync(modelVersionId));
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