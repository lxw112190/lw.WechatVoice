# -*- coding: utf-8 -*-
"""Build encrypted/plain SQLite + WAL fixtures and Python reference outputs.

For every success case the current Python cryptodb.snapshot is run to produce
expected.db; tests/CryptoDbCheck.cs then requires the C# port to produce
byte-identical snapshots on the same inputs, and the same accept/reject
outcome on the error cases. Synthetic data only; never touches a WeChat
account or process.
"""
import json
import shutil
import sqlite3
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path[:0] = [str(ROOT / "worker"), str(ROOT / ".deps" / "vendor")]
from test_core import checksum, create_reserved, encrypted_file, encrypt_page, transform_wal, writable  # noqa: E402
import cryptodb  # noqa: E402

TARGET = ROOT / "test-results" / "crypto-fixtures"


def wal_fixture(parent, reserved=False, second=False):
    import uuid
    root = parent / ("wal-" + uuid.uuid4().hex)
    root.mkdir(parents=True, exist_ok=True)
    source = root / "live.db"
    if reserved:
        create_reserved(source)
    else:
        with writable(source) as db:
            db.execute("CREATE TABLE sample(id INTEGER PRIMARY KEY,value TEXT)")
            db.execute("INSERT INTO sample VALUES(1,'before')")
    import sqlite3
    live = sqlite3.connect(str(source))
    live.execute("PRAGMA journal_mode=WAL")
    live.execute("PRAGMA wal_autocheckpoint=0")
    base = root / "base.db"
    shutil.copyfile(source, base)
    live.execute("UPDATE sample SET value='after' WHERE id=1")
    live.commit()
    if second:
        live.execute("UPDATE sample SET value='later' WHERE id=1")
        live.commit()
    shutil.copyfile(str(source) + "-wal", str(base) + "-wal")
    live.close()
    return base


def add(cases, name, source, key, expect, root):
    folder = TARGET / name
    shutil.rmtree(folder, ignore_errors=True)
    folder.mkdir(parents=True)
    shutil.copyfile(source, folder / "source.db")
    wal = Path(str(source) + "-wal")
    if wal.exists():
        shutil.copyfile(wal, folder / "source.db-wal")
    case = {"key": key.hex() if key is not None else None, "expect": expect, "generator": root.name}
    expected = folder / "expected.db"
    try:
        cryptodb.snapshot(folder / "source.db", expected, key)
        outcome = "success"
        case["expected"] = expected.name
    except ValueError:
        outcome = "error"
    if outcome != expect:
        raise RuntimeError("%s: Python reference outcome %r != expected %r" % (name, outcome, expect))
    (folder / "case.json").write_text(json.dumps(case, indent=2), encoding="utf-8")
    cases.append(name)


