"""One bounded JSON request on stdin, NDJSON progress/results on stdout."""
import csv
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import traceback
import uuid
import capture
import diagnostics
import speech_cache
from cryptodb import MAGIC, snapshot, signature, live_snapshot
import library
import voice_codec
from transcriber import LocalRecognizer


class Cancelled(Exception): pass


def emit(value):
    if value.get('type')=='progress':diagnostics.phase(value)
    print(json.dumps(value,ensure_ascii=True),flush=True)


def write_json(path,value):
    path=Path(path);temporary=path.with_suffix('.json.part')
    try:
        with temporary.open('w',encoding='utf-8') as out:
            json.dump(value,out,ensure_ascii=False,indent=2);out.flush();os.fsync(out.fileno())
        temporary.replace(path)
    finally: temporary.unlink(missing_ok=True)


def ensure_outside(output,source):
    if Path(output).resolve().is_relative_to(Path(source).resolve()):
        raise ValueError('工作/输出目录不能放在微信源账号目录内')


def running():
    if os.name!='nt': return False
    tasklist=Path(os.environ['SystemRoot'])/'System32/tasklist.exe'
    for image in ('Weixin.exe','WeChat.exe'):
        data=subprocess.check_output([str(tasklist),'/FI','IMAGENAME eq '+image,'/FO','CSV','/NH'],timeout=10,
            creationflags=subprocess.CREATE_NO_WINDOW)
        if image.lower().encode() in data.lower(): return True
    return False


def session_paths(workspace,session):
    if not isinstance(session,str) or not __import__('re').fullmatch('[0-9a-f]{32}',session):
        raise ValueError('读取会话无效，请重新读取目录')
    folder=workspace/'sessions'/session
    return folder,folder/'snapshots',folder/'index.sqlite'


def recognize_page(workspace,request,check):
    ids=request.get('ids')
    if not isinstance(ids,list) or not 1<=len(ids)<=50 or len(set(ids))!=len(ids):raise ValueError('请指定本页1至50条不重复语音')
    folder,snapshots,index=session_paths(workspace,request.get('session'))
    rows=list(library.records(index,request))
    if len(rows)!=len(ids):raise ValueError('本页语音范围已改变，请刷新后重试')
    base=Path(sys.executable).parent.parent if getattr(sys,'frozen',False) else Path(__file__).resolve().parent.parent
    processed=cached=recognized=failed=0;cancelled=False
    temporary=folder/'recognition-tmp';temporary.mkdir(exist_ok=True)
    try:
        with LocalRecognizer(base/'asr',check) as recognizer:
            for row in rows:
                check();raw=temporary/(uuid.uuid4().hex+'.silk');wav=raw.with_suffix('.wav')
                try:
                    data=library.payload(snapshots,row);digest=hashlib.sha256(data).hexdigest();value=speech_cache.lookup(index,row['id'],digest)
                    if value:cached+=1
                    else:
                        raw.write_bytes(data);voice_codec.isolated(raw,wav,check);text=recognizer.recognize(wav);check()
                        speech_cache.save(index,row['id'],digest,'READY' if text else 'EMPTY',text);recognized+=1
                except Cancelled:raise
                except Exception as exc:
                    failed+=1;diagnostics.failure(exc,'page_recognition')
                    try:speech_cache.save(index,row['id'],'','ERROR','')
                    except Exception as cache_exc:diagnostics.failure(cache_exc,'speech_cache')
                finally:raw.unlink(missing_ok=True);wav.unlink(missing_ok=True)
                processed+=1;emit({'type':'progress','phase':'page_recognition','processed':processed,'total':len(rows),'cached':cached,'recognized':recognized,'failed':failed})
    except Cancelled:cancelled=True
    return {'processed':processed,'total':len(rows),'cached':cached,'recognized':recognized,'failed':failed,'cancelled':cancelled}


