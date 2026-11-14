# -*- coding: utf-8 -*-
"""Synthetic db_storage + Python reference outputs for the library port.

Covers sender resolution paths (server id match, local fallback, server id
mismatch not falling back, ambiguity), unknown chat ids, schema incompatibility
issues, filters, sorting (incl. transcript cache join), payload, filename
cleaning and the speech cache. Python library.build/queries run first; their
results become expected.json so tests/LibraryCheck.cs compares the C# port on
identical inputs. Synthetic data only.
"""
import hashlib
import json
import shutil
import sqlite3
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path[:0] = [str(ROOT / "worker"), str(ROOT / ".deps" / "vendor")]
from test_core import writable  # noqa: E402
import library  # noqa: E402
import speech_cache  # noqa: E402

TARGET = ROOT / "test-results" / "library-fixtures"
BLOB = bytes(range(256)) * 3


def table(name):
    return "Msg_" + hashlib.md5(name.encode("utf-8")).hexdigest()


def build_storage():
    storage = TARGET / "db_storage"
    shutil.rmtree(storage, ignore_errors=True)
    storage.mkdir(parents=True)
    with writable(storage / "contact.db") as db:
        db.executescript("CREATE TABLE contact(username TEXT,remark TEXT,nick_name TEXT);")
        db.executemany("INSERT INTO contact VALUES(?,?,?)", [
            ("chat", "测试会话", ""), ("friend_alice", "好友 Alice", ""), ("demo@chatroom", "周末群", ""),
            ("a", "甲", ""), ("b", "乙", ""), ("c", "丙", ""), ("stranger", "", ""),
        ])
    # media_0.db: full sender-resolution matrix
    with writable(storage / "media_0.db") as db:
        db.executescript(
            "CREATE TABLE Name2Id(user_name TEXT);"
            "INSERT INTO Name2Id(rowid,user_name) VALUES(7,'chat'),(8,'demo@chatroom'),(9,'friend_alice');"
            "CREATE TABLE VoiceInfo(chat_name_id INTEGER,local_id INTEGER,create_time INTEGER,svr_id INTEGER,voice_data BLOB);")
        rows = [
            (7, 1, 1700000000, 100, BLOB),    # chat: server match
            (7, 2, 1700000001, None, BLOB),   # chat: local fallback (no server id)
            (7, 3, 1700000002, 0, BLOB),      # chat: local fallback (zero server id)
            (7, 4, 1700000040, 500, BLOB),    # chat: server id mismatch must NOT fall back
            (8, 1, 1700000010, 200, BLOB),    # group: server match, single sender
            (8, 2, 1700000011, 201, BLOB),    # group: ambiguous -> unknown sender
            (9, 1, 1700000020, None, BLOB),   # alice: local fallback
            (10, 1, 1700000030, 300, BLOB),   # unknown chat id -> 未知会话
        ]
        db.executemany("INSERT INTO VoiceInfo VALUES(?,?,?,?,?)", rows)
    # media_1.db: incompatible VoiceInfo -> issue, not fatal
    with writable(storage / "media_1.db") as db:
        db.executescript("CREATE TABLE Name2Id(user_name TEXT); CREATE TABLE VoiceInfo(local_id INTEGER,voice_data BLOB);")
        db.execute("INSERT INTO VoiceInfo VALUES(1,?)", (BLOB,))
    # message shards
    with writable(storage / "message_0.db") as db:
        db.executescript("CREATE TABLE Name2Id(user_name TEXT); INSERT INTO Name2Id(rowid,user_name) VALUES(3,'a'),(4,'b');")
        db.execute("CREATE TABLE %s(local_id INTEGER,create_time INTEGER,local_type INTEGER,real_sender_id INTEGER,server_id TEXT)" % table("chat"))
        db.executemany("INSERT INTO %s VALUES(?,?,34,?,?)" % table("chat"), [
            (1, 1700000000, 3, "100"), (2, 1700000001, 3, ""), (3, 1700000002, 3, "0"), (4, 1700000040, 3, "999")])
        db.execute("CREATE TABLE %s(local_id INTEGER,create_time INTEGER,local_type INTEGER,real_sender_id INTEGER,server_id TEXT)" % table("demo@chatroom"))
        db.executemany("INSERT INTO %s VALUES(?,?,34,?,?)" % table("demo@chatroom"), [
            (1, 1700000010, 3, "200"), (2, 1700000011, 3, "201"), (2, 1700000011, 4, "201")])
    with writable(storage / "message_1.db") as db:
        db.executescript("CREATE TABLE Name2Id(user_name TEXT); INSERT INTO Name2Id(rowid,user_name) VALUES(3,'c');")
        db.execute("CREATE TABLE %s(local_id INTEGER,create_time INTEGER,local_type INTEGER,real_sender_id INTEGER,server_id TEXT)" % table("friend_alice"))
        db.execute("INSERT INTO %s VALUES(1,1700000020,34,3,'')" % table("friend_alice"))
    # message_2.db: Name2Id without user_name -> incompatible shard issue
    with writable(storage / "message_2.db") as db:
        db.executescript("CREATE TABLE Name2Id(bad TEXT); INSERT INTO Name2Id VALUES('x');")
    # message_3.db: table missing real_sender_id -> field issue, sender unknown
    with writable(storage / "message_3.db") as db:
        db.executescript("CREATE TABLE Name2Id(user_name TEXT); INSERT INTO Name2Id(rowid,user_name) VALUES(3,'d');")
        db.execute("CREATE TABLE %s(local_id INTEGER,create_time INTEGER,local_type INTEGER,server_id TEXT)" % table("chat"))
        db.execute("INSERT INTO %s VALUES(9,1700000090,34,'')" % table("chat"))
    return storage


