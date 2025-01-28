using System.ComponentModel.DataAnnotations;
using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

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

    public static BitmapImage CreateBitmapImage(byte[] thumbnailData)
    {
        if (thumbnailData == null)
            return null;

        var image = new BitmapImage();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        stream.WriteAsync(thumbnailData.AsBuffer()).GetResults();
        stream.Seek(0);
        image.SetSource(stream);
        return image;
    }
}