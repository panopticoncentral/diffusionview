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
using DiffusionView.Database;
using System.Threading.Channels;

namespace DiffusionView.Service;

public sealed partial class PhotoService : IDisposable
{
    private readonly PhotoDatabase _db = new();
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private readonly Channel<string> _scanQueue;
    private readonly Task _processingTask;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private bool _disposed;

    public event EventHandler<FolderChangedEventArgs> FolderAdded;
    public event EventHandler<FolderChangedEventArgs> FolderRemoved;
    public event EventHandler<PhotoChangedEventArgs> PhotoAdded;
    public event EventHandler<PhotoChangedEventArgs> PhotoRemoved;

    public PhotoService()
    {
        // Create an unbounded channel for scan requests
        _scanQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // Start the background processing task
        _processingTask = ProcessScanQueueAsync(_cancellationTokenSource.Token);
    }

    /*
     * File watching
     */

    private async Task HandleFileChangeAsync(string path, FileChangeType changeType)
    {
        if (_disposed) return;

        try
        {
            switch (changeType)
            {
                case FileChangeType.Created:
                case FileChangeType.Modified:
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    var parentFolder = _db.Folders.FirstOrDefault(f => path.StartsWith(f.Path));
                    if (parentFolder == null) return;

                    var props = await file.GetBasicPropertiesAsync();
                    var imageProps = await file.Properties.GetImagePropertiesAsync();

                    var photo = await _db.Photos.FirstOrDefaultAsync(p => p.Path == path);
                    var isNew = photo == null;

                    if (isNew)
                    {
                        photo = new Photo
                        {
                            Path = path,
                            Name = file.Name,
                            FolderId = parentFolder.Id
                        };
                        _db.Photos.Add(photo);
                    }

                    photo.DateTaken = imageProps.DateTaken.DateTime;
                    photo.FileSize = props.Size;
                    photo.Width = (int)imageProps.Width;
                    photo.Height = (int)imageProps.Height;
                    photo.LastModified = props.DateModified.DateTime;

                    using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.PicturesView, 200);
                    await using var stream = thumbnail.AsStreamForRead();
                    var bytes = new byte[stream.Length];
                    await stream.ReadExactlyAsync(bytes);
                    photo.ThumbnailData = bytes;

                    await _db.SaveChangesAsync();

                    if (!isNew)
                    {
                        PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(photo));
                    }

                    PhotoAdded?.Invoke(this, new PhotoChangedEventArgs(photo));
                    break;
                }

                case FileChangeType.Deleted:
                    var existingPhoto = await _db.Photos.FirstOrDefaultAsync(p => p.Path == path);
                    if (existingPhoto == null) return;

                    _db.Photos.Remove(existingPhoto);
                    await _db.SaveChangesAsync();

                    PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(existingPhoto));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(changeType), changeType, null);
            }
        }
        catch (Exception)
        {
            // Log or handle the error appropriately
        }
    }

    private void StartWatcher(string path)
    {
        if (_watchers.ContainsKey(path)) return;

        var watcher = new FileSystemWatcher(path)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            Filter = "*.png",
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        watcher.Created += async (s, e) => await HandleFileChangeAsync(e.FullPath, FileChangeType.Created);
        watcher.Changed += async (s, e) => await HandleFileChangeAsync(e.FullPath, FileChangeType.Modified);
        watcher.Deleted += async (s, e) => await HandleFileChangeAsync(e.FullPath, FileChangeType.Deleted);
        watcher.Renamed += async (s, e) =>
        {
            await HandleFileChangeAsync(e.OldFullPath, FileChangeType.Deleted);
            await HandleFileChangeAsync(e.FullPath, FileChangeType.Created);
        };

        _watchers.TryAdd(path, watcher);
    }

    /*
     * Folder scanning
     */

    private async Task ScanFolderAsync(string path)
    {
        if (!Directory.Exists(path)) return;

        var folder = await StorageFolder.GetFolderFromPathAsync(path);
        var queryOptions = new QueryOptions(CommonFileQuery.OrderByName, [".png"]);
        var query = folder.CreateFileQueryWithOptions(queryOptions);

        var files = await query.GetFilesAsync();
        foreach (var file in files)
        {
            await HandleFileChangeAsync(file.Path, FileChangeType.Created);
        }
    }

    private async Task ProcessScanQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var path in _scanQueue.Reader.ReadAllAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    await ScanFolderAsync(path);
                }
                catch (Exception)
                {
                    // Log error but continue processing queue
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation, no action needed
        }
    }

    private void QueueFolderScan(string path)
    {
        _scanQueue.Writer.TryWrite(path);
    }

    /*
     * Initialization
     */

    public void Initialize()
    {
        _db.Database.EnsureCreated();

        var folders = _db.Folders.ToList();
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder.Path))
            {
                _db.Folders.Remove(folder);
                continue;
            }

            FolderAdded?.Invoke(this, new FolderChangedEventArgs(folder.Name, folder.Path));

            StartWatcher(folder.Path);
            QueueFolderScan(folder.Path);
        }

        _db.SaveChanges();
    }

    /*
     * Add folder
     */

    public async Task AddFolderAsync(StorageFolder folder)
    {
        var folderPath = folder.Path;

        // Check if folder already exists in database
        if (_db.Folders.Any(f => f.Path == folderPath))
            return;

        // Add the folder to database
        var newFolder = new Folder
        {
            Name = folder.Name,
            Path = folderPath
        };

        _db.Folders.Add(newFolder);
        await _db.SaveChangesAsync();

        FolderAdded?.Invoke(this, new FolderChangedEventArgs(folder.Name, folderPath));

        // Start watching the folder
        StartWatcher(folderPath);

        // Queue initial scan
        QueueFolderScan(folderPath);
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
        var photos = await _db.Photos
            .Where(p => p.Path.StartsWith(folderPath))
            .ToListAsync();

        return photos
            .Where(p => Path.GetDirectoryName(p.Path) == folderPath)
            .Select(p => new PhotoItem
            {
                FileName = p.Name,
                FilePath = p.Path,
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
        if (_disposed) return;
        _disposed = true;

        // Cancel the processing task
        _cancellationTokenSource.Cancel();
        try
        {
            _processingTask.Wait();
        }
        catch (AggregateException)
        {
            // Expected if task was cancelled
        }
        _cancellationTokenSource.Dispose();

        // Clean up watchers
        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        // Dispose database
        _db.Dispose();
    }
}