using static DiffusionView.MainWindow;
using System;

namespace DiffusionView;

internal record PhotoMetadata
{
    public DateTime? DateTaken { get; init; }
    public ulong FileSize { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}