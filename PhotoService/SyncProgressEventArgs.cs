using System;

namespace DiffusionView.PhotoService;

public class SyncProgressEventArgs(int processed, int total, string folder, string status) : EventArgs
{
    public int ProcessedFolders { get; } = processed;
    public int TotalFolders { get; } = total;
    public string CurrentFolder { get; } = folder;
    public string Status { get; } = status;
}