def dump_rows(path, sql):
    with library.read_only(path) as db:
        return [list(r) for r in db.execute(sql)]


def main():
    shutil.rmtree(TARGET, ignore_errors=True)
    TARGET.mkdir(parents=True)
    storage = build_storage()
    index = TARGET / "py-index.sqlite"
    report = library.build(storage, index, lambda: None, lambda value: None)
    expected = {"report": report}
    expected["contacts"] = dump_rows(index, "SELECT * FROM contacts ORDER BY user")
    expected["messages"] = dump_rows(index, "SELECT * FROM messages ORDER BY chat_hash,local_id,stamp,sender,server_id")
    expected["voices"] = dump_rows(index, "SELECT * FROM voices ORDER BY id")
    request = {"chat": "chat"}
    expected["count_chat"] = library.counting(index, request)
    expected["count_all"] = library.counting(index, {})
    expected["listing"] = library.listing(index, {"include_contacts": True, "page": 0})
    expected["listing"]["items"] = expected["listing"]["items"]
    # sorted records: one column per sort, ascending and descending
    expected["sorted"] = {}
    for column in ["stamp", "sender", "label", "bytes", "local_id"]:
        expected["sorted"][column + "_asc"] = [r["id"] for r in library.records(index, {"sort": column, "descending": False}, 0)]
        expected["sorted"][column + "_desc"] = [r["id"] for r in library.records(index, {"sort": column, "descending": True}, 0)]
    # filters
    rows = [r[0] for r in dump_rows(index, "SELECT id FROM voices ORDER BY id")]
    expected["filtered"] = {
        "dates": [r["id"] for r in library.records(index, {"after": 1700000010, "before": 1700000025}, 0)],
        "ids": [r["id"] for r in library.records(index, {"ids": rows[:2]}, 0)],
        "empty_ids_count": library.counting(index, {"ids": []}),
    }
    # payload and filenames
    first = list(library.records(index, {}, 0))[0]
    expected["payload"] = library.payload(storage, first).hex()
    expected["filename"] = library.filename(first)
    expected["wav_filename"] = library.wav_filename(first, "  今天<|zh|>一起??出去**走走  <|en|>abc  ")
    expected["wav_filename_empty"] = library.wav_filename(first, "")
    # speech cache: save/lookup/annotate and cache-backed sorting
    digest = hashlib.sha256(library.payload(storage, first)).hexdigest()
    speech_cache.save(index, first["id"], digest, "READY", "今天天气不错")
    other = list(library.records(index, {}, 0))[1]
    speech_cache.save(index, other["id"], "d" * 64, "EMPTY", "")
    speech_cache.save(index, rows[2], "e" * 64, "ERROR", "失败可重试")
    expected["cache"] = {
        "lookup_hit": speech_cache.lookup(index, first["id"], digest)["text"],
        "lookup_digest_miss": speech_cache.lookup(index, first["id"], "f" * 64),
        "lookup_error_reused": speech_cache.lookup(index, rows[2], "e" * 64),
    }
    cache_items = [dict(r) for r in list(library.records(index, {}, 0))]
    expected["cache_annotate_warning"] = speech_cache.annotate(index, cache_items)
    expected["cache_items"] = cache_items
    expected["sorted"]["transcript_asc"] = [r["id"] for r in library.records(index, {"sort": "transcript", "descending": False}, 0)]
    expected["sorted"]["status_asc"] = [r["id"] for r in library.records(index, {"sort": "recognition_status", "descending": False}, 0)]
    expected["sorted"]["status_desc"] = [r["id"] for r in library.records(index, {"sort": "recognition_status", "descending": True}, 0)]
    # invalid requests
    def rejected(fn):
        try:
            fn()
            return False
        except ValueError:
            return True
    expected["rejected"] = {
        "reversed_dates": rejected(lambda: library.counting(index, {"after": 9, "before": 8})),
        "bad_sort": rejected(lambda: list(library.records(index, {"sort": "evil;drop", "descending": False}, 0))),
        "bad_ids": rejected(lambda: library.counting(index, {"ids": ["not-hex"]})),
    }
    (TARGET / "expected.json").write_text(json.dumps(expected, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print("library fixtures at", TARGET, "count", report["count"], "issues", len(report["issues"]))


if __name__ == "__main__":
    main()
