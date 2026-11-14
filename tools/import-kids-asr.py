"""Reuse only ASR code, fixed model and native dependencies from the supplied r5 source."""
import hashlib
import json
from pathlib import Path
import shutil
import sys

ROOT=Path(__file__).resolve().parents[1]


def main(source):
    source=Path(source).resolve();dest=ROOT/'.deps/asr-kids'
    if hashlib.sha256((source/'models/sensevoice/model.int8.onnx').read_bytes()).hexdigest()!='c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51':
        raise ValueError('SenseVoice model differs from fixed original')
    files=['models/sensevoice/'+name for name in ['model.int8.onnx','tokens.txt','LICENSE','README.md']]
    files+=['runtime/'+name for name in ['DirectML.dll','msvcp140_1.dll','msvcp140.dll','onnxruntime.dll','sherpa-onnx-c-api.dll','vcruntime140_1.dll','vcruntime140.dll']]
    files+=['third_party/sherpa/lib/net45/sherpa-onnx.dll']
    records=[]
    for relative in files:
        src=source/relative
        target=dest/relative if relative.startswith('models/') else dest/src.name
        target.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(src,target)
        records.append({'path':target.relative_to(dest).as_posix(),'bytes':target.stat().st_size,'sha256':hashlib.sha256(target.read_bytes()).hexdigest(),'source':relative})
    for name in ['SenseVoice-LICENSE.txt','sherpa-onnx-2.0.1-LICENSE.txt','onnxruntime-LICENSE.txt']:
        shutil.copyfile(source/'licenses'/name,ROOT/'licenses'/name)
    for src in (source/'licenses/native-r4').glob('*'):
        if src.is_file(): shutil.copyfile(src,ROOT/'licenses'/('KidsNative-'+src.name+'.txt'))
    original=source/'src/KidsVoice.Workshop/NativeSpeechApi.cs'
    target=ROOT/'src/AsrWorker/NativeSpeechApi.cs';target.parent.mkdir(parents=True,exist_ok=True)
    target.write_text(original.read_text(encoding='utf-8').replace('namespace KidsVoice.Workshop','namespace WechatVoice.Asr'),encoding='utf-8')
    (ROOT/'asr-resources.json').write_text(json.dumps({'source':'KidsVoice-Workshop-20261102-r5-source','sherpa':'2.0.1','native_speech_api_original_sha256':hashlib.sha256(original.read_bytes()).hexdigest(),'files':records},indent=2),encoding='utf-8')
    print('Reused ASR resources:',len(records),'No settings, keys, recordings, TTS or conversation data imported.')


if __name__=='__main__': main(sys.argv[1])
