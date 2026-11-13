# Security

## Threat model: what we defend against

| Threat | Defence |
|---|---|
| Storage provider is breached, or a bucket credential leaks | Private files are encrypted **before** upload; the provider only ever has ciphertext |
| Someone intercepts traffic | HTTPS everywhere; the CLI refuses to send the API key over plain `http://` to a remote host |
| Database leak (e.g. a backup in the wrong place) | API keys are stored only as SHA-256 hashes; DB backups are encrypted with `age` |
| Someone guesses or spams the API | Random 256-bit API keys; per-IP rate limiting (HTTP 429) |
| A file is tampered with or silently corrupted | AES-GCM authentication on every chunk; SHA-256 fixity checks; content-addressed names |
| Private file exposed through the public API | Public routes filter on `Visibility = Public` **and** `Encrypted = false`; API rejects public+encrypted and private+unencrypted uploads |
| My own mistakes (deleting the wrong thing) | Soft deletes, append-only versions, audit log, additive replication |
| Losing access to one provider or company | Second copy at a different provider; S3 standard; manifest inside the bucket |

**Out of scope:** a compromised laptop (if malware has my master key, it's game over, as with any system), and the master key itself being lost (see *Key management*).

---

## The four places security happens

1. **In transit:** TLS (HTTPS). The host terminates TLS; the CLI enforces https for remote URLs.
2. **At rest (provider):** B2/R2 encrypt their disks. Good, but *they* hold those keys.
3. **At rest (ours):** client-side envelope encryption. Only I hold the key. This is the layer that matters.
4. **Access control:** API keys for writes, public read routes that can only see public data, and short-lived signed URLs for file access.

### Hashing vs encryption (they are different things)

- **Hashing** (SHA-256) is one-way. It turns any file into a 64-character fingerprint. You can't get the file back, but any change gives a different fingerprint. Used here for integrity, deduplication, and storing API keys.
- **Encryption** (AES-256-GCM) is two-way with a key. Used here for confidentiality.

---

## Encryption format: PAENC v1

Implemented in [`cli/archive_cli/crypto.py`](../cli/archive_cli/crypto.py); tests in `cli/tests/test_crypto.py`.

### Envelope encryption

```
master key (KEK, 32 bytes, only with me)
   └── wraps ──> data key (DEK, 32 random bytes, one per file)
                     └── encrypts ──> the file
```

- One leaked data key exposes one file, not the archive.
- Rotating the master key = re-wrapping tiny data keys, not re-encrypting gigabytes.
- The same pattern is used by AWS KMS, Google Cloud KMS and password managers.

### Cipher: AES-256-GCM (authenticated encryption)

GCM gives confidentiality **and** a 16-byte authentication tag. Any modification to the ciphertext or the header makes decryption fail loudly instead of returning silently corrupted data. Its one rule, *never reuse a key+nonce pair*, is guaranteed because every file gets a fresh key and every chunk a distinct nonce.

### Streaming in chunks

Files are sealed in 4 MiB chunks so a multi-GB file never has to fit in memory.

```
nonce (12 bytes) = random prefix (7) | chunk counter (4, big-endian) | last-chunk flag (1)
associated data  = the full file preamble (magic + version + header)
```

This blocks reordering chunks (counter mismatch), truncating the file at a chunk boundary (the new final chunk wasn't sealed as "last"), mixing chunks between files (different keys), and editing the header (it's authenticated). Each of these attacks has a test.

### Byte layout

```
"PAENC" | 0x01 | header length N (uint32 BE) | header JSON (N bytes) | sealed chunks...

header JSON: { "alg": "AES-256-GCM-STREAM", "chunk_size": 4194304, "key_id": "<16 hex>",
               "wrap_nonce": b64, "wrapped_dek": b64, "nonce_prefix": b64 }
```

`key_id` is a fingerprint of the master key (SHA-256 of a label + key, truncated). It identifies the key without revealing it.

---

## Key management (the part that actually matters)

The master key is the single most important thing in the system. **Lose it and private files are unrecoverable by anyone. Leak it and they're readable.**

- Generate once with `archive keygen`.
- Store it in a password manager **and** on paper in a safe place. Paper doesn't get hacked or expire.
- Provide it only through the `ARCHIVE_MASTER_KEY` environment variable, never as a command-line flag (flags end up in shell history).
- It is never sent to the API or the storage provider.

### API keys

- Format `pa_` + 256 random bits (base64url). The prefix makes leaked keys easy to spot (GitHub secret scanning works the same way).
- Shown once at creation; only the SHA-256 is stored. A fast hash is fine because the key is random. Human passwords need slow hashes (Argon2/bcrypt) because they're guessable.
- Revoke with `dotnet run -- revoke-key <name>`.

---

## Checklist before going live

- [ ] Bucket is **private** (no public listing); public files are reached only through signed URLs
- [ ] Storage credentials are scoped to one bucket (B2 "application key" restricted to the bucket)
- [ ] Bucket versioning or Object Lock enabled at the provider (protects against a stolen credential deleting things)
- [ ] Secrets only in env vars on the host; `.env` never committed
- [ ] HTTPS on the API host
- [ ] CORS origin list is just the portfolio's domain
- [ ] Master key in password manager + paper; restore drill done (see RUNBOOK)
