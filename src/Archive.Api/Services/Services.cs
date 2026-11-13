using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Archive.Api.Data;
using Archive.Api.Storage;
using Archive.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Api.Services;

/// <summary>Adds an audit row to the current unit of work; it is saved with the change it describes.</summary>
public sealed class AuditLog(ArchiveDbContext db, IHttpContextAccessor http)
{
    public void Record(string action, string target, string? detail = null)
    {
        var actor = http.HttpContext?.User.FindFirstValue(ClaimTypes.Name) ?? "system";
        db.AuditEvents.Add(new AuditEvent { Actor = actor, Action = action, Target = target, Detail = detail });
    }
}

/// <summary>
/// "Fixity" is the digital-preservation word for "is this file still exactly what we
/// stored?". Disks and providers can silently corrupt data (bit rot). The only way
/// to know is to re-read the bytes and re-hash them.
/// </summary>
public sealed class FixityChecker(IObjectStore store, ILogger<FixityChecker> log)
{
    public async Task CheckAsync(AssetVersion version, CancellationToken ct)
    {
        var size = await store.GetSizeAsync(version.StorageKey, ct);
        if (size is null)
        {
            version.Status = VersionStatus.Missing;
            log.LogError("Fixity: object {Key} for version {Id} is MISSING", version.StorageKey, version.Id);
        }
        else
        {
            await using var stream = await store.OpenReadAsync(version.StorageKey, ct);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20]; // 1 MB at a time: constant memory, any file size
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                hasher.AppendData(buffer, 0, read);

            var actual = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (actual == version.Sha256Stored && size == version.SizeBytes)
            {
                version.Status = VersionStatus.Verified;
            }
            else
            {
                version.Status = VersionStatus.Corrupt;
                log.LogError("Fixity: object {Key} is CORRUPT (expected {Expected}, got {Actual})",
                    version.StorageKey, version.Sha256Stored, actual);
            }
        }
        version.LastVerifiedAt = DateTimeOffset.UtcNow;
    }
}

public sealed class FixityOptions
{
    public bool Enabled { get; set; } = true;
    public double IntervalHours { get; set; } = 24;
    public int BatchSize { get; set; } = 50;
    public int ReverifyAfterDays { get; set; } = 30;
}

/// <summary>
/// Background job: every N hours, re-verify the files that were checked longest ago.
/// Small batches keep each run cheap; over a month every file gets checked.
/// </summary>
public sealed class FixityService(IServiceScopeFactory scopes, IOptions<FixityOptions> options, ILogger<FixityService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled) { log.LogInformation("Fixity checks disabled."); return; }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(o.IntervalHours));
        do
        {
            try { await RunOnceAsync(o, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Fixity run failed; will retry next interval.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(FixityOptions o, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
        var checker = scope.ServiceProvider.GetRequiredService<FixityChecker>();

        var cutoff = DateTimeOffset.UtcNow.AddDays(-o.ReverifyAfterDays);
        var due = await db.AssetVersions
            .Where(v => (v.Status == VersionStatus.Stored || v.Status == VersionStatus.Verified)
                        && (v.LastVerifiedAt == null || v.LastVerifiedAt < cutoff))
            .OrderBy(v => v.LastVerifiedAt.HasValue) // never-verified first (false sorts before true)
            .ThenBy(v => v.LastVerifiedAt)
            .Take(o.BatchSize)
            .ToListAsync(ct);

        foreach (var version in due)
        {
            await checker.CheckAsync(version, ct);
            db.AuditEvents.Add(new AuditEvent
            {
                Actor = "fixity", Action = "version.verify",
                Target = version.StorageKey, Detail = version.Status.ToString(),
            });
            await db.SaveChangesAsync(ct); // save as we go, so a crash mid-batch loses nothing
        }
        if (due.Count > 0) log.LogInformation("Fixity: checked {Count} object(s).", due.Count);
    }
}

/// <summary>Writes manifest/index.json + manifest/README.txt into the bucket. See Archive.Core/Manifest.cs for why.</summary>
public sealed class ManifestExporter(
    ArchiveDbContext db, IObjectStore store, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions)
{
    private readonly JsonSerializerOptions json = jsonOptions.Value.SerializerOptions;

    public async Task<int> ExportAsync(CancellationToken ct)
    {
        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Include(p => p.Assets.Where(a => a.DeletedAt == null))
                .ThenInclude(a => a.Versions)
            .AsSplitQuery()
            .OrderBy(p => p.Slug)
            .ToListAsync(ct);

        var doc = new ManifestDocument(
            FormatVersion: 1,
            GeneratedAt: DateTimeOffset.UtcNow,
            Generator: "project-archive api",
            Projects: projects.Select(p => new ManifestProject(
                p.Slug, p.Title, p.Year, p.Category, p.Visibility, p.Summary, p.Tags,
                p.RepoUrl, p.LiveUrl, p.PrototypeUrl,
                p.Assets.OrderBy(a => a.Path).Select(a => new ManifestAsset(
                    a.Path, a.Kind, a.Role, a.Visibility, a.ContentType,
                    a.Versions.Where(v => v.Status != VersionStatus.Pending).OrderBy(v => v.Number)
                        .Select(v => new ManifestVersion(
                            v.Number, v.OriginalFileName, v.StorageKey, v.SizeBytes,
                            v.Sha256Plain, v.Sha256Stored, v.Encrypted, v.KeyId, v.Status,
                            v.CreatedAt, v.LastVerifiedAt))
                        .ToList()))
                    .ToList()))
                .ToList());

        var indented = new JsonSerializerOptions(json) { WriteIndented = true };
        await store.PutTextAsync(StorageKeys.ManifestPrefix + "index.json",
            JsonSerializer.Serialize(doc, indented), "application/json", ct);
        await store.PutTextAsync(StorageKeys.ManifestPrefix + "README.txt",
            ManifestReadme.Text, "text/plain; charset=utf-8", ct);
        return projects.Count;
    }
}
