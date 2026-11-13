using System.Text;
using System.Text.RegularExpressions;

namespace Archive.Core;

/// <summary>
/// Content addressing: an object's key in the bucket IS its SHA-256 hash.
/// - Same bytes always get the same key, so duplicates are stored once.
/// - The key doubles as a checksum: re-hash the object, compare to its own name.
/// - Two-level fan-out (ab/cd/...) keeps any one "folder" small, which matters for
///   tools like rclone and for browsing the bucket by hand.
/// </summary>
public static class StorageKeys
{
    public const string ObjectPrefix = "objects/";
    public const string ManifestPrefix = "manifest/";

    public static string ForObject(string sha256Hex)
    {
        if (!Sha256Hex.IsValid(sha256Hex))
            throw new ArgumentException("Expected a lowercase 64-character SHA-256 hex string.", nameof(sha256Hex));
        return $"{ObjectPrefix}{sha256Hex[..2]}/{sha256Hex[2..4]}/{sha256Hex}";
    }
}

public static partial class Sha256Hex
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);
}

/// <summary>URL-safe project identifiers: "get-up-and-go", "sheisdesign".</summary>
public static partial class Slug
{
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? value) => value is { Length: > 0 and <= 80 } && Pattern().IsMatch(value);

    public static string From(string title)
    {
        // "Café & Co" -> "cafe-and-co": split accents off letters (FormD), drop the accents,
        // turn every run of other characters into a single dash.
        var sb = new StringBuilder();
        var lastWasDash = true; // true at the start so we never begin with a dash
        foreach (var ch in title.Replace("&", " and ").Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') { sb.Append(ch); lastWasDash = false; }
            else if (!lastWasDash) { sb.Append('-'); lastWasDash = true; }
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 80 ? slug[..80].TrimEnd('-') : slug;
    }
}

/// <summary>
/// Logical paths come from the client, so they are untrusted input.
/// Normalising them blocks path tricks ("../../etc") and keeps one spelling per file.
/// </summary>
public static class AssetPaths
{
    public const int MaxLength = 512;

    public static bool TryNormalize(string? raw, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) { error = "Path is required."; return false; }

        var p = raw.Trim().Replace('\\', '/');
        while (p.Contains("//")) p = p.Replace("//", "/");
        p = p.TrimStart('/');

        if (p.Length == 0) { error = "Path is required."; return false; }
        if (p.Length > MaxLength) { error = $"Path is longer than {MaxLength} characters."; return false; }
        if (p.EndsWith('/')) { error = "Path must point to a file, not a folder."; return false; }
        if (p.Any(char.IsControl)) { error = "Path contains control characters."; return false; }

        foreach (var segment in p.Split('/'))
        {
            if (segment is "." or "..") { error = "Path may not contain '.' or '..' segments."; return false; }
        }

        normalized = p;
        return true;
    }
}

/// <summary>Guesses an asset's kind and MIME type from its extension.</summary>
public static class FileKinds
{
    private static readonly Dictionary<string, (AssetKind Kind, string ContentType)> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Design
            [".fig"] = (AssetKind.Design, "application/octet-stream"),
            [".jam"] = (AssetKind.Design, "application/octet-stream"),
            [".deck"] = (AssetKind.Design, "application/octet-stream"),
            [".ai"] = (AssetKind.Design, "application/postscript"),
            [".psd"] = (AssetKind.Design, "image/vnd.adobe.photoshop"),
            [".xd"] = (AssetKind.Design, "application/octet-stream"),
            [".sketch"] = (AssetKind.Design, "application/octet-stream"),
            // Images
            [".svg"] = (AssetKind.Image, "image/svg+xml"),
            [".png"] = (AssetKind.Image, "image/png"),
            [".jpg"] = (AssetKind.Image, "image/jpeg"),
            [".jpeg"] = (AssetKind.Image, "image/jpeg"),
            [".webp"] = (AssetKind.Image, "image/webp"),
            [".gif"] = (AssetKind.Image, "image/gif"),
            [".avif"] = (AssetKind.Image, "image/avif"),
            // Documents (screenplays included)
            [".pdf"] = (AssetKind.Document, "application/pdf"),
            [".docx"] = (AssetKind.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            [".doc"] = (AssetKind.Document, "application/msword"),
            [".pptx"] = (AssetKind.Document, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
            [".xlsx"] = (AssetKind.Document, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            [".odt"] = (AssetKind.Document, "application/vnd.oasis.opendocument.text"),
            [".md"] = (AssetKind.Document, "text/markdown"),
            [".txt"] = (AssetKind.Document, "text/plain"),
            [".rtf"] = (AssetKind.Document, "application/rtf"),
            [".fdx"] = (AssetKind.Document, "application/xml"),      // Final Draft
            [".fountain"] = (AssetKind.Document, "text/plain"),      // plain-text screenplay format
            // Video / audio
            [".mp4"] = (AssetKind.Video, "video/mp4"),
            [".mov"] = (AssetKind.Video, "video/quicktime"),
            [".webm"] = (AssetKind.Video, "video/webm"),
            [".mp3"] = (AssetKind.Audio, "audio/mpeg"),
            [".wav"] = (AssetKind.Audio, "audio/wav"),
            // Code
            [".ts"] = (AssetKind.Code, "text/plain"),
            [".tsx"] = (AssetKind.Code, "text/plain"),
            [".js"] = (AssetKind.Code, "text/javascript"),
            [".jsx"] = (AssetKind.Code, "text/plain"),
            [".css"] = (AssetKind.Code, "text/css"),
            [".html"] = (AssetKind.Code, "text/html"),
            [".json"] = (AssetKind.Code, "application/json"),
            [".cs"] = (AssetKind.Code, "text/plain"),
            [".py"] = (AssetKind.Code, "text/x-python"),
            [".sql"] = (AssetKind.Code, "application/sql"),
            [".bundle"] = (AssetKind.Code, "application/octet-stream"), // git bundle
            // Archives
            [".zip"] = (AssetKind.Archive, "application/zip"),
            [".tar"] = (AssetKind.Archive, "application/x-tar"),
            [".gz"] = (AssetKind.Archive, "application/gzip"),
            [".7z"] = (AssetKind.Archive, "application/x-7z-compressed"),
            // Fonts
            [".ttf"] = (AssetKind.Font, "font/ttf"),
            [".otf"] = (AssetKind.Font, "font/otf"),
            [".woff2"] = (AssetKind.Font, "font/woff2"),
        };

    public static (AssetKind Kind, string ContentType) Detect(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return Map.TryGetValue(ext, out var hit) ? hit : (AssetKind.Other, "application/octet-stream");
    }
}

/// <summary>Lets "ux-design", "UxDesign" and "ux_design" all mean the same enum value.</summary>
public static class EnumText
{
    public static bool TryParse<T>(string? text, out T value) where T : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var compact = text.Replace("-", "").Replace("_", "");
        return Enum.TryParse(compact, ignoreCase: true, out value) && Enum.IsDefined(value);
    }
}
