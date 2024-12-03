using Microsoft.EntityFrameworkCore;
using System.IO;
using System;

namespace DiffusionView.PhotoService;

public sealed partial class PhotoDatabase : DbContext
{
    public DbSet<StoredFolder> Folders { get; set; }
    public DbSet<StoredPhoto> Photos { get; set; }

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
        modelBuilder.Entity<StoredFolder>()
            .HasIndex(f => f.Path)
            .IsUnique();

        modelBuilder.Entity<StoredPhoto>()
            .HasIndex(p => p.FilePath)
            .IsUnique();
    }
}
