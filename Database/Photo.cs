using System.ComponentModel.DataAnnotations;
using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Collections.Generic;

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
    [MaxLength(1024)]
    public string Prompt { get; set; }
    [MaxLength(1024)]
    public string NegativePrompt { get; set; }
    public int Steps { get; set; }
    public int GeneratedWidth { get; set; }
    public int GeneratedHeight { get; set; }
    [MaxLength(64)]
    public string Sampler { get; set; }
    public double CfgScale { get; set; }
    public long Seed { get; set; }
    [MaxLength(256)]
    public string Model { get; set; }
    public long ModelHash { get; set; }
    [MaxLength(64)]
    public string Version { get; set; }
    public Dictionary<string, string> OtherParameters { get; set; } = new();
    [MaxLength(1024)]
    public string Raw { get; set; }
}