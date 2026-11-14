"""Check publication boundaries and pinned files without inspecting any account."""
import importlib.util
import json
import re
from pathlib import Path
import hashlib

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('public_tree', ROOT / 'tools/export-repository.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
paths = list(module.public_files())
assert re.fullmatch(r'\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?', (ROOT / 'VERSION').read_text().strip())
version = (ROOT / 'VERSION').read_text().strip()
assert '"worker"] = "' + version + '"' in (ROOT / 'src/VoiceEngine/Program.cs').read_text(encoding='utf-8'), 'VERSION and engine self-test disagree'
assert len(paths) == len(set(paths))
for path in paths:
    assert path.is_file(), path
    assert path.stat().st_size < 100 * 1024 * 1024, 'Large model belongs in release/cache: ' + str(path)
    assert not {'.deps', 'workspace', 'deliveries', 'test-results', '.git'}.intersection(path.relative_to(ROOT).parts)
    if path.suffix.lower() in {'.cs', '.py', '.md', '.json', '.yml', '.ps1'}:
        text = path.read_text(encoding='utf-8-sig')
        assert not re.search(r'sk-[a-zA-Z0-9]{24,}', text), 'Potential credential: ' + str(path)
        assert not re.search(r'[A-Z]:[\\/](?:Users[\\/][^\\/ \r\n]+[\\/]|webchat[\\/])', text), 'Local user path: ' + str(path)
resources = json.loads((ROOT / 'asr-resources.json').read_text(encoding='utf-8'))
for row in resources['files']:
    if row['path'].endswith('.onnx'):
        assert row['download_url'].startswith('https://huggingface.co/')
    else:
        path = ROOT / 'third_party/asr' / row['path']
        assert path.stat().st_size == row['bytes'] and hashlib.sha256(path.read_bytes()).hexdigest() == row['sha256'], row['path']
sdk = ROOT / 'third_party/silk-sdk'
record = json.loads((sdk / 'silk-sdk-provenance.json').read_text(encoding='utf-8'))
for row in record['files']:
    assert hashlib.sha256((sdk / row['path'].replace('\\', '/')).read_bytes()).hexdigest() == row['sha256'], row['path']
print('Public repository checks passed: %d files; no model, private workspace or oversized Git file.' % len(paths))
