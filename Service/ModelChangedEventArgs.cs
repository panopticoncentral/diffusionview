using System;

namespace DiffusionView.Service;

public class ModelChangedEventArgs(long hash, string name, string version) : EventArgs
{
    public long Hash { get; } = hash;
    public string Name { get; } = name;
    public string Version { get; } = version;
}