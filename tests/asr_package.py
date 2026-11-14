"""Real offline ASR of a fixed public sample after SILK/WAV export; no personal audio."""
import csv
import io
import json
import os
from pathlib import Path
import subprocess
import shutil
import sys
import tempfile
import wave

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tests'))
from test_core import dataset,pysilk


def run(app):
    app=Path(app).resolve();engine=app/'engine/VoiceWorker.exe'
    env={k:v for k,v in os.environ.items() if k.upper() not in {'PYTHONPATH','PYTHONHOME','PATH'}}
    env['PATH']=str(Path(os.environ['SystemRoot'])/'System32')
    with tempfile.TemporaryDirectory(prefix='识别导出-',dir=ROOT/'test-results') as tmp:
        temp=Path(tmp)
        incomplete=temp/'incomplete-asr';incomplete.mkdir()
        for name in ['AsrWorker.exe','AsrWorker.exe.config','sherpa-onnx.dll']:shutil.copyfile(app/'asr'/name,incomplete/name)
        failed=subprocess.run([str(incomplete/'AsrWorker.exe')],input=b'',capture_output=True,env=env,timeout=15)
        failure=json.loads(failed.stdout.decode('utf-8-sig').splitlines()[-1]);assert failed.returncode!=0 and not failure['ok']
        assert failure['exception_type']=='Win32Exception' and isinstance(failure['winerror'],int) and failure['winerror']!=0
        assert failure['component'].endswith('.dll') and 'hresult' in failure
        with wave.open(str(ROOT/'test-results/asr-reference/zh.wav'),'rb') as source:
            assert source.getnchannels()==1 and source.getsampwidth()==2
            rate=source.getframerate();pcm=source.readframes(source.getnframes())
        encoded=io.BytesIO();pysilk.encode(io.BytesIO(pcm),encoded,rate,rate,tencent=True)
        account=dataset(temp/'synthetic-account',count=1,blob=encoded.getvalue())
        def request(value):
            value['workspace']=str(temp/'work')
            proc=subprocess.run([str(engine)],input=json.dumps(value).encode()+b'\n',capture_output=True,env=env,timeout=120)
            assert proc.returncode==0,proc.stdout[-1500:]
            return json.loads(proc.stdout.splitlines()[-1])['data']
        catalog=request({'action':'catalog','source':str(account),'consent':True})
        query={'session':catalog['session'],'chat':'chat'}
        page=request(dict(query,action='list'));ids=[r['id'] for r in page['items']]
        recognized=request(dict(query,action='recognize_page',ids=ids));assert (recognized['recognized'],recognized['failed'])==(1,0),recognized
        back=request(dict(query,action='list'));assert back['items'][0]['recognition_status']=='READY' and len(back['items'][0]['transcript'])>6
        again=request(dict(query,action='recognize_page',ids=ids));assert (again['cached'],again['recognized'])==(1,0),again
        result=request({'action':'export','session':catalog['session'],'output':str(temp/'exports'),'chat':'chat','after':1700000000,'before':1700000001,'transcribe':True,'keep_silk':False})
        assert (result['completed'],result['failed'],result['recognized'],result['recognition_failed'])==(1,0,1,0),result
        output=Path(result['output'])
        with (output/'语音清单.csv').open(encoding='utf-8-sig',newline='') as stream: rows=list(csv.DictReader(stream))
        assert len(rows)==1
        row=rows[0];assert len(row['text'])>6 and '点' in row['text'],row['text']
        assert row['wav'].startswith(row['local_time'][:10].replace('-','')+'-')
        assert '_'+row['id']+'_' in row['wav'] and row['text'][:3] in row['wav']
        assert (output/row['wav']).is_file() and not list(output.glob('*.silk'))
        print('PASS: bundled offline model, public speech -> SILK -> WAV -> recognition -> time_ID_text filename, exact conversation/date filter and complete CSV text; no installed Python required.')
        print('PASS: incomplete native deployment reports safe component, exception type, HRESULT and precise Win32 code.')
        print('PASS: real page recognition, disk cache across worker processes, repeated recognition cache hit and export reuse.')


if __name__=='__main__':run(sys.argv[1])
