using System;

namespace DiffusionView.Service
{
    public class ScanProgressEventArgs(string path, int processedFiles, int totalFiles) : EventArgs
    {
        public string Path { get; } = path;
        public int ProcessedFiles { get; } = processedFiles;
        public int TotalFiles { get; } = totalFiles;
        public double Progress => TotalFiles > 0 ? (double)ProcessedFiles / TotalFiles : 0;
    }
}
