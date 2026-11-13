using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Archive.Api.Data;
using Archive.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Archive.Api.Auth;

/// <summary>
/// API keys for the uploader CLI.
///
/// Why SHA-256 here, when passwords need slow hashes like Argon2/bcrypt?
/// A password is low-entropy (a human picked it), so attackers who steal the hash
/// can guess billions of candidates; a slow hash makes each guess expensive.
/// These keys are 256 bits of pure randomness, so there is nothing to guess; a fast
/// hash is enough, and it lets us look the key up directly by its hash.
///
/// Why hash at all? If the database leaks (a backup in the wrong place), the
/// attacker gets hashes, not working keys.
/// </summary>
public static class ApiKeyAuth
{
    public const string Scheme = "ApiKey";
    public const string Header = "X-Api-Key";
    public const string KeyPrefix = "pa_"; // recognisable prefix: easy to spot if one ever leaks into a repo

    public static string NewKey() =>
        KeyPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static bool LooksValid(string? key) =>
        key is { Length: > 20 and < 100 } && key.StartsWith(KeyPrefix, StringComparison.Ordinal);

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>`dotnet run -- create-key laptop` prints a new key ONCE. Only its hash is saved.</summary>
    public static async Task CreateKeyAsync(IServiceProvider services, string name)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
        var key = NewKey();
        db.ApiKeys.Add(new ApiKey { Name = name, Prefix = key[..10], HashHex = Hash(key) });
        db.AuditEvents.Add(new AuditEvent { Actor = "console", Action = "api-key.create", Target = name });
        await db.SaveChangesAsync();
        Console.WriteLine($"API key for '{name}' (shown once, store it in your password manager):");
        Console.WriteLine(key);
    }

    public static async Task RevokeKeyAsync(IServiceProvider services, string name)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
        var count = await db.ApiKeys.Where(k => k.Name == name && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, DateTimeOffset.UtcNow));
        db.AuditEvents.Add(new AuditEvent { Actor = "console", Action = "api-key.revoke", Target = name });
        await db.SaveChangesAsync();
        Console.WriteLine($"Revoked {count} key(s) named '{name}'.");
    }
}

public sealed class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ArchiveDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuth.Header, out var values))
            return AuthenticateResult.NoResult();

        var presented = values.ToString();
        if (!ApiKeyAuth.LooksValid(presented))
            return AuthenticateResult.Fail("Malformed API key.");

        // We look the key up BY its hash. There is no "compare the secret character by
        // character" step, so there is no timing side-channel to leak how much matched.
        var hash = ApiKeyAuth.Hash(presented);
        var key = await db.ApiKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.HashHex == hash && k.RevokedAt == null, Context.RequestAborted);
        if (key is null)
            return AuthenticateResult.Fail("Invalid or revoked API key.");

        await db.ApiKeys.Where(k => k.Id == key.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, DateTimeOffset.UtcNow), Context.RequestAborted);

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, key.Name), new Claim("key_id", key.Id.ToString())], ApiKeyAuth.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), ApiKeyAuth.Scheme));
    }
}
