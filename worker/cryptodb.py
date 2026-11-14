"""Authenticated SQLCipher4 snapshots. Never opens the source database for writing."""
import hashlib
import contextlib
import hmac
import os
from pathlib import Path
import sqlite3
import struct
import tempfile
import time
from Cryptodome.Cipher import AES

PAGE = 4096
RESERVE = 80
MAGIC = b'SQLite format 3\0'
MAX_DB = 32 * 1024**3


@contextlib.contextmanager
def read_only(path):
    connection = sqlite3.connect(Path(path).resolve().as_uri()+'?mode=ro&immutable=1', uri=True)
    connection.execute('PRAGMA query_only=ON')
    connection.execute('PRAGMA trusted_schema=OFF')
    connection.text_factory = lambda raw: raw.decode('utf-8', 'replace')
    try:
        yield connection
    finally:
        connection.close()


def derive(master, salt):
    return hashlib.pbkdf2_hmac('sha512', master, salt, 256000, dklen=32)


def mac_key(key, salt):
    return hashlib.pbkdf2_hmac('sha512', key, bytes(x ^ 0x3a for x in salt), 2, dklen=32)


def authenticated(page, number, key, salt):
    if len(page) != PAGE:
        return False
    start = 16 if number == 1 else 0
    return hmac.compare_digest(hmac.new(mac_key(key, salt), page[start:-64]+struct.pack('<I', number), 'sha512').digest(), page[-64:])


def decrypt_page(page, number, key, salt):
    if not authenticated(page, number, key, salt):
        raise ValueError('数据库页认证失败；密钥不匹配或文件损坏，未使用该副本')
    start = 16 if number == 1 else 0
    plain = AES.new(key, AES.MODE_CBC, page[-RESERVE:-RESERVE+16]).decrypt(page[start:-RESERVE])
    return (MAGIC if start else b'')+plain+page[-RESERVE:]


def first_page_key(path, candidate, kind='raw'):
    with Path(path).open('rb') as stream:
        page = stream.read(PAGE)
    salt = page[:16]
    key = derive(candidate, salt) if kind == 'master' else candidate
    return key if len(key) == 32 and authenticated(page, 1, key, salt) else None


def checksum(data, previous=(0, 0), endian='<'):
    if len(data) % 8:
        raise ValueError('WAL校验长度无效')
    a, b = previous
    for x, y in struct.iter_unpack(endian+'II', data):
        a = (a+x+b) & 0xffffffff
        b = (b+y+a) & 0xffffffff
    return a, b


