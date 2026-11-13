namespace Archive.Core;

// ---------------------------------------------------------------------------
// The manifest is a plain JSON description of everything in the archive, written
// INTO the bucket next to the files. It exists so the archive outlives this app:
// if the API and database are both gone in 2036, someone with the bucket and the
// master key can still find, verify and decrypt every file using only this JSON
// and the format notes in manifest/README.txt.
// ---------------------------------------------------------------------------

public sealed record ManifestDocument(
    int FormatVersion,
    DateTimeOffset GeneratedAt,
    string Generator,
    IReadOnlyList<ManifestProject> Projects);

public sealed record ManifestProject(
    string Slug,
    string Title,
    int? Year,
    ProjectCategory Category,
    Visibility Visibility,
    string? Summary,
    IReadOnlyList<string> Tags,
    string? RepoUrl,
    string? LiveUrl,
    string? PrototypeUrl,
    IReadOnlyList<ManifestAsset> Assets);

public sealed record ManifestAsset(
    string Path,
    AssetKind Kind,
    AssetRole Role,
    Visibility Visibility,
    string ContentType,
    IReadOnlyList<ManifestVersion> Versions);

public sealed record ManifestVersion(
    int Number,
    string OriginalFileName,
    string StorageKey,
    long SizeBytes,
    string Sha256Plain,
    string Sha256Stored,
    bool Encrypted,
    string? KeyId,
    VersionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastVerifiedAt);

public static class ManifestReadme
{
    public const string Text = """
        PROJECT ARCHIVE - HOW TO READ THIS BUCKET WITHOUT THE APP
        =========================================================

        objects/ab/cd/<sha256>   Every stored file. The file name is the SHA-256 of
                                 its bytes, so you can always check a file is intact:
                                     sha256sum <file>   must equal its own name.

        manifest/index.json      Every project, asset and version: which object is
                                 which original file, its original name and hashes.

        db-backups/              Nightly PostgreSQL dumps (optional extra copy).

        Encrypted files (manifest "encrypted": true) use the format "PAENC v1":
          bytes 0-4   ASCII "PAENC"
          byte  5     format version (1)
          bytes 6-9   header length N (big-endian uint32)
          next N      JSON header: chunk size, wrapped data key, nonces, key id
          rest        chunks of AES-256-GCM ciphertext (+16-byte tag each)

        Decrypt with the CLI from the source repo:
            archive decrypt <object-file> <output-file>
        It needs the master key (ARCHIVE_MASTER_KEY). Without that key the
        encrypted files cannot be recovered by anyone. That is the point.

        Full spec: docs/SECURITY.md in the source repository.
        """;
}
