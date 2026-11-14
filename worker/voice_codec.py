"""Bounded SILK decoding in an isolated process. No codec runs in the GUI."""
import io
import json
from pathlib import Path
import subprocess
import sys
import time
import wave

RATE=24000
MAX_PCM=RATE*2*90


def packet_stream(raw):
    data=raw[1:] if raw.startswith(b'\x02') else raw
    if not data.startswith(b'#!SILK_V3'): raise ValueError('不是支持的SILK v3语音')
    starts=[9]
    if data[9:11]==b'\r\n': starts.append(11)
    elif data[9:10]==b'\n': starts.append(10)
    for start in starts:
        at=start;packets=[]
        while at<len(data):
            begin=at
            if at+2>len(data): break
            size=int.from_bytes(data[at:at+2],'little');at+=2
            if size==65535:
                if at!=len(data): break
                at=begin
                break
            # Native SILK SDK packet buffer capacity; reject before native code.
            if not 0<size<=1250 or at+size>len(data): break
            at+=size;packets.append(data[begin:at])
            if len(packets)>4500: break
        valid_end=at==len(data) or (at+2==len(data) and data[at:]==b'\xff\xff')
        if valid_end and packets and len(packets)<=4500 and sum(map(len,packets))==at-start:
            count=len(packets)
            # Pinned pysilk 0.2.8 reads packets until EOF; its decoder does not
            # consume the signed -1 terminator. Strip only validated framing.
            framed=b'\x02#!SILK_V3'+b''.join(packets)
            return framed,count
    raise ValueError('SILK帧长度不完整或不支持，原始文件已保留')


def validate_packets(raw):
    return packet_stream(raw)[1]


class BoundedBuffer(io.BytesIO):
    def write(self,data):
        if self.tell()+len(data)>MAX_PCM: raise ValueError('语音超过90秒解码上限')
        return super().write(data)


def decode(source, target):
    import pysilk
    source,target=Path(source),Path(target)
    if not 0<source.stat().st_size<=8*1024*1024: raise ValueError('语音大小无效')
    raw=source.read_bytes()
    if not (raw.startswith(b'\x02#!SILK_V3') or raw.startswith(b'#!SILK_V3')):
        raise ValueError('不是支持的SILK v3语音，已保留原始文件')
    value,_=packet_stream(raw)
    out=BoundedBuffer();pysilk.decode(io.BytesIO(value),out,RATE);pcm=out.getvalue()
    if not 64<len(pcm)<=MAX_PCM or len(pcm)%2: raise ValueError('SILK解码失败，原始文件已保留')
    temporary=target.with_suffix('.wav.part')
    try:
        with wave.open(str(temporary),'wb') as out:
            out.setnchannels(1);out.setsampwidth(2);out.setframerate(RATE);out.writeframes(pcm)
        with wave.open(str(temporary),'rb') as check:
            if check.getnframes()*2!=len(pcm) or check.getframerate()!=RATE or check.getnchannels()!=1:
                raise ValueError('WAV回读验证失败')
        if target.exists(): raise FileExistsError('已有WAV不覆盖')
        temporary.replace(target)
        return len(pcm)/(RATE*2)
    finally:
        temporary.unlink(missing_ok=True)


def isolated(source,target,check):
    command=[sys.executable] if getattr(sys,'frozen',False) else [sys.executable,str(Path(__file__).with_name('cli.py'))]
    process=subprocess.Popen(command+['--decode'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,
        creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
    request=json.dumps({'source':str(source),'target':str(target)}).encode('utf-8')+b'\n'
    start=time.monotonic()
    try:
        process.stdin.write(request);process.stdin.close()
        while process.poll() is None:
            check()
            if time.monotonic()-start>8: raise ValueError('语音解码超时，原始文件已保留')
            time.sleep(.04)
        raw=process.stdout.read(65536)
        if process.returncode!=0: raise ValueError('语音解码失败，原始文件已保留')
        result=json.loads(raw)
        if not result.get('ok'): raise ValueError(result.get('error','语音转换失败'))
        return result['duration']
    finally:
        if process.poll() is None: process.kill();process.wait(timeout=3)
        process.stdout.close()
        target.with_suffix('.wav.part').unlink(missing_ok=True)
        if target.exists() and process.returncode!=0: target.unlink(missing_ok=True)
