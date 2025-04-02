﻿using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Globalization;
using DiffusionView.Database;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;

namespace DiffusionView;

public partial class PhotoItem(Photo photo) : INotifyPropertyChanged
{
    private string _fileName = photo.Name;
    private string _filePath = photo.Path;
    private string _fileSize = FormatFileSize(photo.FileSize);
    private string _resolution = $"{photo.Width} x {photo.Height}";
    private int _width = photo.Width;
    private int _height = photo.Height;
    private BitmapImage _thumbnail;
    private byte[] _thumbnailData = photo.ThumbnailData;
    private bool _isSelected;
    private string _lastModified = photo.LastModified.ToString("g");

    private string _prompt = photo.Prompt ?? "No prompt available";
    private string _negativePrompt = photo.NegativePrompt ?? "No negative prompt available";
    private string _steps = photo.Steps != 0 ? photo.Steps.ToString() : string.Empty;
    private int _generatedWidth = photo.GeneratedWidth;
    private int _generatedHeight = photo.GeneratedHeight;
    private string _generatedResolution = $"{photo.GeneratedWidth} x {photo.GeneratedHeight}";
    private string _sampler = photo.Sampler ?? string.Empty;
    private string _cfgScale = photo.CfgScale != 0 ? photo.CfgScale.ToString("F1") : string.Empty;
    private string _seed = photo.Seed != 0 ? photo.Seed.ToString() : string.Empty;
    private string _modelName = photo.ModelName;
    private string _modelVersionName = photo.ModelVersionName;
    private string _clipSkip = photo.ClipSkip != 0 ? photo.ClipSkip.ToString() : string.Empty;
    private string _denoisingStrength = photo.DenoisingStrength != 0 ? photo.DenoisingStrength.ToString(CultureInfo.InvariantCulture) : string.Empty;
    private string _variationSeed = photo.VariationSeed != 0 ? photo.VariationSeed.ToString() : string.Empty;
    private string _hiresSteps = photo.HiresSteps != 0 ? photo.HiresSteps.ToString() : string.Empty;
    private string _variationSeedStrength = photo.VariationSeedStrength != 0 ? photo.VariationSeedStrength.ToString(CultureInfo.InvariantCulture) : string.Empty;
    private string _hiresUpscale = photo.HiresUpscale != 0 ? photo.HiresUpscale.ToString(CultureInfo.InvariantCulture) : string.Empty;
    private string _hiresUpscaler = photo.HiresUpscaler ?? string.Empty;
    private string _vae = photo.Vae ?? string.Empty;
    private string _scheduleType = photo.ScheduleType ?? string.Empty;
    private string _remixOf = photo.RemixOfId != 0 ? $"https://civitai.com/images/{photo.RemixOfId}" : string.Empty; 
    private string _version = photo.Version ?? string.Empty;
    private Dictionary<string, string> _otherParameters = new(photo.OtherParameters);
    private string _raw = photo.Raw ?? "No raw data available";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public string LastModified
    {
        get => _lastModified;
        set => SetProperty(ref _lastModified, value);
    }

    public string FileSize
    {
        get => _fileSize;
        set => SetProperty(ref _fileSize, value);
    }

    public int Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    public int Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }

    public string Resolution
    {
        get => _resolution;
        set => SetProperty(ref _resolution, value);
    }

    public BitmapImage Thumbnail
    {
        get
        {
            if (_thumbnail == null && _thumbnailData != null)
            {
                _thumbnail = CreateBitmapImage(_thumbnailData);
            }
            return _thumbnail;
        }
        set => SetProperty(ref _thumbnail, value);
    }

    public byte[] ThumbnailData
    {
        get => _thumbnailData;
        set
        {
            SetProperty(ref _thumbnailData, value);
            Thumbnail = null;
        }
    }

    // Stable Diffusion metadata properties
    public string Prompt
    {
        get => _prompt;
        set => SetProperty(ref _prompt, value);
    }

    public string NegativePrompt
    {
        get => _negativePrompt;
        set => SetProperty(ref _negativePrompt, value);
    }

    public string Steps
    {
        get => _steps;
        set => SetProperty(ref _steps, value);
    }

    public int GeneratedWidth
    {
        get => _generatedWidth;
        set => SetProperty(ref _generatedWidth, value);
    }

    public int GeneratedHeight
    {
        get => _generatedHeight;
        set => SetProperty(ref _generatedHeight, value);
    }

    public string GeneratedResolution
    {
        get => _generatedResolution;
        set => SetProperty(ref _generatedResolution, value);
    }

    public string Sampler
    {
        get => _sampler;
        set => SetProperty(ref _sampler, value);
    }

    public string CfgScale
    {
        get => _cfgScale;
        set => SetProperty(ref _cfgScale, value);
    }

    public string Seed
    {
        get => _seed;
        set => SetProperty(ref _seed, value);
    }

    public string ModelName
    {
        get => _modelName;
        set => SetProperty(ref _modelName, value);
    }

    public string ModelVersionName
    {
        get => _modelVersionName;
        set => SetProperty(ref _modelVersionName, value);
    }

    public string ClipSkip
    {
        get => _clipSkip;
        set => SetProperty(ref _clipSkip, value);
    }

    public string DenoisingStrength
    {
        get => _denoisingStrength;
        set => SetProperty(ref _denoisingStrength, value);
    }

    public string VariationSeed
    {
        get => _variationSeed;
        set => SetProperty(ref _variationSeed, value);
    }

    public string VariationSeedStrength
    {
        get => _variationSeedStrength;
        set => SetProperty(ref _variationSeedStrength, value);
    }

    public string HiresUpscale
    {
        get => _hiresUpscale;
        set => SetProperty(ref _hiresUpscale, value);
    }

    public string HiresUpscaler
    {
        get => _hiresUpscaler;
        set => SetProperty(ref _hiresUpscaler, value);
    }

    public string HiresSteps
    {
        get => _hiresSteps;
        set => SetProperty(ref _hiresSteps, value);
    }

    public string Vae
    {
        get => _vae;
        set => SetProperty(ref _vae, value);
    }

    public string RemixOf
    {
        get => _remixOf;
        set => SetProperty(ref _remixOf, value);
    }

    public string ScheduleType
    {
        get => _scheduleType;
        set => SetProperty(ref _scheduleType, value);
    }

    public string Version
    {
        get => _version;
        set => SetProperty(ref _version, value);
    }

    public Dictionary<string, string> OtherParameters
    {
        get => _otherParameters;
        set
        {
            _otherParameters = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OtherParameters)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ParametersList)));
        }
    }

    public IEnumerable<KeyValuePair> ParametersList => OtherParameters.Select(kvp => new KeyValuePair(kvp.Key, kvp.Value)).OrderBy(kvp => kvp.Key);

    public string Raw
    {
        get => _raw;
        set => SetProperty(ref _raw, value);
    }

    public event PropertyChangedEventHandler PropertyChanged;

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

    protected void SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
    {
        if (Equals(storage, value)) return;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static BitmapImage CreateBitmapImage(byte[] thumbnailData)
    {
        if (thumbnailData == null)
            return null;

        var image = new BitmapImage();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        stream.WriteAsync(thumbnailData.AsBuffer()).GetResults();
        stream.Seek(0);
        image.SetSource(stream);
        return image;
    }
}
