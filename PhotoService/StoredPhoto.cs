using System.ComponentModel.DataAnnotations;
using System;

namespace DiffusionView.PhotoService;

public sealed class StoredPhoto
{
    [Key]
    public int Id { get; set; }
    public required string FilePath { get; set; }
    public required string FileName { get; set; }
    public int FolderId { get; set; }
    public DateTime? DateTaken { get; set; }
    public ulong FileSize { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[]? ThumbnailData { get; set; }
    public DateTime LastModified { get; set; }
}