def export(workspace,request,check):
    folder,snapshots,index=session_paths(workspace,request.get('session'))
    state=json.loads((folder/'session.json').read_text(encoding='utf-8'))
    total=library.counting(index,request);started=time.monotonic()
    check()
    output=Path(request['output']).resolve();ensure_outside(output,state['source'])
    output.mkdir(parents=True,exist_ok=True)
    destination=output/('微信语音-'+datetime.now().strftime('%Y%m%d-%H%M%S')+'-'+uuid.uuid4().hex[:8])
    destination.mkdir()
    completed=failed=recognized=recognition_failed=0;cancelled=False
    use_asr=request.get('transcribe') is True
    base=Path(sys.executable).parent.parent if getattr(sys,'frozen',False) else Path(__file__).resolve().parent.parent
    fields=['id','chat','label','sender','stamp','local_time','local_id','server_id','silk','wav','duration','sha256','text','asr_status','status']
    def report(phase):
        elapsed=max(0,time.monotonic()-started);processed=completed+failed
        emit({'type':'progress','phase':phase,'completed':completed,'failed':failed,'processed':processed,
            'total':total,'recognized':recognized,'recognition_failed':recognition_failed,
            'elapsed_seconds':round(elapsed,1),'remaining_seconds':round(elapsed/processed*(total-processed),1) if processed else None,
            'output':str(destination)})
    try:
        report('export')
        with LocalRecognizer(base/'asr',check) as recognizer,(destination/'progress.ndjson').open('w',encoding='utf-8') as journal,(destination/'语音清单.csv').open('w',encoding='utf-8-sig',newline='') as stream:
            writer=csv.DictWriter(stream,fieldnames=fields);writer.writeheader()
            for row in library.records(index,request):
                check();result={k:row[k] for k in fields if k in row}
                try: result['local_time']=datetime.fromtimestamp(row['stamp']).isoformat()
                except (ValueError,OSError,OverflowError): result['local_time']='未知'
                try:
                    data=library.payload(snapshots,row);name=library.filename(row)
                    raw=destination/(name+'.silk');wav=destination/(name+'.wav')
                    with raw.open('xb') as out: out.write(data)
                    result.update(silk=raw.name,sha256=hashlib.sha256(data).hexdigest())
                    result['duration']=voice_codec.isolated(raw,wav,check)
                    text='';result['asr_status']='未启用'
                    if use_asr:
                        report('recognize')
                        try:
                            try:value=speech_cache.lookup(index,row['id'],result['sha256'])
                            except Exception as cache_exc:diagnostics.failure(cache_exc,'speech_cache');value=None
                            text=value['text'] if value else recognizer.recognize(wav)
                            if not value:
                                try:speech_cache.save(index,row['id'],result['sha256'],'READY' if text else 'EMPTY',text)
                                except Exception as cache_exc:diagnostics.failure(cache_exc,'speech_cache')
                            if text: recognized+=1;result['asr_status']='已识别'
                            else: recognition_failed+=1;result['asr_status']='未识别到文字'
                        except Cancelled: raise
                        except Exception as exc:
                            diagnostics.failure(exc,'recognize')
                            recognition_failed+=1;result['asr_status']=str(exc)[:200]
                    result['text']=text
                    renamed=destination/library.wav_filename(row,text)
                    if renamed.exists(): raise FileExistsError('已有WAV不覆盖')
                    wav.rename(renamed)
                    if request.get('keep_silk',True) is False:
                        raw.unlink();result['silk']=''
                    result.update(wav=renamed.name,status='已导出WAV');completed+=1
                except Cancelled:
                    # The current decoded WAV may exist even though this row did not finish.
                    # Record it in the partial journal without reporting it as a completed item.
                    result.update(status='已取消；本条保留已生成文件',asr_status='已取消' if use_asr else '未启用')
                    if 'duration' in result and wav.exists(): result['wav']=wav.name
                    writer.writerow({k:("'"+str(v) if isinstance(v,str) and v.startswith(('=','+','-','@','\t','\r')) else v) for k,v in result.items()})
                    journal.write(json.dumps(result,ensure_ascii=False)+'\n');journal.flush();stream.flush()
                    raise
                except Exception as exc:
                    diagnostics.failure(exc,'decode_export')
                    failed+=1;result['status']=str(exc)[:300]
                # Prevent formula interpretation when opening user-controlled names in Excel.
                writer.writerow({k:("'"+str(v) if isinstance(v,str) and v.startswith(('=','+','-','@','\t','\r')) else v) for k,v in result.items()})
                journal.write(json.dumps(result,ensure_ascii=False)+'\n');journal.flush();stream.flush()
                report('export')
    except Cancelled:
        cancelled=True
    final={'completed':completed,'failed':failed,'cancelled':cancelled,'output':str(destination),
        'total':total,'processed':completed+failed,'remaining':total-completed-failed,'elapsed_seconds':round(time.monotonic()-started,1),
        'recognized':recognized,'recognition_failed':recognition_failed,'naming':'时间_唯一编号_识别文字摘要.wav',
        'source_issues':state.get('issues',[]),'note':'本地缓存可能不含全部历史语音；未知发送者不做猜测。'}
    write_json(destination/('partial.json' if cancelled else 'manifest.json'),final)
    return final


