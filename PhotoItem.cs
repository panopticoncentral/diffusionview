using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace DiffusionView;

public partial class PhotoItem : INotifyPropertyChanged
{
    private string _fileName;
    private string _filePath;
    private DateTime? _dateTaken;
    private ulong _fileSize;
    private int _width;
    private int _height;
    private BitmapImage _thumbnail;
    private bool _isSelected;

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

    public int ThumbnailWidth => (int)((double)Width / Height) * 300;

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
