using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Threading;
using System;
using Windows.Storage.Search;
using Windows.Storage;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DiffusionView.Database;
using System.Threading.Channels;
using MetadataExtractor;
using MetadataExtractor.Formats.Png;
using Directory = System.IO.Directory;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Util;

namespace DiffusionView.Service;

public sealed partial class PhotoService : IDisposable
{
    private readonly ConcurrentDictionary<string, RootWatcher> _watchers = new();

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

    private static async Task ExtractPngMetadata(Stream stream, Photo photo)
    {
        var directories = ImageMetadataReader.ReadMetadata(stream);

        var textDirectories = directories.OfType<PngDirectory>().Where(d => d.Name == "PNG-tEXt").ToList();
        if (textDirectories.Count != 1)
        {
            photo.Raw = "Expected exactly one PNG-tEXt directory";
            return;
        }

        var textChunks = textDirectories.First().Tags
            .Where(t => t.Type == PngDirectory.TagTextualData)
            .Select(t => t.Description)
            .ToList();

        if (textChunks.Count != 1) throw new FormatException("Expected exactly one textual data chunk");

        var raw = textChunks.First();
        await ParseStableDiffusionMetadata(raw, photo);
    }

    private static async Task ExtractJpegMetadata(Stream stream, Photo photo)
    {
        var directories = ImageMetadataReader.ReadMetadata(stream);

        var exifSubIfdDirectory = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        if (exifSubIfdDirectory == null)
        {
            photo.Raw = "No Exif SubIFD directory found";
            return;
        }

        var textChunks = exifSubIfdDirectory.Tags
            .Where(t => t.Type == ExifDirectoryBase.TagUserComment)
            .Select(t => t.Description)
            .ToList();

        if (textChunks.Count == 0)
        {
            photo.Raw = "No UserComment tag in Exif SubIFD";
            return;
        }

        if (textChunks.Count != 1) throw new FormatException("Expected exactly one textual data chunk");

        var userComment = CleanUserComment(textChunks.First());

        if (string.IsNullOrWhiteSpace(userComment)) throw new FormatException("Empty UserComment tag in Exif SubIFD");

        await ParseStableDiffusionMetadata(userComment, photo);
    }

    private static string CleanUserComment(string comment)
    {
        if (comment.StartsWith("ASCII\0", StringComparison.OrdinalIgnoreCase))
            return comment[6..];
        if (comment.StartsWith("UNICODE\0", StringComparison.OrdinalIgnoreCase))
            return comment[8..];

        return comment;
    }