def apply_wal(wal, target, key, salt, check, encrypted_output=False):
    """Validate the complete WAL before replay; discard only valid uncommitted tail frames."""
    wal = Path(wal)
    if not wal.exists() or wal.stat().st_size == 0:
        return 0
    frame_size = PAGE+24
    size = wal.stat().st_size
    if size > MAX_DB*2 or size < 32:
        raise ValueError('WAL大小无效，未使用该副本')
    with wal.open('rb') as stream:
        header = stream.read(32)
        magic, version, page_size = struct.unpack('>III', header[:12])
        if magic not in (0x377f0682, 0x377f0683) or version != 3007000 or page_size != PAGE:
            raise ValueError('WAL格式不受支持，未忽略近期数据')
        endian = '<' if magic == 0x377f0682 else '>'
        running = checksum(header[:24], endian=endian)
        if running != struct.unpack('>II', header[24:]):
            raise ValueError('WAL文件头校验失败')
        last_commit, committed_pages = -1, 0
        logical_end=False
        for index in range((size-32)//frame_size):
            check()
            frame = stream.read(frame_size)
            number, pages = struct.unpack('>II', frame[:8])
            # WCDB keeps a preallocated WAL after checkpoint. Old-generation
            # frames and arbitrary physical tail bytes are not current frames.
            # SQLite defines the salt change as the logical end of this WAL.
            if frame[8:16]!=header[16:24]:
                logical_end=True;break
            if number == 0 or number*PAGE > MAX_DB or pages*PAGE > MAX_DB:
                raise ValueError('WAL帧边界或salt无效')
            running = checksum(frame[:8]+frame[24:], running, endian)
            if running != struct.unpack('>II', frame[16:24]):
                raise ValueError('WAL帧校验失败，请退出微信后重新准备')
            if key is not None and not authenticated(frame[24:], number, key, salt):
                raise ValueError('WAL加密页认证失败')
            if pages:
                last_commit, committed_pages = index, pages
        if not logical_end and (size-32)%frame_size:
            raise ValueError('当前代际WAL帧未完整落盘，未使用该副本')
        if last_commit < 0:
            return 0
        stream.seek(32)
        with Path(target).open('r+b') as out:
            for index in range(last_commit+1):
                check()
                frame = stream.read(frame_size)
                number = struct.unpack('>I', frame[:4])[0]
                page = frame[24:]
                out.seek((number-1)*PAGE)
                out.write(decrypt_page(page, number, key, salt) if key is not None and not encrypted_output else page)
            out.truncate(committed_pages*PAGE)
        return committed_pages


def page_layout(stream, key, salt, physical_pages, committed_pages, check):
    """Trust allocation metadata only after authenticating page one and all trunks."""
    stream.seek(0)
    first = decrypt_page(stream.read(PAGE), 1, key, salt)
    if int.from_bytes(first[16:18], 'big') != PAGE or first[20] != RESERVE:
        raise ValueError('数据库页大小或保留区不受支持')
    integer = lambda data, offset: int.from_bytes(data[offset:offset+4], 'big')
    declared = integer(first, 28)
    valid_size = declared > 0 and integer(first, 24) == integer(first, 92)
    pages = committed_pages or (declared if valid_size else physical_pages)
    if not 0 < pages <= physical_pages or pages*PAGE > MAX_DB:
        raise ValueError('数据库逻辑页数超出已冻结文件范围')
    trunk, expected = integer(first, 32), integer(first, 36)
    if expected >= pages or (expected == 0) != (trunk == 0):
        raise ValueError('空闲页链与数据库首页计数不一致')
    leaves = bytearray(pages+1); seen = bytearray(pages+1); count = 0
    while trunk:
        check()
        if not 1 < trunk <= pages or seen[trunk]: raise ValueError('空闲页链越界、重复或循环')
        seen[trunk] = 1; count += 1
        stream.seek((trunk-1)*PAGE)
        data = decrypt_page(stream.read(PAGE), trunk, key, salt)
        next_trunk, length = integer(data, 0), integer(data, 4)
        if length > (PAGE-RESERVE-8)//4 or count+length > expected:
            raise ValueError('空闲页主链长度无效')
        for offset in range(length):
            leaf = integer(data, 8+offset*4)
            if not 1 < leaf <= pages or seen[leaf]: raise ValueError('空闲叶页越界或重复')
            leaves[leaf] = seen[leaf] = 1; count += 1
        trunk = next_trunk
    if count != expected: raise ValueError('空闲页总数不匹配')
    stream.seek(0)
    return pages, leaves


def signature(path):
    p = Path(path)
    if not p.exists():
        return None
    stat = p.stat()
    return stat.st_size, stat.st_mtime_ns


def snapshot(source, destination, key, check=lambda: None):
    source, destination = Path(source), Path(destination)
    wal = Path(str(source)+'-wal')
    before = (signature(source), signature(wal))
    if not before[0] or not 0 < before[0][0] <= MAX_DB or before[0][0] % PAGE:
        raise ValueError('数据库大小或页大小不受支持')
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(dir=destination.parent, suffix='.snapshot.part')
    temporary = Path(temporary)
    merged = None
    committed_pages = 0
    try:
        with os.fdopen(fd, 'wb') as out:
            with source.open('rb') as stream:
                first = stream.read(PAGE)
            plain = first.startswith(MAGIC)
            salt = first[:16]
            if not plain and key is None:
                raise ValueError('该数据库尚无验证通过的密钥，请先在微信中打开对应会话并准备密钥')
            if not plain and wal.exists() and wal.stat().st_size:
                # Match SQLite's reader: commit-valid WAL versions supersede
                # obsolete main-file pages. Only modify our own private file.
                merged = Path(str(temporary)+'.encrypted')
                with source.open('rb') as src, merged.open('xb') as dst:
                    remaining = before[0][0]
                    while remaining:
                        check(); block = src.read(min(1024*1024,remaining))
                        if not block: raise ValueError('数据库页读取不完整')
                        dst.write(block); remaining -= len(block)
                committed_pages = apply_wal(wal, merged, key, salt, check, encrypted_output=True)
                with merged.open('r+b') as header: header.write(salt)
            read_path = merged or source
            with read_path.open('rb') as stream:
                pages = read_path.stat().st_size//PAGE
                leaves = None
                if not plain: pages, leaves = page_layout(stream, key, salt, pages, committed_pages, check)
                for number in range(1, pages+1):
                    check()
                    if leaves is not None and leaves[number]:
                        stream.seek(PAGE, 1); out.write(bytes(PAGE)); continue
                    page = stream.read(PAGE)
                    out.write(page if plain else decrypt_page(page, number, key, salt))
        if merged is None:
            apply_wal(wal, temporary, None if plain else key, salt, check)
        if before != (signature(source), signature(wal)):
            raise ValueError('源数据库仍在变化，请完全退出微信后重试')
        try:
            with read_only(temporary) as connection:
                result = connection.execute('PRAGMA quick_check').fetchone()
                if not result or result[0] != 'ok':
                    raise ValueError('数据库副本完整性检查失败')
        except sqlite3.Error as error:
            raise ValueError('数据库副本完整性检查失败') from error
        check()
        if destination.exists():
            raise FileExistsError('已有数据库副本不覆盖，请重新开始一个会话')
        temporary.replace(destination)
    finally:
        if merged is not None: merged.unlink(missing_ok=True)
        temporary.unlink(missing_ok=True)


def live_snapshot(source, destination, key, check=lambda: None, progress=lambda value: None):
    """Freeze one database/WAL pair before decrypting; never open the live DB in SQLite."""
    source,destination=Path(source),Path(destination)
    if destination.exists(): raise FileExistsError('已有数据库副本不覆盖')
    destination.parent.mkdir(parents=True,exist_ok=True)
    wal=Path(str(source)+'-wal')
    for attempt in range(3):
        check()
        try:
            with tempfile.TemporaryDirectory(prefix='live-',dir=destination.parent) as tmp:
                frozen=Path(tmp)/'source.db'
                before=(signature(source),signature(wal))
                for original,target,limit in [(source,frozen,MAX_DB),(wal,Path(str(frozen)+'-wal'),MAX_DB*2)]:
                    check()
                    info=signature(original)
                    if info is None:
                        if original==source: raise ValueError('源数据库不存在')
                        continue
                    if info[0]>limit: raise ValueError('数据库或WAL超过大小限制')
                    with original.open('rb') as reader,target.open('xb') as writer:
                        remaining=info[0]
                        while remaining:
                            check();block=reader.read(min(1024*1024,remaining))
                            if not block: raise ValueError('数据库正在变化，快照未完整复制')
                            writer.write(block);remaining-=len(block)
                if before!=(signature(source),signature(wal)):
                    raise ValueError('数据库正在变化，重试在线快照')
                # The original can change now: decryption and WAL verification
                # consume only this immutable pair, including committed frames.
                snapshot(frozen,destination,key,check)
                return
        except (OSError,ValueError) as exc:
            if isinstance(exc,FileExistsError): raise
            if attempt==2: raise ValueError('在线快照三次未通过：'+str(exc)) from None
            progress({'phase':'snapshot_retry','attempt':attempt+1,'total':3})
            check();time.sleep(.2)
