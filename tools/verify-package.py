"""Test the exact ZIP with synthetic audio only, independent of installed Python."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import wave
import zipfile

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tests'))
from test_core import dataset,tone


def verify_long_paths(temporary, request, blob):
    root = (temporary / '长路径验收').resolve()
    assert root.is_relative_to(temporary.resolve())

    def io_path(path):
        return Path('\\\\?\\UNC\\' + str(path)[2:] if str(path).startswith('\\\\')
                    else '\\\\?\\' + str(path))

    def deep(name):
        path = root / name
        while len(str(path)) < 310:
            path /= '多级目录 #语音%' * 6
        return path

    raw = blob + bytes(256)
    original = dataset(temporary / 'fixture-long', blob=raw)
    source, work, output = (deep(name) for name in ('来源', '快照', '导出'))
    before = {path.relative_to(original): hashlib.sha256(path.read_bytes()).digest()
              for path in original.rglob('*') if path.is_file()}
    try:
        shutil.copytree(original, io_path(source))
        options = {'workspace': str(work)}
        catalog = request(dict(options, action='catalog', source=str(source), consent=True))
        assert catalog['count'] == 3 and not catalog['issues'], catalog
        options['session'] = catalog['session']
        rows = request(dict(options, action='list'))['items']
        assert len(rows) == 3
        result = request(dict(options, action='preview', id=rows[0]['id']))
        assert not result['path'].startswith('\\\\?\\') and io_path(result['path']).is_file()
        exported = request(dict(options, action='export', output=str(output), keep_silk=True, transcribe=False))
        assert (exported['completed'], exported['failed']) == (3, 0), exported
        assert not exported['output'].startswith('\\\\?\\')
        path = io_path(exported['output'])
        originals = list(path.glob('*.silk'))
        assert len(originals) == 3 and all(file.read_bytes() == raw for file in originals)
        wavs = list(path.glob('*.wav'))
        assert len(wavs) == 3
        for wav in wavs:
            with wave.open(str(wav), 'rb') as audio:
                assert audio.getnframes() == 24000 and audio.getframerate() == 24000
        assert all(hashlib.sha256((io_path(source) / file).read_bytes()).digest() == digest
                   for file, digest in before.items())
    finally:
        if root.exists():
            shutil.rmtree(io_path(root))
    print('PASS: 310+ character Chinese/#/% source, snapshot, preview and export paths; padded SILK and source bytes preserved.')


def verify(archive):
    archive=Path(archive).resolve()
    result_root=(ROOT/'test-results').resolve()
    result_root.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='preview2-zip-',dir=result_root) as tmp:
        temporary=Path(tmp).resolve()
        assert temporary.is_relative_to(result_root)
        with zipfile.ZipFile(archive) as zip:
            assert zip.testzip() is None
            for name in zip.namelist():
                assert (temporary/name).resolve().is_relative_to(temporary)
                assert 'workspace' not in Path(name).parts
                assert Path(name).suffix.lower() not in {'.dpapi','.db','.sqlite','.silk','.wav','.pcm','.dmp','.key','.log'}
            zip.extractall(temporary)
        app=temporary/archive.stem
        assert (app/'engine/VoiceWorker.exe.config').is_file()
        for row in json.loads((app/'files.sha256.json').read_text(encoding='utf-8')):
            file=app/row['path']
            assert file.resolve().is_relative_to(app)
            assert file.stat().st_size==row['bytes']
            assert hashlib.sha256(file.read_bytes()).hexdigest()==row['sha256']
        env=dict(os.environ)
        for key in list(env):
            if key.upper() in {'PYTHONHOME','PYTHONPATH','PATH'}: env.pop(key)
        env['PATH']=str(Path(os.environ['SystemRoot'])/'System32')
        engine=app/'engine/VoiceWorker.exe'
        def request(value):
            proc=subprocess.run([str(engine)],input=json.dumps(value).encode()+b'\n',capture_output=True,env=env,timeout=40)
            assert proc.returncode==0,proc.stdout[-2000:]
            response=json.loads(proc.stdout.splitlines()[-1]);assert response['ok']
            return response['data']
        metadata=request({'action':'self-test'})
        assert metadata['worker']==(ROOT/'VERSION').read_text().strip() and metadata['target_wechat']=='4.x' and metadata['verified_wechat_version']=='4.0.5.16'
        raw=tone();base=raw[1:] if raw.startswith(b'\x02') else raw
        size=int.from_bytes(base[9:11],'little')
        packet=base[9:11+size]
        for name,blob,minimum,maximum in [('multi',raw,.9,1.1),('single',b'#!SILK_V3'+packet,.015,.1),('crlf',b'\x02#!SILK_V3\r\n'+packet+b'\xff\xff',.015,.1)]:
            source=dataset(temporary/('fixture-'+name),blob=blob)
            workspace=temporary/('work-'+name)
            catalog=request({'action':'catalog','source':str(source),'workspace':str(workspace),'consent':True})
            assert catalog['count']==3
            exported=request({'action':'export','session':catalog['session'],'workspace':str(workspace),'output':str(temporary/('out-'+name))})
            assert exported['completed']==3 and exported['failed']==0
            output=Path(exported['output'])
            for wav in output.glob('*.wav'):
                with wave.open(str(wav),'rb') as audio:
                    duration=audio.getnframes()/audio.getframerate()
                    assert minimum<=duration<=maximum,duration
            assert len(list(output.glob('*.wav')))==3
            assert all(file.read_bytes()==blob for file in output.glob('*.silk'))
        verify_long_paths(temporary, request, raw)
        print('PASS: exact ZIP CRC and file hashes, private artifact exclusion, bundled engine self-test, 9 GUI-independent exports, multi/single/CRLF durations and original bytes; PATH=System32, no PYTHONHOME/PYTHONPATH.')


if __name__=='__main__':
    verify(sys.argv[1])
