"""A paged private catalogue. Payloads remain in immutable database snapshots."""
import csv
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import sqlite3
from cryptodb import read_only

MAX_BLOB = 8*1024*1024


def ident(value):
    if not re.fullmatch(r'[A-Za-z_][A-Za-z_0-9]*', value):
        raise ValueError('数据库字段名称不受支持')
    return '"'+value+'"'


def columns(db, table):
    return {r[1].lower():r[1] for r in db.execute('PRAGMA table_info('+ident(table)+')')}


def names(db):
    if 'user_name' not in columns(db, 'Name2Id'):
        raise ValueError('Name2Id格式不同，需要补充适配；未猜测联系人')
    return {int(r[0]):str(r[1] or '') for r in db.execute('SELECT rowid,user_name FROM Name2Id')}


def build(folder, index_path, check, progress):
    folder=Path(folder).resolve();index_path=Path(index_path)
    if index_path.exists():
        raise FileExistsError('索引已存在，请新建读取会话')
    db=sqlite3.connect(index_path)
    db.executescript('''CREATE TABLE contacts(user TEXT PRIMARY KEY,label TEXT);
      CREATE TABLE messages(chat_hash TEXT,local_id INTEGER,stamp INTEGER,sender TEXT,server_id TEXT);
      CREATE INDEX msg_key ON messages(chat_hash,local_id,stamp);
      CREATE INDEX msg_server ON messages(chat_hash,server_id,stamp);
      CREATE TABLE voices(id TEXT PRIMARY KEY,db TEXT,rid INTEGER,chat TEXT,label TEXT,
        local_id INTEGER,stamp INTEGER,server_id TEXT,sender TEXT,bytes INTEGER);
      CREATE INDEX voice_time ON voices(stamp,id); CREATE INDEX voice_chat ON voices(chat,stamp);''')
    total=0;expected=0;issues=[];valid_media=0
    try:
        for path in folder.rglob('contact.db'):
            check()
            with read_only(path) as source:
                cols=columns(source,'contact')
                if 'username' not in cols:
                    issues.append('联系人表格式不同，使用账号标识显示');continue
                extras=[c for c in ('remark','nick_name','alias') if c in cols]
                for row in source.execute('SELECT '+','.join(ident(cols[c]) for c in ['username']+extras)+' FROM contact'):
                    check();user=str(row[0] or '')
                    label=next((str(v) for v in row[1:] if v),user)
                    db.execute('INSERT OR REPLACE INTO contacts VALUES(?,?)',(user,label[:200]))
        labels=dict(db.execute('SELECT user,label FROM contacts'))
        for path in folder.rglob('message_*.db'):
            check()
            try:
                with read_only(path) as source:
                    mapping=names(source)
                    tables=[r[0] for r in source.execute("SELECT name FROM sqlite_master WHERE type='table'") if re.fullmatch(r'Msg_[0-9a-fA-F]{32}',r[0])]
                    for table in tables:
                        check();cols=columns(source,table)
                        if not {'local_id','create_time','local_type'} <= cols.keys():
                            issues.append(path.name+': 消息字段不同，发送者可能未知');continue
                        sender=ident(cols['real_sender_id']) if 'real_sender_id' in cols else 'NULL'
                        server=ident(cols['server_id']) if 'server_id' in cols else "''"
                        for local,stamp,sender_id,svr in source.execute('SELECT local_id,create_time,'+sender+','+server+' FROM '+ident(table)+' WHERE (local_type & 4294967295)=34'):
                            check();expected+=1
                            if expected > 2000000: raise ValueError('语音消息超过200万条，首版暂不支持')
                            db.execute('INSERT INTO messages VALUES(?,?,?,?,?)',(table[4:].lower(),int(local),int(stamp),mapping.get(sender_id,''),str(svr or '')))
            except (sqlite3.Error,ValueError) as exc:
                issues.append(path.name+': 消息结构不兼容，发送者标为未知')
        for path in folder.rglob('media_*.db'):
            check()
            with read_only(path) as source:
                cols=columns(source,'VoiceInfo')
                if not {'chat_name_id','local_id','create_time','voice_data'} <= cols.keys():
                    issues.append(path.name+': VoiceInfo格式不同，未读取');continue
                mapping=names(source);valid_media+=1
                server=ident(cols['svr_id']) if 'svr_id' in cols else "''"
                sql='SELECT rowid,chat_name_id,local_id,create_time,'+server+',length(voice_data) FROM VoiceInfo'
                for rid,chat_id,local,stamp,svr,size in source.execute(sql):
                    check();total+=1
                    if total>1000000: raise ValueError('语音超过100万条，首版暂不支持')
                    chat=mapping.get(chat_id,'')
                    if not chat:
                        chat='未知会话-'+str(chat_id)+'-'+path.stem
                    chat_hash=hashlib.md5(chat.encode('utf-8')).hexdigest()
                    # Media and message local IDs need not be the same. Prefer the
                    # exact server ID, still scoped to this conversation and time.
                    server_id=str(svr or '')
                    if server_id.isdigit() and int(server_id)>0:
                        matches=list(db.execute('SELECT DISTINCT sender FROM messages WHERE chat_hash=? AND server_id=? AND stamp=?',(chat_hash,server_id,int(stamp))))
                        if not matches:
                            matches=list(db.execute("SELECT DISTINCT sender FROM messages WHERE chat_hash=? AND local_id=? AND stamp=? AND server_id IN ('','0')",(chat_hash,int(local),int(stamp))))
                    else:
                        matches=list(db.execute('SELECT DISTINCT sender FROM messages WHERE chat_hash=? AND local_id=? AND stamp=?',(chat_hash,int(local),int(stamp))))
                    sender=matches[0][0] if len(matches)==1 and matches[0][0] else ''
                    relative=path.relative_to(folder).as_posix()
                    identity=hashlib.sha256((relative+'|'+str(rid)).encode()).hexdigest()[:24]
                    db.execute('INSERT INTO voices VALUES(?,?,?,?,?,?,?,?,?,?)',(identity,relative,int(rid),chat,labels.get(chat,chat)[:200],int(local),int(stamp),str(svr or ''),labels.get(sender,sender) if sender else '未知发送者',int(size or 0)))
                    if total%200==0: progress({'phase':'catalog','count':total})
        if not valid_media:
            raise ValueError('未找到支持的VoiceInfo表；此数据库结构暂不兼容，不能仅凭微信4.x版本号判定可读取')
        db.commit()
        return {'count':total,'message_voices':expected,'issues':list(dict.fromkeys(issues))[:100]}
    finally:
        db.close()


