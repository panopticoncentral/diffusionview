using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Geolocation;

namespace DiffusionView
{
    public sealed partial class MainWindow : Window
    {
        private readonly PhotoCollection _photoCollection;
        private readonly ThumbnailCache _thumbnailCache;
        private readonly MetadataCache _metadataCache;
        private readonly ImageLoader _imageLoader;
        private readonly SemaphoreSlim _loadingSemaphore = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _loadingCancellation;

        private const int PAGE_SIZE = 50;
        private bool _isLoading;
        private string _currentFolderPath;
        private PhotoSortOption _currentSortOption = PhotoSortOption.Name;
        private bool _sortAscending = true;
        private string _searchFilter = "";
        private string _fileTypeFilter = "";
        private DateTime? _fromDate;
        private DateTime? _toDate;

        private PhotoItem _selectedItem;
        public PhotoItem SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (_selectedItem != value)
                {
                    // Deselect old item
                    if (_selectedItem != null)
                    {
                        _selectedItem.IsSelected = false;
                    }

                    _selectedItem = value;

                    // Select new item
                    if (_selectedItem != null)
                    {
                        _selectedItem.IsSelected = true;
                        UpdatePreviewPane(_selectedItem);
                    }
                }
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            ExtendsContentIntoTitleBar = true;

            _thumbnailCache = new ThumbnailCache();
            _metadataCache = new MetadataCache();
            _imageLoader = new ImageLoader();
            _photoCollection = new PhotoCollection();

            PhotoRepeater.ItemsSource = _photoCollection;
            SetupEventHandlers();
        }

        private void SetupEventHandlers()
        {
            NavView.ItemInvoked += NavView_ItemInvoked;
            AddFolderButton.Tapped += AddFolderButton_Tapped;
        }

