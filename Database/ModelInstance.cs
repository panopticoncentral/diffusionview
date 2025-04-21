using System.ComponentModel.DataAnnotations;

namespace DiffusionView.Database;

public class ModelInstance
{
    [MaxLength(260)]
    public string Path { get; set; }

    public long ModelVersionId { get; set; }

    public Photo Photo { get; set; }
    public Model Model { get; set; }

    public double? Weight { get; set; }
}