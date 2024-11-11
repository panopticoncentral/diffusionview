using System.Collections.Concurrent;
using System;

namespace DiffusionView;

internal class MetadataCache
{
    private readonly ConcurrentDictionary<string, PhotoMetadata> _cache = new();

    public void AddMetadata(string filePath, PhotoMetadata metadata)
    {
        _cache.AddOrUpdate(filePath, metadata, (_, _) => metadata);
    }

    public bool TryGetMetadata(string filePath, out PhotoMetadata metadata)
    {
        return _cache.TryGetValue(filePath, out metadata);
    }

    public DateTime? GetDateTaken(string filePath)
    {
        return _cache.TryGetValue(filePath, out var metadata) ? metadata.DateTaken : null;
    }

    public void RemoveMetadata(string filePath)
    {
        _cache.TryRemove(filePath, out _);
    }

    public void Clear() => _cache.Clear();
}
