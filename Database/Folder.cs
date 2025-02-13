using System.ComponentModel.DataAnnotations;

namespace DiffusionView.Database;

public sealed class Folder
{
    [Key]
    public int Id { get; set; }
    [MaxLength(260)]
    public required string Path { get; set; }
}