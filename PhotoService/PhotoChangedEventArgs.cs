using System;

namespace DiffusionView.PhotoService;

public sealed class PhotoChangedEventArgs(StoredPhoto photo) : EventArgs
{
    public StoredPhoto Photo { get; } = photo;
}