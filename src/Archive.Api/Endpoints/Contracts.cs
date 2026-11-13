using Archive.Core;

namespace Archive.Api.Endpoints;

// ---------------------------------------------------------------------------
// DTOs ("data transfer objects"): the exact JSON shapes the API accepts and
// returns. They are separate from the database entities on purpose: you can
// rename a column without breaking the portfolio, and you can never accidentally
// leak a field (like StorageKey or KeyId) to the public just by adding it to an entity.
// ---------------------------------------------------------------------------

// ----- Public (portfolio) -----

public sealed record PublicProjectSummary(
    string Slug, string Title, int? Year, ProjectCategory Category, string? Summary,
    IReadOnlyList<string> Tags, string? RepoUrl, string? LiveUrl, string? PrototypeUrl, string? CoverUrl);

public sealed record PublicProjectDetail(
    string Slug, string Title, int? Year, ProjectCategory Category, string? Summary,
    IReadOnlyList<string> Tags, string? RepoUrl, string? LiveUrl, string? PrototypeUrl,
    IReadOnlyList<PublicAsset> Assets);

public sealed record PublicAsset(Guid Id, string Path, AssetKind Kind, AssetRole Role, string ContentType, long SizeBytes, string Url);

// ----- Admin (uploader CLI) -----

public sealed record UpsertProjectRequest(
    string Title, int? Year, ProjectCategory Category, string? Summary, Visibility Visibility,
    string[]? Tags, string? RepoUrl, string? LiveUrl, string? PrototypeUrl);

public sealed record StartUploadRequest(
    string ProjectSlug,
    string Path,
    string OriginalFileName,
    long SizeBytes,
    string Sha256Plain,
    string Sha256Stored,
    bool Encrypted,
    string? KeyId,
    Visibility Visibility,
    AssetRole Role = AssetRole.Original);

/// <param name="Status">"upload" (PUT your bytes to UploadUrl), "unchanged" or "deduplicated" (nothing to send).</param>
public sealed record StartUploadResponse(
    string Status, Guid VersionId, int VersionNumber, string? UploadUrl,
    IReadOnlyDictionary<string, string>? UploadHeaders, DateTimeOffset? ExpiresAt);

public sealed record AdminVersion(
    Guid Id, int Number, string OriginalFileName, long SizeBytes, string Sha256Plain, string Sha256Stored,
    bool Encrypted, string? KeyId, VersionStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? LastVerifiedAt);

public sealed record AdminAsset(
    Guid Id, string Path, AssetKind Kind, AssetRole Role, Visibility Visibility, string ContentType,
    IReadOnlyList<AdminVersion> Versions);

public sealed record AdminProject(
    string Slug, string Title, int? Year, ProjectCategory Category, Visibility Visibility, string? Summary,
    IReadOnlyList<string> Tags, string? RepoUrl, string? LiveUrl, string? PrototypeUrl,
    IReadOnlyList<AdminAsset> Assets);

public sealed record DownloadResponse(
    string Url, string OriginalFileName, bool Encrypted, string? KeyId, string Sha256Stored, string Sha256Plain, long SizeBytes);

public sealed record ArchiveStatus(
    int Projects, int Assets, long TotalStoredBytes, IReadOnlyDictionary<string, int> VersionsByStatus,
    DateTimeOffset? OldestVerification);
