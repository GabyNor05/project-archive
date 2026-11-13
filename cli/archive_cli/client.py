"""Thin HTTP client for the archive API. All the rules live in the API; this just calls it."""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import httpx


class ApiError(Exception):
    pass


@dataclass(frozen=True)
class Settings:
    api_url: str
    api_key: str
    master_key_text: str | None

    @classmethod
    def from_env(cls) -> "Settings":
        # Secrets come from environment variables, never from command-line flags:
        # flags end up in your shell history and in `ps` output for other users to see.
        url = os.environ.get("ARCHIVE_API_URL", "http://localhost:5080").rstrip("/")
        key = os.environ.get("ARCHIVE_API_KEY")
        if not key:
            raise ApiError("ARCHIVE_API_KEY is not set. Create one with `dotnet run -- create-key <name>`.")
        if url.startswith("http://") and not url.startswith(("http://localhost", "http://127.0.0.1")):
            raise ApiError("Refusing to send your API key over plain http to a remote server. Use https://.")
        return cls(url, key, os.environ.get("ARCHIVE_MASTER_KEY"))


class ArchiveClient:
    def __init__(self, settings: Settings):
        self._http = httpx.Client(
            base_url=settings.api_url,
            headers={"X-Api-Key": settings.api_key},
            timeout=httpx.Timeout(60.0, connect=10.0),
        )
        # Uploads/downloads go to pre-signed storage URLs: a separate client WITHOUT the
        # API key, so the key is never sent to the storage provider by accident.
        self._storage = httpx.Client(timeout=httpx.Timeout(None, connect=15.0), follow_redirects=True)

    def close(self) -> None:
        self._http.close()
        self._storage.close()

    def _call(self, method: str, path: str, **kwargs: Any) -> Any:
        r = self._http.request(method, path, **kwargs)
        if r.status_code >= 400:
            try:
                body = r.json()
                detail = body.get("detail") or body.get("title") or body
                if "errors" in body:
                    detail = f"{detail} {body['errors']}"
            except ValueError:
                detail = r.text
            raise ApiError(f"{method} {path} -> {r.status_code}: {detail}")
        return r.json() if r.content else None

    # projects
    def upsert_project(self, slug: str, body: dict) -> Any:
        return self._call("PUT", f"/v1/admin/projects/{slug}", json=body)

    def list_projects(self) -> list[dict]:
        return self._call("GET", "/v1/admin/projects")

    def get_project(self, slug: str) -> dict:
        return self._call("GET", f"/v1/admin/projects/{slug}")

    def status(self) -> dict:
        return self._call("GET", "/v1/admin/status")

    def export_manifest(self) -> dict:
        return self._call("POST", "/v1/admin/manifest")

    # uploads
    def start_upload(self, body: dict) -> dict:
        return self._call("POST", "/v1/admin/uploads", json=body)

    def complete_upload(self, version_id: str) -> dict:
        return self._call("POST", f"/v1/admin/uploads/{version_id}/complete")

    def put_bytes(self, url: str, headers: dict, path: Path) -> None:
        with path.open("rb") as f:
            headers = {**headers, "Content-Length": str(path.stat().st_size)}
            r = self._storage.put(url, content=f, headers=headers)
        if r.status_code >= 300:
            raise ApiError(f"Storage rejected the upload ({r.status_code}): {r.text[:300]}")

    # downloads
    def download_info(self, version_id: str) -> dict:
        return self._call("GET", f"/v1/admin/versions/{version_id}/download")

    def download_to(self, url: str, dest: Path) -> None:
        with self._storage.stream("GET", url) as r:
            if r.status_code >= 300:
                raise ApiError(f"Storage download failed ({r.status_code}).")
            with dest.open("wb") as f:
                for block in r.iter_bytes(1024 * 1024):
                    f.write(block)