def execute(request):
    base=Path(sys.executable).parent if getattr(sys,'frozen',False) else Path(__file__).resolve().parent.parent
    workspace=Path(request.get('workspace',base/'workspace')).resolve()
    workspace.mkdir(parents=True,exist_ok=True)
    cancel=Path(request['cancel']).resolve() if request.get('cancel') else None
    if cancel and not cancel.is_relative_to(workspace): raise ValueError('取消标记必须位于工作目录内')
    def check():
        if cancel and cancel.exists(): raise Cancelled('用户已取消')
    progress=lambda value:emit(dict(type='progress',**value))
    action=request.get('action')
    connection_report=None
    if action=='connect':
        # One explicit GUI action prepares only the selected PID/account and
        # then freezes copies while WeChat stays logged in.
        connection_report=execute(dict(request,action='keys'))
        action='catalog'
    if action=='keys':
        if request.get('consent') is not True: raise ValueError('请先确认本人或已获授权账号')
        root,_=capture.databases(request['source']);ensure_outside(workspace,root)
        scan_started=time.monotonic()
        root,keys,report=capture.scan(int(request['pid']),root,check,progress)
        diagnostics.event('key_scan_result',selected_pid=int(request['pid']),elapsed_ms=int((time.monotonic()-scan_started)*1000),
            limit_reached=bool(report.get('limit_reached',False)),**{k:v for k,v in report.items() if k in diagnostics.FIELDS and k not in {'elapsed_ms','limit_reached'}})
        if not keys and report['encrypted']:
            diagnostics.event('key_scan_rejected',error_code='KEY_NOT_FOUND')
            raise ValueError('连接失败：未获得通过页认证的密钥。扫描'+str(report.get('scanned_mb',0))+'MB，候选'+str(report.get('candidates',0))+'，配置对象'+str(report.get('config_objects',0))+'。请确认主进程及当前登录账号，播放目标语音后重试；可展开高级设置选择进程。')
        vault=workspace/'keys'/ (hashlib.sha256(str(root).encode()).hexdigest()+'.dpapi')
        previous=capture.load_vault(vault,root)
        valid={}
        for relative,value in previous.items():
            path=(root/relative).resolve()
            if path.is_relative_to(root) and path.is_file() and capture.first_page_key(path,bytes.fromhex(value)):
                valid[relative]=value
        previous=valid;previous.update(keys)
        capture.save_vault(vault,root,previous)
        report['matched']=len(previous)
        return report
    if action=='catalog':
        if request.get('consent') is not True: raise ValueError('请先确认本人或已获授权账号')
        root,files=capture.databases(request['source']);ensure_outside(workspace,root)
        encrypted=False
        for path in files:
            with path.open('rb') as stream:
                encrypted=encrypted or stream.read(16)!=MAGIC
        vault=workspace/'keys'/(hashlib.sha256(str(root).encode()).hexdigest()+'.dpapi')
        keys=capture.load_vault(vault,root)
        session=uuid.uuid4().hex;folder,snapshots,index=session_paths(workspace,session)
        folder.mkdir(parents=True)
        issues=[];success=0
        for pos,path in enumerate(files):
            check();relative=str(path.relative_to(root))
            try:
                wal=Path(str(path)+'-wal')
                if not wal.resolve().is_relative_to(root): raise ValueError('WAL链接越过源目录')
                key=bytes.fromhex(keys[relative]) if relative in keys else None
                live_snapshot(path,snapshots/relative,key,check,progress);success+=1
            except Cancelled: raise
            except Exception as exc:
                diagnostics.failure(exc,'snapshot',database_kind=path.stem.split('_')[0])
                issues.append(path.name+': '+str(exc))
            progress({'phase':'snapshot','current':pos+1,'total':len(files),'success':success})
        try:report=library.build(snapshots,index,check,progress)
        except ValueError as exc:
            if issues:raise ValueError(str(exc)+'；副本详情：'+'；'.join(issues[:8])) from None
            raise
        issues+=report['issues'];report.update(session=session,issues=issues,source=str(root),databases=success,expected_databases=len(files))
        report['snapshot_mode']='online-per-database'
        report['snapshot_note']='各库分别冻结并验证，不代表跨库同一时刻；需要近期语音时可重新连接刷新。'
        if connection_report is not None: report['key_report']=connection_report
        write_json(folder/'session.json',report)
        return report
    if action=='list':
        _,_,index=session_paths(workspace,request.get('session'))
        return library.listing(index,request)
    if action=='count':
        _,_,index=session_paths(workspace,request.get('session'))
        return {'count':library.counting(index,request)}
    if action=='export': return export(workspace,request,check)
    if action=='recognize_page':return recognize_page(workspace,request,check)
    if action=='preview':
        folder,snapshots,index=session_paths(workspace,request.get('session'))
        rows=list(library.records(index,{'ids':[request['id']]}))
        if len(rows)!=1: raise ValueError('该语音未找到')
        cache=folder/'preview';cache.mkdir(exist_ok=True)
        raw=cache/(rows[0]['id']+'.silk');wav=raw.with_suffix('.wav')
        if not wav.exists():
            raw.write_bytes(library.payload(snapshots,rows[0]));voice_codec.isolated(raw,wav,check)
        return {'path':str(wav)}
    if action=='self-test':
        import pysilk
        return {'worker':'1.0.0-preview14','codec':pysilk.__version__,'target_wechat':capture.TARGET_VERSION,'verified_wechat_version':capture.VERIFIED_VERSION,'native_tests':False}
    raise ValueError('不支持的操作')


