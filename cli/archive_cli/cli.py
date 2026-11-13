"""
`archive` - the command-line uploader/downloader for the project archive.

    archive keygen
    archive project whelmer --title "Whelmer" --year 2026 --category interactive-development --public
    archive upload ./Whelmer --project whelmer --public "exports/*.png" --cover exports/cover.png
    archive pull whelmer --out ./restored
    archive decrypt objects/ab/cd/abcd... whelmer.fig     # works with NO api, see docs/RUNBOOK.md
    archive status
"""

from __future__ import annotations

import fnmatch
import tempfile
from pathlib import Path
from typing import Annotated, Optional

import typer

from .client import ApiError, ArchiveClient, Settings
from .crypto import DecryptionError, MasterKey, decrypt_file, encrypt_file, sha256_file

app = typer.Typer(help="Project archive CLI.", no_args_is_help=True, pretty_exceptions_show_locals=False)

# Folders that are rebuildable or huge, never worth archiving file-by-file.
DEFAULT_IGNORES = {"node_modules", ".git", "dist", "build", ".next", ".expo", "__pycache__", ".venv", "bin", "obj", ".DS_Store", "Thumbs.db"}

CATEGORIES = ["interactive-development", "ux-design", "screenwriting", "personal", "other"]


def _master(settings: Settings) -> MasterKey:
    if not settings.master_key_text:
        raise ApiError("ARCHIVE_MASTER_KEY is not set (needed to encrypt/decrypt private files). Run `archive keygen`.")
    return MasterKey.from_text(settings.master_key_text)


def _client() -> tuple[ArchiveClient, Settings]:
    settings = Settings.from_env()
    return ArchiveClient(settings), settings


def _fail(msg: str) -> None:
    typer.secho(msg, fg=typer.colors.RED, err=True)
    raise typer.Exit(1)


@app.command()
def keygen() -> None:
    """Generate a new master key. Do this ONCE, then guard it with your life."""
    key = MasterKey.generate()
    typer.secho("Your new master key (shown once):\n", bold=True)
    typer.echo(f"    {key.to_text()}\n")
    typer.echo(f"Key id: {key.key_id}")
    typer.secho(
        "\nSave it in your password manager AND write it on paper kept somewhere safe.\n"
        "Lose it and every private file in the archive is unrecoverable, by anyone, forever.\n"
        "Then:  export ARCHIVE_MASTER_KEY='<key>'",
        fg=typer.colors.YELLOW,
    )


@app.command()
def project(
    slug: str,
    title: Annotated[str, typer.Option(help="Display title")],
    category: Annotated[str, typer.Option(help=" | ".join(CATEGORIES))] = "other",
    year: Annotated[Optional[int], typer.Option()] = None,
    summary: Annotated[Optional[str], typer.Option()] = None,
    tag: Annotated[Optional[list[str]], typer.Option("--tag", help="Repeatable")] = None,
    repo_url: Annotated[Optional[str], typer.Option()] = None,
    live_url: Annotated[Optional[str], typer.Option()] = None,
    prototype_url: Annotated[Optional[str], typer.Option()] = None,
    public: Annotated[bool, typer.Option("--public/--private", help="Show on the portfolio?")] = False,
) -> None:
    """Create or update a project (safe to run again: it's an upsert)."""
    if category not in CATEGORIES:
        _fail(f"Category must be one of: {', '.join(CATEGORIES)}")
    client, _ = _client()
    try:
        client.upsert_project(slug, {
            "title": title, "year": year, "category": category, "summary": summary,
            "visibility": "public" if public else "private", "tags": tag or [],
            "repoUrl": repo_url, "liveUrl": live_url, "prototypeUrl": prototype_url,
        })
        typer.secho(f"Saved project '{slug}'.", fg=typer.colors.GREEN)
    except ApiError as e:
        _fail(str(e))
    finally:
        client.close()


def _walk(root: Path) -> list[Path]:
    if root.is_file():
        return [root]
    files = []
    for p in sorted(root.rglob("*")):
        if p.is_file() and not any(part in DEFAULT_IGNORES for part in p.relative_to(root).parts):
            files.append(p)
    return files


