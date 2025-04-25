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

        _scanProcessingTask = Task.Run(async () => { await ProcessScanQueueAsync(_cancellationTokenSource.Token); });
    }

    private static void AddModel(Photo photo, Model model, double? weight = null)
    {
        if (model == null || photo.Models.Any(l => l.Model.ModelVersionId == model.ModelVersionId)) return;
        photo.Models.Add(new ModelInstance { Model = model, Weight = weight });
    }

    /*
     * File watching
     */

    private async Task ExtractPngMetadata(PhotoDatabase db, Stream stream, Photo photo)
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
        await ParseStableDiffusionMetadata(db, raw, photo);
    }

    private async Task ExtractJpegMetadata(PhotoDatabase db, Stream stream, Photo photo)
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

        await ParseStableDiffusionMetadata(db, userComment, photo);
    }

    private static string CleanUserComment(string comment)
    {
        if (comment.StartsWith("ASCII\0", StringComparison.OrdinalIgnoreCase))
            return comment[6..];
        if (comment.StartsWith("UNICODE\0", StringComparison.OrdinalIgnoreCase))
            return comment[8..];

        return comment;
    }

    private static List<(string, string)> ParseHashes(string hashString)
    {
        var current = 0;
        var hashPairs = new List<(string, string)>();

        while (hashString[current] == ' ')
        {
            current++;
        }

        if (hashString[current] != '{')
        {
            throw new FormatException("Bad hash");
        }

        do
        {
            current++;

            while (hashString[current] == ' ')
            {
                current++;
            }

            if (hashString[current++] != '"')
            {
                throw new FormatException("Bad hash");
            }

            var name = new StringBuilder();
            while (hashString[current] != '"')
            {
                name.Append(hashString[current++]);
            }

            current++;

            if (hashString[current++] != ':')
            {
                throw new FormatException("Bad hash");
            }

            while (hashString[current] == ' ')
            {
                current++;
            }

            if (hashString[current++] != '"')
            {
                throw new FormatException("Bad hash");
            }

            var hash = new StringBuilder();
            while (hashString[current] != '"')
            {
                hash.Append(hashString[current++]);
            }

            if (hashString[current++] != '"')
            {
                throw new FormatException("Bad hash");
            }

            hashPairs.Add((name.ToString(), hash.ToString()));
        } while (hashString[current] == ',');

        if (hashString[current] != '}')
        {
            throw new FormatException("Bad hash");
        }

        return hashPairs;
    }

    private async Task ProcessLoraHashes(PhotoDatabase db, string loraHashesString, Photo photo)
    {
        if (string.IsNullOrWhiteSpace(loraHashesString)) return;

        try
        {
            var loraPairs = loraHashesString.Trim('"').Split(',').Select(pair =>
            {
                var pairValues = pair.Split(':');
                var name = pairValues[0].Trim();
                var hash = pairValues[1].Trim();
                return (name, hash);
            }).ToList();

            foreach (var (name, hash) in loraPairs)
            {
                if (!long.TryParse(hash, NumberStyles.HexNumber, null, out var hashValue))
                    throw new FormatException("Bad hash");
                var model = await FetchModelInformationByHashAsync(db, hashValue, "LORA");
                AddModel(photo, model);
            }
        }
        catch (Exception ex)
        {
            photo.OtherParameters["Lora Parsing Error"] = ex.Message;
        }
    }

    private async Task ProcessHashes(PhotoDatabase db, string hashString, Photo photo)
    {
        if (string.IsNullOrWhiteSpace(hashString)) return;

        try
        {
            var hashPairs = ParseHashes(hashString);

            foreach (var (name, hash) in hashPairs)
            {
                if (name is "vae" or "model") continue;

                if (!name.StartsWith("lora:", StringComparison.InvariantCultureIgnoreCase)
                    && !name.StartsWith("embed:"))
                {
                    continue;
                }

                if (!long.TryParse(hash, NumberStyles.HexNumber, null, out var hashValue))
                    throw new FormatException("Bad hash");

                if (name.StartsWith("embed:"))
                {
                    var model = await FetchModelInformationByHashAsync(db, hashValue, "TextualInversion");
                    AddModel(photo, model);
                }
                else
                {
                    var model = await FetchModelInformationByHashAsync(db, hashValue, "LORA");
                    AddModel(photo, model);
                }
            }
        }
        catch (Exception ex)
        {
            photo.OtherParameters["Hash Parsing Error"] = ex.Message;
        }
    }

    private async Task ProcessTextualInversions(PhotoDatabase db, string tiHashesString, Photo photo)
    {
        if (string.IsNullOrWhiteSpace(tiHashesString)) return;

        try
        {
            var tiPairs = tiHashesString.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var pair in tiPairs)
            {
                var parts = pair.Split(':', 2);
                if (parts.Length != 2) continue;

                var name = parts[0].Trim().Trim('"');
                var hash = parts[1].Trim()[..10];

                if (!long.TryParse(hash, NumberStyles.HexNumber, null, out var hashValue))
                {
                    throw new FormatException("Invalid hash");
                }

                var model = await FetchModelInformationByHashAsync(db, hashValue, "TextualInversion");
                AddModel(photo, model);
            }
        }
        catch (Exception ex)
        {
            photo.OtherParameters["TI Parsing Error"] = ex.Message;
        }
    }

    private async Task ParseStableDiffusionMetadata(PhotoDatabase db, string raw, Photo photo)
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
                case "adetailer model":
                    photo.ADetailerModel = value;
                    break;

                case "adetailer confidence":
                    if (double.TryParse(value, out var confidence))
                        photo.ADetailerConfidence = confidence;
                    break;

                case "adetailer dilate/erode":
                case "adetailer dilate erode":
                    if (int.TryParse(value, out var dilateErode))
                        photo.ADetailerDilateErode = dilateErode;
                    break;

                case "adetailer mask blur":
                    if (int.TryParse(value, out var maskBlur))
                        photo.ADetailerMaskBlur = maskBlur;
                    break;

                case "adetailer denoising strength":
                    if (double.TryParse(value, out var detailerDenoisingStrength))
                        photo.ADetailerDenoisingStrength = detailerDenoisingStrength;
                    break;

                case "adetailer inpaint only masked":
                    photo.ADetailerInpaintOnlyMasked = value.Equals("True", StringComparison.OrdinalIgnoreCase);
                    break;

                case "adetailer inpaint padding":
                    if (int.TryParse(value, out var inpaintPadding))
                        photo.ADetailerInpaintPadding = inpaintPadding;
                    break;

                case "adetailer version":
                    photo.ADetailerVersion = value;
                    break;

                case "civitai metadata":
                    ProcessCivitaiMetadata(value, photo);
                    break;

                case "civitai resources":
                    await ProcessCivitaiResources(db, photo, value);
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

                case "emphasis":
                    if (value == "No norm")
                    {
                        photo.NoEmphasisNorm = true;
                    }
                    else
                    {
                        throw new FormatException("Unexpected emphasis");
                    }

                    break;

                case "hashes":
                    await ProcessHashes(db, value, photo);
                    break;

                case "hires cfg scale":
                    if (!double.TryParse(value, out var hiresCfgScale))
                        throw new FormatException("Invalid hires steps value");
                    photo.HiresCfgScale = hiresCfgScale;
                    break;

                case "hires prompt":
                    photo.HiresPrompt = value;
                    break;

                case "hires steps":
                    if (!int.TryParse(value, out var hiresSteps))
                        throw new FormatException("Invalid hires steps value");
                    photo.HiresSteps = hiresSteps;
                    break;

                case "hires upscale":
                    if (!double.TryParse(value, out var hiresUpscale))
                        throw new FormatException("Invalid hires upscale value");
                    photo.HiresUpscale = hiresUpscale;
                    break;

                case "hires upscaler":
                    photo.HiresUpscaler = value;
                    break;

                case "lora hashes":
                    await ProcessLoraHashes(db, value, photo);
                    break;

                case "model":
                    // Ignore.
                    break;

                case "model hash":
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    if (!long.TryParse(value, NumberStyles.HexNumber, null, out var modelHash))
                        throw new FormatException("Invalid hex model hash");

                    var model = await FetchModelInformationByHashAsync(db, modelHash, "Checkpoint");
                    AddModel(photo, model);
                    break;

                case "rng":
                    photo.Rng = value;
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

                case "ti hashes":
                    await ProcessTextualInversions(db, value, photo);
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

    private async Task ProcessCivitaiResources(PhotoDatabase db, Photo photo, string value)
    {
        var elements = JsonSerializer.Deserialize<List<JsonElement>>(value);

        foreach (var element in elements)
        {
            var type = element.GetProperty("type").GetString();

            switch (type)
            {
                case "checkpoint":
                {
                    var model = await FetchModelInformationByVersionIdAsync(db,
                        element.GetProperty("modelVersionId").GetInt32(),
                        "Checkpoint");

                    AddModel(photo, model);
                }
                    break;

                case "lora":
                {
                    var model = await FetchModelInformationByVersionIdAsync(db,
                        element.GetProperty("modelVersionId").GetInt32(),
                        "LORA");

                    if (element.TryGetProperty("weight", out var weightElement))
                    {
                        AddModel(photo, model, weightElement.GetDouble());
                    }
                    else
                    {
                        AddModel(photo, model);
                    }
                }
                    break;

                case "embed":
                {
                    var model = await FetchModelInformationByVersionIdAsync(db,
                        element.GetProperty("modelVersionId").GetInt32(),
                        "TextualInversion");

                    AddModel(photo, model);
                }
                    break;

                case "vae":
                {
                    var model = await FetchModelInformationByVersionIdAsync(db,
                        element.GetProperty("modelVersionId").GetInt32(),
                        "");

                    if (model != null)
                    {
                        photo.Vae = model.ModelVersionName;
                    }
                }
                    break;

                default:
                {
                    var model = await FetchModelInformationByVersionIdAsync(db,
                        element.GetProperty("modelVersionId").GetInt32(),
                        "");

                    if (model != null)
                    {
                        photo.OtherParameters[$"civitai resource: {type}"] = model.ModelName;
                    }
                }
                    break;
            }
        }
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

    private async Task<Model> GetOrCreateModel(PhotoDatabase db, long modelVersionId, string modelVersionName,
        long modelId, string modelName, string kind)
    {
        var model = await db.Models.FirstOrDefaultAsync(m => m.ModelVersionId == modelVersionId);
        if (model != null) return model;
        model = new Model
        {
            ModelVersionId = modelVersionId,
            ModelVersionName = modelVersionName,
            ModelId = modelId,
            ModelName = modelName,
            Kind = kind
        };
        db.Models.Add(model);
        if (kind == "Checkpoint")
        {
            ModelAdded?.Invoke(this,
                new ModelChangedEventArgs(model.ModelVersionId, model.ModelName, model.ModelVersionName));
        }

        await db.SaveChangesAsync();

        return model;
    }

    private async Task<Model> FetchModelInformationAsync(PhotoDatabase db, string url, string kind)
    {
        var client = new HttpClient();
        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            var json = await response.Content.ReadAsStringAsync();
            var document = JsonDocument.Parse(json);

            var modelVersionId = document.RootElement.GetProperty("id").GetInt64();
            var modelVersionName = document.RootElement.GetProperty("name").GetString();
            var modelName = document.RootElement.GetProperty("model").GetProperty("name").GetString();
            var modelId = document.RootElement.GetProperty("modelId").GetInt64();
            var modelKind = document.RootElement.GetProperty("model").GetProperty("type").GetString();

            if (!string.IsNullOrWhiteSpace(kind) && modelKind != kind && (modelKind != "LoCon" || kind != "LORA"))
            {
                return null;
            }

            return await GetOrCreateModel(db, modelVersionId, modelVersionName, modelId, modelName, modelKind);
        }
        catch (Exception)
        {
            // Ignore error
        }

        return null;
    }

    private async Task<Model> FetchModelInformationByHashAsync(PhotoDatabase db, long modelHash, string kind)
    {
        return await FetchModelInformationAsync(db,
            $"https://civitai.com/api/v1/model-versions/by-hash/{modelHash:X10}", kind);
    }


    private async Task<Model> FetchModelInformationByVersionIdAsync(PhotoDatabase db, int modelVersionId, string kind)
    {
        return await FetchModelInformationAsync(db, $"https://civitai.com/api/v1/model-versions/{modelVersionId}",
            kind);
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

        await using var db = new PhotoDatabase();

        var photosToRemove = await db.Photos
            .Include(p => p.Models)
            .ThenInclude(m => m.Model)
            .Where(p => p.Path.StartsWith(path))
            .ToListAsync();

        foreach (var photo in photosToRemove)
        {
            PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(photo));
        }

        db.Photos.RemoveRange(photosToRemove);
        await db.SaveChangesAsync();

        await RemoveUnusedModels();
        
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

        var photo = await db.Photos
            .Include(p => p.Models)
            .ThenInclude(l => l.Model)
            .FirstOrDefaultAsync(p => p.Path == path);
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
                    await ExtractPngMetadata(db, stream, photo);
                    await LoadScaledImageAsync(file, photo, 400);
                    break;
                case FileType.Jpeg:
                    await ExtractJpegMetadata(db, stream, photo);
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

        if (photo.Models.All(m => m.Model.Kind != "Checkpoint"))
        {
            var model = await FetchModelInformationByVersionIdAsync(db, 0, "Checkpoint");
            AddModel(photo, model);
        }

        if (!isNew)
        {
            PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(photo));
        }

        PhotoAdded?.Invoke(this, new PhotoChangedEventArgs(photo));

        await db.SaveChangesAsync();
    }

    private async Task RemoveUnusedModels()
    {
        await using var db = new PhotoDatabase();
        var usedModels = await db.Photos.SelectMany(p => p.Models.Select(m => m.ModelVersionId)).ToHashSetAsync();
        var unusedModels = await db.Models.Where(m => !usedModels.Contains(m.ModelVersionId)).ToListAsync();
        
        foreach (var model in unusedModels)
        {
            ModelRemoved?.Invoke(this, new ModelChangedEventArgs(model.ModelVersionId, model.ModelName, model.ModelVersionName));
        }
        
        db.Models.RemoveRange(unusedModels);
        await db.SaveChangesAsync();
    }

    private async Task FileDeletedAsync(string path)
    {
        await using var db = new PhotoDatabase();
        var existingPhoto = await db.Photos
            .Include(p => p.Models)
            .ThenInclude(m => m.Model)
            .FirstOrDefaultAsync(p => p.Path == path);
        if (existingPhoto == null) return;

        db.Photos.Remove(existingPhoto);
        PhotoRemoved?.Invoke(this, new PhotoChangedEventArgs(existingPhoto));

        await db.SaveChangesAsync();
        await RemoveUnusedModels();
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
                var query = folder.CreateFileQueryWithOptions(
                    new QueryOptions(CommonFileQuery.OrderByName, [".png", ".jpg"])
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

        foreach (var model in await db.Models
                     .Where(m => m.Kind == "Checkpoint")
                     .ToListAsync())
        {
            ModelAdded?.Invoke(this,
                new ModelChangedEventArgs(model.ModelVersionId, model.ModelName, model.ModelVersionName));
        }

        await db.SaveChangesAsync();
        await RemoveUnusedModels();

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
        return await db.Photos
            .Where(p => p.Path.StartsWith(folderPath))
            .Include(p => p.Models)
            .ThenInclude(l => l.Model)
            .AsNoTracking()
            .ToListAsync();
    }

    public static async Task<List<Photo>> GetPhotosByModelVersionIdAsync(long modelVersionId)
    {
        var db = new PhotoDatabase();
        return await db.Photos
            .Include(p => p.Models)
            .ThenInclude(l => l.Model)
            .AsNoTracking()
            .Where(p => p.Models.Any(m => m.ModelVersionId == modelVersionId))
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