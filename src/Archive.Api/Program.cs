using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Archive.Api;
using Archive.Api.Auth;
using Archive.Api.Data;
using Archive.Api.Endpoints;
using Archive.Api.Services;
using Archive.Api.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

// Console commands share the app's config + database wiring:
//   dotnet run -- create-key <name>
//   dotnet run -- revoke-key <name>
string? cliCommand = null, cliArg = null;
if (args is [("create-key" or "revoke-key") and var c, var a]) { cliCommand = c; cliArg = a; }
var isCli = cliCommand is not null;

var builder = WebApplication.CreateBuilder(isCli ? [] : args);
var config = builder.Configuration;

// ---------- configuration (secrets come from env vars / user-secrets, never appsettings.json)
builder.Services.AddOptions<StorageOptions>()
    .Bind(config.GetSection("Storage"))
    .Validate(o => o.Endpoint.Length > 0 && o.Bucket.Length > 0 && o.AccessKeyId.Length > 0 && o.SecretAccessKey.Length > 0,
        "Storage settings are incomplete (Endpoint, Bucket, AccessKeyId, SecretAccessKey).")
    .ValidateOnStart(); // fail at boot, not at the first upload
builder.Services.Configure<UploadOptions>(config.GetSection("Uploads"));
builder.Services.Configure<FixityOptions>(config.GetSection("Fixity"));

// ---------- data + storage
builder.Services.AddDbContext<ArchiveDbContext>(o =>
    o.UseNpgsql(config.GetConnectionString("Archive")
                ?? throw new InvalidOperationException("ConnectionStrings:Archive is not set.")));
builder.Services.AddSingleton<IObjectStore, S3ObjectStore>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<FixityChecker>();
builder.Services.AddScoped<ManifestExporter>();
if (!isCli) builder.Services.AddHostedService<FixityService>();

// ---------- JSON: enums as "ux-design" style strings, both in and out
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
});

// ---------- security
builder.Services.AddAuthentication(ApiKeyAuth.Scheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(ApiKeyAuth.Scheme, null);
builder.Services.AddAuthorization();

// CORS: browsers will only let pages on these origins call the API from JavaScript.
var origins = config.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddPolicy(Cors.Portfolio, p => p.WithOrigins(origins).WithMethods("GET")));

// Rate limiting: caps requests per client IP, so a script (or a bot) can't hammer the API
// or run up the storage bill. Excess requests get HTTP 429 Too Many Requests.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(RateLimits.Public, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy(RateLimits.Admin, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddProblemDetails(); // errors come back as standard RFC 9457 JSON

var app = builder.Build();

// ---------- database migrations
// For a single-instance app, applying pending migrations at startup is simple and safe.
// (Multi-server systems run them as a separate deploy step instead, so two servers
// don't race to migrate the same database.)
if (config.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Database.MigrateAsync();
}

switch (cliCommand)
{
    case "create-key": await ApiKeyAuth.CreateKeyAsync(app.Services, cliArg!); return;
    case "revoke-key": await ApiKeyAuth.RevokeKeyAsync(app.Services, cliArg!); return;
}

// ---------- HTTP pipeline (order matters)
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPublicEndpoints();
app.MapAdminEndpoints();

app.Run();

namespace Archive.Api
{
    public static class Cors { public const string Portfolio = "portfolio"; }
    public static class RateLimits { public const string Public = "public"; public const string Admin = "admin"; }
}
