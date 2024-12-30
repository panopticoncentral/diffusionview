using System.ComponentModel.DataAnnotations;
using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace DiffusionView.Database;

public sealed class Photo
{
    [Key]
    public int Id { get; set; }
    [MaxLength(260)]
    public required string Path { get; set; }
    [MaxLength(260)]
    public required string Name { get; set; }
    [ForeignKey("Folder")]
    public int FolderId { get; set; }
    public DateTime? DateTaken { get; set; }
    public ulong FileSize { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] ThumbnailData { get; set; }
    public DateTime LastModified { get; set; }
}