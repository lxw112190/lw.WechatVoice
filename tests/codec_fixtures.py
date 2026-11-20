# -*- coding: utf-8 -*-
"""SILK variants + Python reference decode outputs for the codec port.

tone() encodes a 1-second sine wave; the variants cover the framing the
scanner accepts (multi-packet, single packet, CRLF header, -1 terminator,
no 0x02 prefix, short zero padding) plus rejections. The Python voice_codec.decode output is the
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
        "lf": header + b"\n" + first,
        "crlf": b"\x02#!SILK_V3\r\n" + first,
        "bad-magic": b"not silk at all",
        "truncated": raw[:-5],
        "empty": b"",
        "oversize": raw + b"\x00" * (8 * 1024 * 1024),
    }
    padded = {
        "padded-multi-min": ("multi", 2),
        "padded-multi-max": ("multi", 256),
        "padded-no-prefix": ("no-prefix", 38),
        "padded-single-lf": ("lf", 6),
        "padded-single-crlf": ("crlf", 12),
    }
    invalid_tails = {
        "odd-padding": raw + bytes(3),
        "long-padding": raw + bytes(258),
        "nonzero-padding": raw + b"\x00\x00\x00\x01",
        "interleaved-zero-length": raw + bytes(2) + first,
        "padding-after-terminator": raw + b"\xff\xff" + bytes(2),
        "padding-without-packets": header + bytes(2),
        "truncated-with-padding": raw[:-5] + bytes(2),
    }
    cases.update({name: cases[original] + bytes(length) for name, (original, length) in padded.items()})
    cases.update(invalid_tails)
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
    for name, (original, _) in padded.items():
        assert expected[name] == expected[original] and expected[name]["expect"] == "success", name
        assert (TARGET / name / "expected.wav").read_bytes() == (TARGET / original / "expected.wav").read_bytes(), name
    for name in invalid_tails:
        assert expected[name]["expect"] == "error" and expected[name]["packets"] is None, name
    (TARGET / "expected.json").write_text(json.dumps(expected, indent=2), encoding="utf-8")
    print("codec fixtures at", TARGET)


if __name__ == "__main__":
    main()
