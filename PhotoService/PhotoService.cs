using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Threading;
using System;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;
using Windows.Storage.Streams;
using Windows.Storage;
using System.Collections.Concurrent;

namespace DiffusionView.PhotoService;

public sealed partial class PhotoService : IDisposable
{
    private readonly PhotoDatabase _db;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private readonly ConcurrentQueue<(int FolderId, StorageFolder Folder)> _scanQueue = new();
    private readonly CancellationTokenSource _queueCts = new();
    private Task _scanTask;
    private volatile bool _isPaused;

    private readonly Dictionary<string, CancellationTokenSource> _scanCancellations = new();

    public event EventHandler<PhotoChangedEventArgs> PhotoAdded;
    public event EventHandler<PhotoChangedEventArgs> PhotoRemoved;
    public event EventHandler<PhotoChangedEventArgs> PhotoUpdated;
    public event EventHandler<FolderCleanupEventArgs> FolderRemoved;
    public event EventHandler<SyncProgressEventArgs> SyncProgress;
    public event EventHandler<SyncProgressEventArgs> ScanProgress;

    public PhotoService()
    {
        _db = new PhotoDatabase();
        _db.Database.EnsureCreated();
        StartQueueProcessor();
    }

    /*
     * Folder scanning
     */

    private void StartQueueProcessor()
    {
        _scanTask = Task.Run(async () =>
        {
            while (!_queueCts.Token.IsCancellationRequested)
            {
                try
                {
                    // Wait if paused or no items
                    while (_isPaused || !_scanQueue.TryPeek(out _))
                    {
                        await Task.Delay(100, _queueCts.Token);
                    }

                    // Process next item if available
                    if (_scanQueue.TryDequeue(out var item))
                    {
                        await ScanFolderInBackgroundAsync(item.FolderId, item.Folder, _queueCts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Log error but continue processing queue
                    await Task.Delay(1000, _queueCts.Token); // Backoff on error
                }
            }
        }, _queueCts.Token);
    }

    private async Task ScanFolderInBackgroundAsync(int folderId, StorageFolder folder, CancellationToken cancellationToken)
    {
        var queryOptions = new QueryOptions
        {
            FolderDepth = FolderDepth.Deep,
            IndexerOption = IndexerOption.UseIndexerWhenAvailable
        };

        queryOptions.SetPropertyPrefetch(
            PropertyPrefetchOptions.BasicProperties | PropertyPrefetchOptions.ImageProperties,
            ["System.GPS.Latitude", "System.GPS.Longitude"]);

        queryOptions.FileTypeFilter.Add(".png");
        queryOptions.FileTypeFilter.Add(".jpg");
        queryOptions.FileTypeFilter.Add(".jpeg");

        var query = folder.CreateFileQueryWithOptions(queryOptions);
        var files = await query.GetFilesAsync();
        var totalFiles = files.Count;
        var processedFiles = 0;

        foreach (var file in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await AddOrUpdatePhotoAsync(folderId, file);
            processedFiles++;

            ScanProgress?.Invoke(this, new SyncProgressEventArgs(
                processedFiles,
                totalFiles,
                folder.Path,
                $"Scanning photos ({processedFiles}/{totalFiles})"
            ));
        }
    }

    /*
     * Database handling
     */

    private async Task AddOrUpdatePhotoAsync(int folderId, StorageFile file, StoredPhoto existingPhoto = null)
    {
        var properties = await file.GetBasicPropertiesAsync();
        var imageProperties = await file.Properties.GetImagePropertiesAsync();

        var photo = existingPhoto ?? new StoredPhoto
        {
            FilePath = file.Path,
            FileName = file.Name,
            FolderId = folderId
        };

        var isNew = existingPhoto == null;

        photo.DateTaken = imageProperties.DateTaken.LocalDateTime;
        photo.FileSize = properties.Size;
        photo.Width = (int)imageProperties.Width;
        photo.Height = (int)imageProperties.Height;
        photo.LastModified = properties.DateModified.LocalDateTime;

        if (photo.ThumbnailData == null)
        {
            using var thumbnailStream = await file.GetThumbnailAsync(
                ThumbnailMode.PicturesView, 200, ThumbnailOptions.UseCurrentScale);
            using var memStream = new MemoryStream();
            await thumbnailStream.AsStreamForRead().CopyToAsync(memStream);
            photo.ThumbnailData = memStream.ToArray();
        }

        if (isNew)
        {
            _db.Photos.Add(photo);
            await _db.SaveChangesAsync();
            PhotoAdded?.Invoke(this, new PhotoChangedEventArgs(photo));
        }
        else
        {
            await _db.SaveChangesAsync();
            PhotoUpdated?.Invoke(this, new PhotoChangedEventArgs(photo));
        }
    }

    /*
     * File watching
     */

    private static bool IsImageFile(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".png";
    }

    private async Task HandleFileChangeAsync(int folderId, string filePath, FileChangeType changeType)
    {
        if (!IsImageFile(filePath))
        {
            return;
        }

        await _syncLock.WaitAsync();
        try
        {
            switch (changeType)
            {
                case FileChangeType.Created:
                case FileChangeType.Modified:
                    var file = await StorageFile.GetFileFromPathAsync(filePath);
                    await AddOrUpdatePhotoAsync(folderId, file);
                    break;

                case FileChangeType.Deleted:
                    var photo = await _db.Photos.FirstOrDefaultAsync(p => p.FilePath == filePath);
                    if (photo != null)
                    {
                        _db.Photos.Remove(photo);
                        await _db.SaveChangesAsync();
                        PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(photo));
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(changeType), changeType, null);
            }
        }
        catch (Exception)
        {
            // Handle file access errors
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private void StartWatching(StoredFolder folder)
    {
        if (_watchers.ContainsKey(folder.Path))
        {
            return;
        }

        var watcher = new FileSystemWatcher(folder.Path)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };

        watcher.Created += async (s, e) => await HandleFileChangeAsync(folder.Id, e.FullPath, FileChangeType.Created);
        watcher.Deleted += async (s, e) => await HandleFileChangeAsync(folder.Id, e.FullPath, FileChangeType.Deleted);
        watcher.Changed += async (s, e) => await HandleFileChangeAsync(folder.Id, e.FullPath, FileChangeType.Modified);

        _watchers[folder.Path] = watcher;
    }

    /*
     * Initialization
     */
    
    public async Task CleanupDatabaseAsync()
    {
        var folders = await _db.Folders.ToListAsync();
        var foldersToRemove = new List<StoredFolder>();

        foreach (var folder in folders)
        {
            try
            {
                await StorageFolder.GetFolderFromPathAsync(folder.Path);
            }
            catch (Exception)
            {
                foldersToRemove.Add(folder);
                FolderRemoved?.Invoke(this, new FolderCleanupEventArgs(folder.Path, "Folder is no longer accessible"));
            }
        }

        foreach (var folder in foldersToRemove)
        {
            var photos = await _db.Photos.Where(p => p.FolderId == folder.Id).ToListAsync();
            _db.Photos.RemoveRange(photos);
            _db.Folders.Remove(folder);

            if (!_watchers.TryGetValue(folder.Path, out var watcher))
            {
                continue;
            }

            watcher.Dispose();
            _watchers.Remove(folder.Path);
        }

        var remainingPhotos = await _db.Photos.ToListAsync();
        var photosToRemove = new List<StoredPhoto>();

        foreach (var photo in remainingPhotos)
        {
            try
            {
                await StorageFile.GetFileFromPathAsync(photo.FilePath);
            }
            catch (Exception)
            {
                photosToRemove.Add(photo);
                PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(photo));
            }
        }

        _db.Photos.RemoveRange(photosToRemove);

        await _db.SaveChangesAsync();
    }

    private async Task SyncFolderContentsAsync(int folderId, StorageFolder folder)
    {
        var queryOptions = new QueryOptions
        {
            FolderDepth = FolderDepth.Deep,
            IndexerOption = IndexerOption.UseIndexerWhenAvailable
        };

        queryOptions.SetPropertyPrefetch(
            PropertyPrefetchOptions.BasicProperties | PropertyPrefetchOptions.ImageProperties,
            ["System.GPS.Latitude", "System.GPS.Longitude"]);

        queryOptions.FileTypeFilter.Add(".png");
        queryOptions.FileTypeFilter.Add(".jpg");
        queryOptions.FileTypeFilter.Add(".jpeg");

        var query = folder.CreateFileQueryWithOptions(queryOptions);
        var files = await query.GetFilesAsync();

        var existingPhotos = await _db.Photos
            .Where(p => p.FolderId == folderId)
            .ToDictionaryAsync(p => p.FilePath);

        var processedFiles = new HashSet<string>();

        foreach (var file in files)
        {
            processedFiles.Add(file.Path);

            var properties = await file.GetBasicPropertiesAsync();
            var lastModified = properties.DateModified.LocalDateTime;

            if (existingPhotos.TryGetValue(file.Path, out var existingPhoto))
            {
                if (existingPhoto.LastModified != lastModified)
                {
                    await AddOrUpdatePhotoAsync(folderId, file, existingPhoto);
                }
            }
            else
            {
                await AddOrUpdatePhotoAsync(folderId, file);
            }
        }

        var deletedPhotos = existingPhotos.Values
            .Where(p => !processedFiles.Contains(p.FilePath))
            .ToList();

        foreach (var deletedPhoto in deletedPhotos)
        {
            _db.Photos.Remove(deletedPhoto);
            PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(deletedPhoto));
        }

        if (deletedPhotos.Count != 0)
        {
            await _db.SaveChangesAsync();
        }
    }

