using Archive.Api.Data;
using Archive.Api.Storage;
using Archive.Core;
using Microsoft.EntityFrameworkCore;

namespace Archive.Api.Endpoints;

/// <summary>
/// Read-only, unauthenticated endpoints for the portfolio site.
/// Rule: these only ever return things marked Public that aren't encrypted or deleted.
/// Every filter below exists to enforce that rule.
/// </summary>
public static class PublicEndpoints
{
    public static void MapPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/v1")
            .RequireCors(Cors.Portfolio)
            .RequireRateLimiting(RateLimits.Public)
            .WithTags("Public");

        g.MapGet("/projects", ListProjects);
        g.MapGet("/projects/{slug}", GetProject);
        g.MapGet("/assets/{id:guid}/content", GetAssetContent);
    }

    private static string ContentUrl(Guid assetId) => $"/v1/assets/{assetId}/content";

    private static async Task<IResult> ListProjects(ArchiveDbContext db, string? category, string? tag, CancellationToken ct)
    {
        var q = db.Projects.AsNoTracking().Where(p => p.Visibility == Visibility.Public && p.DeletedAt == null);

        if (category is not null)
        {
            if (!EnumText.TryParse<ProjectCategory>(category, out var cat))
                return Results.Problem($"Unknown category '{category}'.", statusCode: 400);
            q = q.Where(p => p.Category == cat);
        }
        if (!string.IsNullOrWhiteSpace(tag)) q = q.Where(p => p.Tags.Contains(tag));

        var rows = await q
            .OrderByDescending(p => p.Year).ThenBy(p => p.Title)
            .Select(p => new
            {
                Project = p,
                CoverId = p.Assets
                    .Where(a => a.Role == AssetRole.Cover && a.Visibility == Visibility.Public && a.DeletedAt == null)
                    .Select(a => (Guid?)a.Id)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return Results.Ok(rows.Select(r => new PublicProjectSummary(
            r.Project.Slug, r.Project.Title, r.Project.Year, r.Project.Category, r.Project.Summary, r.Project.Tags,
            r.Project.RepoUrl, r.Project.LiveUrl, r.Project.PrototypeUrl,
            r.CoverId is { } id ? ContentUrl(id) : null)));
    }

    private static async Task<IResult> GetProject(ArchiveDbContext db, string slug, CancellationToken ct)
    {
        var p = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Slug == slug && x.Visibility == Visibility.Public && x.DeletedAt == null, ct);
        if (p is null) return Results.NotFound();

        var assets = await db.Assets.AsNoTracking()
            .Where(a => a.ProjectId == p.Id && a.Visibility == Visibility.Public && a.DeletedAt == null)
            .Select(a => new
            {
                Asset = a,
                Latest = a.Versions
                    .Where(v => v.Status == VersionStatus.Stored || v.Status == VersionStatus.Verified)
                    .OrderByDescending(v => v.Number)
                    .Select(v => new { v.SizeBytes, v.Encrypted })
                    .FirstOrDefault(),
            })
            .OrderBy(x => x.Asset.Path)
            .ToListAsync(ct);

        var visible = assets
            .Where(x => x.Latest is { Encrypted: false })
            .Select(x => new PublicAsset(x.Asset.Id, x.Asset.Path, x.Asset.Kind, x.Asset.Role,
                x.Asset.ContentType, x.Latest!.SizeBytes, ContentUrl(x.Asset.Id)))
            .ToList();

        return Results.Ok(new PublicProjectDetail(p.Slug, p.Title, p.Year, p.Category, p.Summary, p.Tags,
            p.RepoUrl, p.LiveUrl, p.PrototypeUrl, visible));
    }

    /// <summary>
    /// Doesn't stream the file itself: it redirects to a short-lived signed URL, so the
    /// bytes come straight from storage (faster, and no bandwidth through the API).
    /// </summary>
    private static async Task<IResult> GetAssetContent(ArchiveDbContext db, IObjectStore store, Guid id, CancellationToken ct)
    {
        var version = await db.AssetVersions.AsNoTracking()
            .Where(v => v.AssetId == id
                        && v.Asset!.Visibility == Visibility.Public && v.Asset.DeletedAt == null
                        && v.Asset.Project!.Visibility == Visibility.Public && v.Asset.Project.DeletedAt == null
                        && (v.Status == VersionStatus.Stored || v.Status == VersionStatus.Verified))
            .OrderByDescending(v => v.Number)
            .FirstOrDefaultAsync(ct);

        if (version is null || version.Encrypted) return Results.NotFound();

        var url = await store.PresignGetAsync(version.StorageKey, TimeSpan.FromMinutes(10));
        return Results.Redirect(url.ToString());
    }
}
