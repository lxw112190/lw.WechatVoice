# -*- coding: utf-8 -*-
"""Synthetic memory image + encrypted db_storage for the capture/key_reader port.

The image embeds the same literal/config layouts the real scanner looks for:
    - raw ASCII key literal x'<hex>' (finds media_1.db)
    - wide UTF-16 key literal
    - Config.Cipher name + std::string node + config object whose masked blob
      holds a master literal x'<master><salt>' (derivation finds media_0.db)
Python key_reader.extract runs first and its result becomes expected.json, so
tests/CaptureCheck.cs compares the C# port against the Python reference on
identical bytes. Synthetic data only; never reads a WeChat process or account.
"""
import json
import shutil
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path[:0] = [str(ROOT / "worker"), str(ROOT / ".deps" / "vendor")]
from test_core import create_reserved, encrypted_file  # noqa: E402
import cryptodb  # noqa: E402
import key_reader  # noqa: E402
import capture  # noqa: E402

TARGET = ROOT / "test-results" / "capture-fixtures"
SALT = b"b" * 16
KEY2 = bytes(range(32))  # raw page key of media_1.db
MASTER = bytes(range(0x80, 0xA0))  # derive(MASTER, salt) = page key of media_1.db
KEY1 = cryptodb.derive(MASTER, SALT)  # page key of media_0.db


def build_db_storage():
    storage = TARGET / "db_storage"
    shutil.rmtree(storage, ignore_errors=True)
    storage.mkdir(parents=True)
    for name, key in [("media_0.db", KEY1), ("media_1.db", KEY2)]:
        plain = storage / ("plain-" + name)
        create_reserved(plain)
        encrypted_file(plain, storage / name, key, SALT)
        plain.unlink()
    create_reserved(storage / "contact.db")  # plaintext: skipped by the scanner
    return storage


def build_image():
    image = bytearray(0x20000)
    symbol = 0x18000
    node = 0x10000
    obj = 0x14000
    blob_addr = 0x16000

    # raw ASCII literal of KEY2
    image[0x8000:0x8000 + 2 + 64 + 1] = b"x'" + KEY2.hex().encode() + b"'"
    # wide UTF-16 literal of KEY2
    image[0x9000:0x9000 + 4 + 64 * 2 + 2] = b"x\x00'\x00" + KEY2.hex().encode("utf-16-le") + b"'\x00"
    # config name symbol
    image[symbol:symbol + len(key_reader.CONFIG_NAME)] = key_reader.CONFIG_NAME
    # std::string node referencing the symbol, then a config object pointer
    struct.pack_into("<Q", image, node + 16, symbol)
    struct.pack_into("<Q", image, node + 24, len(key_reader.CONFIG_NAME))
    struct.pack_into("<Q", image, node + 40, obj)
    # config object descriptor at obj+0x88 -> blob address/length
    literal = b"x'" + MASTER.hex().encode() + SALT.hex().encode() + b"'"
    masked = bytes(value ^ key_reader.CONFIG_MASK[i % len(key_reader.CONFIG_MASK)] for i, value in enumerate(literal))
    struct.pack_into("<Q", image, obj + 0x88 + 8, blob_addr)
    struct.pack_into("<Q", image, obj + 0x88 + 16, len(masked))
    image[blob_addr:blob_addr + len(masked)] = masked
    return bytes(image), len(literal)


def python_reference(image, storage):
    pages = {}
    for name in ["media_0.db", "media_1.db"]:
        page = (storage / name).read_bytes()[:cryptodb.PAGE]
        pages.setdefault(page[:16], []).append((name, page))
    matcher = key_reader.Matcher(pages, lambda: None)

    def read(pointer, count):
        return image[pointer:pointer + count]

    key_reader.extract([(0, len(image))], read, matcher, capture.candidates, lambda: None)
    return matcher


def candidates_vectors():
    # Edge literals: plain 32-byte key, key+salt pair, odd length, long literal
    # with multiple offsets, wide literal.
    block = (
        b"x'" + b"a" * 64 + b"' "
        b"x'" + b"b" * 96 + b"' "
        b"x'" + b"c" * 65 + b"' "
        b"x'" + b"d" * 128 + b"' "
        b"x\x00'\x00" + (b"e" * 64).decode().encode("utf-16-le") + b"'\x00"
    )
    return block, [[k.hex(), (s.hex() if s else None)] for k, s in capture.candidates(block)]


def main():
    shutil.rmtree(TARGET, ignore_errors=True)
    TARGET.mkdir(parents=True)
    storage = build_db_storage()
    image, literal_length = build_image()
    matcher = python_reference(image, storage)
    (TARGET / "memory.bin").write_bytes(image)
    block, vectors = candidates_vectors()
    (TARGET / "candidates.bin").write_bytes(block)
    expected = {
        "found": dict(matcher.found),
        "stats": {
            "candidates": matcher.stats["candidates"],
            "config_names": matcher.stats["config_names"],
            "config_objects": matcher.stats["config_objects"],
            "master_checks": matcher.stats["master_checks"],
        },
        "candidates": vectors,
        "keys": {"key1": KEY1.hex(), "key2": KEY2.hex(), "master": MASTER.hex(), "salt": SALT.hex(), "literal_length": literal_length},
    }
    (TARGET / "expected.json").write_text(json.dumps(expected, indent=2), encoding="utf-8")
    assert len(matcher.found) == 2 and matcher.stats["config_objects"] >= 1 and matcher.stats["master_checks"] >= 1, expected
    print("capture fixtures at", TARGET, "found", matcher.found.keys())


if __name__ == "__main__":
    main()
