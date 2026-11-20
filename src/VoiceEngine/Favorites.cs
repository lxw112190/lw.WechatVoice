using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using WechatVoice;

namespace VoiceEngine
{
    // Independent implementation of the documented favorite schema. Source files
    // are never edited. Only content-matched audio is frozen into the session.
    internal static class Favorites
    {
        private const int MaxAudio = 8 * 1024 * 1024;
        private sealed class Voice
        {
            internal long Favorite, Stamp;
            internal string Id, Title, Sender, Md5, Server, Relative;
            internal long Bytes;
        }
        private static string Text(XElement node, string name) { return (string)node.Element(name) ?? ""; }
        private static XDocument Xml(string value)
        {
            if (value.Length > 16 * 1024 * 1024) throw new InvalidDataException("收藏内容过大");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 };
            using (var reader = XmlReader.Create(new StringReader(value), settings)) return XDocument.Load(reader);
        }
        private static bool Digest(string value) { return value.Length == 32 && value.All(Uri.IsHexDigit); }
        private static string Md5(byte[] value) { using (var hash = MD5.Create()) return BitConverter.ToString(hash.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
        private static void Parse(XElement root, long type, int depth, Voice parent, List<Voice> output, ref int budget)
        {
            if (depth > 6) return;
            foreach (var node in root.Elements("datalist").Elements("dataitem"))
            {
                if (--budget < 0) throw new InvalidDataException("收藏嵌套条目过多");
                long kind; long.TryParse((string)node.Attribute("datatype"), out kind);
                string format = Text(node, "datafmt").TrimStart('.').ToLowerInvariant();
                bool voice = kind == ((type == 14 || depth > 0) ? 4 : 3);
                if (new[] { "silk", "amr", "wav", "mp3", "m4a" }.Contains(format)) voice = true;
                else if (new[] { "mp4", "mov", "webm" }.Contains(format)) voice = false;
                if (voice)
                {
                    output.Add(new Voice { Favorite = parent.Favorite, Stamp = parent.Stamp, Title = parent.Title,
                        Sender = Text(node,"datasrcname") == "" ? parent.Sender : Text(node,"datasrcname"),
                        Md5 = Text(node,"fullmd5").Trim().ToLowerInvariant(),
                        Server = Text(node,"fromnewmsgid") == "" ? parent.Server : Text(node,"fromnewmsgid") });
                }
                var nested = node.Element("recorditem");
                if (nested != null)
                {
                    XElement inner = nested;
                    if (nested.Value.TrimStart().StartsWith("<")) inner = Xml(nested.Value).Root;
                    if (inner != null) Parse(inner, type, depth + 1, parent, output, ref budget);
                }
            }
        }
        private static bool Linked(string path)
        {
            for (string p = FileSystemPath.FullPath(path); p != null; p = Path.GetDirectoryName(p))
                if ((File.GetAttributes(FileSystemPath.ForIo(p)) & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }
        private static IEnumerable<string> CacheFiles(string root, Action check)
        {
            if (!Directory.Exists(FileSystemPath.ForIo(root)) || Linked(root)) yield break;
            var pending = new Stack<string>(); pending.Push(root);
            int count = 0;
            while (pending.Count != 0)
            {
                check(); string directory = pending.Pop();
                foreach (string path in FileSystemPath.EnumerateFileSystemEntries(directory))
                {
                    check(); if (++count > 100000) throw new InvalidDataException("收藏缓存超过10万项，请缩小本地缓存后重试");
                    var attributes = File.GetAttributes(FileSystemPath.ForIo(path));
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(path); else yield return path;
                }
            }
        }
        private static string Save(string snapshots, byte[] data)
        {
            string hash = Library.Sha256Bytes(data), relative = "favorite-audio/" + hash + ".silk";
            string target = Path.Combine(snapshots, relative);
            Directory.CreateDirectory(FileSystemPath.ForIo(Path.GetDirectoryName(target)));
            if (!File.Exists(FileSystemPath.ForIo(target)))
            {
                string part=target+".part";
                try { using (var file = new FileStream(FileSystemPath.ForIo(part), FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(data, 0, data.Length); File.Move(FileSystemPath.ForIo(part), FileSystemPath.ForIo(target)); }
                finally { if(File.Exists(FileSystemPath.ForIo(part))) File.Delete(FileSystemPath.ForIo(part)); }
            }
            return relative;
        }
        internal static Dictionary<string, object> Build(string snapshots, string index, string sourceRoot, Action check, Action<Dictionary<string, object>> progress)
        {
            var voices = new List<Voice>(); var issues = new List<string>();
            string favorite = Path.Combine(snapshots, "favorite", "favorite.db");
            if (!File.Exists(FileSystemPath.ForIo(favorite))) throw new InvalidDataException("收藏库副本缺失或认证失败；请先在微信打开收藏后重新连接");
            using (var source = SqliteConnection.OpenReadOnly(favorite))
            {
                var columns = Library.Columns(source, "fav_db_item");
                foreach (string field in new[] { "local_id", "type", "update_time", "content", "fromusr", "realchatname", "flag" })
                    if (!columns.ContainsKey(field)) throw new InvalidDataException("收藏表字段不受支持，未猜测内容");
                using (var rows = source.Prepare("SELECT local_id,type,update_time,CASE WHEN length(CAST(content AS BLOB))<=16777216 THEN content ELSE '' END,fromusr,realchatname FROM fav_db_item WHERE COALESCE(flag,0)=0 ORDER BY update_time,local_id"))
                    while (rows.Step())
                    {
                        check(); if (voices.Count > 100000) throw new InvalidDataException("收藏语音超过10万条");
                        long type = rows.Int64(1) & 0xffffffffL;
                        if (type != 3 && type != 14 && type != 18) continue;
                        var parent = new Voice { Favorite = rows.Int64(0), Stamp = rows.Int64(2), Sender = rows.Text(5) ?? rows.Text(4) ?? "", Server = "", Title = "收藏语音" };
                        if (string.IsNullOrEmpty(parent.Sender)) parent.Sender = rows.Text(4) ?? "";
                        int first = voices.Count;
                        try
                        {
                            var doc = Xml(rows.Text(3) ?? "");
                            var root = doc.Root.Name.LocalName == "favitem" ? doc.Root : doc.Descendants("favitem").FirstOrDefault();
                            if (root == null) throw new InvalidDataException();
                            parent.Title = Text(root,"title"); if (parent.Title == "") parent.Title = Text(root,"desc");
                            parent.Title = parent.Title.Replace('\r',' ').Replace('\n',' '); if (parent.Title.Length > 160) parent.Title = parent.Title.Substring(0,160);
                            if (parent.Title == "") parent.Title = "收藏语音";
                            var origin = root.Element("source"); if (origin != null) parent.Server = Text(origin,"msgid") == "" ? Text(origin,"sourceid") : Text(origin,"msgid");
                            int budget = 1000; Parse(root,type,0,parent,voices,ref budget);
                        }
                        catch (Exception ex) when (ex is XmlException || ex is InvalidDataException)
                        { voices.RemoveRange(first,voices.Count-first); if (issues.Count < 100) issues.Add("一条收藏 XML 无效、含外部实体或超出解析限制，未读取其内容"); }
                        if (voices.Count == first && type == 3) voices.Add(parent);
                        for (int i = first; i < voices.Count; i++) voices[i].Id = Library.Sha256("favorite|" + parent.Favorite + "|" + (i-first)).Substring(0,24);
                    }
            }
            var needed = new HashSet<string>(voices.Where(v => v.Md5 != null && Digest(v.Md5)).Select(v => v.Md5));
            var found = new Dictionary<string, Tuple<string,long>>();
            progress(new Dictionary<string,object>{{"phase","favorites"},{"total",voices.Count},{"success",0}});
            string account = Path.GetFileName(sourceRoot).Equals("db_storage",StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(sourceRoot) : sourceRoot;
            long scanned = 0;
            int cacheScanned=0,cacheUnreadable=0,cacheHits=0,serverHits=0,ambiguous=0,md5Rejected=0,noServer=0;
            bool cachePresent=Directory.Exists(FileSystemPath.ForIo(Path.Combine(account,"business","favorite","data"))) || Directory.Exists(FileSystemPath.ForIo(Path.Combine(account,"business","favorite","temp")));
            foreach (string bucket in new[] { "data", "temp" })
                foreach (string path in CacheFiles(Path.Combine(account,"business","favorite",bucket),check))
                {
                    if (found.Count == needed.Count) break;
                    check(); cacheScanned++; var file = new FileInfo(FileSystemPath.ForIo(path)); if (file.Length <= 0 || file.Length > MaxAudio) continue;
                    scanned += file.Length; if (scanned > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("收藏缓存扫描超过4GiB，请清理无关缓存后重试");
                    try
                    {
                        byte[] data;
                        if (Linked(path)) continue;
                        using (var stream = new FileStream(FileSystemPath.ForIo(path),FileMode.Open,FileAccess.Read,FileShare.Read))
                        {
                            if (stream.Length > MaxAudio) continue;
                            using (var copy = new MemoryStream()) { var block = new byte[65536]; int n; while ((n=stream.Read(block,0,block.Length))>0) { check(); copy.Write(block,0,n); } data=copy.ToArray(); }
                        }
                        string digest = Md5(data);
                        if (needed.Contains(digest)) { found[digest] = Tuple.Create(Save(snapshots,data),(long)data.Length); progress(new Dictionary<string,object>{{"phase","favorites"},{"total",needed.Count},{"success",found.Count}}); }
                    }
                    catch (IOException) { cacheUnreadable++; }
                    catch (UnauthorizedAccessException) { cacheUnreadable++; }
                }
            foreach (var voice in voices)
            {
                check(); Tuple<string,long> cached;
                if (voice.Md5 != null && found.TryGetValue(voice.Md5,out cached)) { voice.Relative=cached.Item1; voice.Bytes=cached.Item2; cacheHits++; }
                else
                {
                    ulong server;
                    if (ulong.TryParse(voice.Server,out server) && server > 0)
                    {
                        var matches = new Dictionary<string,byte[]>();
                        foreach (string media in Directory.Exists(FileSystemPath.ForIo(Path.Combine(snapshots,"message"))) ? FileSystemPath.GetFiles(Path.Combine(snapshots,"message"),"media_*.db") : new string[0])
                            using (var db=SqliteConnection.OpenReadOnly(media))
                            {
                                var columns=Library.Columns(db,"VoiceInfo"); if (!columns.ContainsKey("svr_id") || !columns.ContainsKey("voice_data")) continue;
                                using (var rows=db.Prepare("SELECT voice_data FROM VoiceInfo WHERE svr_id=? AND length(voice_data) BETWEEN 1 AND 8388608 LIMIT 9"))
                                {
                                    rows.Bind(1,unchecked((long)server));
                                    int examined=0;
                                    while(rows.Step()) { check(); if(++examined>8) { matches["truncated-query"]=new byte[0]; break; } byte[] data=rows.Blob(0); if (voice.Md5!=null && Digest(voice.Md5) && Md5(data)!=voice.Md5) { md5Rejected++; continue; } matches[Library.Sha256Bytes(data)]=data; if(matches.Count>1)break; }
                                }
                                if(matches.Count>1)break;
                            }
                        if(matches.Count==1 && !matches.ContainsKey("truncated-query")) { byte[] data=matches.Values.First();voice.Relative=Save(snapshots,data);voice.Bytes=data.Length; serverHits++; }
                        else if(matches.Count>1 || matches.ContainsKey("truncated-query")) ambiguous++;
                    }
                    else noServer++;
                }
            }
            using(var db=SqliteConnection.OpenReadWrite(index))
            {
                db.Exec("BEGIN IMMEDIATE; CREATE TABLE contacts(user TEXT PRIMARY KEY,label TEXT); CREATE TABLE messages(chat_hash TEXT,local_id INTEGER,stamp INTEGER,sender TEXT,server_id TEXT); CREATE TABLE voices(id TEXT PRIMARY KEY,db TEXT,rid INTEGER,chat TEXT,label TEXT,local_id INTEGER,stamp INTEGER,server_id TEXT,sender TEXT,bytes INTEGER); CREATE INDEX voice_time ON voices(stamp,id); CREATE INDEX voice_chat ON voices(chat,stamp);");
                var incomplete=new HashSet<long>(voices.Where(v=>v.Bytes==0).Select(v=>v.Favorite));
                foreach(var voice in voices)
                {
                    check(); using(var insert=db.Prepare("INSERT INTO voices VALUES(?,?,?,?,?,?,?,?,?,?)"))
                    { insert.Execute(new object[]{voice.Id,voice.Relative??("favorite-audio/missing-"+voice.Id+".silk"),0,"favorite-"+voice.Favorite,(incomplete.Contains(voice.Favorite)?"[含缺失原件] ":"")+voice.Title,voice.Favorite,voice.Stamp,voice.Server??"",voice.Sender,voice.Bytes}); }
                }
                check();db.Exec("COMMIT");
            }
            int missing=voices.Count(v=>v.Bytes==0);
            if(missing>0)issues.Add(missing+" 条收藏语音原件缺失或匹配不唯一；请在微信打开并下载后重新读取。缺失项导出会计为失败。");
            progress(new Dictionary<string,object>{{"phase","favorites"},{"total",voices.Count},{"success",voices.Count-missing}});
            var summary=new Dictionary<string,object>{{"favorites",true},{"count",voices.Count},{"favorite_missing",missing},{"favorite_cache_present",cachePresent},{"favorite_cache_scanned",cacheScanned},{"favorite_cache_hits",cacheHits},{"favorite_server_hits",serverHits},{"favorite_ambiguous",ambiguous},{"favorite_md5_rejected",md5Rejected},{"favorite_no_server",noServer},{"favorite_cache_unreadable",cacheUnreadable}};
            WorkerDiagnostics.Event("favorite_summary",summary);
            return new Dictionary<string,object>{{"count",voices.Count},{"issues",issues},{"favorites",true},{"missing",missing}};
        }
    }
}
