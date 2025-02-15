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
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DiffusionView.Database;
using System.Threading.Channels;
using Windows.Graphics.Imaging;
using MetadataExtractor;
using MetadataExtractor.Formats.Png;
using Directory = System.IO.Directory;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;

namespace DiffusionView.Service;

public sealed partial class PhotoService : IDisposable
{
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();

    private readonly Channel<string> _scanQueue;
    private readonly Task _scanProcessingTask;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private bool _disposed;

    public event EventHandler<FolderChangedEventArgs> FolderAdded;
    public event EventHandler<FolderChangedEventArgs> FolderRemoved;
    public event EventHandler<PhotoChangedEventArgs> PhotoAdded;
    public event EventHandler<PhotoChangedEventArgs> PhotoRemoved;
    public event EventHandler<ModelChangedEventArgs> ModelAdded;
    public event EventHandler<ModelChangedEventArgs> ModelRemoved;
    public event EventHandler<ScanProgressEventArgs> ScanProgress;

    public PhotoService()
    {
        _scanQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _scanProcessingTask = Task.Run(async () =>
        {
            await ProcessScanQueueAsync(_cancellationTokenSource.Token);
        });
    }

    /*
     * File watching
     */

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
            if (negativePromptKeyValue[1].Trim().ToLowerInvariant().Equals("negative prompt"))
                throw new FormatException();

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
                    if (!long.TryParse(value, NumberStyles.HexNumber, null, out var modelHash))
                        throw new FormatException();
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

    public static async Task<(string Name, string Version)?> FetchModelInformationAsync(long modelHash)
    {
        var url = $"https://civitai.com/api/v1/model-versions/by-hash/{modelHash:X10}";
        var client = new HttpClient();
        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;

        try
        {
            var json = await response.Content.ReadAsStringAsync();
            var document = JsonDocument.Parse(json);

            var version = document.RootElement.GetProperty("name").GetString();
            var name = document.RootElement.GetProperty("model").GetProperty("name").GetString();

            return (name, version);
        }
        catch (Exception)
        {
            return null;
        }
    }

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

