# -*- coding: utf-8 -*-
"""Rebuild the decrypted synthetic db_storage fixture for tests/FormsCheck.cs.

Expected shape (matches FormsCheck conversation/export expectations):
    contact.db  : chat(测试会话), friend_alice(好友 Alice), demo@chatroom(周末群)
    media_0.db  : chat x3 (oldest/most), demo@chatroom x2, friend_alice x1 (newest/fewest)
    message_0/1 : one shard each, sender ids resolve through per-shard Name2Id
The conversation list is built from voices GROUP BY chat, so every conversation
needs at least one voice; counts drive the count/type sort checks and the 3-WAV
export checks in FormsCheck.cs. Synthetic data only; never reads real accounts,
keys or process memory.
"""
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "worker"))
sys.path.insert(0, str(ROOT / ".deps" / "vendor"))

from test_core import dataset, tone, writable  # noqa: E402

target = ROOT / "test-results" / "gui-fixture" / "source"
shutil.rmtree(target, ignore_errors=True)
dataset(target)
with writable(target / "contact.db") as db:
    db.execute("DELETE FROM contact")
    db.executemany(
        "INSERT INTO contact VALUES(?,?,?)",
        [("chat", "测试会话", ""), ("friend_alice", "好友 Alice", ""), ("demo@chatroom", "周末群", "")],
    )
with writable(target / "media_0.db") as db:
    db.execute("INSERT INTO Name2Id(rowid,user_name) VALUES(8,'demo@chatroom')")
    db.execute("INSERT INTO Name2Id(rowid,user_name) VALUES(9,'friend_alice')")
    for chat_id, stamp in [(8, 1700000010), (8, 1700000011), (9, 1700000020)]:
        db.execute("INSERT INTO VoiceInfo VALUES(?,?,?,?,?)", (chat_id, 1, stamp, 100, tone()))
print("GUI fixture rebuilt at", target)
