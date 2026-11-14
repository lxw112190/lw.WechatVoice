"""Synthetic fixtures only. Never touches a WeChat account or process."""
import ctypes
import contextlib
from datetime import datetime
import hashlib
import hmac
import io
import json
import math
import os
from pathlib import Path
import shutil
import sqlite3
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import uuid
import wave

ROOT=Path(__file__).resolve().parents[1]
sys.path[:0]=[str(ROOT/'worker'),str(ROOT/'.deps/vendor')]
from Cryptodome.Cipher import AES
import pysilk
import capture
import cryptodb
import library
import voice_codec
import cli
import key_reader
import diagnostics
import speech_cache


@contextlib.contextmanager
def writable(path):
    db=sqlite3.connect(path)
    try:
        yield db
        db.commit()
    finally: db.close()


def tone():
    pcm=struct.pack('<'+('h'*24000),*(int(10000*math.sin(2*math.pi*440*x/24000)) for x in range(24000)))
    out=io.BytesIO();pysilk.encode(io.BytesIO(pcm),out,24000,24000,tencent=True)
    return out.getvalue()


def create_reserved(path):
    # Exercise real SQLite reserve pages, not a mutually agreeing fake page parser.
    dll=next((p for p in [Path(sys.base_prefix)/'Library/bin/sqlite3.dll',Path(sys.base_prefix)/'DLLs/sqlite3.dll',Path(sys.base_prefix)/'sqlite3.dll'] if p.is_file()), Path(sys.base_prefix)/'DLLs/sqlite3.dll')
    if not dll.exists(): raise unittest.SkipTest('Encrypted fixture needs SQLite C API with reserve_bytes file control')
    api=ctypes.CDLL(str(dll));pointer=ctypes.c_void_p()
    api.sqlite3_open.argtypes=[ctypes.c_char_p,ctypes.POINTER(ctypes.c_void_p)]
    api.sqlite3_exec.argtypes=[ctypes.c_void_p,ctypes.c_char_p,ctypes.c_void_p,ctypes.c_void_p,ctypes.c_void_p]
    api.sqlite3_file_control.argtypes=[ctypes.c_void_p,ctypes.c_char_p,ctypes.c_int,ctypes.c_void_p]
    api.sqlite3_close.argtypes=[ctypes.c_void_p]
    assert api.sqlite3_open(str(path).encode('utf-8'),ctypes.byref(pointer))==0
    try:
        reserve=ctypes.c_int(80)
        assert api.sqlite3_file_control(pointer,b'main',38,ctypes.byref(reserve))==0
        assert api.sqlite3_exec(pointer,b'PRAGMA page_size=4096; CREATE TABLE sample(id INTEGER PRIMARY KEY,value TEXT); INSERT INTO sample VALUES(1,\'before\');',None,None,None)==0
    finally: api.sqlite3_close(pointer)
    assert path.read_bytes()[20]==80


def encrypt_page(page,number,key,salt):
    start=16 if number==1 else 0
    iv=bytes([number%256])*16
    cipher=AES.new(key,AES.MODE_CBC,iv).encrypt(page[start:4016])
    body=(salt if number==1 else b'')+cipher+iv
    hkey=hashlib.pbkdf2_hmac('sha512',key,bytes(x^58 for x in salt),2,32)
    return body+hmac.new(hkey,body[start:]+number.to_bytes(4,'little'),hashlib.sha512).digest()


