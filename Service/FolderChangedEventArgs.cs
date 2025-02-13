using System;

namespace DiffusionView.Service;

public class FolderChangedEventArgs(string path) : EventArgs
{
    public string Path { get; } = path;
}