    private static async Task ParseStableDiffusionMetadata(string raw, Photo photo)
    {
        photo.Raw = raw;

        var lines = raw.Split(['\n'], StringSplitOptions.RemoveEmptyEntries);
        var currentLine = 0;

        while (currentLine < lines.Length && string.IsNullOrWhiteSpace(lines[currentLine]))
        {
            currentLine++;
        }

        if (currentLine >= lines.Length) throw new FormatException("No content in metadata");

        var prompt = lines[currentLine];
        if (prompt.StartsWith("parameters:", StringComparison.InvariantCultureIgnoreCase))
        {
            prompt = prompt[11..];
        }

        currentLine++;

        while (currentLine < lines.Length &&
               !lines[currentLine].StartsWith("negative prompt", StringComparison.InvariantCultureIgnoreCase) &&
               !lines[currentLine].StartsWith("steps", StringComparison.InvariantCultureIgnoreCase))
        {
            prompt += $" {lines[currentLine].Trim()}";
            currentLine++;
        }

        photo.Prompt = prompt.Trim();

        if (currentLine < lines.Length &&
            lines[currentLine].StartsWith("negative prompt", StringComparison.InvariantCultureIgnoreCase))
        {
            var negativePromptKeyValue = lines[currentLine].Split(':', 2);
            if (negativePromptKeyValue.Length != 2) throw new FormatException("Invalid negative prompt format");

            var negativePrompt = negativePromptKeyValue[1].Trim();
            currentLine++;

            while (currentLine < lines.Length &&
                   !lines[currentLine].StartsWith("steps", StringComparison.InvariantCultureIgnoreCase))
            {
                negativePrompt += $" {lines[currentLine].Trim()}";
                currentLine++;
            }

            photo.NegativePrompt = negativePrompt.Trim();
        }

        if (currentLine >= lines.Length)
            return; // No parameter data found, but we have prompts at least

        var otherParameters = new LineParser(lines[currentLine].Trim());

        while (otherParameters.MorePairs)
        {
            var (key, value) = otherParameters.GetNextKeyValuePair();

            switch (key)
            {
                case "civitai metadata":
                    ProcessCivitaiMetadata(value, photo);
                    break;

                case "civitai resources":
                    photo.ModelVersionId = ProcessCivitaiResources(value);
                    break;

                case "clip skip":
                    if (!int.TryParse(value, out var clipSkip)) throw new FormatException("Invalid clip skip value");
                    photo.ClipSkip = clipSkip;
                    break;

                case "cfg scale":
                    if (!double.TryParse(value, out var cfgScale)) throw new FormatException("Invalid cfg scale value");
                    photo.CfgScale = cfgScale;
                    break;

                case "created date":
                    // Ignore.
                    break;

                case "denoising strength":
                    if (!double.TryParse(value, out var denoisingStrength))
                        throw new FormatException("Invalid denoising strength value");
                    photo.DenoisingStrength = denoisingStrength;
                    break;

                case "hires upscale":
                    if (!double.TryParse(value, out var hiresUpscale))
                        throw new FormatException("Invalid hires upscale value");
                    photo.HiresUpscale = hiresUpscale;
                    break;

                case "hires upscaler":
                    photo.HiresUpscaler = value;
                    break;

                case "model":
                    // Ignore.
                    break;

                case "model hash":
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    if (!long.TryParse(value, NumberStyles.HexNumber, null, out var modelHash))
                        throw new FormatException("Invalid hex model hash");

                    photo.ModelVersionId = await FetchModelVersionIdAsync(modelHash) ?? 0;
                    break;

                case "sampler":
                    photo.Sampler = value;
                    break;

                case "schedule type":
                    photo.ScheduleType = value;
                    break;

                case "seed":
                    if (!long.TryParse(value, out var decimalSeed))
                        throw new FormatException("Invalid decimal seed value");
                    photo.Seed = decimalSeed;
                    break;

                case "size":
                    var size = value.Split('x', 2);
                    if (size.Length != 2) throw new FormatException("Invalid size format");

                    if (!int.TryParse(size[0], out var width)) throw new FormatException("Invalid width value");
                    photo.GeneratedWidth = width;

                    if (!int.TryParse(size[1], out var height)) throw new FormatException("Invalid height value");
                    photo.GeneratedHeight = height;
                    break;

                case "steps":
                    if (!int.TryParse(value, out var steps)) throw new FormatException("Invalid steps value");
                    photo.Steps = steps;
                    break;

                case "vae":
                    photo.Vae = value;
                    break;

                case "vae hash":
                    // Ignore
                    break;

                case "variation seed":
                    if (!long.TryParse(value, out var variationSeed))
                        throw new FormatException("Invalid variation seed value");
                    photo.VariationSeed = variationSeed;
                    break;

                case "variation seed strength":
                    if (!double.TryParse(value, out var variationSeedStrength))
                        throw new FormatException("Invalid variation seed strength value");
                    photo.VariationSeedStrength = variationSeedStrength;
                    break;

                case "version":
                    photo.Version = value;
                    break;

                default:
                    photo.OtherParameters[key] = value;
                    break;
            }
        }
    }

    private static long ProcessCivitaiResources(string value)
    {
        var elements = JsonSerializer.Deserialize<List<JsonElement>>(value);

        foreach (var element in elements)
        {
            var type = element.GetProperty("type").GetString();

            if (type == "checkpoint")
            {
                return element.GetProperty("modelVersionId").GetInt64();
            }
        }

        return 0;
    }

    private static void ProcessCivitaiMetadata(string value, Photo photo)
    {
        var metadata = JsonSerializer.Deserialize<JsonElement>(value);

        // Loop through the properties and handle each one
        foreach (var property in metadata.EnumerateObject())
        {
            switch (property.Name)
            {
                case "remixOfId":
                    photo.RemixOfId = property.Value.GetInt64();
                    break;
                default:
                    photo.OtherParameters[$"civitai metadata: {property.Name}"] = property.Value.ToString();
                    break;
            }
        }
    }

    private static async Task<long?> FetchModelVersionIdAsync(long modelHash)
    {
        var url = $"https://civitai.com/api/v1/model-versions/by-hash/{modelHash:X10}";
        var client = new HttpClient();
        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode) return null;

