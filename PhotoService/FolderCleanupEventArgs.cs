using System;

namespace DiffusionView.PhotoService;

public class FolderCleanupEventArgs(string folderPath, string reason) : EventArgs
{
    public string FolderPath { get; } = folderPath;
    public string Reason { get; } = reason;
}