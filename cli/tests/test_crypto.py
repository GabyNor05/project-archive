import io
import os

import pytest

from archive_cli.crypto import (
    DecryptionError,
    MasterKey,
    decrypt_stream,
    encrypt_stream,
)

SMALL_CHUNK = 64  # tiny chunks so tests exercise many-chunk files quickly


def roundtrip(data: bytes, chunk: int = SMALL_CHUNK) -> bytes:
    key = MasterKey.generate()
    enc = io.BytesIO()
    encrypt_stream(io.BytesIO(data), enc, key, chunk)
    out = io.BytesIO()
    decrypt_stream(io.BytesIO(enc.getvalue()), out, key)
    return out.getvalue()


@pytest.mark.parametrize("size", [0, 1, 63, 64, 65, 128, 1000])
def test_roundtrip_sizes(size):
    data = os.urandom(size)
    assert roundtrip(data) == data


def encrypted(data: bytes, key: MasterKey) -> bytes:
    buf = io.BytesIO()
    encrypt_stream(io.BytesIO(data), buf, key, SMALL_CHUNK)
    return buf.getvalue()


def header_len(blob: bytes) -> int:
    return 10 + int.from_bytes(blob[6:10], "big")


def test_same_file_encrypts_differently_each_time():
    key = MasterKey.generate()
    assert encrypted(b"hello", key) != encrypted(b"hello", key)


def test_wrong_key_fails():
    blob = encrypted(b"secret screenplay", MasterKey.generate())
    with pytest.raises(DecryptionError):
        decrypt_stream(io.BytesIO(blob), io.BytesIO(), MasterKey.generate())


def test_flipped_bit_in_body_fails():
    key = MasterKey.generate()
    blob = bytearray(encrypted(os.urandom(300), key))
    blob[header_len(blob) + 5] ^= 0x01
    with pytest.raises(DecryptionError):
        decrypt_stream(io.BytesIO(bytes(blob)), io.BytesIO(), key)


def test_edited_header_fails():
    key = MasterKey.generate()
    blob = encrypted(os.urandom(300), key)
    tampered = blob.replace(b'"chunk_size":64', b'"chunk_size":65')
    assert tampered != blob
    with pytest.raises(DecryptionError):
        decrypt_stream(io.BytesIO(tampered), io.BytesIO(), key)


def test_truncation_at_chunk_boundary_is_detected():
    key = MasterKey.generate()
    blob = encrypted(os.urandom(64 * 4), key)  # exactly 4 full chunks
    sealed = SMALL_CHUNK + 16
    truncated = blob[: header_len(blob) + sealed * 2]  # drop the last two whole chunks
    with pytest.raises(DecryptionError):
        decrypt_stream(io.BytesIO(truncated), io.BytesIO(), key)


def test_swapped_chunks_are_detected():
    key = MasterKey.generate()
    blob = encrypted(os.urandom(64 * 3), key)
    h, s = header_len(blob), SMALL_CHUNK + 16
    c0, c1, rest = blob[h : h + s], blob[h + s : h + 2 * s], blob[h + 2 * s :]
    with pytest.raises(DecryptionError):
        decrypt_stream(io.BytesIO(blob[:h] + c1 + c0 + rest), io.BytesIO(), key)


def test_key_text_roundtrip_and_id_stable():
    key = MasterKey.generate()
    again = MasterKey.from_text(key.to_text())
    assert again.key == key.key and again.key_id == key.key_id


def test_rejects_short_key():
    with pytest.raises(ValueError):
        MasterKey(b"too short")