    private async Task HandleFileChangeAsync(string path, FileChangeType changeType)
    {
        if (_disposed) return;

        try
        {
            await using var db = new PhotoDatabase();

            switch (changeType)
            {
                case FileChangeType.Created:
                case FileChangeType.Modified:
                {
                    if (Directory.Exists(path)) return;

                    var parentFolder = db.Folders.FirstOrDefault(f => path.StartsWith(f.Path));
                    if (parentFolder == null) return;

                    var file = await StorageFile.GetFileFromPathAsync(path);
                    var props = await file.GetBasicPropertiesAsync();
                    var imageProps = await file.Properties.GetImagePropertiesAsync();

                    var photo = await db.Photos.FirstOrDefaultAsync(p => p.Path == path);
                    var isNew = photo == null;

                    if (isNew)
                    {
                        photo = new Photo
                        {
                            Path = path,
                            Name = file.Name,
                            FolderId = parentFolder.Id
                        };
                        db.Photos.Add(photo);
                    }

                    photo.DateTaken = imageProps.DateTaken.DateTime;
                    photo.FileSize = props.Size;
                    photo.Width = (int)imageProps.Width;
                    photo.Height = (int)imageProps.Height;
                    photo.LastModified = props.DateModified.DateTime;

                    var thumbnail = await LoadScaledImageAsync(file, 400);
                    photo.ThumbnailData = thumbnail;

                    var stream = await file.OpenStreamForReadAsync();
                    ExtractMetadata(stream, photo);

                    if (!isNew)
                    {
                        PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(photo));
                    }

                    PhotoAdded?.Invoke(this, new PhotoChangedEventArgs(photo));

                    var model = await db.Models.FirstOrDefaultAsync(m => m.Id == photo.ModelHash);
                    if (model == null)
                    {
                        var modelInfo = await FetchModelInformationAsync(photo.ModelHash);

                        model = new Model
                        {
                            Id = photo.ModelHash,
                            Name = modelInfo?.Name ?? photo.Model,
                            Version = modelInfo?.Version ?? photo.ModelHash.ToString("X10")
                        };
                        db.Models.Add(model);
                        ModelAdded?.Invoke(this, new ModelChangedEventArgs(model.Id, model.Name, model.Version));
                    }
                    break;
                }

                case FileChangeType.Deleted:
                    if (Directory.Exists(path))
                    {
                        var photos = await db.Photos
                            .Where(p => p.Path.StartsWith(path))
                            .ToListAsync();

                        foreach (var photo in photos)
                        {
                            db.Photos.Remove(photo);
                            PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(photo));
                        }

                        // If this is a root folder, remove it from the database
                        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Path == path);
                        if (folder == null) return;

                        db.Folders.Remove(folder);
                        FolderRemoved?.Invoke(this, new FolderChangedEventArgs(folder.Path));
                    }
                    else
                    {
                        var existingPhoto = await db.Photos.FirstOrDefaultAsync(p => p.Path == path);
                        if (existingPhoto == null) return;

                        db.Photos.Remove(existingPhoto);

                        PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(existingPhoto));
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(changeType), changeType, null);
            }

            await db.SaveChangesAsync();

            var models = db.Models.ToList();
            foreach (var model in models.Where(model => !db.Photos.Any(p => p.ModelHash == model.Id)))
            {
                db.Models.Remove(model);
                ModelRemoved?.Invoke(this, new ModelChangedEventArgs(model.Id, model.Name, model.Version));
            }

            await db.SaveChangesAsync();
        }
        catch (Exception)
        {
            // Ignore error for now
        }
    }

    private void StartWatcher(string path)
    {
        if (_watchers.ContainsKey(path)) return;

        var watcher = new FileSystemWatcher(path)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
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

    private async Task ProcessScanQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var path in _scanQueue.Reader.ReadAllAsync(cancellationToken))
            {
                if (!Directory.Exists(path)) return;

                var folder = await StorageFolder.GetFolderFromPathAsync(path);
                var queryOptions = new QueryOptions(CommonFileQuery.OrderByName, [".png"]);
                var query = folder.CreateFileQueryWithOptions(queryOptions);

                var files = (await query.GetFilesAsync()).Select(f => f.Path).ToList();
                var processedFiles = 0;

                foreach (var file in files.TakeWhile(file => !cancellationToken.IsCancellationRequested))
                {
                    try
                    {
                        Photo photo;
                        await using (var db = new PhotoDatabase())
                        {
                            photo = await db.Photos.FirstOrDefaultAsync(p => p.Path == file, cancellationToken);
                        }

                        if (photo == null)
                        {
                            await HandleFileChangeAsync(file, FileChangeType.Created);
                        }

                        processedFiles++;
                        ScanProgress?.Invoke(this, new ScanProgressEventArgs(path, processedFiles, files.Count));
                    }
                    catch (Exception)
                    {
                        // Log error but continue processing queue
                    }
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

    private async Task FireFolderEventAsync(StorageFolder folder)
    {
        FolderAdded?.Invoke(this, new FolderChangedEventArgs(folder.Path));
        var subFolders = await folder.GetFoldersAsync();
        foreach (var subFolder in subFolders)
        {
            await FireFolderEventAsync(subFolder);
        }
    }

    public async Task InitializeAsync()
    {
        List<Folder> folders;
        await using (var db = new PhotoDatabase())
        {
            await db.Database.EnsureCreatedAsync();

            folders = await db.Folders.ToListAsync();
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder.Path))
                {
                    db.Folders.Remove(folder);
                    continue;
                }

                await FireFolderEventAsync(await StorageFolder.GetFolderFromPathAsync(folder.Path));
            }

            var models = await db.Models.ToListAsync();

            foreach (var model in models)
            {
                if (!await db.Photos.AnyAsync(p => p.ModelHash == model.Id))
                {
                    db.Models.Remove(model);
                }

                ModelAdded?.Invoke(this, new ModelChangedEventArgs(model.Id, model.Name, model.Version));
            }

            await db.SaveChangesAsync();
        }

        foreach (var folder in folders)
        {
            StartWatcher(folder.Path);
            QueueFolderScan(folder.Path);
        }
    }

    /*
     * Add folder
     */

    public async Task AddFolderAsync(StorageFolder folder)
    {
        var folderPath = folder.Path;

        await using (var db = new PhotoDatabase())
        {
            if (db.Folders.Any(f => f.Path == folderPath)) return;

            var newFolder = new Folder
            {
                Path = folderPath
            };

            db.Folders.Add(newFolder);
            await db.SaveChangesAsync();
        }

        FolderAdded?.Invoke(this, new FolderChangedEventArgs(folderPath));

        StartWatcher(folderPath);
        QueueFolderScan(folderPath);
    }

    /*
     * Get photos for folder
     */

    public static async Task<List<Photo>> GetPhotosForFolderAsync(string folderPath)
    {
        var db = new PhotoDatabase();
        var photos = await db.Photos
            .Where(p => p.Path.StartsWith(folderPath))
            .AsNoTracking()
            .ToListAsync();

        return photos
            .Where(p => Path.GetDirectoryName(p.Path) == folderPath)
            .ToList();
    }

    public static async Task<List<Photo>> GetPhotosByModelAsync(long modelHash)
    {
        var db = new PhotoDatabase();
        return await db.Photos
            .Where(p => p.ModelHash == modelHash)
            .AsNoTracking()
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
            Task.WaitAll(_scanProcessingTask);
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
    }

    private sealed class LineParser(string line)
    {
        private int _index;

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
                if (line[_index] == '"' && line.IndexOf('"', _index + 1) != -1)
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