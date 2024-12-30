using System;

namespace DiffusionView.Service;

public class FolderChangedEventArgs(string name, string path) : EventArgs
{
    public string Name { get; } = name;

    public string Path { get; } = path;
}