    public async Task InitializeAsync()
    {
        await _syncLock.WaitAsync();
        try
        {
            // Pause queue processing during initialization
            _isPaused = true;

            await CleanupDatabaseAsync();

            var folders = await _db.Folders.ToListAsync();
            var totalFolders = folders.Count;
            var processedFolders = 0;

            foreach (var folder in folders)
            {
                try
                {
                    var storageFolder = await StorageFolder.GetFolderFromPathAsync(folder.Path);
                    await SyncFolderContentsAsync(folder.Id, storageFolder);
                    StartWatching(folder);

                    // Queue folder for full scan
                    _scanQueue.Enqueue((folder.Id, storageFolder));

                    processedFolders++;
                    SyncProgress?.Invoke(this, new SyncProgressEventArgs(
                        processedFolders,
                        totalFolders,
                        folder.Path,
                        "Synchronizing folder contents"
                    ));
                }
                catch (Exception ex)
                {
                    FolderRemoved?.Invoke(this, new FolderCleanupEventArgs(folder.Path, ex.Message));
                    _db.Folders.Remove(folder);
                }
            }

            await _db.SaveChangesAsync();
        }
        finally
        {
            _syncLock.Release();
            // Resume queue processing
            _isPaused = false;
        }
    }

    /*
     * Add folder
     */

