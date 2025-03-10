using System;
using System.IO;

namespace DiffusionView.Service
{
    internal sealed partial class RootWatcher : IDisposable
    {
        private readonly FileSystemWatcher _directoryWatcher;
        private readonly FileSystemWatcher _photoWatcher;

        private bool _disposed;

        public event EventHandler<DirectoryCreatedEventArgs> DirectoryCreated;
        public event EventHandler<DirectoryDeletedEventArgs> DirectoryDeleted;
        public event EventHandler<DirectoryRenamedEventArgs> DirectoryRenamed;
        public event EventHandler<FileCreatedEventArgs> FileCreated;
        public event EventHandler<FileDeletedEventArgs> FileDeleted;
        public event EventHandler<FileRenamedEventArgs> FileRenamed;
        public event EventHandler<FileChangedEventArgs> FileChanged;

        public RootWatcher(string rootPath)
        {
            _directoryWatcher = new FileSystemWatcher(rootPath)
            {
                EnableRaisingEvents = true,
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.DirectoryName
            };

            _directoryWatcher.Created += (sender, e) =>
            {
                DirectoryCreated?.Invoke(this, new DirectoryCreatedEventArgs(e.FullPath));
            };

            _directoryWatcher.Deleted += (sender, e) =>
            {
                DirectoryDeleted?.Invoke(this, new DirectoryDeletedEventArgs(e.FullPath));
            };

            _directoryWatcher.Renamed += (sender, e) =>
            {
                DirectoryRenamed?.Invoke(this, new DirectoryRenamedEventArgs(e.OldFullPath, e.FullPath));
            };

            _photoWatcher = new FileSystemWatcher(rootPath)
            {
                EnableRaisingEvents = true,
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                Filter = "*.png"
            };

            _photoWatcher.Created += (sender, e) =>
            {
                FileCreated?.Invoke(this, new FileCreatedEventArgs(e.FullPath));
            };

            _photoWatcher.Deleted += (sender, e) =>
            {
                FileDeleted?.Invoke(this, new FileDeletedEventArgs(e.FullPath));
            };

            _photoWatcher.Renamed += (sender, e) =>
            {
                FileRenamed?.Invoke(this, new FileRenamedEventArgs(e.OldFullPath, e.FullPath));
            };

            _photoWatcher.Changed += (sender, e) =>
            {
                FileChanged?.Invoke(this, new FileChangedEventArgs(e.FullPath));
            };
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                _directoryWatcher.Dispose();
                _photoWatcher.Dispose();
            }

            _disposed = true;
        }

        ~RootWatcher()
        {
            Dispose(false);
        }
    }

    public class DirectoryCreatedEventArgs(string path) : EventArgs
    {
        public string Path { get; } = path;
    }

    public class DirectoryDeletedEventArgs(string path) : EventArgs
    {
        public string Path { get; } = path;
    }

    public class DirectoryRenamedEventArgs(string previousPath, string newPath) : EventArgs
    {
        public string PreviousPath { get; } = previousPath;
        public string NewPath { get; } = newPath;
    }

    public class FileCreatedEventArgs(string path) : EventArgs
    {
        public string Path { get; } = path;
    }

    public class FileDeletedEventArgs(string path) : EventArgs
    {
        public string Path { get; } = path;
    }

    public class FileRenamedEventArgs(string previousPath, string newPath) : EventArgs
    {
        public string PreviousPath { get; } = previousPath;
        public string NewPath { get; } = newPath;
    }

    public class FileChangedEventArgs(string path) : EventArgs
    {
        public string Path { get; } = path;
    }
}
