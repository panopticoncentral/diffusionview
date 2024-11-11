using System;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Threading;
using Windows.Storage;

namespace DiffusionView;

internal class ImageLoader
{
    private readonly ConcurrentDictionary<string, Task> _loadingTasks = new();
    private readonly SemaphoreSlim _concurrencyLimiter = new(3); // Max 3 concurrent loads

    public async Task PreloadImageAsync(string filePath)
    {
        if (_loadingTasks.ContainsKey(filePath)) return;

        var loadTask = LoadImageAsync(filePath);
        _loadingTasks.TryAdd(filePath, loadTask);

        try
        {
            await loadTask;
        }
        finally
        {
            _loadingTasks.TryRemove(filePath, out _);
        }
    }

    private async Task LoadImageAsync(string filePath)
    {
        await _concurrencyLimiter.WaitAsync();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }
}