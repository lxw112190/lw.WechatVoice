"""Read-only, selected-PID WeChat 4.x key matching for one local account."""
import ctypes as c
from ctypes import wintypes as w
import json
import os
from pathlib import Path
import re
import time
from cryptodb import PAGE, MAGIC, first_page_key
from key_reader import Matcher,extract
import diagnostics

TARGET_VERSION = '4.x'
VERIFIED_VERSION = '4.0.5.16'
PATTERN = re.compile(rb"[xX]'([0-9a-fA-F]{64,192})'")
WIDE_PATTERN = re.compile(rb"[xX]\x00'\x00((?:[0-9a-fA-F]\x00){64,192})'\x00")


def databases(folder):
    root = Path(folder).resolve()
    if (root/'db_storage').is_dir():
        root = (root/'db_storage').resolve()
    elif any(child.is_dir() and (child/'db_storage').is_dir() for child in root.iterdir()):
        raise ValueError('此目录含多个/下一级账号，请先选择一个具体账号，未混合读取')
    files = []
    for path in root.rglob('*.db'):
        if not path.resolve().is_relative_to(root):
            raise ValueError('源目录包含越界链接，未读取')
        if path.name.lower() == 'contact.db' or re.fullmatch(r'(?:media|message)_\d+\.db', path.name, re.I):
            if not 0 < path.stat().st_size <= 32*1024**3:
                raise ValueError('数据库大小超出支持范围')
            files.append(path)
            if len(files) > 512:
                raise ValueError('数据库数量超过512个，请只选择一个账号')
    if not any(p.name.lower().startswith('media_') for p in files):
        raise ValueError('没有找到media_*.db；请选择一个微信账号目录或db_storage目录')
    if sum(p.stat().st_size for p in files) > 64*1024**3:
        raise ValueError('所选数据库超过64GiB，首版暂不支持')
    return root, sorted(files)


def _windows():
    if os.name != 'nt' or c.sizeof(c.c_void_p) != 8:
        raise OSError('读取微信进程仅支持Windows x64')


def validate_process_identity(name, version):
    if str(name).lower() not in ('weixin.exe','wechat.exe'):
        raise ValueError('所选进程不是微信，未读取其内存')
    if not re.fullmatch(r'4\.\d+\.\d+\.\d+',str(version),flags=re.ASCII):
        raise ValueError('支持 Windows 微信4.x；检测到 '+str(version)+'，未读取其内存')


def process_details(pid):
    _windows()
    kernel = c.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [w.DWORD,w.BOOL,w.DWORD]
    kernel.OpenProcess.restype = w.HANDLE
    kernel.CloseHandle.argtypes = [w.HANDLE]
    kernel.QueryFullProcessImageNameW.argtypes = [w.HANDLE,w.DWORD,w.LPWSTR,c.POINTER(w.DWORD)]
    handle = kernel.OpenProcess(0x1000, False, int(pid))
    if not handle:
        raise c.WinError(c.get_last_error())
    try:
        path = c.create_unicode_buffer(32768);length = w.DWORD(len(path))
        if not kernel.QueryFullProcessImageNameW(handle,0,path,c.byref(length)):
            raise c.WinError(c.get_last_error())
    finally:
        kernel.CloseHandle(handle)
    executable = Path(path.value)
    version_api = c.WinDLL('version', use_last_error=True)
    version_api.GetFileVersionInfoSizeW.argtypes = [w.LPCWSTR,c.POINTER(w.DWORD)]
    ignored = w.DWORD();size = version_api.GetFileVersionInfoSizeW(str(executable),c.byref(ignored))
    if not size:
        raise ValueError('无法读取微信文件版本')
    data = c.create_string_buffer(size)
    version_api.GetFileVersionInfoW.argtypes = [w.LPCWSTR,w.DWORD,w.DWORD,c.c_void_p]
    if not version_api.GetFileVersionInfoW(str(executable),0,size,data):
        raise c.WinError(c.get_last_error())
    pointer = c.c_void_p();length = w.UINT()
    version_api.VerQueryValueW.argtypes = [c.c_void_p,w.LPCWSTR,c.POINTER(c.c_void_p),c.POINTER(w.UINT)]
    if not version_api.VerQueryValueW(data,'\\',c.byref(pointer),c.byref(length)) or length.value < 52 or not pointer.value:
        raise ValueError('微信版本信息不完整')
    values = c.cast(pointer,c.POINTER(w.DWORD))
    if values[0] != 0xFEEF04BD:
        raise ValueError('微信文件版本结构无效，未读取其内存')
    version = '.'.join(map(str,(values[2]>>16,values[2]&65535,values[3]>>16,values[3]&65535)))
    diagnostics.event('process_identity',version=version,selected_pid=int(pid))
    validate_process_identity(executable.name,version)
    return {'pid':int(pid),'version':version,'executable':str(executable)}


