# Runbook

How to maintain, move and recover the archive. Do a **restore drill** once a year: an untested backup is a hope, not a backup.

## Routine

| When | What | How |
|---|---|---|
| Automatic, daily | Fixity: re-hash the oldest-checked files | Built into the API (`Fixity` settings) |
| Nightly | Copy primary → replica | `scripts/replicate.sh` (cron or scheduled GitHub Action) |
| Nightly | Encrypted DB backup into the bucket | `scripts/backup-db.sh` |
| After big uploads | Refresh the manifest | `archive manifest` |
| Monthly | Health check | `archive status`: any `corrupt`/`missing` → see below |
| Yearly | Restore drill | Pull a project to a new folder; decrypt one object by hand with no API |
| Yearly | Update .NET to the current LTS | Bump `TargetFramework`, `dotnet restore`, run tests, commit lock file |

## A file is `corrupt` or `missing`

1. `archive status` shows which; the API logs name the storage key.
2. The replica has its own copy: `rclone copy replica:<bucket>/objects/ab/cd/<hash> primary:<bucket>/objects/ab/cd/`.
3. `POST /v1/admin/versions/{id}/verify` (or wait for the next fixity run). It should become `verified`.

Because names are hashes, you can check the replica's copy is correct before restoring it: `sha256sum` must equal the file name.

## Move to a different storage provider

1. Create the new bucket (private) and a key scoped to it.
2. `rclone copy old:<bucket> new:<bucket> --checksum`, then `rclone check old:<bucket> new:<bucket> --one-way --checksum`.
3. Change `Storage__Endpoint`, `Storage__Region`, `Storage__Bucket` and the keys on the API host. Restart.
4. Nothing in the database changes: storage keys are relative.

## Move the API / database to a different host

1. `pg_dump` from the old database, `psql` into the new one (or restore the latest file in `db-backups/`).
2. Deploy the Docker image (`src/Archive.Api/Dockerfile`) with the same env vars, pointing at the new DB.
3. Update `ARCHIVE_API_URL` locally and the portfolio's API URL.

## Recover with NO app and NO database (worst case)

You need only: access to a bucket (primary or replica), the master key, and the `cli/` folder from this repo (or the format spec in `docs/SECURITY.md` to reimplement it).

```bash
rclone copy primary:<bucket>/manifest ./manifest     # what's what
# find the file you want in manifest/index.json -> its storageKey
rclone copy primary:<bucket>/objects/ab/cd/<hash> .
sha256sum <hash>                                     # must equal the file name
export ARCHIVE_MASTER_KEY=...
archive decrypt <hash> Whelmer.fig                   # if "encrypted": true; otherwise just rename
```

## Rotate an API key

```bash
cd src/Archive.Api
dotnet run -- create-key laptop-2026
# update ARCHIVE_API_KEY locally, confirm it works, then:
dotnet run -- revoke-key laptop
```

## Master key compromised (leaked)

1. `archive keygen` → new key.
2. For every private file: pull, then re-upload with the new key (new versions get the new `keyId`).
3. Ask the provider to permanently remove old encrypted objects (bucket lifecycle rule on old keys) once the new versions are `verified`.

(A planned `archive rewrap` command would make this faster: only the small wrapped data key in each header changes. See ROADMAP.)

## Purge something for real

Soft-deleted rows and their objects stay by design. To truly remove (e.g. legal reasons): delete the object from **both** buckets and every DB backup that references it, then remove the rows in SQL. Record why in the audit table.
