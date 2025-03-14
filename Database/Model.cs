﻿using System.ComponentModel.DataAnnotations;

namespace DiffusionView.Database;

public sealed class Model
{
    [Key]
    public long Hash { get; set; }

    [MaxLength(260)]
    public required string Name { get; set; }

    [MaxLength(260)]
    public required string Version { get; set; }
}