def candidates(block):
    for pattern,encoding in [(PATTERN,'ascii'),(WIDE_PATTERN,'utf-16-le')]:
        for match in pattern.finditer(block):
            value=match[1].decode(encoding)
            if len(value)%2: continue
            offsets={0}
            if len(value)>96:
                offsets.update(range(0,len(value)-63,32));offsets.add(len(value)-64)
            for offset in sorted(offsets):
                salt=value[offset+64:offset+96]
                yield bytes.fromhex(value[offset:offset+64]),bytes.fromhex(salt) if len(salt)==32 else None


def scan(pid, folder, check, progress):
    details = process_details(pid)
    root, files = databases(folder)
    wanted = {}
    for path in files:
        with path.open('rb') as stream:
            first = stream.read(PAGE)
        if not first.startswith(MAGIC):
            wanted.setdefault(first[:16],[]).append(path)
    if not wanted:
        return root, {}, {'matched':0,'encrypted':0,'version':details['version']}
    class Region(c.Structure):
        _fields_=[('base',c.c_void_p),('allocation',c.c_void_p),('allocation_protect',w.DWORD),('alignment',w.DWORD),('size',c.c_size_t),('state',w.DWORD),('protect',w.DWORD),('kind',w.DWORD)]
    kernel = c.WinDLL('kernel32',use_last_error=True)
    kernel.OpenProcess.argtypes=[w.DWORD,w.BOOL,w.DWORD];kernel.OpenProcess.restype=w.HANDLE
    kernel.CloseHandle.argtypes=[w.HANDLE]
    kernel.VirtualQueryEx.argtypes=[w.HANDLE,c.c_void_p,c.POINTER(Region),c.c_size_t];kernel.VirtualQueryEx.restype=c.c_size_t
    kernel.ReadProcessMemory.argtypes=[w.HANDLE,c.c_void_p,c.c_void_p,c.c_size_t,c.POINTER(c.c_size_t)];kernel.ReadProcessMemory.restype=w.BOOL
    kernel.QueryFullProcessImageNameW.argtypes=[w.HANDLE,w.DWORD,w.LPWSTR,c.POINTER(w.DWORD)]
    handle = kernel.OpenProcess(0x0400|0x0010,False,int(pid))
    if not handle:
        raise OSError('无法只读打开选定微信进程，Win32='+str(c.get_last_error())+'；请确认微信仍存在，并以相同用户/权限运行')
    pages={}
    for salt,paths in wanted.items():
        pages[salt]=[]
        for path in paths:
            with path.open('rb') as stream: page=stream.read(PAGE)
            pages[salt].append((str(path.relative_to(root)),page))
    start, address, scanned, last_progress = time.monotonic(),0,0,0
    class LimitReached(Exception): pass
    def limited_check():
        check()
        if time.monotonic()-start>=120 or scanned>=2*1024**3:raise LimitReached()
    matcher=Matcher(pages,limited_check)
    memory_stats={'read_calls':0,'read_failures':0,'partial_reads':0,'last_read_error':0,'region_count':0}
    try:
        current=c.create_unicode_buffer(32768);length=w.DWORD(len(current))
        if not kernel.QueryFullProcessImageNameW(handle,0,current,c.byref(length)) or current.value.casefold()!=details['executable'].casefold():
            raise ValueError('微信进程在准备期间改变，未读取内存，请刷新进程')
        regions=[]
        while address < 0x7fffffffffff:
            limited_check();region=Region()
            if not kernel.VirtualQueryEx(handle,c.c_void_p(address),c.byref(region),c.sizeof(region)):
                break
            base = region.base or 0
            if not region.size or base+region.size <= address:
                break
            address = base+region.size
            if region.state != 0x1000 or region.protect & (0x100|0x01) or not region.protect & (0x02|0x04|0x08|0x40|0x80):
                continue
            priority=0 if region.kind==0x20000 and region.protect&0xff in (4,8,64,128) else 1
            regions.append((priority,base,region.size))
            if len(regions)>65536:raise ValueError('进程内存区域数量超限')
        memory_stats['region_count']=len(regions)
        def read_memory(pointer,count):
            nonlocal scanned,last_progress
            limited_check()
            if not 0<=pointer<0x800000000000 or not 0<count<=1024*1024:return b''
            buffer=c.create_string_buffer(count);amount=c.c_size_t()
            success=kernel.ReadProcessMemory(handle,c.c_void_p(pointer),buffer,count,c.byref(amount))
            memory_stats['read_calls']+=1
            if not success:
                memory_stats['last_read_error']=c.get_last_error()
                memory_stats['partial_reads' if amount.value else 'read_failures']+=1
            scanned+=amount.value
            if time.monotonic()-last_progress>.5:
                progress(dict(phase='keys',matched=len(matcher.found),encrypted=sum(map(len,wanted.values())),scanned_mb=scanned//1048576,**matcher.stats));last_progress=time.monotonic()
            return buffer.raw[:amount.value]
        extract([(base,size) for _,base,size in sorted(regions)],read_memory,matcher,candidates,limited_check)
    except LimitReached:
        matcher.stats['limit_reached']=True
    finally:
        kernel.CloseHandle(handle)
        diagnostics.event('key_scan_io',version=details['version'],selected_pid=int(pid),**memory_stats)
    return root,matcher.found,dict(matched=len(matcher.found),encrypted=sum(map(len,wanted.values())),version=details['version'],scanned_mb=scanned//1048576,**matcher.stats,**memory_stats)


def protect(data, decrypt=False):
    _windows()
    class Blob(c.Structure):
        _fields_=[('length',w.DWORD),('data',c.POINTER(c.c_ubyte))]
    raw=(c.c_ubyte*len(data)).from_buffer_copy(data);source=Blob(len(data),raw);target=Blob()
    crypto=c.WinDLL('crypt32',use_last_error=True);kernel=c.WinDLL('kernel32',use_last_error=True)
    function=crypto.CryptUnprotectData if decrypt else crypto.CryptProtectData
    function.argtypes=[c.POINTER(Blob),c.c_void_p,c.c_void_p,c.c_void_p,c.c_void_p,w.DWORD,c.POINTER(Blob)];function.restype=w.BOOL
    kernel.LocalFree.argtypes=[c.c_void_p];kernel.LocalFree.restype=c.c_void_p
    if not function(c.byref(source),None,None,None,None,1,c.byref(target)):
        raise c.WinError(c.get_last_error())
    try:
        return c.string_at(target.data,target.length)
    finally:
        kernel.LocalFree(target.data)


def save_vault(path, root, keys):
    payload=json.dumps({'schema':1,'root':str(Path(root).resolve()),'keys':keys},ensure_ascii=False).encode('utf-8')
    path=Path(path);path.parent.mkdir(parents=True,exist_ok=True);temporary=path.with_suffix('.part')
    try:
        temporary.write_bytes(protect(payload));temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def load_vault(path, root):
    path=Path(path)
    if not path.is_file():
        return {}
    if path.stat().st_size > 256*1024:
        raise ValueError('本机密钥文件过大')
    value=json.loads(protect(path.read_bytes(),True))
    if value.get('schema') != 1 or value.get('root') != str(Path(root).resolve()):
        raise ValueError('密钥文件与当前所选账号不一致')
    keys=value.get('keys',{})
    if not isinstance(keys,dict) or len(keys)>512 or any(not isinstance(v,str) or not re.fullmatch('[0-9a-f]{64}',v) for v in keys.values()):
        raise ValueError('本机密钥文件格式无效')
    return keys
