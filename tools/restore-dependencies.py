"""Restore a clean checkout; cached bytes are always checked against fixed hashes."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def sha(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(block)
    return value.hexdigest()


def verified(path, record):
    return path.is_file() and path.stat().st_size == record['bytes'] and sha(path) == record['sha256']


def restore(verify_only=False, offline=False):
    sdk = ROOT / 'third_party/silk-sdk'
    sdk_dest = ROOT / '.deps/silk-sdk'
    # Copy only recorded upstream sources, provenance and our export shim.
    lock = json.loads((sdk / 'silk-sdk-provenance.json').read_text(encoding='utf-8'))
    for relative in [x['path'].replace('\\', '/') for x in lock['files']] + ['silk-sdk-provenance.json', 'silk_exports.c']:
        source = sdk / relative
        target = sdk_dest / relative
        if not verify_only:
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
    subprocess.run([sys.executable, str(ROOT / 'tools/verify-silk.py')], check=True, cwd=ROOT)
    resources = json.loads((ROOT / 'asr-resources.json').read_text(encoding='utf-8'))
    for record in resources['files']:
        relative = record['path']
        target = ROOT / '.deps/asr-kids' / relative
        if verified(target, record):
            continue
        if verify_only:
            raise ValueError('Missing or mismatched ASR file: ' + relative)
        target.parent.mkdir(parents=True, exist_ok=True)
        source = ROOT / 'third_party/asr' / relative
        if relative != 'models/sensevoice/model.int8.onnx':
            if not verified(source, record):
                raise ValueError('Vendored ASR file mismatch: ' + relative)
            shutil.copyfile(source, target)
            continue
        if offline:
            raise ValueError('Offline model missing: supply the fixed model in .deps/asr-kids/' + relative)
        url = record['download_url']
        part = target.with_suffix('.download.part')
        for attempt in range(3):
            try:
                request = urllib.request.Request(url, headers={'User-Agent': 'lw.WechatVoice-dependency-restore'})
                with urllib.request.urlopen(request, timeout=120) as incoming, part.open('wb') as outgoing:
                    shutil.copyfileobj(incoming, outgoing, 1024 * 1024)
                if not verified(part, record):
                    raise ValueError('SenseVoice download size/SHA256 mismatch')
                part.replace(target)
                break
            except Exception:
                part.unlink(missing_ok=True)
                if attempt == 2:
                    raise
                time.sleep(2 ** attempt)
    if not verify_only and not offline:
        subprocess.run([sys.executable, '-m', 'pip', 'download', '--only-binary=:all:',
                        '--require-hashes', '-r', str(ROOT / 'requirements-lock.txt'),
                        '-d', str(ROOT / '.deps/wheels')], check=True, cwd=ROOT)
    subprocess.run([sys.executable, str(ROOT / 'tools/verify-dependencies.py')], check=True, cwd=ROOT)
    print('Pinned SDK, ASR runtime/model and Python reference dependencies verified.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--verify-only', action='store_true')
    parser.add_argument('--offline', action='store_true', help='Use already cached model/wheels; restore vendored runtime and SDK without network')
    args = parser.parse_args()
    restore(args.verify_only, args.offline)
