using System.ComponentModel.DataAnnotations;

namespace DiffusionView.Database;

public sealed class Folder
{
    [Key]
    [MaxLength(260)]
    public required string Path { get; set; }
}