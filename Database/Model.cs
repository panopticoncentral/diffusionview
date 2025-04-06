using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DiffusionView.Database;

public sealed class Model
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public required long ModelVersionId { get; set; }

    [MaxLength(260)]
    public required string ModelVersionName { get; set; }

    public required long ModelId { get; set; }

    [MaxLength(260)]
    public required string ModelName { get; set; }

    [MaxLength(32)]
    public required string Kind { get; set; }
}