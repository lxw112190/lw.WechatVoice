# -*- coding: utf-8 -*-
"""SILK variants + Python reference decode outputs for the codec port.

tone() encodes a 1-second sine wave; the variants cover the framing the
scanner accepts (multi-packet, single packet, CRLF header, -1 terminator,
no 0x02 prefix) plus rejections. The Python voice_codec.decode output is the
byte-level reference; tests/CodecCheck.cs requires identical WAVs from the C#
port built on the same SILK SDK C sources. Synthetic audio only.
"""
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path[:0] = [str(ROOT / "worker"), str(ROOT / ".deps" / "vendor")]
from test_core import tone  # noqa: E402
import voice_codec  # noqa: E402

TARGET = ROOT / "test-results" / "codec-fixtures"


def main():
    shutil.rmtree(TARGET, ignore_errors=True)
    TARGET.mkdir(parents=True)
    raw = tone()
    header = b"#!SILK_V3"
    first = raw[1 + 9:1 + 9 + 2 + int.from_bytes(raw[10:12], "little")]
    cases = {
        "multi": raw,
        "no-prefix": raw[1:],
        "terminated": raw + b"\xff\xff",
        "single": header + first,
        "crlf": b"\x02#!SILK_V3\r\n" + first,
        "bad-magic": b"not silk at all",
        "truncated": raw[:-5],
        "empty": b"",
        "oversize": raw + b"\x00" * (8 * 1024 * 1024),
    }
    expected = {}
    for name, blob in cases.items():
        folder = TARGET / name
        folder.mkdir()
        (folder / "source.silk").write_bytes(blob)
        case = {"packets": None, "expect": None}
        try:
            case["packets"] = voice_codec.validate_packets(blob)
            duration = voice_codec.decode(folder / "source.silk", folder / "expected.wav")
            case["expect"] = "success"
            case["duration"] = duration
            case["wav"] = "expected.wav"
        except ValueError as exc:
            case["expect"] = "error"
            case["fragment"] = str(exc)[:40]
        (folder / "case.json").write_text(json.dumps(case, indent=2), encoding="utf-8")
        expected[name] = case
        print(name, case["expect"], case["packets"])
    (TARGET / "expected.json").write_text(json.dumps(expected, indent=2), encoding="utf-8")
    print("codec fixtures at", TARGET)


if __name__ == "__main__":
    main()