def encrypted_file(plain,target,key,salt):
    raw=plain.read_bytes()
    target.write_bytes(b''.join(encrypt_page(raw[i:i+4096],i//4096+1,key,salt) for i in range(0,len(raw),4096)))


def checksum(data,previous=(0,0),endian='<'):
    a,b=previous
    for at in range(0,len(data),8):
        x=int.from_bytes(data[at:at+4],'little' if endian=='<' else 'big');y=int.from_bytes(data[at+4:at+8],'little' if endian=='<' else 'big')
        a=(a+x+b)%2**32;b=(b+y+a)%2**32
    return a,b


def transform_wal(source,target,key,salt):
    raw=source.read_bytes();header=raw[:32];running=checksum(header[:24]);result=bytearray(header[:24]+struct.pack('>II',*running))
    for at in range(32,len(raw),4120):
        frame=raw[at:at+4120];number=int.from_bytes(frame[:4],'big')
        page=encrypt_page(frame[24:],number,key,salt);running=checksum(frame[:8]+page,running)
        result+=frame[:16]+struct.pack('>II',*running)+page
    target.write_bytes(result)


def dataset(root, count=3, blob=None):
    root.mkdir(parents=True,exist_ok=True);blob=tone() if blob is None else blob
    with writable(root/'contact.db') as db:
        db.executescript("CREATE TABLE contact(username TEXT,remark TEXT,nick_name TEXT); INSERT INTO contact VALUES('chat','测试会话',''); INSERT INTO contact VALUES('a','甲',''); INSERT INTO contact VALUES('b','乙','');")
    with writable(root/'media_0.db') as db:
        db.executescript('CREATE TABLE Name2Id(user_name TEXT); INSERT INTO Name2Id(rowid,user_name) VALUES(7,\'chat\'); CREATE TABLE VoiceInfo(chat_name_id INTEGER,local_id INTEGER,create_time INTEGER,svr_id INTEGER,voice_data BLOB);')
        for i in range(count): db.execute('INSERT INTO VoiceInfo VALUES(7,1,?,100,?)',(1700000000+i,blob))
    table='Msg_'+hashlib.md5(b'chat').hexdigest()
    for shard,user in enumerate(['a','b']):
        with writable(root/('message_'+str(shard)+'.db')) as db:
            db.executescript('CREATE TABLE Name2Id(user_name TEXT); INSERT INTO Name2Id(rowid,user_name) VALUES(3,\''+user+'\'); CREATE TABLE '+table+'(local_id INTEGER,create_time INTEGER,local_type INTEGER,real_sender_id INTEGER);')
            db.execute('INSERT INTO '+table+' VALUES(1,?,34,3)',(1700000000+shard,))
    return root


def conversation_dataset(root):
    dataset(root);blob=tone()
    with writable(root/'contact.db') as db:
        db.executemany('INSERT INTO contact VALUES(?,?,?)',[('demo@chatroom','周末群',''),('friend_alice','好友 Alice','')])
    with writable(root/'media_0.db') as db:
        db.executemany('INSERT INTO Name2Id(rowid,user_name) VALUES(?,?)',[(8,'demo@chatroom'),(9,'friend_alice')])
        for chat_id,stamp in [(8,1700000010),(8,1700000011),(9,1700000020)]:
            db.execute('INSERT INTO VoiceInfo VALUES(?,?,?,?,?)',(chat_id,stamp-1700000000,stamp,stamp,blob))
    return root


class CoreTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='语音工坊测试-');self.root=Path(self.temp.name)
    def tearDown(self): self.temp.cleanup()
    def test_authenticated_encrypted_sqlite_and_no_overwrite(self):
        plain=self.root/'合成原库.db';create_reserved(plain);source=self.root/'加密.db';dest=self.root/'副本.db'
        key=bytes(range(32));salt=b'0123456789abcdef';encrypted_file(plain,source,key,salt);before=source.read_bytes()
        self.assertEqual(cryptodb.first_page_key(source,key),key);self.assertIsNone(cryptodb.first_page_key(source,b'x'*32))
        cryptodb.snapshot(source,dest,key)
        with cryptodb.read_only(dest) as db: self.assertEqual(db.execute('SELECT value FROM sample').fetchone()[0],'before')
        self.assertEqual(before,source.read_bytes())
        with self.assertRaises(FileExistsError): cryptodb.snapshot(source,dest,key)
    def test_authentication_rejects_tampered_page(self):
        plain=self.root/'p.db';create_reserved(plain);source=self.root/'e.db';dest=self.root/'d.db'
        key=b'k'*32;encrypted_file(plain,source,key,b's'*16)
        raw=bytearray(source.read_bytes());raw[100]^=1;source.write_bytes(raw)
        with self.assertRaisesRegex(ValueError,'认证'): cryptodb.snapshot(source,dest,key)
        self.assertFalse(dest.exists());self.assertFalse(list(self.root.glob('*.part')))
    def test_master_derivation_independent(self):
        master=b'passphrase';salt=b'0123456789abcdef'
        self.assertEqual(cryptodb.derive(master,salt),hashlib.pbkdf2_hmac('sha512',master,salt,256000,32))
    def wal_fixture(self,reserved=False):
        source=self.root/'live.db'
        if reserved: create_reserved(source)
        else:
            with writable(source) as db: db.execute('CREATE TABLE sample(id INTEGER PRIMARY KEY,value TEXT)');db.execute("INSERT INTO sample VALUES(1,'before')")
        live=sqlite3.connect(source);live.execute('PRAGMA journal_mode=WAL');live.execute('PRAGMA wal_autocheckpoint=0')
        base=self.root/'base.db';shutil.copyfile(source,base)
        live.execute("UPDATE sample SET value='after' WHERE id=1");live.commit()
        shutil.copyfile(str(source)+'-wal',str(base)+'-wal');live.close()
        return base
    def test_plain_wal_latest_committed_value(self):
        source=self.wal_fixture();dest=self.root/'snap.db';before=(source.read_bytes(),Path(str(source)+'-wal').read_bytes())
        cryptodb.snapshot(source,dest,None)
        with cryptodb.read_only(dest) as db: self.assertEqual(db.execute('SELECT value FROM sample').fetchone()[0],'after')
        self.assertEqual(before,(source.read_bytes(),Path(str(source)+'-wal').read_bytes()))
    def test_online_copy_replays_wal_without_waiting_for_original_to_stop(self):
        source=self.wal_fixture();dest=self.root/'live-copy.db'
        original=cryptodb.snapshot
        before=source.read_bytes()
        def decrypt_copy(frozen,target,key,check):
            self.assertNotEqual(Path(frozen),source)
            # Source changes AFTER the pair is frozen must not invalidate it.
            with source.open('ab') as writer: writer.write(b'new data')
            original(frozen,target,key,check)
        with patch.object(cryptodb,'snapshot',side_effect=decrypt_copy):
            cryptodb.live_snapshot(source,dest,None)
        with cryptodb.read_only(dest) as db: self.assertEqual(db.execute('SELECT value FROM sample').fetchone()[0],'after')
        self.assertTrue(source.read_bytes().startswith(before))
    def test_online_retry_is_bounded_and_never_ignores_bad_wal(self):
        source=self.wal_fixture();wal=Path(str(source)+'-wal');wal.write_bytes(wal.read_bytes()[:-1])
        dest=self.root/'bad-live.db';updates=[]
        with self.assertRaisesRegex(ValueError,'三次'):
            cryptodb.live_snapshot(source,dest,None,progress=updates.append)
        self.assertEqual(len(updates),2);self.assertFalse(dest.exists())
        self.assertFalse(list(self.root.glob('live-*')))
    def test_connect_uses_only_explicit_pid_and_account(self):
        source=dataset(self.root/'source');work=self.root/'work'
        def scanner(pid,folder,check,progress):
            self.assertEqual(pid,1234);self.assertEqual(Path(folder),source)
            return source,{}, {'matched':0,'encrypted':0,'version':'4.0.5.16'}
        with patch.object(capture,'scan',side_effect=scanner):
            report=cli.execute({'action':'connect','source':str(source),'pid':1234,'consent':True,'workspace':str(work)})
        self.assertEqual(report['count'],3);self.assertEqual(report['snapshot_mode'],'online-per-database')
        with self.assertRaisesRegex(ValueError,'授权'):
            cli.execute({'action':'connect','source':str(source),'pid':1234,'workspace':str(work)})
    def test_encrypted_wal_replay(self):
        plain=self.wal_fixture(True);source=self.root/'cipher.db';dest=self.root/'out.db';key=b'a'*32;salt=b'b'*16
        encrypted_file(plain,source,key,salt);transform_wal(Path(str(plain)+'-wal'),Path(str(source)+'-wal'),key,salt)
        cryptodb.snapshot(source,dest,key)
        with cryptodb.read_only(dest) as db: self.assertEqual(db.execute('SELECT value FROM sample').fetchone()[0],'after')
    def test_wal_corruption_and_truncation_refuse_snapshot(self):
        source=self.wal_fixture();wal=Path(str(source)+'-wal');good=wal.read_bytes()
        for bad in [good[:-1],good[:60]+bytes([good[60]^1])+good[61:]]:
            wal.write_bytes(bad)
            with self.assertRaises(ValueError): cryptodb.snapshot(source,self.root/'snap.db',None)
            self.assertFalse((self.root/'snap.db').exists())
    def test_valid_uncommitted_wal_tail_is_ignored(self):
        source=self.wal_fixture();wal=Path(str(source)+'-wal');data=wal.read_bytes();frame=data[-4120:];running=struct.unpack('>II',frame[16:24])
        page=bytearray(frame[24:]);pos=page.find(b'after');self.assertGreaterEqual(pos,0);page[pos:pos+5]=b'other'
        first8=frame[:4]+b'\0'*4;newsum=checksum(first8+page,running)
        wal.write_bytes(data+first8+frame[8:16]+struct.pack('>II',*newsum)+page)
        cryptodb.snapshot(source,self.root/'snap.db',None)
        with cryptodb.read_only(self.root/'snap.db') as db: self.assertEqual(db.execute('SELECT value FROM sample').fetchone()[0],'after')
    def test_preallocated_old_wal_generation_is_logical_end(self):
        source=self.wal_fixture();wal=Path(str(source)+'-wal');raw=wal.read_bytes()
        first=bytearray(raw[32:32+4120]);first[8]^=1
        wal.write_bytes(raw[:32]+first+b'old preallocation bytes')
        dest=self.root/'old-wal.db';cryptodb.snapshot(source,dest,None)
        with cryptodb.read_only(dest) as db:self.assertEqual(db.execute('SELECT value FROM sample').fetchone()[0],'before')
    def test_committed_wal_before_old_generation_tail_is_replayed(self):
        source=self.wal_fixture();wal=Path(str(source)+'-wal');raw=wal.read_bytes()
        old=bytearray(raw[-4120:]);old[8]^=1;wal.write_bytes(raw+old+b'padding-not-current-data')
        dest=self.root/'commit-before-tail.db';cryptodb.snapshot(source,dest,None)
        with cryptodb.read_only(dest) as db:self.assertEqual(db.execute('SELECT value FROM sample').fetchone()[0],'after')
    def test_candidates_ascii_utf16_and_unrelated_not_accepted(self):
        key=bytes(range(32));salt=b's'*16;text="x'"+key.hex()+salt.hex()+"'"
        self.assertEqual(list(capture.candidates(text.encode())),[(key,salt)])
        self.assertEqual(list(capture.candidates(text.encode('utf-16-le'))),[(key,salt)])
        self.assertEqual(list(capture.candidates(b'not a key')),[])
    def test_wechat_4x_versions_are_eligible_without_exact_build_pin(self):
        for name in ['Weixin.exe','WeChat.exe','WEIXIN.EXE']:
            for version in ['4.0.5.16','4.0.0.0','4.1.11.37','4.1.12.55','4.9.99.1']:
                with self.subTest(name=name,version=version):capture.validate_process_identity(name,version)
        self.assertEqual(capture.TARGET_VERSION,'4.x');self.assertEqual(capture.VERIFIED_VERSION,'4.0.5.16')
    def test_diagnostics_allowlist_and_safe_error_frames(self):
        work=self.root/'diag';diagnostics.start(work,'a'*32)
        try:
            diagnostics.event('begin',action='export',text='PRIVATE_TRANSCRIPT',source='PRIVATE_ACCOUNT',key='PRIVATE_KEY',output='PRIVATE_FILE',count=3)
            try:raise OSError(5,'PRIVATE_MESSAGE','PRIVATE_PATH')
            except OSError as exc:diagnostics.failure(exc,'snapshot')
            data=''.join(p.read_text(encoding='utf-8') for p in (work/'diagnostics').glob('*'))
            self.assertNotIn('PRIVATE_',data)
            rows=[json.loads(line) for line in data.splitlines()]
            self.assertTrue(any(r.get('exception_type')=='OSError' and r.get('errno')==5 for r in rows))
            self.assertTrue(any(r['event']=='frame' and 'line' in r for r in rows))
            self.assertTrue(all(r['request_id']=='a'*32 for r in rows))
        finally:diagnostics.start(work,None)
    def test_failed_key_scan_keeps_final_statistics_and_reason(self):
        source=dataset(self.root/'source');work=self.root/'diagnostic-work'
        diagnostics.start(work,'a'*32)
        report={'matched':0,'encrypted':6,'version':'4.1.0.30','scanned_mb':777,'candidates':9,'config_names':2,'config_objects':1,'master_checks':3,'limit_reached':False,'read_calls':100,'read_failures':2,'partial_reads':1,'last_read_error':299,'region_count':80}
        try:
            with patch.object(capture,'scan',return_value=(source,{},report)),self.assertRaises(ValueError):
                cli.execute({'action':'keys','source':str(source),'pid':12345,'consent':True,'workspace':str(work)})
            rows=[json.loads(line) for line in diagnostics._path.read_text(encoding='utf-8').splitlines()]
            final=next(r for r in rows if r['event']=='key_scan_result')
            self.assertEqual((final['version'],final['selected_pid'],final['scanned_mb'],final['candidates'],final['config_names']),('4.1.0.30',12345,777,9,2))
            self.assertTrue(any(r.get('error_code')=='KEY_NOT_FOUND' for r in rows))
            self.assertEqual((final['read_calls'],final['read_failures'],final['last_read_error']),(100,2,299))
        finally:diagnostics.start(work,None)
    def test_diagnostics_rotation_retention_and_write_failure_are_nonfatal(self):
        work=self.root/'diag'
        try:
            for i in range(36):
                diagnostics.start(work,f'{i:032x}');diagnostics.event('begin',action='count')
            files=list((work/'diagnostics').glob('worker-*'));self.assertLessEqual(len(files),32)
            current=diagnostics._path;current.write_text('x'*(256*1024),encoding='utf-8');diagnostics.event('complete',count=2)
            self.assertTrue(Path(str(current)+'.1').exists());self.assertLess(current.stat().st_size,4096)
            blocker=work/'block';blocker.write_text('fixture');diagnostics._path=blocker/'no.jsonl';diagnostics.event('complete',count=2)
            diagnostics.start(work,'bad/id');diagnostics.event('begin',action='count');self.assertIsNone(diagnostics._path)
        finally:diagnostics.start(work,None)
    def test_unrelated_executables_other_majors_and_invalid_versions_reject(self):
        for name,version in [('notepad.exe','4.1.12.55'),('Weixin.exe','3.9.12.1'),('Weixin.exe','5.0.0.0'),('WeChat.exe','4.1'),('Weixin.exe','4.1.12.55 beta'),('Weixin.exe','４.1.12.55'),('Weixin.exe',None)]:
            with self.subTest(name=name,version=version),self.assertRaises(ValueError):capture.validate_process_identity(name,version)
    def test_raw_key_literals_and_extended_literals(self):
        key=bytes(range(32));salt=b's'*16
        for encoding in ['ascii','utf-16-le']:
            self.assertEqual(list(capture.candidates(("X'"+key.hex()+"'").encode(encoding))),[(key,None)])
            extended="x'"+'12'*16+key.hex()+salt.hex()+"'"
            self.assertIn((key,salt),list(capture.candidates(extended.encode(encoding))))
        self.assertEqual(list(capture.candidates(b"x'"+b'a'*65+b"'")),[])
    def config_fixture(self,master=False,boundary=False):
        salt=b's'*16;candidate=bytes(range(32));key=cryptodb.derive(candidate,salt) if master else candidate
        page=encrypt_page(bytes(4096),1,key,salt)
        base=0x10000;memory=bytearray(1024*1024+2048 if boundary else 4096)
        symbol=1024*1024-10 if boundary else 100
        memory[symbol:symbol+len(key_reader.CONFIG_NAME)]=key_reader.CONFIG_NAME
        node=300;cfg=600;blob=1100
        struct.pack_into('<QQ',memory,node+16,base+symbol,len(key_reader.CONFIG_NAME))
        struct.pack_into('<Q',memory,node+40,base+cfg)
        literal=("x'"+candidate.hex()+salt.hex()+"'").encode()
        encoded=bytes(value^key_reader.CONFIG_MASK[i%len(key_reader.CONFIG_MASK)] for i,value in enumerate(literal))
        struct.pack_into('<QQ',memory,cfg+0x88+8,base+blob,len(encoded));memory[blob:blob+len(encoded)]=encoded
        def read(pointer,count):
            offset=pointer-base
            return bytes(memory[offset:offset+count]) if 0<=offset<len(memory) else b''
        return memory,[(base,len(memory))],read,{salt:[('media_0.db',page)]},key
    def test_config_cipher_finds_authenticated_key(self):
        memory,regions,read,pages,key=self.config_fixture();matcher=key_reader.Matcher(pages,lambda:None)
        key_reader.extract(regions,read,matcher,capture.candidates,lambda:None)
        self.assertEqual(matcher.found,{'media_0.db':key.hex()});self.assertEqual(matcher.stats['config_objects'],1)
    def test_raw_and_config_paths_combine_for_one_selected_account(self):
        memory,regions,read,pages,config_key=self.config_fixture()
        raw_key=b'r'*32;salt=b't'*16;literal=b"x'"+raw_key.hex().encode()+salt.hex().encode()+b"'"
        memory[2500:2500+len(literal)]=literal
        pages[salt]=[('media_1.db',encrypt_page(bytes(4096),1,raw_key,salt))]
        matcher=key_reader.Matcher(pages,lambda:None)
        key_reader.extract(regions,read,matcher,capture.candidates,lambda:None)
        self.assertEqual(matcher.found,{'media_0.db':config_key.hex(),'media_1.db':raw_key.hex()})
        self.assertTrue(matcher.done())
    def test_config_cipher_master_and_chunk_boundary(self):
        memory,regions,read,pages,key=self.config_fixture(master=True,boundary=True);matcher=key_reader.Matcher(pages,lambda:None)
        key_reader.extract(regions,read,matcher,capture.candidates,lambda:None)
        self.assertEqual(matcher.found,{'media_0.db':key.hex()});self.assertGreater(matcher.stats['master_checks'],0)
    def test_config_bad_pointer_and_wrong_account_do_not_match(self):
        memory,regions,read,pages,key=self.config_fixture()
        other=encrypt_page(bytes(4096),1,b'z'*32,b's'*16)
        matcher=key_reader.Matcher({b's'*16:[('other.db',other)]},lambda:None)
        key_reader.extract(regions,read,matcher,capture.candidates,lambda:None);self.assertEqual(matcher.found,{})
        struct.pack_into('<Q',memory,300+40,0xffffffffffffffff)
        matcher=key_reader.Matcher(pages,lambda:None);key_reader.extract(regions,read,matcher,capture.candidates,lambda:None)
        self.assertEqual(matcher.found,{})
    def test_config_scan_cancellation_and_derivation_bound(self):
        memory,regions,read,pages,key=self.config_fixture()
        def cancel():raise cli.Cancelled()
        with self.assertRaises(cli.Cancelled):key_reader.extract(regions,read,key_reader.Matcher(pages,cancel),capture.candidates,cancel)
        matcher=key_reader.Matcher(pages,lambda:None)
        with patch.object(key_reader,'derive',return_value=b'?'*32) as derive:
            for i in range(100):matcher.accept(i.to_bytes(32,'little'),None,master=True)
            self.assertEqual(derive.call_count,64)
    def test_server_id_precedes_colliding_local_id(self):
        source=dataset(self.root/'source');table='Msg_'+hashlib.md5(b'chat').hexdigest()
        big=9223372036854775806
        with writable(source/'media_0.db') as db: db.execute('UPDATE VoiceInfo SET svr_id=? WHERE create_time=1700000000',(big,))
        with writable(source/'message_0.db') as db:
            db.execute('ALTER TABLE '+table+' ADD COLUMN server_id INTEGER')
            db.execute('UPDATE '+table+' SET local_id=999,server_id=?',(big,))
        with writable(source/'message_1.db') as db:
            db.execute('ALTER TABLE '+table+' ADD COLUMN server_id INTEGER')
            db.execute('INSERT INTO '+table+' VALUES(1,1700000000,34,3,123)')
        index=self.root/'index.sqlite';library.build(source,index,lambda:None,lambda x:None)
        row=next(library.records(index,{}));self.assertEqual(row['sender'],'甲');self.assertEqual(row['server_id'],str(big))
    def test_mismatched_server_id_cannot_fall_back_to_local_id(self):
        source=dataset(self.root/'source');table='Msg_'+hashlib.md5(b'chat').hexdigest()
        with writable(source/'message_0.db') as db:
            db.execute('ALTER TABLE '+table+' ADD COLUMN server_id INTEGER')
            db.execute('UPDATE '+table+' SET server_id=999')
        index=self.root/'index.sqlite';library.build(source,index,lambda:None,lambda x:None)
        self.assertEqual(next(library.records(index,{}))['sender'],'未知发送者')
    def test_server_id_stays_scoped_to_conversation_and_time(self):
        source=dataset(self.root/'source');table='Msg_'+hashlib.md5(b'chat').hexdigest()
        with writable(source/'message_0.db') as db:
            db.execute('ALTER TABLE '+table+' ADD COLUMN server_id INTEGER')
            db.execute('UPDATE '+table+' SET server_id=100,create_time=1700000100')
            other='Msg_'+hashlib.md5(b'other-chat').hexdigest()
            db.execute('CREATE TABLE '+other+' AS SELECT * FROM '+table)
            db.execute('UPDATE '+other+' SET create_time=1700000000')
        index=self.root/'index.sqlite';library.build(source,index,lambda:None,lambda x:None)
        self.assertEqual(next(library.records(index,{}))['sender'],'未知发送者')
    def test_silk_header_variants_and_single_packet(self):
        raw=tone();base=raw[1:] if raw.startswith(b'\x02') else raw
        size=int.from_bytes(base[9:11],'little');packet=base[9:11+size]
        for header in [b'#!SILK_V3',b'\x02#!SILK_V3']:
            for newline in [b'',b'\n',b'\r\n']:
                for end in [b'',b'\xff\xff']:
                    source=self.root/'one.silk';target=self.root/(uuid.uuid4().hex+'.wav')
                    value=header+newline+packet+end;source.write_bytes(value)
                    self.assertEqual(voice_codec.validate_packets(value),1)
                    duration=voice_codec.isolated(source,target,lambda:None)
                    self.assertTrue(.015<=duration<=.1,duration)
                    self.assertEqual(source.read_bytes(),value)
    def test_native_packet_capacity_and_trailing_garbage_rejected(self):
        raw=tone()
        for data in [b'#!SILK_V3'+(1251).to_bytes(2,'little')+b'x'*1251,raw+b'bad',b'#!SILK_V3\0\0']:
            with self.assertRaises(ValueError): voice_codec.validate_packets(data)
    @unittest.skipUnless(os.name=='nt','Windows DPAPI')
    def test_dpapi_vault_is_bound_to_root(self):
        path=self.root/'keys.dpapi';secret={'media_0.db':'11'*32};capture.save_vault(path,self.root,secret)
        self.assertNotIn(('11'*32).encode(),path.read_bytes());self.assertEqual(capture.load_vault(path,self.root),secret)
        with self.assertRaises(ValueError): capture.load_vault(path,self.root/'other')
    def test_catalog_sender_ids_are_shard_local_and_timestamp_bound(self):
        source=dataset(self.root/'source');index=self.root/'index.sqlite';report=library.build(source,index,lambda:None,lambda x:None)
        rows=list(library.records(index,{}));self.assertEqual(report['count'],3)
        self.assertEqual([r['sender'] for r in rows],['甲','乙','未知发送者'])
        self.assertEqual(len({library.filename(r) for r in rows}),3)
    def test_conflicting_message_sender_stays_unknown(self):
        source=dataset(self.root/'source');table='Msg_'+hashlib.md5(b'chat').hexdigest()
        with writable(source/'message_1.db') as db: db.execute('INSERT INTO '+table+' VALUES(1,1700000000,34,3)')
        index=self.root/'index.sqlite';library.build(source,index,lambda:None,lambda x:None)
        self.assertEqual(next(library.records(index,{}))['sender'],'未知发送者')
    def test_pagination_filters_and_empty_unknown_schema(self):
        source=dataset(self.root/'source',113);index=self.root/'index.sqlite';library.build(source,index,lambda:None,lambda x:None)
        page=library.listing(index,{'page':2});self.assertEqual(page['count'],113);self.assertEqual(len(page['items']),13)
        self.assertEqual(library.listing(index,{'after':1700000110})['count'],3)
        other=self.root/'unknown';other.mkdir()
        with writable(other/'media_0.db') as db: db.execute('CREATE TABLE Unsupported(value BLOB)')
        with self.assertRaisesRegex(ValueError,'VoiceInfo'): library.build(other,self.root/'bad.sqlite',lambda:None,lambda x:None)
    def test_conversation_counts_and_latest_voice_metadata(self):
        source=conversation_dataset(self.root/'source');index=self.root/'index.sqlite'
        library.build(source,index,lambda:None,lambda x:None)
        contacts={row['user']:row for row in library.listing(index,{})['contacts']}
        self.assertEqual(set(contacts),{'chat','demo@chatroom','friend_alice'})
        self.assertEqual((contacts['chat']['count'],contacts['chat']['first_stamp'],contacts['chat']['latest_stamp']),(3,1700000000,1700000002))
        self.assertEqual((contacts['demo@chatroom']['count'],contacts['demo@chatroom']['latest_stamp']),(2,1700000011))
        # Voice paging/selected chat does not change conversation-wide metadata.
        narrowed=library.listing(index,{'chat':'chat','after':1700000002,'page':1})
        self.assertEqual(narrowed['count'],1);self.assertEqual(narrowed['items'],[])
        self.assertEqual(narrowed['contacts'],library.listing(index,{})['contacts'])
    def test_count_and_page_do_not_enumerate_contacts_or_voice_payloads(self):
        work,request=self.prepare_export();request.update(action='count',chat='chat',after=1700000001,before=1700000002)
        with patch.object(library,'records',side_effect=AssertionError('Count must not load records')):
            self.assertEqual(cli.execute(request),{'count':1})
        _,_,index=cli.session_paths(work,request['session'])
        page=library.listing(index,dict(request,include_contacts=False))
        self.assertEqual(page['count'],1);self.assertEqual(len(page['items']),1);self.assertEqual(page['contacts'],[])
    def test_empty_selection_stays_empty_and_invalid_ranges_reject(self):
        work,request=self.prepare_export();_,_,index=cli.session_paths(work,request['session'])
        self.assertEqual(library.counting(index,{'ids':[]}),0)
        self.assertEqual(list(library.records(index,{'ids':[]})),[])
        self.assertEqual(library.counting(index,{'before':0}),0)
        for invalid in [{'ids':None},{'ids':'abc'},{'after':5,'before':5},{'after':6,'before':5}]:
            with self.assertRaises(ValueError):library.counting(index,invalid)
        request['ids']=[];report=cli.export(work,request,lambda:None)
        self.assertEqual((report['total'],report['completed'],report['failed']),(0,0,0))
        self.assertFalse(list(Path(report['output']).glob('*.wav')))
    def test_progress_totals_are_truthful_for_success_and_failure(self):
        work,request=self.prepare_export();events=[];original=library.payload
        def payload(folder,row):
            if row['stamp']==1700000000: raise ValueError('Synthetic decode failure')
            return original(folder,row)
        with patch.object(cli,'emit',side_effect=events.append),patch.object(library,'payload',side_effect=payload):
            report=cli.export(work,request,lambda:None)
        self.assertEqual((report['total'],report['processed'],report['completed'],report['failed'],report['remaining']),(2,2,1,1,0))
        self.assertEqual([e['processed'] for e in events],[0,1,2])
        self.assertTrue(all(e['total']==2 and e['elapsed_seconds']>=0 for e in events))
        self.assertIsNone(events[0]['remaining_seconds']);self.assertEqual(events[-1]['remaining_seconds'],0)
    def test_silk_isolated_decoding_has_signal_and_duration(self):
        source=self.root/'tone.silk';target=self.root/'tone.wav';source.write_bytes(tone())
        duration=voice_codec.isolated(source,target,lambda:None);self.assertTrue(.9<=duration<=1.1)
        with wave.open(str(target),'rb') as wav:
            self.assertEqual((wav.getnchannels(),wav.getsampwidth(),wav.getframerate()),(1,2,24000))
            samples=struct.unpack('<'+'h'*wav.getnframes(),wav.readframes(wav.getnframes()));self.assertGreater(max(samples),1000)
    def test_truncated_silk_is_not_reported_as_a_short_success(self):
        raw=tone();source=self.root/'cut.silk';target=self.root/'cut.wav';source.write_bytes(raw[:-1])
        with self.assertRaises(ValueError): voice_codec.decode(source,target)
        self.assertFalse(target.exists());self.assertEqual(source.read_bytes(),raw[:-1])
    def prepare_export(self,blob=None):
        work=self.root/'workspace';session=uuid.uuid4().hex;folder=work/'sessions'/session;folder.mkdir(parents=True)
        source=dataset(folder/'snapshots',2,blob);library.build(source,folder/'index.sqlite',lambda:None,lambda x:None)
        cli.write_json(folder/'session.json',{'source':str(self.root/'original'),'issues':[]})
        return work,{'action':'export','session':session,'output':str(self.root/'exports'),'workspace':str(work)}
    def test_cli_complete_export_and_failure_preserves_original(self):
        for blob in [None,b'bad format']:
            # Independent work roots per case.
            if blob is not None: self.root=self.root/'second';self.root.mkdir()
            work,request=self.prepare_export(blob)
            env=dict(os.environ,PYTHONPATH=str(ROOT/'.deps/vendor'))
            result=subprocess.run([sys.executable,str(ROOT/'worker/cli.py')],input=json.dumps(request).encode()+b'\n',capture_output=True,env=env,timeout=30)
            self.assertEqual(result.returncode,0,result.stdout)
            report=json.loads(result.stdout.splitlines()[-1])['data'];out=Path(report['output'])
            self.assertEqual(report['completed'],2 if blob is None else 0);self.assertEqual(report['failed'],0 if blob is None else 2)
            self.assertEqual(len(list(out.glob('*.silk'))),2);self.assertTrue((out/'manifest.json').exists())
            self.assertNotIn('key',(out/'manifest.json').read_text(encoding='utf-8'))
    def test_export_cancel_partial_and_no_success_manifest(self):
        work,request=self.prepare_export();flag=[False];original=cli.emit
        def output(value):
            if value.get('completed')==1: flag[0]=True
        def check():
            if flag[0]: raise cli.Cancelled()
        cli.emit=output
        try: report=cli.export(work,request,check)
        finally: cli.emit=original
        out=Path(report['output']);self.assertTrue(report['cancelled']);self.assertEqual(report['completed'],1)
        self.assertEqual((report['processed'],report['remaining']),(1,1))
        self.assertTrue((out/'partial.json').exists());self.assertFalse((out/'manifest.json').exists())
    def test_transcription_filename_order_and_safe_summary(self):
        row={'stamp':1700000000,'id':'a'*24}
        value=library.wav_filename(row,'<|zh|>你好/世界:*?\n今天很好。')
        self.assertRegex(value,r'^\d{8}-\d{6}_'+('a'*24)+'_')
        self.assertNotRegex(value,r'[<>:"/\\|?*\x00-\x1f]')
        self.assertTrue(value.endswith('.wav'))
        self.assertIn('未识别语音',library.wav_filename(row,''))
        self.assertLess(len(library.wav_filename(row,'很长的文字'*100)),100)
        self.assertNotEqual(value,library.wav_filename(dict(row,id='b'*24),'你好'))
    def test_transcribe_export_full_text_and_wav_only(self):
        work,request=self.prepare_export();request.update(transcribe=True,keep_silk=False)
        with patch.object(cli,'LocalRecognizer') as factory:
            factory.return_value.__enter__.return_value.recognize.side_effect=['你好，今天很好。','一起出去走走？']
            report=cli.export(work,request,lambda:None)
        out=Path(report['output']);self.assertEqual(report['completed'],2);self.assertEqual(report['recognized'],2)
        self.assertFalse(list(out.glob('*.silk')))
        lines=[json.loads(v) for v in (out/'progress.ndjson').read_text(encoding='utf-8').splitlines()]
        self.assertEqual(lines[0]['text'],'你好，今天很好。')
        for row in lines:
            self.assertTrue((out/row['wav']).is_file());self.assertRegex(row['wav'],r'^\d{8}-\d{6}_'+row['id']+'_')
    def test_page_recognition_persists_across_pages_and_export_reuses_cache(self):
        work,request=self.prepare_export();_,_,index=cli.session_paths(work,request['session'])
        request.update(action='recognize_page',ids=[r['id'] for r in library.records(index,{})])
        with patch.object(cli,'LocalRecognizer') as factory:
            model=factory.return_value.__enter__.return_value;model.recognize.side_effect=['你好，测试缓存。','第二条。']
            report=cli.execute(request);self.assertEqual((report['recognized'],report['cached'],report['failed']),(2,0,0));self.assertEqual(model.recognize.call_count,2)
        self.assertEqual(library.listing(index,{'page':1})['items'],[])
        back=library.listing(index,{'page':0})['items'];self.assertEqual([r['transcript'] for r in back],['你好，测试缓存。','第二条。'])
        with patch.object(cli,'LocalRecognizer') as factory:
            model=factory.return_value.__enter__.return_value;model.recognize.side_effect=AssertionError('Already cached')
            again=cli.execute(request);self.assertEqual(again['cached'],2)
            result=cli.execute(dict(request,action='export',transcribe=True));self.assertEqual((result['completed'],result['recognized']),(2,2));model.recognize.assert_not_called()
        other=self.root/'other-session';other.mkdir();self.assertEqual(speech_cache.read(other/'index.sqlite',request['ids']),{})
    def test_page_recognition_retries_errors_and_reuses_empty_results(self):
        work,request=self.prepare_export();_,_,index=cli.session_paths(work,request['session']);ids=[r['id'] for r in library.records(index,{})];request.update(action='recognize_page',ids=ids)
        with patch.object(cli,'LocalRecognizer') as factory:
            factory.return_value.__enter__.return_value.recognize.side_effect=[ValueError('Synthetic failure'),'']
            first=cli.execute(request);self.assertEqual(first['failed'],1)
        self.assertEqual([r['recognition_status'] for r in library.listing(index,{})['items']],['ERROR','EMPTY'])
        with patch.object(cli,'LocalRecognizer') as factory:
            model=factory.return_value.__enter__.return_value;model.recognize.return_value='重试成功'
            retried=cli.execute(request);self.assertEqual((retried['recognized'],retried['cached'],retried['failed']),(1,1,0));self.assertEqual(model.recognize.call_count,1)
        with writable(index.parent/'snapshots/media_0.db') as db:db.execute('UPDATE VoiceInfo SET voice_data=? WHERE create_time=1700000000',(tone()+b'changed',))
        self.assertIsNone(speech_cache.lookup(index,ids[0],'changed-digest'))
    def test_page_recognition_cancel_keeps_completed_cache_and_validates_scope(self):
        work,request=self.prepare_export();_,_,index=cli.session_paths(work,request['session']);ids=[r['id'] for r in library.records(index,{})];request.update(action='recognize_page',ids=ids)
        flag=[False]
        def check():
            if flag[0]:raise cli.Cancelled()
        def emit(value):
            if value.get('phase')=='page_recognition' and value.get('processed')==1:flag[0]=True
        with patch.object(cli,'LocalRecognizer') as factory,patch.object(cli,'emit',side_effect=emit):
            factory.return_value.__enter__.return_value.recognize.return_value='保留第一条'
            result=cli.recognize_page(work,request,check)
        self.assertTrue(result['cancelled']);self.assertEqual(result['processed'],1)
        self.assertEqual([r['recognition_status'] for r in library.listing(index,{})['items']],['READY','UNCACHED'])
        for invalid in [None,[],ids+ids,['f'*24],['x'],['a'*24]*51]:
            with self.assertRaises(ValueError):cli.execute(dict(request,ids=invalid))
        with self.assertRaises(ValueError):cli.execute(dict(request,chat='other-chat'))
    def test_corrupt_transcript_cache_does_not_hide_voice_list_or_fail_wav_export(self):
        work,request=self.prepare_export();_,_,index=cli.session_paths(work,request['session']);speech_cache.path(index).write_bytes(b'broken SQLite')
        listing=library.listing(index,{});self.assertTrue(listing['cache_warning']);self.assertEqual(len(listing['items']),2)
        with patch.object(cli,'LocalRecognizer') as factory:
            factory.return_value.__enter__.return_value.recognize.return_value='仍能识别'
            result=cli.execute(dict(request,transcribe=True));self.assertEqual((result['completed'],result['recognized']),(2,2))
    def test_transcript_cache_survives_real_fifty_item_page_boundary(self):
        source=dataset(self.root/'source',51);index=self.root/'index.sqlite';library.build(source,index,lambda:None,lambda x:None)
        rows=list(library.records(index,{},0));self.assertEqual(len(rows),50)
        for row in rows:speech_cache.save(index,row['id'],'fixture-digest','READY','缓存-'+str(row['stamp']))
        following=library.listing(index,{'page':1});self.assertEqual(len(following['items']),1);self.assertEqual(following['items'][0]['recognition_status'],'UNCACHED')
        back=library.listing(index,{'page':0});self.assertTrue(all(r['recognition_status']=='READY' and r['transcript'].startswith('缓存-') for r in back['items']))
    def test_voice_sort_is_global_numeric_stable_and_preserves_filters(self):
        source=dataset(self.root/'source',113);index=self.root/'index.sqlite';library.build(source,index,lambda:None,lambda x:None)
        with writable(index) as db:db.execute('UPDATE voices SET bytes=CASE stamp%3 WHEN 0 THEN 2 WHEN 1 THEN 10 ELSE 100 END,local_id=stamp-1700000000')
        for field in ['bytes','local_id','stamp']:
            for descending in [False,True]:
                pages=[library.listing(index,{'sort':field,'descending':descending,'page':p})['items'] for p in range(3)]
                rows=[row for page in pages for row in page];self.assertEqual(len(rows),113);self.assertEqual(len({r['id'] for r in rows}),113)
                values=[r[field] for r in rows];self.assertEqual(values,sorted(values,reverse=descending))
                self.assertEqual([r['id'] for r in rows],[r['id'] for r in [*library.records(index,{'sort':field,'descending':descending},0),*library.records(index,{'sort':field,'descending':descending},1),*library.records(index,{'sort':field,'descending':descending},2)]])
        selected=library.listing(index,{'sort':'bytes','descending':True,'chat':'chat','after':1700000110})
        self.assertEqual(selected['count'],3);self.assertTrue(all(r['stamp']>=1700000110 for r in selected['items']))
        for invalid in [{'sort':'stamp; DROP TABLE voices'},{'sort':'unknown'},{'sort':'bytes','descending':'DESC'}]:
            with self.assertRaises(ValueError):library.listing(index,invalid)
    def test_voice_transcript_and_status_sort_uses_private_cache_before_paging(self):
        source=dataset(self.root/'source',53);index=self.root/'index.sqlite';library.build(source,index,lambda:None,lambda x:None)
        rows=list(library.records(index,{}))
        for i,row in enumerate(rows[:51]):speech_cache.save(index,row['id'],'fixture','READY',str(100-i).zfill(3))
        speech_cache.save(index,rows[0]['id'],'fixture','EMPTY','');speech_cache.save(index,rows[1]['id'],'fixture','ERROR','')
        for field in ['transcript','recognition_status','sender','label']:
            result=[r for p in [0,1] for r in library.listing(index,{'sort':field,'page':p,'chat':'chat','after':1700000000})['items']]
            self.assertEqual(len(result),53);self.assertEqual(len({r['id'] for r in result}),53)
            if field=='transcript':self.assertEqual([r['transcript'] for r in result],sorted(r['transcript'] for r in result))
            if field=='recognition_status':
                rank={'READY':0,'EMPTY':1,'UNCACHED':2,'ERROR':3};values=[rank[r['recognition_status']] for r in result];self.assertEqual(values,sorted(values))
        self.assertEqual(speech_cache.read(index,[rows[2]['id']])[rows[2]['id']]['text'],'098')
    def test_asr_failure_does_not_fail_wav_export(self):
        work,request=self.prepare_export();request.update(transcribe=True)
        with patch.object(cli,'LocalRecognizer') as factory:
            factory.return_value.__enter__.return_value.recognize.side_effect=ValueError('识别组件不可用')
            report=cli.export(work,request,lambda:None)
        self.assertEqual((report['completed'],report['failed'],report['recognition_failed']),(2,0,2))
        self.assertTrue(all('未识别语音' in p.name for p in Path(report['output']).glob('*.wav')))
    def test_asr_cancellation_preserves_wav_and_partial(self):
        work,request=self.prepare_export();request.update(transcribe=True)
        with patch.object(cli,'LocalRecognizer') as factory:
            factory.return_value.__enter__.return_value.recognize.side_effect=cli.Cancelled()
            report=cli.export(work,request,lambda:None)
        out=Path(report['output']);self.assertTrue(report['cancelled']);self.assertTrue(list(out.glob('*.wav')))
        journal=[json.loads(line) for line in (out/'progress.ndjson').read_text(encoding='utf-8').splitlines()]
        self.assertEqual(len(journal),1);self.assertIn('已取消',journal[0]['status']);self.assertTrue((out/journal[0]['wav']).exists())
        self.assertEqual((report['completed'],report['remaining']),(0,2))
        self.assertTrue((out/'partial.json').exists());self.assertFalse((out/'manifest.json').exists())
    def test_export_date_range_and_conversation_boundaries(self):
        work,request=self.prepare_export();request.update(chat='chat',after=1700000001,before=1700000002,keep_silk=False)
        report=cli.export(work,request,lambda:None);self.assertEqual(report['completed'],1)
        request['chat']='other';report=cli.export(work,request,lambda:None);self.assertEqual(report['completed'],0)
    def test_source_output_boundary_and_payload_size_limit(self):
        with self.assertRaises(ValueError): cli.ensure_outside(self.root/'src/output',self.root/'src')
        source=dataset(self.root/'source',blob=b'x'*(library.MAX_BLOB+1));index=self.root/'index.sqlite';library.build(source,index,lambda:None,lambda x:None)
        row=next(library.records(index,{}))
        with self.assertRaises(ValueError): library.payload(source,row)
        row['label']='CON';self.assertIn('_CON',library.filename(row))
        row['db']='../escape.db'
        with self.assertRaises(ValueError): library.payload(source,row)


if __name__=='__main__': unittest.main(verbosity=2)
