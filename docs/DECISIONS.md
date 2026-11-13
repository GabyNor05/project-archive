# Architecture decisions

Short records of each significant choice: the context, what was decided, and what was rejected. This is the "why did you do it this way?" document.

---

## 001 · Store files in object storage, metadata in PostgreSQL

**Context.** Files range from a few KB to several GB (large Figma files). We need search and filtering ("all public UX projects"), version history, and cheap, durable storage.

**Decision.** File bytes go to S3-compatible object storage. Everything *about* the files goes in PostgreSQL.

**Why.**
- Databases are bad at big binary blobs. Backups balloon, memory spikes, and moving providers means moving one giant database.
- Object storage is built for durability: it keeps redundant copies across disks and machines. It's also far cheaper per GB than database storage.
- Queries ("projects tagged react-native, newest first") are what relational databases are good at.

**Rejected.** Files in the database (`bytea`): simple but doesn't scale and is painful to move. Plain Google Drive: no API you control, no integrity checks, tied to one account (the very problem this project exists to solve).

---

## 002 · Speak S3, not a vendor's SDK features

**Decision.** Use only the basic S3 operations (PUT, GET, HEAD, pre-signed URLs) through one `IObjectStore` interface.

**Why.** Almost every provider implements the S3 protocol: AWS, Backblaze B2, Cloudflare R2, Wasabi, MinIO (self-hosted). Moving providers = copy the bucket with `rclone`, change four config values. No vendor-specific feature is allowed to sneak in.

**Primary: Backblaze B2** (cheapest per GB; downloads free up to 3× what you store). **Replica: Cloudflare R2** (10 GB free tier, no egress fees). Two different companies, so one going down or changing terms can't take out both copies.

---

## 003 · Content addressing (name = SHA-256 of the bytes)

**Decision.** Objects are stored at `objects/<h[0..2]>/<h[2..4]>/<sha256>`.

**Why.**
- **Integrity for free.** Re-hash any object; if the result isn't its own name, it's corrupt. Anyone can check this with `sha256sum` in 2040.
- **Deduplication.** The same cover image used in two projects is stored once.
- **Safe replication.** An existing key's content never changes, so copying to a replica is always additive and incremental.

This is the same idea Git uses for its objects.

---

## 004 · Uploads go straight to storage via pre-signed URLs

**Decision.** The API never streams file bytes. It hands the client a signed URL valid for one PUT to one key for 30 minutes, then verifies the result.

**Why.** A 2 GB upload through the API would tie up a server, hit request timeouts, and double the bandwidth. A signed URL gives exactly one narrow permission, temporarily: least privilege.

**Trade-off.** Single PUTs max out at 5 GiB. Multipart upload is on the roadmap if a file ever exceeds that.

---

## 005 · Append-only versions, soft deletes, audit log

**Decision.** Uploading a changed file creates a new `AssetVersion`; old versions stay. Deletes set `DeletedAt` instead of removing rows. Every write adds an `AuditEvent`.

**Why.** An archive's worst failure is losing data by *your own* mistake: a bad script, the wrong folder, a typo. Making destructive actions impossible through the API removes that whole class of failure. Purging is a deliberate, manual runbook step.

---

## 006 · Client-side envelope encryption (AES-256-GCM)

**Decision.** Private files are encrypted on the uploader's machine before upload, with a fresh 256-bit key per file, wrapped by a master key that never leaves the owner. Public portfolio files are stored unencrypted.

**Why.** "Encrypted at rest" by the provider protects against stolen disks, but the provider holds those keys. Client-side encryption means a breach of the provider, a leaked bucket credential, or a subpoena to the provider yields only ciphertext. Details in [SECURITY.md](SECURITY.md).

**Trade-off.** If the master key is lost, private files are gone forever. Mitigated by keeping the key in a password manager and on paper.

---

## 007 · C# / ASP.NET Core for the API

**Why C#.**
- Statically typed: whole categories of bugs are caught at compile time.
- **EF Core migrations**: schema changes are versioned C# files in git, reviewed like code and applied in order. This is how teams evolve production databases without hand-editing them.
- .NET has **LTS releases** with multi-year support, which suits a 10-year project.
- C# and TypeScript share a designer (Anders Hejlsberg), so the jump from my React/TS work is small.

**Rejected.** Java/Spring Boot: equally valid, more ceremony for a project this size. Node/Express: would work, but adds nothing new to learn. Firebase: fast to start, but proprietary and hard to leave, the opposite of the goal.

---

## 008 · Python for the uploader CLI

**Why.** A CLI suits bulk, scriptable, large uploads better than a web form, and it keeps the master key on my machine. Python is a good second language to learn, and its `cryptography` package (maintained by the Python Cryptographic Authority, built on OpenSSL) is the standard choice for crypto in Python. We only use its high-level AES-GCM interface, never hand-rolled primitives.

---

## 009 · Two hashes per version

`Sha256Plain` (the original file) identifies the *content*. It's used to skip re-uploading unchanged files and to verify a restore. `Sha256Stored` (the bytes in the bucket, i.e. the ciphertext for private files) is used for the storage key and fixity checks. For public files they're identical.

---

## 010 · Enums stored as text, Guid v7 ids

Enums are stored as `'UxDesign'` rather than `1`, so a raw SQL dump read years later is self-explanatory, and reordering the C# enum can't silently change old data. Ids are time-ordered UUID v7: globally unique like v4, but new rows append to the end of the index instead of fragmenting it.