def query_filter(request):
    terms=[];args=[]
    if request.get('chat'):
        terms.append('chat=?');args.append(str(request['chat']))
    for field,operator in [('after','>='),('before','<')]:
        if request.get(field) is not None:
            terms.append('stamp'+operator+'?');args.append(int(request[field]))
    if request.get('after') is not None and request.get('before') is not None and int(request['after'])>=int(request['before']):
        raise ValueError('开始日期必须早于结束边界')
    ids=request.get('ids')
    if 'ids' in request:
        if not isinstance(ids,list) or len(ids)>500 or any(not re.fullmatch('[0-9a-f]{24}',str(x)) for x in ids):
            raise ValueError('选择项无效或超过500条')
        terms.append('id IN ('+','.join('?' for _ in ids)+')' if ids else '0');args.extend(ids)
    return (' WHERE '+' AND '.join(terms) if terms else ''),args


def records(index_path, request, page=None):
    where,args=query_filter(request)
    with read_only(index_path) as db:
        db.row_factory=sqlite3.Row
        sql='SELECT * FROM voices'+where+' ORDER BY stamp,id'
        if page is not None:
            import speech_cache
            fields={'stamp':'v.stamp','sender':'v.sender COLLATE NOCASE','label':'v.label COLLATE NOCASE','bytes':'v.bytes','local_id':'v.local_id','transcript':"''",'recognition_status':'CAST(2 AS INTEGER)'}
            column=request.get('sort','stamp');descending=request.get('descending',False)
            if column not in fields or not isinstance(descending,bool):raise ValueError('语音排序条件无效')
            join=''
            if column in ('transcript','recognition_status') and speech_cache.path(index_path).exists():
                try:
                    db.execute('ATTACH DATABASE ? AS speech',(speech_cache.path(index_path).resolve().as_uri()+'?mode=ro',))
                    db.execute('SELECT id FROM speech.transcripts LIMIT 0')
                    join=' LEFT JOIN speech.transcripts t ON t.id=v.id AND t.profile=?';args.append(speech_cache.PROFILE)
                    fields['transcript']="COALESCE(t.text,'') COLLATE NOCASE"
                    fields['recognition_status']="CASE t.status WHEN 'READY' THEN 0 WHEN 'EMPTY' THEN 1 WHEN 'ERROR' THEN 3 ELSE 2 END"
                except sqlite3.Error:
                    pass # Cache warnings are reported by listing/annotate; source rows remain readable.
            sql='SELECT v.* FROM (SELECT * FROM voices'+where+') v'+join+' ORDER BY '+fields[column]+(' DESC' if descending else ' ASC')+',v.stamp ASC,v.id ASC'
            sql+=' LIMIT 50 OFFSET ?';args.append(max(0,int(page))*50)
        for row in db.execute(sql,args):
            yield dict(row)


