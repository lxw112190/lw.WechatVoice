"""Record distribution bytes without machine paths or private runtime state."""
import hashlib
import json
from pathlib import Path
import sys
ROOT=Path(__file__).resolve().parents[1]
def hash_file(path): return hashlib.sha256(path.read_bytes()).hexdigest()
value={'schema':1,'target':'win-x64','python_build_reference':sys.version.split()[0],
 'runtime_files':[{'path':'engine/'+name,'bytes':(ROOT/'src/VoiceEngine/bin/Release'/source).stat().st_size,'sha256':hash_file(ROOT/'src/VoiceEngine/bin/Release'/source)}
    for name,source in [('VoiceWorker.exe','VoiceEngine.exe'),('VoiceWorker.exe.config','VoiceEngine.exe.config'),('silk.dll','silk.dll')]],
 'wheels':[{'file':p.name,'sha256':hash_file(p),'source':'https://pypi.org/'} for p in sorted((ROOT/'.deps/wheels').glob('*.whl'))],
 'reference':{'repository':'https://github.com/qiaodogbear/ChatTrace','commit':'f9fcdb42e8fb99a527c0bcd7d6ff7ea4bc891563','archive_sha256':'1b0fbc42fd50d591765e14a0a4442a93dac6aa2cbf62b8ee2f116246dcef3e8b'},
 'format_references':[{'repository':'https://github.com/lxw112190/Moect','commit':'3d08feceae6492fe2f4f9f8e03b02ec411970c90','license':'GPL-3.0','bundled_source':False}],
 'comparison_reference':{'repository':'https://github.com/lxw112190/Moect','commit':'88dffc7d0d06632f8061b0a19aa82b38337d7ee5','date':'2026-11-10','key_decrypt_reader_service_unchanged':True,'bundled_source':False},
 'asr_resources':json.loads((ROOT/'asr-resources.json').read_text(encoding='utf-8')) if (ROOT/'asr-resources.json').exists() else None,
 'native_engine':{'implementation':'C#/.NET Framework 4.8 x64','sqlite':'Windows winsqlite3',
   'silk_sources':json.loads((ROOT/'.deps/silk-sdk/silk-sdk-provenance.json').read_text(encoding='utf-8')),
   'silk_shim_sha256':hash_file(ROOT/'.deps/silk-sdk/silk_exports.c'),
   'silk_runtime_sha256':hash_file(ROOT/'src/VoiceEngine/bin/Release/silk.dll')},
 'licenses':[{'file':p.name,'sha256':hash_file(p)} for p in sorted((ROOT/'licenses').glob('*')) if p.is_file()]}
(ROOT/'dependency-provenance.json').write_text(json.dumps(value,indent=2,ensure_ascii=False),encoding='utf-8')
print('Dependency provenance recorded; no account data included.')
