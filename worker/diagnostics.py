"""Bounded, allowlisted diagnostic metadata; never serialize request/media contents."""
import json
import os
from pathlib import Path
import re
import time
import traceback

_path=None
_request_id=''
_stage='request'
_last={}
FIELDS={'action','phase','count','total','current','success','matched','encrypted','scanned_mb',
    'candidates','config_names','config_objects','master_checks','limit_reached','completed','failed',
    'processed','cached','recognized','recognition_failed','cancelled','version','databases','expected_databases',
    'issue_count','selected_count','transcribe','keep_silk','has_chat','has_dates','elapsed_ms',
    'exception_type','component','errno','winerror','hresult','exit_code','stage','database_kind','module','function','line','error_code','selected_pid',
    'read_calls','read_failures','partial_reads','last_read_error','region_count'}


def start(workspace,request_id):
    global _path,_request_id,_stage,_last
    _path=None;_last={};_stage='request'
    if not re.fullmatch('[0-9a-f]{32}',str(request_id)): return
    try:
        directory=Path(workspace)/'diagnostics';directory.mkdir(parents=True,exist_ok=True)
        _request_id=request_id;_path=directory/('worker-'+request_id+'.jsonl')
        files=sorted(directory.glob('worker-*.jsonl*'),key=lambda p:p.stat().st_mtime,reverse=True)
        for old in files[31:]:
            if re.fullmatch(r'worker-[0-9a-f]{32}\.jsonl(?:\.1)?',old.name):
                try:old.unlink()
                except OSError:pass
    except OSError:_path=None


def event(name,**values):
    if _path is None:return
    try:
        record={'utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),'request_id':_request_id,'event':name}
        for key,value in values.items():
            if key not in FIELDS or value is None:continue
            if isinstance(value,(bool,int,float)):record[key]=value
            elif isinstance(value,str) and re.fullmatch(r'[A-Za-z0-9_. -]{1,90}',value):record[key]=value
        if _path.exists() and _path.stat().st_size>=256*1024:
            backup=Path(str(_path)+'.1');_path.replace(backup)
        with _path.open('a',encoding='utf-8') as stream:stream.write(json.dumps(record,ensure_ascii=True)+'\n')
    except (OSError,ValueError):pass


def phase(value):
    global _stage
    name=value.get('phase','request');_stage=name
    now=time.monotonic()
    if now-_last.get(name,-100)<2:return
    _last[name]=now;event('progress',**{k:v for k,v in value.items() if k in FIELDS})


def failure(exc,stage=None,**values):
    event('failure',stage=stage or _stage,exception_type=type(exc).__name__,errno=getattr(exc,'errno',None),winerror=getattr(exc,'winerror',None),**values)
    # Function/line only. No exception message, source text, locals or absolute paths.
    for frame in traceback.extract_tb(exc.__traceback__)[-10:]:
        event('frame',module=Path(frame.filename).stem,function=frame.name,line=frame.lineno)
