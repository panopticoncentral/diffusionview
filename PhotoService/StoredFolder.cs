using System.ComponentModel.DataAnnotations;
using System;

namespace DiffusionView.PhotoService;

public sealed class StoredFolder
{
    [Key]
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Path { get; set; }
    public DateTime LastScanned { get; set; }
}