using System.ComponentModel.DataAnnotations;

namespace DiffusionView.Database;

public sealed class Model
{
    [Key]
    public required long ModelVersionId { get; set; }

    [MaxLength(260)]
    public required string ModelVersionName { get; set; }

    public required long ModelId { get; set; }

    [MaxLength(260)]
    public required string ModelName { get; set; }
}