        private async void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer is NavigationViewItem item)
            {
                if (item == AddFolderButton)
                    return;

                var folderName = item.Content.ToString();
                var folder = GetFolderByName(folderName);
                if (folder != null)
                {
                    _currentFolderPath = folder.FolderPath;
                    await LoadPhotosFromFolder(folder.FolderPath);
                }
            }
        }
        
        private void PhotoItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is PhotoItem photo)
            {
                SelectedItem = photo;
            }
        }

        private async void AddFolderButton_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var folderPicker = new FolderPicker();
            folderPicker.FileTypeFilter.Add("*");

            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            StorageFolder folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                var folderItem = new FolderItem
                {
                    Name = folder.Name,
                    FolderPath = folder.Path
                };

                AddFolderToNavigation(folderItem);
                _currentFolderPath = folder.Path;
                await LoadPhotosFromFolder(folder.Path);
            }
        }

        private async Task LoadPhotosFromFolder(string folderPath)
        {
            _photoCollection.Clear();
            _currentFolderPath = folderPath;

            try
            {
                _loadingCancellation?.Cancel();
                _loadingCancellation = new CancellationTokenSource();
                var cancellationToken = _loadingCancellation.Token;

                var queryOptions = new QueryOptions
                {
                    FolderDepth = FolderDepth.Deep,
                    IndexerOption = IndexerOption.UseIndexerWhenAvailable
                };

                // Set up the property prefetch for better performance
                queryOptions.SetPropertyPrefetch(PropertyPrefetchOptions.BasicProperties |
                                               PropertyPrefetchOptions.ImageProperties,
                                               new string[] { "System.GPS.Latitude", "System.GPS.Longitude" });

                // Add file type filters if specified
                if (!string.IsNullOrEmpty(_fileTypeFilter))
                {
                    queryOptions.FileTypeFilter.Add(_fileTypeFilter);
                }
                else
                {
                    // Default image file types
                    queryOptions.FileTypeFilter.Add(".jpg");
                    queryOptions.FileTypeFilter.Add(".jpeg");
                    queryOptions.FileTypeFilter.Add(".png");
                    queryOptions.FileTypeFilter.Add(".gif");
                    queryOptions.FileTypeFilter.Add(".bmp");
                }

                // Start the loading indicator
                LoadingMoreRing.IsActive = true;
                _isLoading = true;

                var folder = await StorageFolder.GetFolderFromPathAsync(folderPath);
                var query = folder.CreateFileQueryWithOptions(queryOptions);

                // Get initial batch of files
                var files = await query.GetFilesAsync(0, PAGE_SIZE);

                foreach (var file in files.Where(f => FilterMatches(f)))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var photoItem = await CreatePhotoItemAsync(file);
                    _photoCollection.Add(photoItem);

                    // Start preloading the full image
                    _ = _imageLoader.PreloadImageAsync(photoItem.FilePath);
                }

                // Apply current sorting
                _photoCollection.ApplySort(_currentSortOption, _sortAscending);

                // Clear selection
                SelectedItem = null;
                PreviewImage.Source = null;
            }
            catch (OperationCanceledException)
            {
                // Loading was cancelled, ignore
            }
            catch (Exception ex)
            {
                // Handle or log any errors
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
                LoadingMoreRing.IsActive = false;
            }
        }

        private async void UpdatePreviewPane(PhotoItem photo)
        {
            if (photo == null)
            {
                PreviewImage.Source = null;
                return;
            }

            // Update the preview image
            try
            {
                var bitmap = new BitmapImage(new Uri(photo.FilePath));
                PreviewImage.Source = bitmap;

                // Update details
                FileNameText.Text = photo.FileName;
                DateTakenText.Text = photo.DateTaken?.ToString("MMMM dd, yyyy") ?? "Unknown";
                SizeText.Text = FormatFileSize(photo.FileSize);
                ResolutionText.Text = $"{photo.Width} x {photo.Height}";
                LocationText.Text = photo.Location != null
                    ? $"{photo.Location.Latitude:F6}, {photo.Location.Longitude:F6}"
                    : "No location data";
            }
            catch (Exception ex)
            {
                // Handle any loading errors
                PreviewImage.Source = null;
                // Optionally show error message
            }
        }

        private string FormatFileSize(ulong bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double size = bytes;

            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.##} {sizes[order]}";
        }

        private void AddFolderToNavigation(FolderItem folderItem)
        {
            var navItem = new NavigationViewItem
            {
                Content = folderItem.Name,
                Icon = new SymbolIcon(Symbol.Folder)
            };
            NavView.MenuItems.Add(navItem);
        }

        private async void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_isLoading) return;

            var scrollViewer = (ScrollViewer)sender;
            var verticalOffset = scrollViewer.VerticalOffset;
            var maxVerticalOffset = scrollViewer.ScrollableHeight;

            if (maxVerticalOffset - verticalOffset <= 200) // Load more when near bottom
            {
                await LoadMorePhotosAsync();
            }
        }

        private async Task LoadMorePhotosAsync()
        {
            if (_isLoading) return;

            await _loadingSemaphore.WaitAsync();
            try
            {
                _isLoading = true;
                LoadingMoreRing.IsActive = true;

                _loadingCancellation?.Cancel();
                _loadingCancellation = new CancellationTokenSource();
                var cancellationToken = _loadingCancellation.Token;

                var queryOptions = new QueryOptions
                {
                    FolderDepth = FolderDepth.Deep,
                    IndexerOption = IndexerOption.UseIndexerWhenAvailable
                };

                queryOptions.SetPropertyPrefetch(PropertyPrefetchOptions.BasicProperties |
                                               PropertyPrefetchOptions.ImageProperties,
                                               new string[] { "System.GPS.Latitude", "System.GPS.Longitude" });

                if (!string.IsNullOrEmpty(_fileTypeFilter))
                {
                    queryOptions.FileTypeFilter.Add(_fileTypeFilter);
                }

                var folder = await StorageFolder.GetFolderFromPathAsync(_currentFolderPath);
                var query = folder.CreateFileQueryWithOptions(queryOptions);

                var files = (await query.GetFilesAsync())
                    .Where(f => FilterMatches(f))
                    .Skip(_photoCollection.Count)
                    .Take(PAGE_SIZE);

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var photoItem = await CreatePhotoItemAsync(file);
                    _photoCollection.Add(photoItem);
                    _ = _imageLoader.PreloadImageAsync(photoItem.FilePath);
                }

                _photoCollection.ApplySort(_currentSortOption, _sortAscending);
            }
            catch (OperationCanceledException)
            {
                // Loading was cancelled, ignore
            }
            finally
            {
                _isLoading = false;
                LoadingMoreRing.IsActive = false;
                _loadingSemaphore.Release();
            }
        }

        private bool FilterMatches(StorageFile file)
        {
            if (!string.IsNullOrEmpty(_searchFilter) &&
                !file.Name.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (_fromDate.HasValue || _toDate.HasValue)
            {
                var dateTaken = _metadataCache.GetDateTaken(file.Path);
                if (dateTaken.HasValue)
                {
                    if (_fromDate.HasValue && dateTaken < _fromDate.Value) return false;
                    if (_toDate.HasValue && dateTaken > _toDate.Value) return false;
                }
            }

            return true;
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
                    Location = await GetPhotoLocation(file)
                };

                _metadataCache.AddMetadata(file.Path, metadata);
                UpdatePhotoItemFromMetadata(photoItem, metadata);
            }
            finally
            {
                photoItem.IsLoading = false;
            }
        }

        private void UpdatePhotoItemFromMetadata(PhotoItem photoItem, PhotoMetadata metadata)
        {
            photoItem.DateTaken = metadata.DateTaken;
            photoItem.FileSize = metadata.FileSize;
            photoItem.Width = metadata.Width;
            photoItem.Height = metadata.Height;
            photoItem.Location = metadata.Location;
        }

        private async Task<GeoLocation> GetPhotoLocation(StorageFile file)
        {
            try
            {
                var gps = await file.Properties.GetImagePropertiesAsync();
                if (gps.Latitude.HasValue && gps.Longitude.HasValue)
                {
                    return new GeoLocation
                    {
                        Latitude = gps.Latitude.Value,
                        Longitude = gps.Longitude.Value
                    };
                }
            }
            catch { }
            return null;
        }

        private async void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is PhotoItem selectedPhoto)
            {
                var file = await StorageFile.GetFileFromPathAsync(selectedPhoto.FilePath);
                await Launcher.LaunchFileAsync(file);
            }
        }

        private async void ShareButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is PhotoItem selectedPhoto)
            {
                var dataTransferManager = DataTransferManager.GetForCurrentView();
                // Implement sharing logic
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is PhotoItem selectedPhoto)
            {
                var dialog = new ContentDialog
                {
                    Title = "Delete Photo",
                    Content = $"Are you sure you want to delete {selectedPhoto.FileName}?",
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    var file = await StorageFile.GetFileFromPathAsync(selectedPhoto.FilePath);
                    await file.DeleteAsync();
                    _photoCollection.Remove(selectedPhoto);
                    _thumbnailCache.RemoveThumbnail(selectedPhoto.FilePath);
                    _metadataCache.RemoveMetadata(selectedPhoto.FilePath);
                }
            }
        }

        private void ViewMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is AppBarButton button)
            {
                var viewMode = button.Tag.ToString();
                // Implement view mode switching logic
            }
        }

        private void SortBy_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem menuItem)
            {
                _currentSortOption = Enum.Parse<PhotoSortOption>(menuItem.Tag.ToString());
                _photoCollection.ApplySort(_currentSortOption, _sortAscending);
            }
        }

        private void SortDirection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleMenuFlyoutItem toggleItem)
            {
                _sortAscending = toggleItem.IsChecked;
                _photoCollection.ApplySort(_currentSortOption, _sortAscending);
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchFilter = SearchBox.Text;
            RefreshPhotos();
        }

        private void FileType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox)
            {
                _fileTypeFilter = comboBox.SelectedItem.ToString() == "All" ? "" : comboBox.SelectedItem.ToString();
                RefreshPhotos();
            }
        }

        private void DateFilter_Changed(object sender, DatePickerValueChangedEventArgs e)
        {
            if (sender is DatePicker datePicker)
            {
                if (datePicker.Name == "FromDatePicker")
                    _fromDate = datePicker.Date.Date;
                else
                    _toDate = datePicker.Date.Date;

                RefreshPhotos();
            }
        }

        private async void RefreshPhotos()
        {
            _photoCollection.Clear();
            await LoadMorePhotosAsync();
        }

        private FolderItem GetFolderByName(string name)
        {
            // Implement folder lookup logic
            return null;
        }

        // Support Classes

        private class ThumbnailCache
        {
            private readonly ConcurrentDictionary<string, BitmapImage> _cache = new();
            private const int MAX_CACHE_SIZE = 200;

            public async Task<BitmapImage> GetThumbnailAsync(string filePath)
            {
                if (_cache.TryGetValue(filePath, out var cached))
                {
                    return cached;
                }

                var thumbnail = new BitmapImage();
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(filePath);
                    using var thumbnailStream = await file.GetThumbnailAsync(ThumbnailMode.PicturesView);
                    await thumbnail.SetSourceAsync(thumbnailStream);

                    if (_cache.Count >= MAX_CACHE_SIZE)
                    {
                        var removeKeys = _cache.Keys.Take(_cache.Count - MAX_CACHE_SIZE + 1);
                        foreach (var key in removeKeys)
                        {
                            _cache.TryRemove(key, out _);
                        }
                    }
                    _cache.TryAdd(filePath, thumbnail);
                }
                catch
                {
                    thumbnail = new BitmapImage(new Uri("ms-appx:///Assets/PlaceholderImage.png"));
                }

                return thumbnail;
            }

            public void RemoveThumbnail(string filePath)
            {
                _cache.TryRemove(filePath, out _);
            }

            public void Clear() => _cache.Clear();
        }

        private class MetadataCache
        {
            private readonly ConcurrentDictionary<string, PhotoMetadata> _cache = new();

            public void AddMetadata(string filePath, PhotoMetadata metadata)
            {
                _cache.AddOrUpdate(filePath, metadata, (_, _) => metadata);
            }

            public bool TryGetMetadata(string filePath, out PhotoMetadata metadata)
            {
                return _cache.TryGetValue(filePath, out metadata);
            }

            public DateTime? GetDateTaken(string filePath)
            {
                return _cache.TryGetValue(filePath, out var metadata) ? metadata.DateTaken : null;
            }

            public void RemoveMetadata(string filePath)
            {
                _cache.TryRemove(filePath, out _);
            }

            public void Clear() => _cache.Clear();
        }

        private class ImageLoader
        {
            private readonly ConcurrentDictionary<string, Task> _loadingTasks = new();
            private readonly SemaphoreSlim _concurrencyLimiter = new(3); // Max 3 concurrent loads

            public async Task PreloadImageAsync(string filePath)
            {
                if (_loadingTasks.ContainsKey(filePath)) return;

                var loadTask = LoadImageAsync(filePath);
                _loadingTasks.TryAdd(filePath, loadTask);

                try
                {
                    await loadTask;
                }
                finally
                {
                    _loadingTasks.TryRemove(filePath, out _);
                }
            }

            private async Task LoadImageAsync(string filePath)
            {
                await _concurrencyLimiter.WaitAsync();
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(filePath);
                    using var stream = await file.OpenReadAsync();
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                }
                finally
                {
                    _concurrencyLimiter.Release();
                }
            }
        }

        private class PhotoCollection : ObservableCollection<PhotoItem>
        {
            public void ApplySort(PhotoSortOption sortOption, bool ascending)
            {
                var sorted = sortOption switch
                {
                    PhotoSortOption.Name => this.OrderBy(p => p.FileName),
                    PhotoSortOption.Date => this.OrderBy(p => p.DateTaken ?? DateTime.MaxValue),
                    PhotoSortOption.Size => this.OrderBy(p => p.FileSize),
                    _ => this.OrderBy(p => p.FileName)
                };

                if (!ascending)
                {
                    sorted = (IOrderedEnumerable<PhotoItem>)sorted.Reverse();
                }

                var items = sorted.ToList();
                this.Clear();
                foreach (var item in items)
                {
                    this.Add(item);
                }
            }
        }

        public class PhotoItem : INotifyPropertyChanged
        {
            private string fileName;
            private string filePath;
            private DateTime? dateTaken;
            private ulong fileSize;
            private int width;
            private int height;
            private bool isLoading;
            private BitmapImage thumbnail;
            private GeoLocation location;
            private bool isSelected;
            public bool IsSelected
            {
                get => isSelected;
                set => SetProperty(ref isSelected, value);
            }

            public string FileName
            {
                get => fileName;
                set => SetProperty(ref fileName, value);
            }

            public string FilePath
            {
                get => filePath;
                set => SetProperty(ref filePath, value);
            }

            public DateTime? DateTaken
            {
                get => dateTaken;
                set => SetProperty(ref dateTaken, value);
            }

            public ulong FileSize
            {
                get => fileSize;
                set => SetProperty(ref fileSize, value);
            }

            public int Width
            {
                get => width;
                set => SetProperty(ref width, value);
            }

            public int Height
            {
                get => height;
                set => SetProperty(ref height, value);
            }

            public bool IsLoading
            {
                get => isLoading;
                set => SetProperty(ref isLoading, value);
            }

            public BitmapImage Thumbnail
            {
                get => thumbnail;
                set => SetProperty(ref thumbnail, value);
            }

            public GeoLocation Location
            {
                get => location;
                set => SetProperty(ref location, value);
            }

            public event PropertyChangedEventHandler PropertyChanged;

            protected void SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
            {
                if (!Equals(storage, value))
                {
                    storage = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                }
            }
        }

        private record PhotoMetadata
        {
            public DateTime? DateTaken { get; init; }
            public ulong FileSize { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
            public GeoLocation Location { get; init; }
        }

        public record GeoLocation
        {
            public double Latitude { get; init; }
            public double Longitude { get; init; }
        }

        private class FolderItem
        {
            public string Name { get; set; }
            public string FolderPath { get; set; }
        }

        private enum PhotoSortOption
        {
            Name,
            Date,
            Size
        }
    }
}