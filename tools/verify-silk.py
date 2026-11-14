"""Verify the pinned SDK sources; local shim and output are recorded separately."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def verify(root=ROOT):
    sdk = root / '.deps/silk-sdk'
    record = json.loads((sdk / 'silk-sdk-provenance.json').read_text(encoding='utf-8'))
    assert record['commit'] == '507be6bca8ce1fb977a061481f1d79e8c610e309'
    for entry in record['files']:
        path = (sdk / entry['path'].replace('\\', '/')).resolve()
        assert path.is_relative_to(sdk.resolve()), 'SDK path escapes source directory'
        assert hashlib.sha256(path.read_bytes()).hexdigest() == entry['sha256'], 'SILK source mismatch: ' + entry['path']
    assert (sdk / 'silk_exports.c').is_file(), 'Missing SILK export shim'
    return record


if __name__ == '__main__':
    print('Verified %d pinned SILK SDK source files and export shim.' % len(verify()['files']))
