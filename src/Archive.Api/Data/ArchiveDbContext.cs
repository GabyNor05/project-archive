using Archive.Core;
using Microsoft.EntityFrameworkCore;

namespace Archive.Api.Data;

/// <summary>
/// The EF Core "unit of work": tracks changes to entities and turns them into SQL.
/// All table rules live in OnModelCreating, and every change to them becomes a
/// migration (a versioned, reviewable C# file in Data/Migrations, committed to git).
/// </summary>
public sealed class ArchiveDbContext(DbContextOptions<ArchiveDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetVersion> AssetVersions => Set<AssetVersion>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Store enums as readable text ("UxDesign") instead of numbers (1).
        // A raw SQL query or a pg_dump read in 2036 still makes sense, and
        // re-ordering the enum in C# can't silently change the meaning of old rows.
        b.Properties<ProjectCategory>().HaveConversion<string>().HaveMaxLength(40);
        b.Properties<Visibility>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<AssetKind>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<AssetRole>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<VersionStatus>().HaveConversion<string>().HaveMaxLength(20);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Project>(e =>
        {
            e.Property(p => p.Slug).HasMaxLength(80);
            e.Property(p => p.Title).HasMaxLength(200);
            e.Property(p => p.Summary).HasMaxLength(4000);
            e.Property(p => p.RepoUrl).HasMaxLength(500);
            e.Property(p => p.LiveUrl).HasMaxLength(500);
            e.Property(p => p.PrototypeUrl).HasMaxLength(500);
            // Slugs are unique among live projects; a soft-deleted project frees its slug.
            e.HasIndex(p => p.Slug).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            // Tags are a native PostgreSQL text[] array; a GIN index makes "has tag X" fast.
            e.HasIndex(p => p.Tags).HasMethod("gin");
        });

        b.Entity<Asset>(e =>
        {
            e.Property(a => a.Path).HasMaxLength(AssetPaths.MaxLength);
            e.Property(a => a.ContentType).HasMaxLength(200);
            e.HasOne(a => a.Project).WithMany(p => p.Assets)
                .HasForeignKey(a => a.ProjectId)
                .OnDelete(DeleteBehavior.Restrict); // never cascade-delete history
            e.HasIndex(a => new { a.ProjectId, a.Path }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
        });

        b.Entity<AssetVersion>(e =>
        {
            e.Property(v => v.OriginalFileName).HasMaxLength(255);
            e.Property(v => v.Sha256Plain).HasMaxLength(64).IsFixedLength();
            e.Property(v => v.Sha256Stored).HasMaxLength(64).IsFixedLength();
            e.Property(v => v.KeyId).HasMaxLength(32);
            e.Property(v => v.StorageKey).HasMaxLength(200);
            e.HasOne(v => v.Asset).WithMany(a => a.Versions)
                .HasForeignKey(v => v.AssetId)
                .OnDelete(DeleteBehavior.Restrict);
            // The database itself refuses two "version 3"s for one asset, even if two
            // uploads race each other. Rules in the DB beat rules only in code.
            e.HasIndex(v => new { v.AssetId, v.Number }).IsUnique();
            e.HasIndex(v => v.Sha256Stored);
            e.HasIndex(v => new { v.Status, v.LastVerifiedAt }); // what the fixity job scans
            e.Ignore(v => v.IsAvailable);
        });

        b.Entity<ApiKey>(e =>
        {
            e.Property(k => k.Name).HasMaxLength(100);
            e.Property(k => k.Prefix).HasMaxLength(16);
            e.Property(k => k.HashHex).HasMaxLength(64).IsFixedLength();
            e.HasIndex(k => k.HashHex).IsUnique();
        });

        b.Entity<AuditEvent>(e =>
        {
            e.Property(a => a.Actor).HasMaxLength(100);
            e.Property(a => a.Action).HasMaxLength(60);
            e.Property(a => a.Target).HasMaxLength(600);
            e.Property(a => a.Detail).HasMaxLength(2000);
            e.HasIndex(a => a.At);
        });
    }
}
