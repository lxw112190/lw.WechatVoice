"""Explicit allowlists: never distribute workspace, keys, recordings or local accounts."""
from datetime import datetime
import hashlib
import json
from pathlib import Path
import shutil
import sys
import zipfile
ROOT=Path(__file__).resolve().parents[1]
VERSION=(ROOT/'VERSION').read_text(encoding='utf-8').strip()


def digest(path):
    h=hashlib.sha256()
    with path.open('rb') as src:
        while block:=src.read(1024*1024): h.update(block)
    return h.hexdigest()


def archive(folder,path):
    with zipfile.ZipFile(path,'x',zipfile.ZIP_DEFLATED,compresslevel=9) as out:
        for file in sorted(folder.rglob('*')):
            if file.is_file(): out.write(file,folder.name+'/'+file.relative_to(folder).as_posix())


def manifest(folder):
    records=[{'path':p.relative_to(folder).as_posix(),'bytes':p.stat().st_size,'sha256':digest(p)} for p in sorted(folder.rglob('*')) if p.is_file() and p != folder/'files.sha256.json']
    (folder/'files.sha256.json').write_text(json.dumps(records,ensure_ascii=False,indent=2),encoding='utf-8')


def package():
    from importlib.util import spec_from_file_location, module_from_spec
    spec = spec_from_file_location('verify_silk', ROOT/'tools/verify-silk.py')
    verifier = module_from_spec(spec); spec.loader.exec_module(verifier)
    silk_sources = verifier.verify(ROOT)
    delivery=ROOT/'deliveries'/datetime.now().strftime('%Y%m%d-%H%M%S');delivery.mkdir(parents=True)
    binary=delivery/('WechatVoice-Workshop-'+VERSION+'-win-x64');binary.mkdir()
    app=ROOT/'src/WechatVoice.Workshop/bin/Release'
    for name in ['WechatVoice.Workshop.exe','WechatVoice.Workshop.exe.config']:
        shutil.copyfile(app/name,binary/name)
    # C# engine (VoiceEngine) ships as the worker; the Python engine stays in
    # the build system as a fallback until a real authorized account revalidates
    # the port end to end.
    engine=binary/'engine';engine.mkdir()
    engine_src=ROOT/'src/VoiceEngine/bin/Release'
    shutil.copyfile(engine_src/'VoiceEngine.exe',engine/'VoiceWorker.exe')
    shutil.copyfile(engine_src/'silk.dll',engine/'silk.dll')
    shutil.copytree(ROOT/'dist/AsrWorker',binary/'asr')
    for name in ['README.md','README.en.md','LICENSE','VERSION','CHANGELOG.md','THIRD-PARTY-NOTICES.md','dependency-provenance.json']:
        shutil.copyfile(ROOT/name,binary/name)
    shutil.copytree(ROOT/'licenses',binary/'licenses');shutil.copytree(ROOT/'docs',binary/'docs')
    (binary/'version.json').write_text(json.dumps({'version':VERSION,'target_wechat':'4.x','previously_verified_wechat':['4.0.5.16'],'real_account_verified':False,'platform':'win-x64'},indent=2),encoding='utf-8')
    source=delivery/('WechatVoice-Workshop-'+VERSION+'-source');source.mkdir()
    for name in ['AGENTS.md','.gitignore','.gitattributes','.editorconfig','LICENSE','VERSION','CHANGELOG.md','CONTRIBUTING.md','SECURITY.md','WechatVoice.Workshop.sln','README.md','README.en.md','THIRD-PARTY-NOTICES.md','requirements-lock.txt','dependency-provenance.json','asr-resources.json']:
        shutil.copyfile(ROOT/name,source/name)
    for directory in ['worker','src','tests','tools','licenses','docs','.github','third_party']:
        shutil.copytree(ROOT/directory,source/directory,ignore=shutil.ignore_patterns('bin','obj','__pycache__','*.user'))
    shutil.copytree(ROOT/'.deps/wheels',source/'.deps/wheels')
    shutil.copytree(ROOT/'.deps/asr-kids',source/'.deps/asr-kids')
    # Ship exactly the pinned native sources plus our shim, not local .obj/.dll output.
    sdk = source/'.deps/silk-sdk'; sdk.mkdir()
    for name in [entry['path'].replace('\\','/') for entry in silk_sources['files']] + ['silk-sdk-provenance.json','silk_exports.c']:
        target = sdk/name; target.parent.mkdir(parents=True,exist_ok=True)
        shutil.copyfile(ROOT/'.deps/silk-sdk'/name,target)
    # Source and runtime allowlists reject common private artifact extensions.
    for folder in [binary,source]:
        for p in folder.rglob('*'):
            if p.is_file() and p.suffix.lower() in {'.dpapi','.db','.sqlite','.silk','.wav','.pcm','.dmp','.key','.log'}:
                raise ValueError('Private artifact in package: '+str(p))
    return delivery,binary,source


if __name__=='__main__':
    delivery,binary,source=package()
    print(json.dumps({'delivery':str(delivery),'binary':str(binary),'source':str(source)}))