def main():
    started=time.monotonic()
    try:
        # Deployment includes the CPython/Cython backend only, independent of global shell settings.
        os.environ.pop('SILK_USE_CFFI',None)
        line=sys.stdin.buffer.readline(262145)
        if len(line)>262144: raise ValueError('请求超过256KiB')
        request=json.loads(line)
        if not isinstance(request,dict): raise ValueError('请求格式错误')
        if '--decode' not in sys.argv:
            diagnostics.start(request.get('workspace',Path(sys.executable).parent/'workspace'),request.get('request_id'))
            diagnostics.event('begin',action=request.get('action'),selected_count=len(request.get('ids',[])) if isinstance(request.get('ids'),list) else 0,
                has_chat=bool(request.get('chat')),has_dates='after' in request or 'before' in request,transcribe=request.get('transcribe') is True,keep_silk=request.get('keep_silk',True))
        if '--decode' in sys.argv:
            emit({'ok':True,'duration':voice_codec.decode(request['source'],request['target'])})
        else:
            result=execute(request)
            diagnostics.event('complete',elapsed_ms=int((time.monotonic()-started)*1000),**{k:v for k,v in result.items() if k in diagnostics.FIELDS})
            if 'issues' in result:diagnostics.event('catalog_summary',issue_count=len(result['issues']))
            if 'key_report' in result:diagnostics.event('key_summary',**{k:v for k,v in result['key_report'].items() if k in diagnostics.FIELDS})
            emit({'type':'result','ok':True,'data':result})
        return 0
    except Cancelled:
        diagnostics.event('cancelled',elapsed_ms=int((time.monotonic()-started)*1000))
        emit({'type':'result','ok':False,'cancelled':True,'error':'操作已取消，已生成的副本和文件保留在工作目录'});return 2
    except Exception as exc:
        diagnostics.failure(exc)
        # No traceback, database content or request payload in stdout/stderr.
        emit({'type':'result','ok':False,'error':str(exc)[:1000]});return 1


if __name__=='__main__':
    raise SystemExit(main())
