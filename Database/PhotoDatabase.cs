﻿using Microsoft.EntityFrameworkCore;
using System.IO;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DiffusionView.Database;

public sealed partial class PhotoDatabase : DbContext
{
    public DbSet<Folder> Folders { get; set; }
    public DbSet<Photo> Photos { get; set; }
    public DbSet<Model> Models { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiffusionView"
        );

        var x = Directory.CreateDirectory(appDataPath);

        var dbPath = Path.Combine(appDataPath, "photos.db");
        options.UseSqlite($"Data Source={dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var serializerOptions = new JsonSerializerOptions();

        modelBuilder.Entity<Photo>()
            .Property(p => p.OtherParameters)
            .HasConversion(
                v => JsonSerializer.Serialize(v, serializerOptions),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, serializerOptions)
            );

        modelBuilder.Entity<ModelInstance>()
            .HasKey(l => new { l.Path, l.ModelVersionId });

        modelBuilder.Entity<ModelInstance>()
            .HasOne(l => l.Photo)
            .WithMany(p => p.Models)
            .HasForeignKey(l => l.Path);

        modelBuilder.Entity<ModelInstance>()
            .HasOne(l => l.Model)
            .WithMany()
            .HasForeignKey(l => l.ModelVersionId);
    }
}