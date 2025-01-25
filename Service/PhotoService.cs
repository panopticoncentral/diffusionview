using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Threading;
using System;
using Windows.Storage.Search;
using Windows.Storage.Streams;
using Windows.Storage;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using DiffusionView.Database;
using System.Threading.Channels;
using Windows.Graphics.Imaging;
using MetadataExtractor;
using MetadataExtractor.Formats.Png;
using Directory = System.IO.Directory;

namespace DiffusionView.Service;

public sealed partial class PhotoService : IDisposable
{
    private readonly PhotoDatabase _db = new();
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private readonly Channel<string> _scanQueue;
    private readonly Channel<(string Path, int PhotoId)> _thumbnailQueue;
    private readonly Task _scanProcessingTask;
    private readonly Task _thumbnailProcessingTask;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private bool _disposed;

    public event EventHandler<FolderChangedEventArgs> FolderAdded;
    public event EventHandler<FolderChangedEventArgs> FolderRemoved;
    public event EventHandler<PhotoChangedEventArgs> PhotoAdded;
    public event EventHandler<PhotoChangedEventArgs> PhotoRemoved;
    public event EventHandler<PhotoChangedEventArgs> ThumbnailLoaded;
    public event EventHandler<ModelChangedEventArgs> ModelAdded;
    public event EventHandler<ModelChangedEventArgs> ModelRemoved;

    public PhotoService()
    {
        _scanQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _thumbnailQueue = Channel.CreateUnbounded<(string, int)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _scanProcessingTask = ProcessScanQueueAsync(_cancellationTokenSource.Token);
        _thumbnailProcessingTask = ProcessThumbnailQueueAsync(_cancellationTokenSource.Token);
    }

    /*
     * File watching
     */
    
    public static async Task<byte[]> LoadScaledImageAsync(StorageFile sourceFile, uint targetHeight)
    {
        using var sourceStream = await sourceFile.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(sourceStream);

        var aspectRatio = (double)decoder.PixelWidth / decoder.PixelHeight;
        var newWidth = (uint)(targetHeight * aspectRatio);

        using var resizedStream = new InMemoryRandomAccessStream();

        var encoder = await BitmapEncoder.CreateForTranscodingAsync(resizedStream, decoder);

        encoder.BitmapTransform.ScaledHeight = targetHeight;
        encoder.BitmapTransform.ScaledWidth = newWidth;

        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;

        await encoder.FlushAsync();

        var bytes = new byte[resizedStream.Size];
        await resizedStream.ReadAsync(bytes.AsBuffer(), (uint)resizedStream.Size, InputStreamOptions.None);

        return bytes;
    }

    public static void ExtractMetadata(Stream stream, Photo photo)
    {
        var directories = ImageMetadataReader.ReadMetadata(stream);

        var textDirectories = directories.OfType<PngDirectory>().Where(d => d.Name == "PNG-tEXt").ToList();
        if (textDirectories.Count != 1) throw new FormatException();

        var textChunks = textDirectories.First().Tags
            .Where(t => t.Type == PngDirectory.TagTextualData)
            .Select(t => t.Description)
            .ToList();

        if (textChunks.Count != 1) throw new FormatException();

        var raw = textChunks.First();
        photo.Raw = raw;

        var lines = raw.Split(['\n'], StringSplitOptions.RemoveEmptyEntries);
        var currentLine = 0;

        var promptKeyValue = lines[currentLine].Split(':', 2);
        if (promptKeyValue.Length != 2) throw new FormatException();
        if (!promptKeyValue[0].Trim().ToLowerInvariant().Equals("parameters")) throw new FormatException();

        var prompt = promptKeyValue[1].Trim();
        currentLine++;

        while (!lines[currentLine].StartsWith("negative prompt", StringComparison.InvariantCultureIgnoreCase) &&
               !lines[currentLine].StartsWith("steps", StringComparison.InvariantCultureIgnoreCase))
        {
            prompt += $" {lines[currentLine].Trim()}";
            currentLine++;
        }
        
        photo.Prompt = prompt;

        if (lines[currentLine].StartsWith("negative prompt", StringComparison.InvariantCultureIgnoreCase))
        {
            var negativePromptKeyValue = lines[currentLine].Split(':', 2);
            if (negativePromptKeyValue.Length != 2) throw new FormatException();
            if (negativePromptKeyValue[1].Trim().ToLowerInvariant().Equals("negative prompt")) throw new FormatException();

            var negativePrompt = negativePromptKeyValue[1].Trim();
            currentLine++;

            while (!lines[currentLine].StartsWith("steps", StringComparison.InvariantCultureIgnoreCase))
            {
                prompt += $" {lines[currentLine].Trim()}";
                currentLine++;
            }

            photo.NegativePrompt = negativePrompt;
        }

        var otherParameters = new LineParser(lines[currentLine]);

        while (otherParameters.MorePairs)
        {
            var (key, value) = otherParameters.GetNextKeyValuePair();

            switch (key)
            {
                case "steps":
                    if (!int.TryParse(value, out var steps)) throw new FormatException();
                    photo.Steps = steps;
                    break;
                case "sampler":
                    photo.Sampler = value;
                    break;
                case "cfg scale":
                    if (!double.TryParse(value, out var cfgScale)) throw new FormatException();
                    photo.CfgScale = cfgScale;
                    break;
                case "seed":
                    if (!long.TryParse(value, NumberStyles.HexNumber, null, out var seed)) throw new FormatException();
                    photo.Seed = seed;
                    break;
                case "size":
                {
                    var size = value.Split('x', 2);
                    if (size.Length != 2) throw new FormatException();
                    if (!int.TryParse(size[0], out var width)) throw new FormatException();
                    photo.GeneratedWidth = width;
                    if (!int.TryParse(size[1], out var height)) throw new FormatException();
                    photo.GeneratedHeight = height;
                    break;
                }
                case "model hash":
                {
                    if (!long.TryParse(value, NumberStyles.HexNumber, null, out var modelHash)) throw new FormatException();
                    photo.ModelHash = modelHash;
                    break;

                }
                case "model":
                    photo.Model = value;
                    break;
                case "version":
                    photo.Version = value;
                    break;
                default:
                    photo.OtherParameters[key] = value;
                    break;
            }
        }

        currentLine++;
        if (currentLine != lines.Length) throw new FormatException();
    }

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

