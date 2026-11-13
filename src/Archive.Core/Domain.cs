namespace Archive.Core;

// ---------------------------------------------------------------------------
// The data model, in plain C# classes ("POCOs"). EF Core maps these to tables
// in Archive.Api/Data/ArchiveDbContext.cs, so this file knows nothing about SQL.
//
//   Project 1 ──< Asset 1 ──< AssetVersion
//
// A Project is "Whelmer". An Asset is one logical file inside it, e.g.
// "design/whelmer.fig". Every time you upload a changed copy of that file a new
// AssetVersion row is added. Versions are never overwritten (append-only), which
// is how you get history and can't destroy old work by accident.
// ---------------------------------------------------------------------------

public enum ProjectCategory { InteractiveDevelopment, UxDesign, Screenwriting, Personal, Other }

public enum Visibility { Private, Public }

public enum AssetKind { Design, Document, Image, Video, Audio, Code, Archive, Font, Other }

/// <summary>Original = the source file. Preview = an export for showing off. Cover = the portfolio thumbnail.</summary>
public enum AssetRole { Original, Preview, Cover }

/// <summary>
/// Lifecycle of one stored copy:
/// Pending  -> API has handed out an upload URL, bytes not confirmed yet.
/// Stored   -> object exists in the bucket with the right size.
/// Verified -> object was re-read and its SHA-256 matched (fixity check).
/// Corrupt  -> object was re-read and the hash did NOT match. Restore from replica.
/// Missing  -> object could not be found in the bucket at all.
/// </summary>
public enum VersionStatus { Pending, Stored, Verified, Corrupt, Missing }

public class Project
{
    // Guid v7 is time-ordered, so new rows land at the end of the index instead of at
    // random places (a v4 Guid is random). Same uniqueness, kinder to the database.
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public int? Year { get; set; }
    public ProjectCategory Category { get; set; } = ProjectCategory.Other;
    public string? Summary { get; set; }
    public Visibility Visibility { get; set; } = Visibility.Private;
    public string[] Tags { get; set; } = [];

    // Mirrors the portfolio's existing data shape: dev projects get repo/live links,
    // UX projects get a prototype link.
    public string? RepoUrl { get; set; }
    public string? LiveUrl { get; set; }
    public string? PrototypeUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Soft delete: the row stays, it is just hidden. Nothing is ever really lost by one bad command.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public List<Asset> Assets { get; set; } = [];
}

public class Asset
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>Logical path inside the project, e.g. "design/whelmer.fig". Unique per project.</summary>
    public required string Path { get; set; }
    public AssetKind Kind { get; set; }
    public AssetRole Role { get; set; } = AssetRole.Original;
    public Visibility Visibility { get; set; } = Visibility.Private;
    public required string ContentType { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }

    public List<AssetVersion> Versions { get; set; } = [];
}

public class AssetVersion
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid AssetId { get; set; }
    public Asset? Asset { get; set; }

    /// <summary>1, 2, 3... per asset.</summary>
    public int Number { get; set; }
    public required string OriginalFileName { get; set; }

    /// <summary>Size of the bytes actually in the bucket (ciphertext if encrypted).</summary>
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the original file. Identifies the content; used to skip re-uploading unchanged files.</summary>
    public required string Sha256Plain { get; set; }

    /// <summary>SHA-256 of the bytes in the bucket. Used for fixity checks and as the storage key.</summary>
    public required string Sha256Stored { get; set; }

    public bool Encrypted { get; set; }

    /// <summary>Which master key wrapped this file's data key (a fingerprint, never the key itself).</summary>
    public string? KeyId { get; set; }

    public required string StorageKey { get; set; }
    public VersionStatus Status { get; set; } = VersionStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StoredAt { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }

    public bool IsAvailable => Status is VersionStatus.Stored or VersionStatus.Verified;
}

public class ApiKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }

    /// <summary>First few characters, safe to show in logs/UI so you can tell keys apart.</summary>
    public required string Prefix { get; set; }

    /// <summary>SHA-256 of the full key. The key itself is never stored.</summary>
    public required string HashHex { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Append-only record of every write: who did what, when. Answers "how did this get here?" years later.</summary>
public class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string Target { get; set; }
    public string? Detail { get; set; }
}