    public async Task AddFolderAsync(StorageFolder folder)
    {
        // Pause queue processing while adding new folder
        _isPaused = true;

        try
        {
            await _syncLock.WaitAsync();
            try
            {
                var storedFolder = new StoredFolder
                {
                    Name = folder.Name,
                    Path = folder.Path,
                    LastScanned = DateTime.Now
                };

                _db.Folders.Add(storedFolder);
                await _db.SaveChangesAsync();
                StartWatching(storedFolder);

                // Queue the folder for scanning
                _scanQueue.Enqueue((storedFolder.Id, folder));
            }
            finally
            {
                _syncLock.Release();
            }
        }
        finally
        {
            // Resume queue processing
            _isPaused = false;
        }
    }

    /*
     * Get photos for folder
     */

    private static BitmapImage CreateBitmapImage(byte[] data)
    {
        if (data == null)
        {
            return null;
        }

        var image = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        stream.WriteAsync(data.AsBuffer()).GetResults();
        stream.Seek(0);
        image.SetSource(stream);
        return image;
    }

    public async Task<List<PhotoItem>> GetPhotosForFolderAsync(string folderPath)
    {
        var folder = await _db.Folders.FirstOrDefaultAsync(f => f.Path == folderPath);
        if (folder == null)
        {
            return [];
        }

        var photos = await _db.Photos
            .Where(p => p.FolderId == folder.Id)
            .OrderByDescending(p => p.DateTaken)
            .ToListAsync();

        return photos.Select(p => new PhotoItem
        {
            FileName = p.FileName,
            FilePath = p.FilePath,
            DateTaken = p.DateTaken,
            FileSize = p.FileSize,
            Width = p.Width,
            Height = p.Height,
            Thumbnail = CreateBitmapImage(p.ThumbnailData)
        }).ToList();
    }

    /*
     * Dispose
     */

    public void Dispose()
    {
        _queueCts.Cancel();
        try
        {
            _scanTask?.Wait(1000); // Give queue processor time to shut down
        }
        catch (Exception)
        {
            // Ignore exceptions during shutdown
        }

        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }
        _watchers.Clear();

        _db.Dispose();
        _syncLock.Dispose();
        _queueCts.Dispose();
    }
}