def main():
    shutil.rmtree(TARGET, ignore_errors=True)
    TARGET.mkdir(parents=True)
    cases = []
    key = bytes(range(32))
    salt = b"b" * 16

    # 1-2 plain
    plain = TARGET / "work"
    plain.mkdir(exist_ok=True)
    with writable(plain / "plain.db") as db:
        db.execute("CREATE TABLE sample(id INTEGER PRIMARY KEY,value TEXT)")
        db.execute("INSERT INTO sample VALUES(1,'before')")
    add(cases, "plain-no-wal", plain / "plain.db", None, "success", plain)
    add(cases, "plain-wal", wal_fixture(plain), None, "success", plain)

    # 3 uncommitted valid tail ignored
    tail = wal_fixture(plain)
    wal = Path(str(tail) + "-wal")
    data = wal.read_bytes()
    frame = data[-4120:]
    running = struct.unpack(">II", frame[16:24])
    page = bytearray(frame[24:])
    pos = page.find(b"after")
    page[pos:pos + 5] = b"other"
    first8 = frame[:4] + b"\0" * 4
    newsum = checksum(first8 + page, running)
    wal.write_bytes(data + first8 + frame[8:16] + struct.pack(">II", *newsum) + page)
    add(cases, "plain-wal-uncommitted-tail", tail, None, "success", plain)

    # 4 old-generation salt frame starts a logical end
    old = wal_fixture(plain)
    wal = Path(str(old) + "-wal")
    raw = wal.read_bytes()
    first = bytearray(raw[32:32 + 4120])
    first[8] ^= 1
    wal.write_bytes(raw[:32] + first + b"old preallocation bytes")
    add(cases, "plain-wal-old-generation", old, None, "success", plain)

    # 5 committed frames before an old-generation tail are replayed
    commit = wal_fixture(plain)
    wal = Path(str(commit) + "-wal")
    raw = wal.read_bytes()
    stale = bytearray(raw[-4120:])
    stale[8] ^= 1
    wal.write_bytes(raw + stale + b"padding-not-current-data")
    add(cases, "plain-wal-commit-before-old-tail", commit, None, "success", plain)

    # 6-8 refuse corruption/truncation/bad header
    trunc = wal_fixture(plain)
    Path(str(trunc) + "-wal").write_bytes(Path(str(trunc) + "-wal").read_bytes()[:-1])
    add(cases, "plain-wal-truncated", trunc, None, "error", plain)
    corrupt = wal_fixture(plain)
    wal = Path(str(corrupt) + "-wal")
    bad = bytearray(wal.read_bytes())
    bad[60] ^= 1
    wal.write_bytes(bad)
    add(cases, "plain-wal-corrupted-frame", corrupt, None, "error", plain)
    head = wal_fixture(plain)
    wal = Path(str(head) + "-wal")
    bad = bytearray(wal.read_bytes())
    bad[0] ^= 1
    wal.write_bytes(bad)
    add(cases, "plain-wal-bad-header", head, None, "error", plain)

    # 9-12 encrypted — the page layout requires real reserve-80 pages (WCDB format)
    enc = TARGET / "enc"
    enc.mkdir(exist_ok=True)
    create_reserved(enc / "plain.db")
    encrypted_file(enc / "plain.db", enc / "cipher.db", key, salt)
    add(cases, "encrypted-no-wal", enc / "cipher.db", key, "success", enc)
    cipher_wal = wal_fixture(enc, reserved=True)
    encrypted_file(cipher_wal, enc / "cipher-wal.db", key, salt)
    transform_wal(Path(str(cipher_wal) + "-wal"), enc / "cipher-wal.db-wal", key, salt)
    add(cases, "encrypted-wal", enc / "cipher-wal.db", key, "success", enc)
    add(cases, "encrypted-wrong-key", enc / "cipher.db", bytes([9]) * 32, "error", enc)
    tamper = wal_fixture(enc, reserved=True)
    encrypted_file(tamper, enc / "tamper.db", key, salt)
    transform_wal(Path(str(tamper) + "-wal"), enc / "tamper.db-wal", key, salt)
    bad = bytearray(Path(enc / "tamper.db-wal").read_bytes())
    bad[32 + 24 + 100] ^= 1
    Path(enc / "tamper.db-wal").write_bytes(bad)
    add(cases, "encrypted-wal-tampered-page", enc / "tamper.db", key, "error", enc)

    # 13 real SQLite reserve-bytes file (skipped only when the dev C API DLL is missing)
    try:
        reserved_wal = wal_fixture(TARGET / "res", reserved=True)
        encrypted_file(reserved_wal, TARGET / "res" / "cipher.db", key, salt)
        transform_wal(Path(str(reserved_wal) + "-wal"), TARGET / "res" / "cipher.db-wal", key, salt)
        add(cases, "reserved-encrypted-wal", TARGET / "res" / "cipher.db", key, "success", TARGET / "res")
        # Reused per-database crypto state must remain correct across many pages,
        # including detecting a corrupt page near the end, not just page one.
        large_plain = TARGET / "res" / "many-plain.db"
        create_reserved(large_plain)
        with writable(large_plain) as db:
            db.execute('CREATE TABLE page_load(id INTEGER PRIMARY KEY,data BLOB)')
            db.executemany('INSERT INTO page_load VALUES(?,?)', [(i, bytes([i % 251]) * 3500) for i in range(512)])
        large_cipher = TARGET / "res" / "many-cipher.db"
        encrypted_file(large_plain, large_cipher, key, salt)
        add(cases, "encrypted-many-pages", large_cipher, key, "success", TARGET / "res")
        damaged = bytearray(large_cipher.read_bytes()); damaged[-100] ^= 1
        large_cipher.write_bytes(damaged)
        add(cases, "encrypted-late-tamper", large_cipher, key, "error", TARGET / "res")
    except Exception as exc:
        (TARGET / "res").mkdir(parents=True, exist_ok=True)
        (TARGET / "res" / "skip.txt").write_text(str(exc), encoding="utf-8")

    # Valid final WAL pages supersede obsolete base-file contents.
    frame_size = 4120
    def wal_checksums(raw):
        raw = bytearray(raw); running = checksum(raw[:24]); raw[24:32] = struct.pack('>II', *running)
        for at in range(32, len(raw), frame_size):
            running = checksum(raw[at:at+8] + raw[at+24:at+frame_size], running)
            raw[at+16:at+24] = struct.pack('>II', *running)
        return raw
    base = (enc / 'cipher-wal.db').read_bytes()
    wal_raw = (enc / 'cipher-wal.db-wal').read_bytes()
    page_number = int.from_bytes(wal_raw[32:36], 'big')
    assert page_number > 1
    bad_base = bytearray(base); bad_base[(page_number-1)*4096+100] ^= 1
    probe = enc / 'replacement.db'; probe_wal = Path(str(probe)+'-wal')
    probe.write_bytes(bad_base); probe_wal.write_bytes(wal_raw)
    add(cases, 'encrypted-base-replaced', probe, key, 'success', enc)
    assert (TARGET/'encrypted-base-replaced/expected.db').read_bytes() == (TARGET/'encrypted-wal/expected.db').read_bytes()
    altered = bytearray(wal_raw); altered[32+24+100] ^= 1
    probe_wal.write_bytes(wal_checksums(altered))
    add(cases, 'encrypted-replacement-bad-hmac', probe, key, 'error', enc)
    altered = bytearray(wal_raw)
    for at in range(32, len(altered), frame_size): altered[at+4:at+8] = bytes(4)
    probe_wal.write_bytes(wal_checksums(altered))
    add(cases, 'encrypted-uncommitted-cannot-replace', probe, key, 'error', enc)
    probe.write_bytes(base + b'X'*4096); probe_wal.write_bytes(wal_raw)
    add(cases, 'encrypted-wal-discards-old-tail', probe, key, 'success', enc)

    multi = wal_fixture(enc, reserved=True, second=True)
    encrypted_file(multi, probe, key, salt)
    transform_wal(Path(str(multi)+'-wal'), probe_wal, key, salt)
    raw = probe_wal.read_bytes(); assert len(raw) >= 32+2*frame_size
    add(cases, 'encrypted-last-committed-version', probe, key, 'success', enc)
    with cryptodb.read_only(TARGET/'encrypted-last-committed-version/expected.db') as db:
        assert db.execute('SELECT value FROM sample WHERE id=1').fetchone()[0] == 'later'
    altered = bytearray(raw); altered[-frame_size+4:-frame_size+8] = bytes(4)
    probe_wal.write_bytes(wal_checksums(altered))
    add(cases, 'encrypted-ignore-uncommitted-version', probe, key, 'success', enc)
    with cryptodb.read_only(TARGET/'encrypted-ignore-uncommitted-version/expected.db') as db:
        assert db.execute('SELECT value FROM sample WHERE id=1').fetchone()[0] == 'after'

    # A fully authenticated freelist authorizes discarding leaf contents only.
    free_plain = enc/'free-plain.db'; create_reserved(free_plain)
    with writable(free_plain) as db:
        db.execute('CREATE TABLE page_load(id INTEGER PRIMARY KEY,data BLOB)')
        db.executemany('INSERT INTO page_load VALUES(?,?)', [(i,bytes([i%251])*3500) for i in range(128)])
        db.execute('DELETE FROM page_load WHERE id<100')
        active = db.execute("SELECT rootpage FROM sqlite_master WHERE name='page_load'").fetchone()[0]
    free_raw = free_plain.read_bytes(); trunk = int.from_bytes(free_raw[32:36],'big')
    trunk_raw = free_raw[(trunk-1)*4096:trunk*4096]
    assert int.from_bytes(trunk_raw[4:8],'big') > 0
    leaf = int.from_bytes(trunk_raw[8:12],'big')
    free_cipher = enc/'free-cipher.db'; encrypted_file(free_plain, free_cipher, key, salt)
    original = free_cipher.read_bytes()
    for name,fill in [('encrypted-free-leaf-zero',bytes(4096)),('encrypted-free-leaf-stale',b'X'*4096)]:
        data = bytearray(original); data[(leaf-1)*4096:leaf*4096] = fill; free_cipher.write_bytes(data)
        add(cases,name,free_cipher,key,'success',enc)
        with cryptodb.read_only(TARGET/name/'expected.db') as db:
            assert db.execute('SELECT count(*) FROM page_load').fetchone()[0] == 28
    data=bytearray(original);data[(trunk-1)*4096+100]^=1;free_cipher.write_bytes(data)
    add(cases,'encrypted-free-trunk-tamper',free_cipher,key,'error',enc)
    data=bytearray(original);data[(active-1)*4096:active*4096]=bytes(4096);free_cipher.write_bytes(data)
    add(cases,'encrypted-active-zero-rejected',free_cipher,key,'error',enc)
    for name,offset,value in [('encrypted-freelist-cycle',0,trunk),('encrypted-freelist-claims-active',8,active)]:
        modified=bytearray(trunk_raw);modified[offset:offset+4]=value.to_bytes(4,'big')
        data=bytearray(original);data[(trunk-1)*4096:trunk*4096]=encrypt_page(modified,trunk,key,salt);free_cipher.write_bytes(data)
        add(cases,name,free_cipher,key,'error',enc)

    # Valid authenticated header size may exclude physical preallocation only.
    tail = enc/'logical-tail.db'; tail.write_bytes((enc/'cipher.db').read_bytes()+b'X'*4096)
    add(cases,'encrypted-logical-preallocation',tail,key,'success',enc)
    header=bytearray((enc/'plain.db').read_bytes()[:4096]);header[24:28]=(int.from_bytes(header[24:28],'big')+1).to_bytes(4,'big')
    data=bytearray(tail.read_bytes());data[:4096]=encrypt_page(header,1,key,salt);tail.write_bytes(data)
    add(cases,'encrypted-invalid-counter-keeps-tail',tail,key,'error',enc)

    # crypto vectors: derive/mac_key/page authentication against the C# port
    vectors = {"key": key.hex(), "salt": salt.hex()}
    vectors["derive"] = cryptodb.derive(b"passphrase", b"0123456789abcdef").hex()
    vectors["mac_key"] = cryptodb.mac_key(key, salt).hex()
    # master-kind key: page encrypted with derive(master, salt)
    master_key = cryptodb.derive(b"master", salt)
    encrypted_file(enc / "plain.db", enc / "master.db", master_key, salt)
    vectors["master_key"] = master_key.hex()
    vectors["first_page_key_ok"] = cryptodb.first_page_key(enc / "cipher.db", key).hex()
    vectors["first_page_key_bad"] = cryptodb.first_page_key(enc / "cipher.db", bytes([9]) * 32)
    vectors["first_page_key_bad"] = vectors["first_page_key_bad"].hex() if vectors["first_page_key_bad"] else None
    vectors["first_page_key_master"] = cryptodb.first_page_key(enc / "master.db", b"master", kind="master").hex()
    vectors["first_page_key_master_raw_rejected"] = cryptodb.first_page_key(enc / "master.db", b"master")
    vectors["first_page_key_master_raw_rejected"] = None if vectors["first_page_key_master_raw_rejected"] is None else "unexpected"
    (TARGET / "crypto-vectors.json").write_text(json.dumps(vectors, indent=2), encoding="utf-8")
    print("crypto fixtures:", len(cases), "cases at", TARGET)


if __name__ == "__main__":
    main()
