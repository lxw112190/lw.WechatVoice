"""One cancellable native ASR process per export, with bounded UTF-8 IPC."""
import json
from pathlib import Path
import queue
import subprocess
import threading
import time
import diagnostics


class LocalRecognizer:
    def __init__(self, root, check):
        self.root=Path(root);self.check=check;self.process=None;self.responses=None
    def __enter__(self): return self
    def __exit__(self,*args): self.close()
    def close(self):
        if self.process is None: return
        process=self.process;self.process=None
        try:
            if process.stdin: process.stdin.close()
            try: process.wait(timeout=2)
            except subprocess.TimeoutExpired: process.kill();process.wait(timeout=3)
        finally:
            diagnostics.event('asr_exit',exit_code=process.returncode)
            if process.stdout: process.stdout.close()
    def _start(self):
        exe=self.root/'AsrWorker.exe'
        if not exe.is_file(): raise ValueError('缺少本地识别组件，WAV仍正常导出')
        self.responses=queue.Queue(maxsize=2)
        self.process=subprocess.Popen([str(exe)],cwd=self.root,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
        output=self.process.stdout;responses=self.responses
        def read():
            try:
                while True:
                    line=output.readline(65537)
                    if not line: responses.put(None,timeout=1);return
                    if len(line)>65536 or not line.endswith(b'\n'): responses.put(None,timeout=1);return
                    responses.put(line,timeout=1)
            except (OSError,ValueError,queue.Full): pass
        threading.Thread(target=read,daemon=True).start()
    def recognize(self,wav):
        self.check()
        if self.process is None: self._start()
        try:
            self.process.stdin.write(json.dumps({'wav':str(Path(wav).resolve())},ensure_ascii=True).encode()+b'\n');self.process.stdin.flush()
            deadline=time.monotonic()+90
            while True:
                self.check()
                if time.monotonic()>deadline: raise ValueError('本地识别超时，WAV已保留')
                try: line=self.responses.get(timeout=.1);break
                except queue.Empty: continue
            if line is None:
                diagnostics.event('asr_exit',exit_code=self.process.poll())
                raise ValueError('本地识别组件提前退出，WAV已保留')
            result=json.loads(line.decode('utf-8-sig'))
            if not result.get('ok'):
                diagnostics.event('asr_error',**{k:result[k] for k in ('exception_type','component','hresult','winerror') if k in result})
                raise ValueError(result.get('error','识别失败'))
            text=result.get('text','')
            if not isinstance(text,str) or len(text)>2000: raise ValueError('识别文字格式无效')
            return text.strip()
        except BaseException:
            self.close();raise
