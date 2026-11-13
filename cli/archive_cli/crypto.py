"""
Client-side envelope encryption for archive files ("PAENC v1").

THE IDEA (envelope encryption)
------------------------------
Two layers of keys:

  master key (KEK, "key-encryption key")   32 random bytes. Lives ONLY with you:
                                           password manager + a paper copy. Never
                                           sent to the API or the storage provider.
  data key (DEK, "data-encryption key")    32 fresh random bytes PER FILE.

Each file is encrypted with its own data key. The data key is then encrypted
("wrapped") with the master key and stored in the file's header. So:

  * Leaking one file's data key exposes one file, not the archive.
  * Rotating the master key only means re-wrapping tiny data keys, not
    re-encrypting gigabytes.
  * The storage provider holds only ciphertext. Their breach is not your breach.

This is the same pattern AWS KMS, Google Cloud KMS and password managers use.

THE CIPHER: AES-256-GCM
-----------------------
AES-GCM is "authenticated encryption": besides hiding the data, it produces a
16-byte tag. If even one bit of the ciphertext (or the header) is changed,
decryption FAILS instead of returning garbage. Encryption without authentication
(e.g. AES-CBC on its own) lets attackers tamper with data undetected.

GCM's one hard rule: never reuse a (key, nonce) pair. Every file has a fresh data
key and every chunk a distinct nonce, so that can't happen.

WHY CHUNKS (a "STREAM" construction)
------------------------------------
A 3 GB .fig can't be encrypted in one go in memory. We split it into 4 MiB chunks,
each sealed separately. Each chunk's nonce encodes its position and whether it's
the LAST chunk:

    nonce (12 bytes) = 7-byte random prefix | 4-byte chunk counter | 1-byte last-flag

So an attacker can't reorder chunks (the counter would mismatch), drop chunks off
the end (the new final chunk wasn't sealed as "last"), or splice chunks between
files (different data key). The whole header is fed in as "associated data", so
it can't be edited either.

FILE LAYOUT
-----------
    b"PAENC" | version (1 byte) | header length N (uint32 big-endian) | header JSON (N bytes)
    | chunk 0 ciphertext+tag | chunk 1 ciphertext+tag | ... | final chunk ciphertext+tag
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import secrets
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import BinaryIO

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

MAGIC = b"PAENC"
FORMAT_VERSION = 1
DEFAULT_CHUNK_SIZE = 4 * 1024 * 1024  # 4 MiB
TAG_SIZE = 16
NONCE_PREFIX_SIZE = 7
WRAP_AAD = b"project-archive/dek-wrap/v1"
MAX_HEADER_SIZE = 64 * 1024


class DecryptionError(Exception):
    """Wrong key, corrupted file, or tampering. Deliberately vague: never say which."""


@dataclass(frozen=True)
class MasterKey:
    key: bytes

    def __post_init__(self) -> None:
        if len(self.key) != 32:
            raise ValueError("Master key must be exactly 32 bytes (256 bits).")

    @property
    def key_id(self) -> str:
        """A fingerprint that tells you WHICH key was used, without revealing the key."""
        return hashlib.sha256(b"project-archive/key-id/v1" + self.key).hexdigest()[:16]

    @classmethod
    def generate(cls) -> "MasterKey":
        return cls(secrets.token_bytes(32))  # OS cryptographic randomness, never random.random()

    @classmethod
    def from_text(cls, text: str) -> "MasterKey":
        try:
            raw = base64.urlsafe_b64decode(text.strip() + "=" * (-len(text.strip()) % 4))
        except Exception as exc:  # noqa: BLE001
            raise ValueError("Master key is not valid base64.") from exc
        return cls(raw)

    def to_text(self) -> str:
        return base64.urlsafe_b64encode(self.key).decode().rstrip("=")


def _nonce(prefix: bytes, counter: int, last: bool) -> bytes:
    if counter >= 2**32:
        raise ValueError("File too large for this format (over 2^32 chunks).")
    return prefix + struct.pack(">I", counter) + (b"\x01" if last else b"\x00")


def _b64(data: bytes) -> str:
    return base64.b64encode(data).decode()


def encrypt_stream(src: BinaryIO, dst: BinaryIO, master: MasterKey, chunk_size: int = DEFAULT_CHUNK_SIZE) -> None:
    dek = AESGCM.generate_key(bit_length=256)
    wrap_nonce = secrets.token_bytes(12)
    wrapped_dek = AESGCM(master.key).encrypt(wrap_nonce, dek, WRAP_AAD)
    prefix = secrets.token_bytes(NONCE_PREFIX_SIZE)

    header = json.dumps(
        {
            "alg": "AES-256-GCM-STREAM",
            "chunk_size": chunk_size,
            "key_id": master.key_id,
            "wrap_nonce": _b64(wrap_nonce),
            "wrapped_dek": _b64(wrapped_dek),
            "nonce_prefix": _b64(prefix),
        },
        separators=(",", ":"),
        sort_keys=True,
    ).encode()
    preamble = MAGIC + bytes([FORMAT_VERSION]) + struct.pack(">I", len(header)) + header
    dst.write(preamble)

    aead = AESGCM(dek)
    counter = 0
    chunk = src.read(chunk_size)
    while True:
        # Read one chunk ahead: we only know a chunk is the last one when the next read is empty.
        nxt = src.read(chunk_size)
        last = len(nxt) == 0
        dst.write(aead.encrypt(_nonce(prefix, counter, last), chunk, preamble))
        if last:
            break
        chunk = nxt
        counter += 1


def read_header(src: BinaryIO) -> tuple[dict, bytes]:
    fixed = src.read(len(MAGIC) + 1 + 4)
    if len(fixed) != len(MAGIC) + 5 or fixed[: len(MAGIC)] != MAGIC:
        raise DecryptionError("Not a PAENC file.")
    if fixed[len(MAGIC)] != FORMAT_VERSION:
        raise DecryptionError(f"Unsupported PAENC version {fixed[len(MAGIC)]}.")
    (length,) = struct.unpack(">I", fixed[-4:])
    if length > MAX_HEADER_SIZE:
        raise DecryptionError("Header too large.")
    header_bytes = src.read(length)
    if len(header_bytes) != length:
        raise DecryptionError("Truncated header.")
    try:
        header = json.loads(header_bytes)
    except ValueError as exc:
        raise DecryptionError("Unreadable header.") from exc
    return header, fixed + header_bytes


def decrypt_stream(src: BinaryIO, dst: BinaryIO, master: MasterKey) -> None:
    header, preamble = read_header(src)
    if header.get("alg") != "AES-256-GCM-STREAM":
        raise DecryptionError("Unknown algorithm.")
    if header.get("key_id") != master.key_id:
        raise DecryptionError(
            f"This file was encrypted with key {header.get('key_id')}, not {master.key_id}."
        )
    try:
        dek = AESGCM(master.key).decrypt(
            base64.b64decode(header["wrap_nonce"]), base64.b64decode(header["wrapped_dek"]), WRAP_AAD
        )
        prefix = base64.b64decode(header["nonce_prefix"])
        chunk_size = int(header["chunk_size"])
    except (InvalidTag, KeyError, ValueError) as exc:
        raise DecryptionError("Could not unwrap the data key.") from exc

    aead = AESGCM(dek)
    sealed = chunk_size + TAG_SIZE
    counter = 0
    chunk = src.read(sealed)
    while True:
        nxt = src.read(sealed)
        last = len(nxt) == 0
        try:
            dst.write(aead.decrypt(_nonce(prefix, counter, last), chunk, preamble))
        except InvalidTag as exc:
            raise DecryptionError("Authentication failed: wrong key, corrupted or tampered file.") from exc
        if last:
            break
        chunk = nxt
        counter += 1


def encrypt_file(src: Path, dst: Path, master: MasterKey, chunk_size: int = DEFAULT_CHUNK_SIZE) -> None:
    with src.open("rb") as fin, dst.open("wb") as fout:
        encrypt_stream(fin, fout, master, chunk_size)


def decrypt_file(src: Path, dst: Path, master: MasterKey) -> None:
    """Writes to a temp file and only renames it into place if EVERY chunk authenticated,
    so a failed decryption never leaves a half-written, half-trusted output behind."""
    tmp = dst.with_name(dst.name + ".partial")
    try:
        with src.open("rb") as fin, tmp.open("wb") as fout:
            decrypt_stream(fin, fout, master)
        os.replace(tmp, dst)
    finally:
        if tmp.exists():
            tmp.unlink()


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()
