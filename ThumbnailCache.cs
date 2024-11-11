using System;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.FileProperties;
using Windows.Storage;

namespace DiffusionView;

internal class ThumbnailCache
{
    private readonly ConcurrentDictionary<string, BitmapImage> _cache = new();
    private const int MaxCacheSize = 200;

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
            using var thumbnailStream = await file.GetThumbnailAsync(ThumbnailMode.PicturesView, 200, ThumbnailOptions.UseCurrentScale);
            await thumbnail.SetSourceAsync(thumbnailStream);

            if (_cache.Count >= MaxCacheSize)
            {
                var removeKeys = _cache.Keys.Take(_cache.Count - MaxCacheSize + 1);
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