using Microsoft.EntityFrameworkCore;
using System.IO;
using System;

namespace DiffusionView.Database;

public sealed partial class PhotoDatabase : DbContext
{
    public DbSet<Folder> Folders { get; set; }
    public DbSet<Photo> Photos { get; set; }

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
        modelBuilder.Entity<Folder>()
            .HasIndex(f => f.Path)
            .IsUnique();

        modelBuilder.Entity<Photo>()
            .HasIndex(p => p.Path)
            .IsUnique();
    }
}