@app.command()
def upload(
    source: Annotated[Path, typer.Argument(exists=True, help="A file or folder")],
    project_slug: Annotated[str, typer.Option("--project")],
    prefix: Annotated[str, typer.Option(help="Put files under this logical folder, e.g. design/")] = "",
    public: Annotated[Optional[list[str]], typer.Option("--public", help="Glob of files to make PUBLIC (unencrypted), repeatable")] = None,
    cover: Annotated[Optional[str], typer.Option(help="Relative path of the portfolio cover image (made public)")] = None,
    dry_run: Annotated[bool, typer.Option(help="Show what would happen, upload nothing")] = False,
) -> None:
    """Upload a file or folder. Private files are encrypted on THIS machine before they leave it."""
    client, settings = _client()
    public_globs = public or []
    root = source if source.is_dir() else source.parent
    files = _walk(source)
    if not files:
        _fail("No files found (node_modules/.git/dist etc. are skipped; archive code as a zip or `git bundle`).")

    counts = {"upload": 0, "unchanged": 0, "deduplicated": 0}
    master: MasterKey | None = None
    try:
        with tempfile.TemporaryDirectory(prefix="archive-") as tmpdir:
            for path in files:
                rel = path.relative_to(root).as_posix()
                logical = f"{prefix.strip('/')}/{rel}" if prefix.strip("/") else rel
                is_cover = cover is not None and rel == cover
                is_public = is_cover or any(fnmatch.fnmatch(rel, g) for g in public_globs)

                sha_plain = sha256_file(path)
                to_send, key_id = path, None
                if not is_public:
                    master = master or _master(settings)
                    to_send = Path(tmpdir) / f"{sha_plain}.paenc"
                    encrypt_file(path, to_send, master)
                    key_id = master.key_id
                sha_stored = sha_plain if is_public else sha256_file(to_send)

                label = f"{'PUBLIC ' if is_public else 'private'} {logical}"
                if dry_run:
                    typer.echo(f"[dry-run] {label} ({path.stat().st_size:,} bytes)")
                    continue

                res = client.start_upload({
                    "projectSlug": project_slug, "path": logical, "originalFileName": path.name,
                    "sizeBytes": to_send.stat().st_size, "sha256Plain": sha_plain, "sha256Stored": sha_stored,
                    "encrypted": not is_public, "keyId": key_id,
                    "visibility": "public" if is_public else "private",
                    "role": "cover" if is_cover else "original",
                })
                if res["status"] == "upload":
                    client.put_bytes(res["uploadUrl"], res["uploadHeaders"], to_send)
                    done = client.complete_upload(res["versionId"])
                    typer.echo(f"  ↑ {label}  v{res['versionNumber']}  {done['status']}")
                else:
                    typer.echo(f"  = {label}  ({res['status']})")
                counts[res["status"]] += 1
                if to_send != path:
                    to_send.unlink(missing_ok=True)
    except (ApiError, DecryptionError) as e:
        _fail(str(e))
    finally:
        client.close()

    typer.secho(f"Done: {counts['upload']} uploaded, {counts['unchanged']} unchanged, "
                f"{counts['deduplicated']} deduplicated.", fg=typer.colors.GREEN)


@app.command()
def pull(
    project_slug: str,
    out: Annotated[Path, typer.Option(help="Folder to restore into")] = Path("restored"),
) -> None:
    """Download the latest version of every file in a project, verify it, decrypt it."""
    client, settings = _client()
    try:
        proj = client.get_project(project_slug)
        dest_root = out / project_slug
        for asset in proj["assets"]:
            good = [v for v in asset["versions"] if v["status"] in ("stored", "verified")]
            if not good:
                typer.secho(f"  ! {asset['path']}: no available version", fg=typer.colors.YELLOW)
                continue
            v = max(good, key=lambda x: x["number"])
            info = client.download_info(v["id"])
            dest = dest_root / asset["path"]
            dest.parent.mkdir(parents=True, exist_ok=True)
            tmp = dest.with_name(dest.name + ".download")
            client.download_to(info["url"], tmp)

            if sha256_file(tmp) != info["sha256Stored"]:
                tmp.unlink()
                _fail(f"Checksum mismatch downloading {asset['path']}. Not trusting it.")
            if info["encrypted"]:
                decrypt_file(tmp, dest, _master(settings))
                tmp.unlink()
            else:
                tmp.replace(dest)
            if sha256_file(dest) != info["sha256Plain"]:
                _fail(f"Restored {asset['path']} doesn't match its original hash.")
            typer.echo(f"  ↓ {asset['path']}  v{v['number']}  ok")
        typer.secho(f"Restored into {dest_root}", fg=typer.colors.GREEN)
    except (ApiError, DecryptionError) as e:
        _fail(str(e))
    finally:
        client.close()


@app.command()
def decrypt(src: Path, dst: Path) -> None:
    """Decrypt one object file offline. Needs only ARCHIVE_MASTER_KEY, no API, no database."""
    import os
    text = os.environ.get("ARCHIVE_MASTER_KEY")
    if not text:
        _fail("ARCHIVE_MASTER_KEY is not set.")
    try:
        decrypt_file(src, dst, MasterKey.from_text(text))
        typer.secho(f"Decrypted to {dst}", fg=typer.colors.GREEN)
    except DecryptionError as e:
        _fail(str(e))


@app.command()
def status() -> None:
    """Archive health: counts, total size, verification status."""
    client, _ = _client()
    try:
        s = client.status()
        typer.echo(f"Projects: {s['projects']}   Assets: {s['assets']}   Stored: {s['totalStoredBytes'] / 1e9:.2f} GB")
        for k, v in sorted(s["versionsByStatus"].items()):
            colour = typer.colors.RED if k in ("corrupt", "missing") else None
            typer.secho(f"  {k:<9} {v}", fg=colour)
        typer.echo(f"Oldest verification: {s['oldestVerification'] or 'never'}")
    except ApiError as e:
        _fail(str(e))
    finally:
        client.close()


@app.command()
def manifest() -> None:
    """Write manifest/index.json + README.txt into the bucket (do this after big changes)."""
    client, _ = _client()
    try:
        r = client.export_manifest()
        typer.secho(f"Manifest written ({r['projects']} projects).", fg=typer.colors.GREEN)
    except ApiError as e:
        _fail(str(e))
    finally:
        client.close()


if __name__ == "__main__":
    app()