        try
        {
            var json = await response.Content.ReadAsStringAsync();
            var document = JsonDocument.Parse(json);

            return document.RootElement.GetProperty("id").GetInt64();
        }
        catch (Exception)
        {
            return null;
        }
    }


    private static async Task<Model> FetchModelInformationAsync(Photo photo)
    {
        var url = $"https://civitai.com/api/v1/model-versions/{photo.ModelVersionId}";
        var client = new HttpClient();
        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode) return new Model { ModelId = 0, ModelName = "<Unknown>", ModelVersionName = "<Unknown>", ModelVersionId = photo.ModelVersionId};

        try
        {
            var json = await response.Content.ReadAsStringAsync();
            var document = JsonDocument.Parse(json);

            var modelVersionName = document.RootElement.GetProperty("name").GetString();
            var modelName = document.RootElement.GetProperty("model").GetProperty("name").GetString();
            var modelId = document.RootElement.GetProperty("modelId").GetInt64();

            return new Model {ModelId = modelId, ModelName = modelName, ModelVersionId = photo.ModelVersionId, ModelVersionName = modelVersionName};
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async Task LoadScaledImageAsync(StorageFile sourceFile, Photo photo, uint targetHeight)
    {
        var fileBytes = await FileIO.ReadBufferAsync(sourceFile);
        var bytes = fileBytes.ToArray();

        using var image = new ImageMagick.MagickImage(bytes);
        var aspectRatio = (double)image.Width / image.Height;
        var newWidth = (uint)(targetHeight * aspectRatio);

        image.Resize(newWidth, targetHeight);

        image.Format = ImageMagick.MagickFormat.Jpeg;
        image.Quality = 90;

        photo.ThumbnailData = image.ToByteArray();
    }

    private void DirectoryCreated(string path)
    {
        FolderAdded?.Invoke(this, new FolderChangedEventArgs(path));
    }

    private async Task DirectoryDeletedAsync(string path)
    {
        FolderRemoved?.Invoke(this, new FolderChangedEventArgs(path));

        // If the folder is a root, remove it from the database
        await using var db = new PhotoDatabase();
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Path == path);
        if (folder != null)
        {
            db.Folders.Remove(folder);
            await db.SaveChangesAsync();
        }

        if (_watchers.ContainsKey(path))
        {
            _watchers.TryRemove(path, out var watcher);
            watcher?.Dispose();
        }
    }

    private async Task DirectoryRenamedAsync(string previousPath, string newPath)
    {
        await using var db = new PhotoDatabase();

        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Path == previousPath);
        if (folder != null)
        {
            folder.Path = newPath;
        }

        var photos = await db.Photos.Where(p => p.Path.StartsWith(previousPath)).ToListAsync();
        foreach (var photo in photos)
        {
            photo.Path = photo.Path.Replace(previousPath, newPath, StringComparison.CurrentCultureIgnoreCase);
        }

        await db.SaveChangesAsync();

        FolderRemoved?.Invoke(this, new FolderChangedEventArgs(previousPath));
        FolderAdded?.Invoke(this, new FolderChangedEventArgs(newPath));
    }

    private async Task FileCreatedAsync(string path)
    {
        await using var db = new PhotoDatabase();
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
                Name = file.Name
            };
            db.Photos.Add(photo);
        }

        photo.DateTaken = imageProps.DateTaken.DateTime;
        photo.FileSize = props.Size;
        photo.Width = (int)imageProps.Width;
        photo.Height = (int)imageProps.Height;
        photo.LastModified = props.DateModified.DateTime;

        var stream = await file.OpenStreamForReadAsync();

        try
        {
            var fileType = FileTypeDetector.DetectFileType(stream);
            switch (fileType)
            {
                case FileType.Png:
                    await ExtractPngMetadata(stream, photo);
                    await LoadScaledImageAsync(file, photo, 400);
                    break;
                case FileType.Jpeg:
                    await ExtractJpegMetadata(stream, photo);
                    await LoadScaledImageAsync(file, photo, 400);
                    break;
                default:
                    photo.Raw = $"Unsupported format: {fileType}";
                    var unsupportedImage = new ImageMagick.MagickImage(ImageMagick.MagickColors.Red, 225, 400);
                    unsupportedImage.Format = ImageMagick.MagickFormat.Jpeg;
                    photo.ThumbnailData = unsupportedImage.ToByteArray();
                    break;
            }
        }
        catch (Exception ex)
        {
            photo.Raw = $"Metadata extraction failed: {ex.Message}";
        }

        var model = await db.Models.FirstOrDefaultAsync(m => m.ModelVersionId == photo.ModelVersionId);
        if (model == null)
        {
            model = await FetchModelInformationAsync(photo);

            if (model.ModelId != 0)
            {
                db.Models.Add(model);
                ModelAdded?.Invoke(this, new ModelChangedEventArgs(model.ModelVersionId, model.ModelName, model.ModelVersionName));
            }
        }

        photo.ModelId = model.ModelId;
        photo.ModelName = model.ModelName;
        photo.ModelVersionName = model.ModelVersionName;

        if (!isNew)
        {
            PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(photo));
        }

        PhotoAdded?.Invoke(this, new PhotoChangedEventArgs(photo));

        await db.SaveChangesAsync();
    }

    private async Task FileDeletedAsync(string path)
    {
        await using var db = new PhotoDatabase();
        var existingPhoto = await db.Photos.FirstOrDefaultAsync(p => p.Path == path);
        if (existingPhoto == null) return;

        db.Photos.Remove(existingPhoto);
        PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(existingPhoto));

        if (!await db.Photos.AnyAsync(p => p.ModelVersionId == existingPhoto.ModelVersionId))
        {
            var model = await db.Models.FirstOrDefaultAsync(m => m.ModelVersionId == existingPhoto.ModelVersionId);
            if (model != null)
            {
                db.Models.Remove(model);
                ModelRemoved?.Invoke(this, new ModelChangedEventArgs(model.ModelVersionId, model.ModelName, model.ModelVersionName));
            }
        }

        await db.SaveChangesAsync();
    }

    private async Task FileRenamedAsync(string previousPath, string newPath)
    {
        await using var db = new PhotoDatabase();

        // If the photo is in the database, update the path
        var photo = await db.Photos.FirstOrDefaultAsync(p => p.Path == previousPath);
        if (photo != null)
        {
            PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(photo));
            photo.Path = newPath;
            await db.SaveChangesAsync();

            PhotoAdded?.Invoke(this, new PhotoChangedEventArgs(photo));
        }
    }

    private void StartRootFolderWatcher(string path)
    {
        if (_watchers.ContainsKey(path)) return;

        var watcher = new RootWatcher(path);

        watcher.DirectoryCreated += (_, e) => DirectoryCreated(e.Path);
        watcher.DirectoryDeleted += async (_, e) => await DirectoryDeletedAsync(e.Path);
        watcher.DirectoryRenamed += async (_, e) => await DirectoryRenamedAsync(e.PreviousPath, e.NewPath);

        // CONSIDER: Ignoring file changes for now

        watcher.FileCreated += async (_, e) => await FileCreatedAsync(e.Path);
        watcher.FileDeleted += async (_, e) => await FileDeletedAsync(e.Path);
        watcher.FileRenamed += async (_, e) => await FileRenamedAsync(e.PreviousPath, e.NewPath);

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
                await using var db = new PhotoDatabase();

                var existingPhotos = await db.Photos
                    .Where(p => p.Path.StartsWith(path))
                    .Select(p => p.Path)
                    .ToHashSetAsync(cancellationToken);

                var folder = await StorageFolder.GetFolderFromPathAsync(path);
                var query = folder.CreateFileQueryWithOptions(new QueryOptions(CommonFileQuery.OrderByName, [".png", ".jpg"])
                    { FolderDepth = FolderDepth.Deep });

                var files = (await query.GetFilesAsync()).Select(f => f.Path).ToList();
                var processedFiles = 0;

                foreach (var file in files.TakeWhile(_ => !cancellationToken.IsCancellationRequested))
                {
                    try
                    {
                        if (!existingPhotos.Remove(file))
                        {
                            await FileCreatedAsync(file);
                        }

                        processedFiles++;
                        ScanProgress?.Invoke(this, new ScanProgressEventArgs(path, processedFiles, files.Count));
                    }
                    catch (Exception)
                    {
                        // Log error but continue processing queue
                    }
                }

                foreach (var file in existingPhotos)
                {
                    await FileDeletedAsync(file);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation, no action needed
        }
    }

    private void QueueRootFolderScan(string path)
    {
        _scanQueue.Writer.TryWrite(path);
    }

    /*
     * Initialization
     */

    private async Task AddRootFolder(string root)
    {
        DirectoryCreated(root);

        var storageFolder = await StorageFolder.GetFolderFromPathAsync(root);
        var folderQuery =
            storageFolder.CreateFolderQueryWithOptions(new QueryOptions { FolderDepth = FolderDepth.Deep });

        foreach (var subFolder in (await folderQuery.GetFoldersAsync()).OrderBy(f => f.Path))
        {
            DirectoryCreated(subFolder.Path);
        }

        StartRootFolderWatcher(root);
        QueueRootFolderScan(root);
    }

    public async Task InitializeAsync()
    {
        await using var db = new PhotoDatabase();
        await db.Database.EnsureCreatedAsync();

        // CONSIDER: There may be orphaned photos in the database if the folder entry was deleted

        foreach (var folder in await db.Folders.ToListAsync())
        {
            if (Directory.Exists(folder.Path))
            {
                await AddRootFolder(folder.Path);
            }
            else
            {
                db.Folders.Remove(folder);
                db.Photos.RemoveRange(db.Photos.Where(p => p.Path.StartsWith(folder.Path)));
            }
        }

        foreach (var model in await db.Models.ToListAsync())
        {
            if (!await db.Photos.AnyAsync(p => p.ModelVersionId == model.ModelVersionId))
            {
                db.Models.Remove(model);
            }
            else
            {
                ModelAdded?.Invoke(this, new ModelChangedEventArgs(model.ModelVersionId, model.ModelName, model.ModelVersionName));
            }
        }

        await db.SaveChangesAsync();
    }

    /*
     * Add folder
     */

    public async Task AddFolderAsync(StorageFolder newFolder)
    {
        await using var db = new PhotoDatabase();
        var folders = await db.Folders.ToListAsync();

        foreach (var folder in folders)
        {
            if (newFolder.Path.StartsWith(folder.Path))
            {
                // This folder is already in the database
                return;
            }

            if (!folder.Path.StartsWith(newFolder.Path)) continue;

            // The new folder is the parent of this folder, so remove it. We don't do a full
            // folder removal because the children will still be valid.
            db.Folders.Remove(folder);
            FolderRemoved?.Invoke(this, new FolderChangedEventArgs(folder.Path));
        }

        db.Folders.Add(new Folder { Path = newFolder.Path });
        await db.SaveChangesAsync();

        await AddRootFolder(newFolder.Path);

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
            //.Where(p => Path.GetDirectoryName(p.Path) == folderPath)
            .ToList();
    }

    public static async Task<List<Photo>> GetPhotosByModelVersionIdAsync(long modelVersionId)
    {
        var db = new PhotoDatabase();
        return await db.Photos
            .Where(p => p.ModelVersionId == modelVersionId)
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
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private sealed class LineParser(string line)
    {
        private int _index;

        public bool MorePairs => _index < line.Length;

        private void ScanMatching(StringBuilder value, char close, bool includeClose)
        {
            while (_index < line.Length && line[_index] != close)
            {
                var current = line[_index++];
                value.Append(current);

                switch (current)
                {
                    case '"':
                        ScanMatching(value, '"', true);
                        break;
                    case '{':
                        ScanMatching(value, '}', true);
                        break;
                    case '[':
                        ScanMatching(value, ']', true);
                        break;
                }
            }

            if (_index >= line.Length) return;
            if (includeClose)
            {
                value.Append(line[_index]);
            }

            _index++;
        }

        public KeyValuePair GetNextKeyValuePair()
        {
            var key = new StringBuilder();
            
            while (_index < line.Length && (line[_index] == ' ' || line[_index] == ','))
            {
                _index++;
            }

            while (line[_index] != ':')
            {
                key.Append(line[_index++]);
            }

            _index++;

            var value = new StringBuilder();
            ScanMatching(value, ',', false);

            return new KeyValuePair(key.ToString().Trim().ToLowerInvariant(), value.ToString().Trim());
        }
    }

    public class FolderChangedEventArgs(string path) : EventArgs
    {
        public string Path { get; } = path;
    }

    public class ModelChangedEventArgs(long modelVersionId, string modelName, string modelVersionName) : EventArgs
    {
        public long ModelVersionId { get; } = modelVersionId;
        public string ModelName { get; } = modelName;
        public string ModelVersionName { get; } = modelVersionName;
    }

    public sealed class PhotoChangedEventArgs(Photo photo) : EventArgs
    {
        public Photo Photo { get; } = photo;
    }

    public class ScanProgressEventArgs(string path, int processedFiles, int totalFiles) : EventArgs
    {
        public string Path { get; } = path;
        public int ProcessedFiles { get; } = processedFiles;
        public int TotalFiles { get; } = totalFiles;
        public double Progress => TotalFiles > 0 ? (double)ProcessedFiles / TotalFiles : 0;
    }
}