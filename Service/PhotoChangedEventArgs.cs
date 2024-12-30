using DiffusionView.Database;
using System;

namespace DiffusionView.Service;

public sealed class PhotoChangedEventArgs(Photo photo) : EventArgs
{
    public Photo Photo { get; } = photo;
}