"""Bounded WCDB configuration traversal, separated from Windows process access.

Layout facts checked against Moect 3d08fece. No injection, dumps or global account scan.
All candidates must authenticate a page from the explicitly selected account.
"""
import struct
from cryptodb import authenticated,derive

CONFIG_NAME=b'com.Tencent.WCDB.Config.Cipher'
CONFIG_MASK=bytes.fromhex('d2c7442458020000004889442450488b450048844c2448488944254048584c24')


class Matcher:
    def __init__(self,pages,check):
        self.pages=pages;self.check=check;self.found={};self.seen=set();self.master_seen=set()
        self.stats={'candidates':0,'config_names':0,'config_objects':0,'master_checks':0}
    def accept(self,key,hint=None,master=False):
        self.check()
        if len(key)!=32:return
        salts=[hint] if hint in self.pages else self.pages
        for salt in salts:
            self.check()
            targets=[(name,page) for name,page in self.pages[salt] if name not in self.found]
            if not targets:continue
            identity=(key,salt)
            raw_new=identity not in self.seen
            if raw_new and len(self.seen)<50000:
                self.seen.add(identity);self.stats['candidates']+=1
                for name,page in targets:
                    if authenticated(page,1,key,salt): self.found[name]=key.hex()
            if master and identity not in self.master_seen and len(self.master_seen)<64:
                self.master_seen.add(identity);self.stats['master_checks']+=1
                derived=derive(key,salt)
                for name,page in targets:
                    if name not in self.found and authenticated(page,1,derived,salt): self.found[name]=derived.hex()
    def done(self): return len(self.found)==sum(map(len,self.pages.values()))


def chunks(regions,read,check):
    for base,size in regions:
        tail=b''
        for offset in range(0,size,1024*1024):
            check();block=read(base+offset,min(size-offset,1024*1024))
            if not block:tail=b'';continue
            yield base+offset-len(tail),tail+block
            tail=(tail+block)[-512:] if len(block)==min(size-offset,1024*1024) else b''


def extract(regions,read,matcher,candidates,check):
    symbols=set()
    # First pass handles the 4.0 raw-key path and locates the configuration name.
    for base,block in chunks(regions,read,check):
        for key,salt in candidates(block): matcher.accept(key,salt)
        if matcher.done():return
        start=0
        while len(symbols)<256:
            pos=block.find(CONFIG_NAME,start)
            if pos<0:break
            symbols.add(base+pos);start=pos+1
    matcher.stats['config_names']=len(symbols)
    if not symbols:return
    references=[struct.pack('<QQ',address,len(CONFIG_NAME)) for address in symbols]
    objects=set();config_candidates=[]
    def integer(data,offset):
        return struct.unpack_from('<Q',data,offset)[0] if len(data)>=offset+8 else 0
    # Config nodes contain a std::string descriptor followed by a config pointer.
    for base,block in chunks(regions,read,check):
        for reference in references:
            start=0
            while True:
                check();pos=block.find(reference,start)
                if pos<0:break
                start=pos+1
                node=read(base+pos-16,80)
                if integer(node,16) not in symbols or integer(node,24)!=len(CONFIG_NAME):continue
                pointer=integer(node,40)
                if not 0x10000<=pointer<0x800000000000 or pointer in objects:continue
                objects.add(pointer)
                if len(objects)>4096:return
                descriptor=read(pointer+0x88,40)
                address=integer(descriptor,8);length=integer(descriptor,16)
                if not 0<length<=1024 or not 0x10000<=address<0x800000000000:continue
                blob=read(address,length)
                if len(blob)!=length:continue
                decoded=bytes(value^CONFIG_MASK[i%len(CONFIG_MASK)] for i,value in enumerate(blob))
                for key,salt in candidates(decoded):
                    matcher.accept(key,salt);config_candidates.append((key,salt))
                matcher.stats['config_objects']=len(objects)
                if matcher.done():return
    # Expensive derivation applies only to structurally verified config candidates.
    for key,salt in config_candidates[:64]:
        matcher.accept(key,salt,master=True)
        if matcher.done():return
