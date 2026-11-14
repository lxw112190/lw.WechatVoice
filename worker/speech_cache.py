"""Private per-snapshot transcript cache. Never part of diagnostic bundles."""
import sqlite3
from contextlib import closing
from pathlib import Path
import diagnostics

PROFILE='sensevoice-c71f0ce0-sherpa2.0.1-auto-itn-v1'


def path(index):return Path(index).parent/'transcripts.sqlite'


def lookup(index,identity,digest):
    rows=read(index,[identity])
    value=rows.get(identity)
    return value if value and value['digest']==digest and value['status'] in ('READY','EMPTY') else None


def read(index,ids):
    file=path(index)
    if not file.exists() or not ids:return {}
    if len(ids)>50:raise ValueError('缓存每次只读取一页')
    with closing(sqlite3.connect(file.resolve().as_uri()+'?mode=ro',uri=True,timeout=5)) as db:
        rows=db.execute('SELECT id,digest,status,text FROM transcripts WHERE profile=? AND id IN ('+','.join('?' for _ in ids)+')',[PROFILE,*ids])
        return {r[0]:{'digest':r[1],'status':r[2],'text':r[3]} for r in rows if r[2] in ('READY','EMPTY','ERROR') and isinstance(r[3],str) and len(r[3])<=2000}


def save(index,identity,digest,status,text):
    if status not in ('READY','EMPTY','ERROR') or not isinstance(text,str) or len(text)>2000:raise ValueError('识别缓存格式无效')
    with closing(sqlite3.connect(path(index),timeout=5)) as db:
        with db:
            db.execute('CREATE TABLE IF NOT EXISTS transcripts(id TEXT PRIMARY KEY,digest TEXT NOT NULL,profile TEXT NOT NULL,status TEXT NOT NULL,text TEXT NOT NULL)')
            db.execute('INSERT OR REPLACE INTO transcripts VALUES(?,?,?,?,?)',(identity,digest,PROFILE,status,text))


def annotate(index,items):
    try:values=read(index,[r['id'] for r in items]);warning=False
    except (sqlite3.Error,OSError) as exc:
        diagnostics.failure(exc,'speech_cache');values={};warning=True
    for row in items:
        value=values.get(row['id'],{})
        row.update(transcript=value.get('text',''),recognition_status=value.get('status','UNCACHED'))
    return warning