def counting(index_path, request):
    where,args=query_filter(request)
    with read_only(index_path) as db:
        return db.execute('SELECT COUNT(*) FROM voices'+where,args).fetchone()[0]


def listing(index_path, request):
    import speech_cache
    count=counting(index_path,request)
    contacts=[]
    if request.get('include_contacts',True):
        with read_only(index_path) as db:
            contacts=[{'user':r[0],'label':r[1],'count':r[2],'first_stamp':r[3],'latest_stamp':r[4]} for r in db.execute('SELECT chat,label,COUNT(*),MIN(stamp),MAX(stamp) FROM voices GROUP BY chat,label ORDER BY label LIMIT 10000')]
    page=max(0,int(request.get('page',0)))
    items=list(records(index_path,request,page));warning=speech_cache.annotate(index_path,items)
    return {'count':count,'page':page,'items':items,'contacts':contacts,'cache_warning':warning}


def payload(folder, row):
    root=Path(folder).resolve();path=(root/row['db']).resolve()
    if not path.is_relative_to(root): raise ValueError('快照路径越界')
    with read_only(path) as db:
        found=db.execute('SELECT length(voice_data) FROM VoiceInfo WHERE rowid=?',(row['rid'],)).fetchone()
        if not found or not found[0] or found[0]>MAX_BLOB:
            raise ValueError('语音为空或大于8MiB，原始数据库副本仍保留')
        data=db.execute('SELECT voice_data FROM VoiceInfo WHERE rowid=?',(row['rid'],)).fetchone()[0]
    return bytes(data)


def filename(row):
    def clean(text):
        value=re.sub(r'[<>:"/\\|?*\x00-\x1f]','_',str(text)).strip(' .')[:36] or '未知'
        if re.match(r'^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)',value,re.I): value='_'+value
        return value
    try: stamp=datetime.fromtimestamp(row['stamp']).strftime('%Y%m%d-%H%M%S')
    except (ValueError,OSError,OverflowError): stamp='时间未知'
    return stamp+'_'+clean(row['label'])+'_'+str(row['local_id'])+'_'+row['id']


def wav_filename(row,text):
    try: stamp=datetime.fromtimestamp(row['stamp']).strftime('%Y%m%d-%H%M%S')
    except (ValueError,OSError,OverflowError): stamp='时间未知'
    summary=re.sub(r'<\|.*?\|>','',str(text or ''))
    summary=re.sub(r'[<>:"/\\|?*\x00-\x1f\x7f]','_',summary)
    summary=re.sub(r'\s+',' ',summary).strip(' ._')[:40].rstrip(' .') or '未识别语音'
    return stamp+'_'+row['id']+'_'+summary+'.wav'
