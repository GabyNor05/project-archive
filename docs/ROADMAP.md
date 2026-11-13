# Roadmap

## Phase 1 · Rescue (now, before the school account closes)
- [ ] Figma: File → Save local copy for every file worth keeping (or the open-source `figma-export` CLI for bulk)
- [ ] Figma: export PDFs/PNGs of key pages alongside each `.fig`
- [ ] Google Takeout of the school Drive → Docs as `.docx` + `.pdf`
- [ ] Code: `git bundle` or zip for anything not already on GitHub
- [ ] Copy everything to an external drive (temporary, until it's in the archive)

## Phase 2 · Core (this repo, v0.1)
- [x] Domain model, EF Core mapping, S3 storage abstraction
- [x] Upload handshake with pre-signed URLs, dedup, unchanged detection
- [x] Client-side envelope encryption + tests
- [x] Public read API for the portfolio, API-key auth, rate limiting
- [x] Fixity background job, manifest export, audit log
- [ ] First migration (`dotnet ef migrations add Initial`), run locally with Docker
- [ ] Create B2 + R2 buckets, deploy the API, upload Phase 1 files

## Phase 3 · Hardening
- [ ] xUnit tests for Archive.Core and integration tests against Testcontainers (real Postgres + MinIO in Docker)
- [ ] GitHub Actions: build + test on every push; nightly replicate + DB backup
- [ ] Multipart uploads for files over 5 GiB
- [ ] `archive rewrap` for master-key rotation without re-uploading
- [ ] Bucket Object Lock / versioning so even a leaked storage key can't delete history
- [ ] OpenAPI document (`/openapi/v1.json`) for API docs

## Phase 4 · Nice to have
- [ ] Small web viewer (Python/Streamlit or a page in the portfolio) to browse the archive
- [ ] Passkey login for an admin web UI
- [ ] Thumbnail generation for public images
- [ ] Search across summaries and tags (PostgreSQL full-text search)
