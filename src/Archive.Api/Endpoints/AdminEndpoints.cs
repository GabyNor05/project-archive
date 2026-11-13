using Archive.Api.Data;
using Archive.Api.Services;
using Archive.Api.Storage;
using Archive.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Api.Endpoints;

public sealed class UploadOptions
{
    /// <summary>A single S3 PUT tops out at 5 GiB. Bigger files need multipart upload (see docs/ROADMAP.md).</summary>
    public long MaxSingleUploadBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>Private files must arrive already encrypted by the client. Defence in depth.</summary>
    public bool RequireEncryptionForPrivate { get; set; } = true;

    /// <summary>Files up to this size are hash-checked immediately on completion; bigger ones wait for the fixity job.</summary>
    public long VerifyOnCompleteMaxBytes { get; set; } = 200L * 1024 * 1024;
}

/// <summary>
/// Write + private-read endpoints, used by the uploader CLI. Every route needs an API key.
/// Upload is a 3-step handshake:
///   1. POST /uploads           -> API records a Pending version, returns a signed PUT URL
///   2. PUT  (signed URL)       -> client sends the bytes STRAIGHT to storage
///   3. POST /uploads/{id}/complete -> API checks the object exists and matches, marks it Stored
/// The database never claims a file is safe until step 3 has proven it.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/v1/admin")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimits.Admin)
            .WithTags("Admin");

        g.MapGet("/status", GetStatus);
        g.MapGet("/projects", ListProjects);
        g.MapGet("/projects/{slug}", GetProject);
        g.MapPut("/projects/{slug}", UpsertProject);
        g.MapDelete("/projects/{slug}", DeleteProject);
        g.MapPost("/uploads", StartUpload);
        g.MapPost("/uploads/{versionId:guid}/complete", CompleteUpload);
        g.MapGet("/versions/{versionId:guid}/download", Download);
        g.MapPost("/versions/{versionId:guid}/verify", VerifyNow);
        g.MapDelete("/assets/{assetId:guid}", DeleteAsset);
        g.MapPost("/manifest", ExportManifest);
    }

    // ---------------------------------------------------------------- projects

    private static async Task<IResult> ListProjects(ArchiveDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Projects.AsNoTracking().Where(p => p.DeletedAt == null)
            .OrderBy(p => p.Slug)
            .Select(p => new { p.Slug, p.Title, p.Year, p.Category, p.Visibility, AssetCount = p.Assets.Count(a => a.DeletedAt == null) })
            .ToListAsync(ct));

    private static async Task<IResult> GetProject(ArchiveDbContext db, string slug, CancellationToken ct)
    {
        var p = await db.Projects.AsNoTracking()
            .Where(x => x.Slug == slug && x.DeletedAt == null)
            .Include(x => x.Assets.Where(a => a.DeletedAt == null).OrderBy(a => a.Path))
                .ThenInclude(a => a.Versions.OrderBy(v => v.Number))
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);
        if (p is null) return Results.NotFound();

        return Results.Ok(new AdminProject(p.Slug, p.Title, p.Year, p.Category, p.Visibility, p.Summary, p.Tags,
            p.RepoUrl, p.LiveUrl, p.PrototypeUrl,
            p.Assets.Select(a => new AdminAsset(a.Id, a.Path, a.Kind, a.Role, a.Visibility, a.ContentType,
                a.Versions.Select(v => new AdminVersion(v.Id, v.Number, v.OriginalFileName, v.SizeBytes,
                    v.Sha256Plain, v.Sha256Stored, v.Encrypted, v.KeyId, v.Status, v.CreatedAt, v.LastVerifiedAt))
                    .ToList()))
                .ToList()));
    }

    /// <summary>PUT = idempotent "create or replace". Running the same command twice gives the same result.</summary>
    private static async Task<IResult> UpsertProject(ArchiveDbContext db, AuditLog audit, string slug, UpsertProjectRequest req, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Slug.IsValid(slug)) errors["slug"] = ["Use lowercase letters, numbers and dashes, e.g. get-up-and-go."];
        if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Length > 200) errors["title"] = ["Title is required (max 200 chars)."];
        if (req.Year is < 1990 or > 2100) errors["year"] = ["Year looks wrong."];
        foreach (var (name, url) in new[] { ("repoUrl", req.RepoUrl), ("liveUrl", req.LiveUrl), ("prototypeUrl", req.PrototypeUrl) })
        {
            if (url is not null && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps))
                errors[name] = ["Must be an absolute https:// URL."];
        }
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var p = await db.Projects.FirstOrDefaultAsync(x => x.Slug == slug && x.DeletedAt == null, ct);
        var created = p is null;
        p ??= db.Projects.Add(new Project { Slug = slug, Title = req.Title }).Entity;

        p.Title = req.Title.Trim();
        p.Year = req.Year;
        p.Category = req.Category;
        p.Summary = req.Summary?.Trim();
        p.Visibility = req.Visibility;
        p.Tags = (req.Tags ?? []).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToArray();
        p.RepoUrl = req.RepoUrl;
        p.LiveUrl = req.LiveUrl;
        p.PrototypeUrl = req.PrototypeUrl;
        p.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Record(created ? "project.create" : "project.update", slug);
        await db.SaveChangesAsync(ct);
        return created ? Results.Created($"/v1/admin/projects/{slug}", new { slug }) : Results.Ok(new { slug });
    }

    private static async Task<IResult> DeleteProject(ArchiveDbContext db, AuditLog audit, string slug, CancellationToken ct)
    {
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Slug == slug && x.DeletedAt == null, ct);
        if (p is null) return Results.NotFound();
        p.DeletedAt = DateTimeOffset.UtcNow; // soft delete: files and rows stay, see docs/RUNBOOK.md to purge
        audit.Record("project.delete", slug);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- uploads

    private static async Task<IResult> StartUpload(
        ArchiveDbContext db, IObjectStore store, AuditLog audit, IOptions<UploadOptions> options,
        StartUploadRequest req, CancellationToken ct)
    {
        var o = options.Value;
        var errors = new Dictionary<string, string[]>();
        if (!AssetPaths.TryNormalize(req.Path, out var path, out var pathError)) errors["path"] = [pathError!];
        if (!Sha256Hex.IsValid(req.Sha256Plain)) errors["sha256Plain"] = ["Lowercase 64-char hex SHA-256 required."];
        if (!Sha256Hex.IsValid(req.Sha256Stored)) errors["sha256Stored"] = ["Lowercase 64-char hex SHA-256 required."];
        if (req.SizeBytes < 0) errors["sizeBytes"] = ["Size can't be negative."];
        if (req.SizeBytes > o.MaxSingleUploadBytes) errors["sizeBytes"] = ["Too big for a single upload (5 GiB). Split or zip it, multipart is on the roadmap."];
        if (string.IsNullOrWhiteSpace(req.OriginalFileName) || req.OriginalFileName.Length > 255) errors["originalFileName"] = ["Required, max 255 chars."];
        if (req.Visibility == Visibility.Public && req.Encrypted) errors["encrypted"] = ["Public files must not be encrypted, or the portfolio couldn't show them."];
        if (req.Visibility == Visibility.Private && !req.Encrypted && o.RequireEncryptionForPrivate) errors["encrypted"] = ["Private files must be encrypted client-side."];
        if (req.Encrypted && string.IsNullOrWhiteSpace(req.KeyId)) errors["keyId"] = ["Encrypted files need the master key's id."];
        if (!req.Encrypted && req.Sha256Plain != req.Sha256Stored) errors["sha256Stored"] = ["Unencrypted files are stored as-is, so both hashes must match."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == req.ProjectSlug && p.DeletedAt == null, ct);
        if (project is null) return Results.Problem($"Project '{req.ProjectSlug}' doesn't exist. Create it first.", statusCode: 404);

        var asset = await db.Assets.Include(a => a.Versions)
            .FirstOrDefaultAsync(a => a.ProjectId == project.Id && a.Path == path && a.DeletedAt == null, ct);

        if (asset is null)
        {
            var (kind, contentType) = FileKinds.Detect(req.OriginalFileName);
            asset = db.Assets.Add(new Asset
            {
                ProjectId = project.Id, Path = path, Kind = kind, ContentType = contentType,
                Role = req.Role, Visibility = req.Visibility,
            }).Entity;
        }
        else
        {
            asset.Role = req.Role;
            asset.Visibility = req.Visibility;

            // Same content as the newest good version? Then there's nothing to do.
            var latest = asset.Versions.Where(v => v.IsAvailable).MaxBy(v => v.Number);
            if (latest is not null && latest.Sha256Plain == req.Sha256Plain && latest.Encrypted == req.Encrypted)
            {
                await db.SaveChangesAsync(ct); // persist any role/visibility change
                return Results.Ok(new StartUploadResponse("unchanged", latest.Id, latest.Number, null, null, null));
            }
        }

        var version = new AssetVersion
        {
            AssetId = asset.Id,
            Number = asset.Versions.Count == 0 ? 1 : asset.Versions.Max(v => v.Number) + 1,
            OriginalFileName = req.OriginalFileName,
            SizeBytes = req.SizeBytes,
            Sha256Plain = req.Sha256Plain,
            Sha256Stored = req.Sha256Stored,
            Encrypted = req.Encrypted,
            KeyId = req.KeyId,
            StorageKey = StorageKeys.ForObject(req.Sha256Stored),
        };
        asset.Versions.Add(version);

        // Content addressing pays off: if these exact bytes are already safely stored
        // (e.g. the same cover image in two projects), point at them, skip the upload.
        var alreadyStored = await db.AssetVersions.AnyAsync(v =>
            v.Sha256Stored == req.Sha256Stored &&
            (v.Status == VersionStatus.Stored || v.Status == VersionStatus.Verified), ct);
        if (alreadyStored)
        {
            version.Status = VersionStatus.Stored;
            version.StoredAt = DateTimeOffset.UtcNow;
        }

        audit.Record("upload.start", $"{project.Slug}/{path}", $"v{version.Number} {req.SizeBytes} bytes{(alreadyStored ? " (dedup)" : "")}");

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Unique index on (AssetId, Number) caught a race between two uploads of the same file.
            return Results.Problem("Another upload of this file is in progress. Try again.", statusCode: 409);
        }

        if (alreadyStored)
            return Results.Ok(new StartUploadResponse("deduplicated", version.Id, version.Number, null, null, null));

        const string uploadContentType = "application/octet-stream"; // bytes are opaque to storage (often ciphertext)
        var ttl = TimeSpan.FromMinutes(30);
        var url = await store.PresignPutAsync(version.StorageKey, uploadContentType, ttl);
        return Results.Ok(new StartUploadResponse("upload", version.Id, version.Number, url.ToString(),
            new Dictionary<string, string> { ["Content-Type"] = uploadContentType }, DateTimeOffset.UtcNow.Add(ttl)));
    }

    private static async Task<IResult> CompleteUpload(
        ArchiveDbContext db, IObjectStore store, FixityChecker fixity, AuditLog audit, IOptions<UploadOptions> options,
        Guid versionId, CancellationToken ct)
    {
        var version = await db.AssetVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null) return Results.NotFound();
        if (version.Status != VersionStatus.Pending) return Results.Ok(new { version.Id, version.Status });

        var size = await store.GetSizeAsync(version.StorageKey, ct);
        if (size is null) return Results.Problem("No object found. Upload the bytes first, then call complete.", statusCode: 409);
        if (size != version.SizeBytes)
            return Results.Problem($"Size mismatch: expected {version.SizeBytes}, storage has {size}. Re-upload.", statusCode: 422);

        version.Status = VersionStatus.Stored;
        version.StoredAt = DateTimeOffset.UtcNow;

        // Small files: prove the hash right now. Big ones: the nightly fixity job will.
        if (version.SizeBytes <= options.Value.VerifyOnCompleteMaxBytes)
        {
            await fixity.CheckAsync(version, ct);
            if (version.Status == VersionStatus.Corrupt)
            {
                version.Status = VersionStatus.Pending; // let the client retry the PUT
                await db.SaveChangesAsync(ct);
                return Results.Problem("Hash mismatch: the stored bytes aren't what you said you'd send. Re-upload.", statusCode: 422);
            }
        }

        audit.Record("upload.complete", version.StorageKey, version.Status.ToString());
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { version.Id, version.Status });
    }

    // ---------------------------------------------------------------- reading / maintenance

    private static async Task<IResult> Download(ArchiveDbContext db, IObjectStore store, AuditLog audit, Guid versionId, CancellationToken ct)
    {
        var v = await db.AssetVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null || !v.IsAvailable) return Results.NotFound();

        var url = await store.PresignGetAsync(v.StorageKey, TimeSpan.FromMinutes(30), v.OriginalFileName);
        audit.Record("version.download", v.StorageKey);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new DownloadResponse(url.ToString(), v.OriginalFileName, v.Encrypted, v.KeyId,
            v.Sha256Stored, v.Sha256Plain, v.SizeBytes));
    }

    private static async Task<IResult> VerifyNow(ArchiveDbContext db, FixityChecker fixity, Guid versionId, CancellationToken ct)
    {
        var v = await db.AssetVersions.FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null) return Results.NotFound();
        await fixity.CheckAsync(v, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { v.Id, v.Status, v.LastVerifiedAt });
    }

    private static async Task<IResult> DeleteAsset(ArchiveDbContext db, AuditLog audit, Guid assetId, CancellationToken ct)
    {
        var a = await db.Assets.FirstOrDefaultAsync(x => x.Id == assetId && x.DeletedAt == null, ct);
        if (a is null) return Results.NotFound();
        a.DeletedAt = DateTimeOffset.UtcNow;
        audit.Record("asset.delete", a.Path);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ExportManifest(ManifestExporter exporter, ArchiveDbContext db, AuditLog audit, CancellationToken ct)
    {
        var count = await exporter.ExportAsync(ct);
        audit.Record("manifest.export", StorageKeys.ManifestPrefix + "index.json", $"{count} projects");
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { projects = count });
    }

    private static async Task<IResult> GetStatus(ArchiveDbContext db, CancellationToken ct)
    {
        var byStatus = await db.AssetVersions.GroupBy(v => v.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(x.Status.ToString()), x => x.Count, ct);

        return Results.Ok(new ArchiveStatus(
            Projects: await db.Projects.CountAsync(p => p.DeletedAt == null, ct),
            Assets: await db.Assets.CountAsync(a => a.DeletedAt == null, ct),
            TotalStoredBytes: await db.AssetVersions.Where(v => v.Status != VersionStatus.Pending).SumAsync(v => v.SizeBytes, ct),
            VersionsByStatus: byStatus,
            OldestVerification: await db.AssetVersions.Where(v => v.LastVerifiedAt != null).MinAsync(v => v.LastVerifiedAt, ct)));
    }
}
