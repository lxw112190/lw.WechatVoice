"""Synthetic favorites only: no WeChat accounts, process reads or real recordings."""
import hashlib
from contextlib import closing
import importlib.util
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import uuid
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]


def check(engine):
    sys.path[:0] = [str(ROOT / 'tests'), str(ROOT / '.deps/vendor')]
    from test_core import tone
    with tempfile.TemporaryDirectory(prefix='favorites-', dir=ROOT / 'test-results') as temp:
        root = Path(temp); account = root / 'account'; dbroot = account / 'db_storage'
        (dbroot / 'favorite').mkdir(parents=True); (dbroot / 'message').mkdir()
        cache = account / 'business/favorite/data'; cache.mkdir(parents=True)
        data = tone(); digest = hashlib.md5(data).hexdigest()
        (cache / 'opaque-cache-name').write_bytes(data)
        # Misleading filename is not evidence: its bytes do not match the MD5.
        (cache / ('b' * 32)).write_bytes(data)
        def block(md5='', server='', datatype=3):
            return f'<dataitem datatype="{datatype}"><fullmd5>{md5}</fullmd5><fromnewmsgid>{server}</fromnewmsgid><datafmt>silk</datafmt></dataitem>'
        def xml(body, title='收藏测试'):
            return f'<favitem><title>{title}</title><datalist>{body}</datalist></favitem>'
        with closing(sqlite3.connect(dbroot / 'favorite/favorite.db')) as db, db:
            db.execute('CREATE TABLE fav_db_item(local_id INTEGER,type INTEGER,update_time INTEGER,content TEXT,fromusr TEXT,realchatname TEXT,flag INTEGER)')
            records = [
                (1, 3, 100, xml(block(digest))),
                (2, 14, 200, xml(block(digest, datatype=4) + '<dataitem datatype="17"><recorditem>' + escape('<recordinfo><datalist>' + block(digest, datatype=4) + '</datalist></recordinfo>') + '</recorditem></dataitem>', '聊天记录')),
                (3, 3, 300, xml(block('b' * 32))),
                (4, 3, 400, xml(block(server=str(2**64-1)))),
                (5, 3, 500, xml(block(server='777'))),
                (6, 3, 600, '<!DOCTYPE favitem [<!ENTITY x SYSTEM "file:///synthetic-private">]><favitem><title>&x;</title></favitem>'),
                (7, 3, 700, xml(block(digest))),
                (8, 1, 800, '<favitem><desc>非语音</desc></favitem>')]
            for i, typ, stamp, content in records:
                db.execute('INSERT INTO fav_db_item VALUES(?,?,?,?,?,?,?)', (i,typ,stamp,content,'synthetic-user','',1 if i == 7 else 0))
        with closing(sqlite3.connect(dbroot / 'message/media_0.db')) as db, db:
            db.execute('CREATE TABLE VoiceInfo(svr_id INTEGER,voice_data BLOB)')
            db.executemany('INSERT INTO VoiceInfo VALUES(?,?)', [(-1,data),(777,data),(777,b'different-bytes')])
        before = {p.relative_to(account): p.read_bytes() for p in account.rglob('*') if p.is_file()}
        work = root / 'work'
        def request(expect_ok=True, **payload):
            payload['workspace'] = str(work)
            payload['request_id'] = uuid.uuid4().hex
            process = subprocess.run([str(engine)],input=json.dumps(payload).encode()+b'\n',capture_output=True,timeout=45)
            result = json.loads(process.stdout.decode('utf-8-sig').splitlines()[-1])
            if not expect_ok:
                assert not result['ok'], result
                return result
            assert result['ok'], result
            return result['data']
        catalog = request(action='catalog',source=str(account),consent=True,favorites=True)
        assert catalog['favorites'] and catalog['count'] == 7 and catalog['missing'] == 3, catalog
        session = catalog['session']; page = request(action='list',session=session)
        assert page['count'] == 7 and len(page['items']) == 7
        assert len(request(action='list',session=session,after=200,before=300)['items']) == 2
        assert request(action='count',session=session,ids=[])['count'] == 0
        good = [x for x in page['items'] if x['bytes'] > 0]
        missing = [x for x in page['items'] if x['bytes'] == 0]
        assert {x['local_id'] for x in missing} == {3,5,6}
        assert len(good) == 4
        # Removing the source cache after snapshot creation must not affect export.
        (cache / 'opaque-cache-name').unlink(); (cache / ('b' * 32)).unlink()
        preview = request(action='preview',session=session,id=good[0]['id'])
        assert preview
        error=request(expect_ok=False,action='preview',session=session,id=missing[0]['id'])
        assert error['error_code']=='FAVORITE_AUDIO_MISSING',error
        frozen=work/'sessions'/session/'snapshots'/good[1]['db']
        original=frozen.read_bytes()
        try:
            frozen.write_bytes(bytes([original[0]^1])+original[1:])
            error=request(expect_ok=False,action='preview',session=session,id=good[1]['id'])
            assert error['error_code']=='FAVORITE_AUDIO_CHANGED',error
        finally:frozen.write_bytes(original)
        export = request(action='export',session=session,output=str(root/'exports'),ids=[x['id'] for x in good],keep_silk=True,transcribe=False)
        assert export['completed'] == 4 and export['failed'] == 0, export
        assert all(p.read_bytes() == data for p in Path(export['output']).glob('*.silk'))
        failed = request(action='export',session=session,output=str(root/'missing'),ids=[x['id'] for x in missing],transcribe=False)
        assert failed['completed'] == 0 and failed['failed'] == 3, failed
        all_export = request(action='export',session=session,output=str(root/'all-favorites'),transcribe=False)
        assert all_export['completed'] == 4 and all_export['failed'] == 3, all_export
        ui=ROOT/'build/FavoritesUiCheck.exe'
        if ui.is_file():
            subprocess.run([str(ui),str(ROOT/'src/WechatVoice.Workshop/bin/Release/WechatVoice.Workshop.exe'),str(engine),str(work),session],check=True,timeout=45)
        for relative, contents in before.items():
            if relative.parts[:2] != ('business','favorite'): assert (account / relative).read_bytes() == contents
        # A favorites-only account does not require the chat media database.
        (dbroot/'message/media_0.db').unlink()
        again = request(action='catalog',source=str(account),consent=True,favorites=True)
        assert again['count'] == 7 and again['missing'] == 7
        logs=[json.loads(line) for p in (work/'diagnostics').glob('worker-*.jsonl') for line in p.read_text(encoding='utf-8-sig').splitlines()]
        summary=next(row for row in logs if row.get('event')=='favorite_summary' and row.get('favorite_missing')==3)
        assert summary['favorite_cache_hits']==3 and summary['favorite_server_hits']==1 and summary['favorite_ambiguous']==1,summary
        assert any(row.get('error_code')=='FAVORITE_AUDIO_MISSING' for row in logs)
        assert any(row.get('error_code')=='FAVORITE_AUDIO_CHANGED' for row in logs)
        print('PASS: favorite MD5 identity, nested record voices, flags, XXE refusal, uint64 server ID, ambiguous/missing sources, date/ID filters, frozen preview/WAV export and source database immutability.')


if __name__ == '__main__':
    (ROOT/'test-results').mkdir(exist_ok=True)
    check(Path(sys.argv[1]).resolve())