                    var stream = await file.OpenStreamForReadAsync();
                    ExtractMetadata(stream, photo);
                        
                    await _db.SaveChangesAsync();

                    if (!isNew)
                    {
                        PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(photo));
                    }

                    PhotoAdded?.Invoke(this, new PhotoChangedEventArgs(photo));
                    _thumbnailQueue.Writer.TryWrite((path, photo.Id));
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
            var photo = await _db.Photos.FirstOrDefaultAsync(p => p.Path == file.Path);
            if (photo == null)
            {
                await HandleFileChangeAsync(file.Path, FileChangeType.Created);
            }
            else if (photo.ThumbnailData == null)
            {
                _thumbnailQueue.Writer.TryWrite((file.Path, photo.Id));
            }
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

    private async Task LoadThumbnailAsync(string path, int photoId)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var photo = await _db.Photos.FindAsync(photoId);

            if (photo == null) return;

            var thumbnail = await LoadScaledImageAsync(file, 300);
            photo.ThumbnailData = thumbnail;
            await _db.SaveChangesAsync();

            ThumbnailLoaded?.Invoke(this, new PhotoChangedEventArgs(photo));
        }
        catch (Exception)
        {
            // Log or handle the error appropriately
        }
    }

    private async Task ProcessThumbnailQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (path, photoId) in _thumbnailQueue.Reader.ReadAllAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    await LoadThumbnailAsync(path, photoId);
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
        }

        var models = _db.Photos
            .Select(p => p.Model ?? "<unknown>")
            .Distinct()
            .OrderBy(m => m)
            .ToList();

        foreach (var model in models)
        {
            ModelAdded?.Invoke(this, new ModelChangedEventArgs(model));
        }

        foreach (var folder in folders)
        {
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

        StartWatcher(folderPath);
        QueueFolderScan(folderPath);
    }

    /*
     * Get photos for folder
     */

    public async Task<List<Photo>> GetPhotosForFolderAsync(string folderPath)
    {
        var photos = await _db.Photos
            .Where(p => p.Path.StartsWith(folderPath))
            .ToListAsync();

        return photos
            .Where(p => Path.GetDirectoryName(p.Path) == folderPath)
            .ToList();
    }

    public async Task<List<Photo>> GetPhotosByModelAsync(string modelName)
    {
        return await _db.Photos
            .Where(p => p.Model == modelName)
            .ToListAsync();
    }

    /*
     * Dispose
     */

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cancellationTokenSource.Cancel();
        try
        {
            Task.WaitAll(_scanProcessingTask, _thumbnailProcessingTask);
        }
        catch (AggregateException)
        {
            // Expected if task was cancelled
        }
        _cancellationTokenSource.Dispose();

        foreach (var watcher in _watchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        _db.Dispose();
    }

    private sealed class LineParser(string line)
    {
        private int _index = 0;

        public bool MorePairs => _index < line.Length;

        public KeyValuePair GetNextKeyValuePair()
        {
            var key = new StringBuilder();
            while (line[_index] != ':')
            {
                key.Append(line[_index++]);
            }

            _index++;

            var value = new StringBuilder();
            while (_index < line.Length && line[_index] != ',')
            {
                if (line[_index] == '"')
                {
                    value.Append(line[_index++]);
                    while (line[_index] != '"')
                    {
                        value.Append(line[_index++]);
                    }
                }

                value.Append(line[_index++]);
            }

            if (_index < line.Length)
            {
                _index++;
            }

            return new KeyValuePair(key.ToString().Trim().ToLowerInvariant(), value.ToString().Trim());
        }
    }
}