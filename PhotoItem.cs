using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System.Collections.Generic;

namespace DiffusionView;

public partial class PhotoItem : INotifyPropertyChanged
{
    // Basic file properties
    private string _fileName;
    private string _filePath;
    private DateTime? _dateTaken;
    private ulong _fileSize;
    private int _width;
    private int _height;
    private BitmapImage _thumbnail;
    private bool _isSelected;
    private DateTime _lastModified;

    // Stable Diffusion metadata
    private string _prompt;
    private string _negativePrompt;
    private int _steps;
    private int _generatedWidth;
    private int _generatedHeight;
    private string _sampler;
    private double _cfgScale;
    private long _seed;
    private string _model;
    private long _modelHash;
    private string _version;
    private Dictionary<string, string> _otherParameters = new();
    private string _raw;

    // Basic file properties
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

    public DateTime? DateTaken
    {
        get => _dateTaken;
        set => SetProperty(ref _dateTaken, value);
    }

    public DateTime LastModified
    {
        get => _lastModified;
        set => SetProperty(ref _lastModified, value);
    }

    public ulong FileSize
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

    public BitmapImage Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
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

    public int Steps
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

    public string Sampler
    {
        get => _sampler;
        set => SetProperty(ref _sampler, value);
    }

    public double CfgScale
    {
        get => _cfgScale;
        set => SetProperty(ref _cfgScale, value);
    }

    public long Seed
    {
        get => _seed;
        set => SetProperty(ref _seed, value);
    }

    public string Model
    {
        get => _model;
        set => SetProperty(ref _model, value);
    }

    public long ModelHash
    {
        get => _modelHash;
        set => SetProperty(ref _modelHash, value);
    }

    public string Version
    {
        get => _version;
        set => SetProperty(ref _version, value);
    }

    public Dictionary<string, string> OtherParameters
    {
        get => _otherParameters;
        set => SetProperty(ref _otherParameters, value);
    }

    public string Raw
    {
        get => _raw;
        set => SetProperty(ref _raw, value);
    }

    public event PropertyChangedEventHandler PropertyChanged;

    protected void SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
    {
        if (Equals(storage, value)) return;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void UpdateVisualState(FrameworkElement element)
    {
        if (element == null) return;

        var button = element.FindName("ImageButton") as Button;
        if (button == null) return;

        VisualStateManager.GoToState(button, IsSelected ? "Selected" : "Unselected", true);
    }
}
