"""Verify raw wheel bytes before extracting, independent of Git line endings."""
import hashlib
import json
from pathlib import Path
import zipfile
ROOT=Path(__file__).resolve().parents[1]
LOCK={
 'cffi-2.1.1-cp312-cp312-win_amd64.whl':'f53e442b08449d42821fa4a4fba000095af9f62742a500f978a9f557ec44339a',
 'pycparser-3.12-py3-none-any.whl':'a076f3f489b39b43ed13563a1dafe42447445bec2e294154ec5c57fcdda4c85e',
 'pycryptodomex-3.23.0-cp37-abi3-win_amd64.whl':'52e5ca58c3a0b0bd5e100a9fbc8015059b05cffc6c66ce9d98b4b45e023443b9',
 'silk_python-0.2.8-cp312-cp312-win_amd64.whl':'b9bb030589150e0d91f8148971eebf6f9211e6839af64dd39b26b9802be242b0'
}
for filename,digest in LOCK.items():
    path=ROOT/'.deps/wheels'/filename
    if hashlib.sha256(path.read_bytes()).hexdigest()!=digest: raise SystemExit('Dependency mismatch: '+filename)
    with zipfile.ZipFile(path) as archive:
        for name in archive.namelist():
            target=(ROOT/'.deps/vendor'/name).resolve()
            if not target.is_relative_to((ROOT/'.deps/vendor').resolve()): raise SystemExit('Wheel path escapes extraction root')
        archive.extractall(ROOT/'.deps/vendor')
print('Verified four pinned Windows x64 Python 3.12 dependency wheels.')
