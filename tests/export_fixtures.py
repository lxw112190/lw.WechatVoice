# -*- coding: utf-8 -*-
"""Export session + Python reference outputs for the export pipeline port.

One synthetic session (plaintext snapshots from library-fixtures), exported by
the Python worker twice: once filtered to the chat conversation, once cancelled
via a pre-created cancel marker. When the real ASR assets are staged next to
the project root, a third single-voice export runs through the actual local
recognition model. tests/ExportCheck.cs compares the C# export pipeline
(manifest fields, CSV bytes, WAV/SILK hashes, cancellation) against these
references. Synthetic data only.
"""
import hashlib
import json
import shutil
import sys
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path[:0] = [str(ROOT / "worker"), str(ROOT / ".deps" / "vendor")]
import cli  # noqa: E402
import library  # noqa: E402
from test_core import tone  # noqa: E402

TARGET = ROOT / "test-results" / "export-fixtures"
SESSION = uuid.uuid4().hex


def digests(folder):
    return {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(folder.glob("*")) if p.is_file() and p.suffix.lower() in (".wav", ".silk")}


def manifest_fields(final):
    return {k: final[k] for k in ("completed", "failed", "cancelled", "total", "processed", "remaining",
                                  "recognized", "recognition_failed", "naming", "note", "source_issues")}


def main():
    shutil.rmtree(TARGET, ignore_errors=True)
    TARGET.mkdir(parents=True)
    storage = TARGET / "library-fixtures" / "db_storage"
    storage.parent.mkdir(parents=True)
    shutil.copytree(ROOT / "test-results" / "library-fixtures" / "db_storage", storage)
    folder = TARGET / "sessions" / SESSION
    (folder / "snapshots").mkdir(parents=True)
    for name in ["contact.db", "media_0.db", "media_1.db", "message_0.db", "message_1.db", "message_2.db", "message_3.db"]:
        source = storage / name
        if source.exists():
            shutil.copyfile(source, folder / "snapshots" / name)
    # The library fixture carries dummy blobs; export needs real SILK payloads.
    import sqlite3
    blob = tone() + bytes(128)
    with sqlite3.connect(str(folder / "snapshots" / "media_0.db")) as db:
        db.execute("UPDATE VoiceInfo SET voice_data=?", (blob,))
        db.commit()
    report = library.build(folder / "snapshots", folder / "index.sqlite", lambda: None, lambda value: None)
    (folder / "session.json").write_text(
        json.dumps({"source": str(storage), "issues": report["issues"]}, ensure_ascii=False), encoding="utf-8")

    expected = {"session": SESSION}
    request = {"session": SESSION, "output": str(TARGET / "py-out"), "keep_silk": True, "transcribe": False, "chat": "chat"}
    final = cli.export(TARGET, request, lambda: None)
    output = Path(final["output"])
    assert final["completed"] == 4 and final["failed"] == 0, final
    expected["export"] = {
        "fields": manifest_fields(final),
        "files": digests(output),
        "csv": (output / "语音清单.csv").read_text(encoding="utf-8-sig"),
        "journal_lines": len((output / "progress.ndjson").read_text(encoding="utf-8").splitlines()),
        "wav_names": sorted(p.name for p in output.glob("*.wav")),
    }

    # cancellation once the destination exists: deterministic partial path
    def check_cancelled():
        if list((TARGET / "py-cancel").glob("微信语音-*")):
            raise cli.Cancelled("用户已取消")
    cancelled = cli.export(TARGET, dict(request, output=str(TARGET / "py-cancel")), check_cancelled)
    cancel_output = Path(cancelled["output"])
    assert cancelled["cancelled"], cancelled
    expected["cancel"] = {
        "fields": manifest_fields(cancelled),
        "has_partial": (cancel_output / "partial.json").exists(),
        "has_manifest": (cancel_output / "manifest.json").exists(),
        "wav_count": len(list(cancel_output.glob("*.wav"))),
        "csv_has_header_only": len((cancel_output / "语音清单.csv").read_text(encoding="utf-8-sig").splitlines()) == 1,
    }

    # real-model recognition when ASR assets are staged
    asr = ROOT / "asr" / "AsrWorker.exe"
    if asr.exists():
        rows = [r for r in library.records(folder / "index.sqlite", {"chat": "chat"}, 0)]
        first_id = rows[0]["id"]
        asr_request = {"session": SESSION, "output": str(TARGET / "py-asr"), "keep_silk": False,
                       "transcribe": True, "ids": [first_id]}
        asr_final = cli.export(TARGET, asr_request, lambda: None)
        asr_output = Path(asr_final["output"])
        expected["asr"] = {
            "id": first_id,
            "recognized": asr_final["recognized"],
            "recognition_failed": asr_final["recognition_failed"],
            "completed": asr_final["completed"],
            "csv": (asr_output / "语音清单.csv").read_text(encoding="utf-8-sig"),
            "wav_names": sorted(p.name for p in asr_output.glob("*.wav")),
        }
    (TARGET / "expected.json").write_text(json.dumps(expected, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print("export fixtures at", TARGET, "asr:", asr.exists())


if __name__ == "__main__":
    main()
