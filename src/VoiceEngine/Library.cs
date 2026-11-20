using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WechatVoice;

namespace VoiceEngine
{
    /// <summary>
    /// A paged private catalogue, ported 1:1 from worker/library.py. Payloads
    /// remain in immutable database snapshots. Comparison tests
    /// (tests/LibraryCheck.cs) require identical index rows, listings, filters
    /// and sorts against the Python reference on the same fixtures.
    /// </summary>
    internal static class Library
    {
        private const long MaxBlob = 8L * 1024 * 1024;
        private static readonly Regex IdentPattern = new Regex(@"^[A-Za-z_][A-Za-z_0-9]*$", RegexOptions.Compiled);
        private static readonly Regex MsgTablePattern = new Regex(@"^Msg_[0-9a-fA-F]{32}$", RegexOptions.Compiled);
        private static readonly Regex IdPattern = new Regex(@"^[0-9a-f]{24}$", RegexOptions.Compiled);

        internal static string Ident(string value)
        {
            if (value == null || !IdentPattern.IsMatch(value))
                throw new InvalidDataException("数据库字段名称不受支持");
            return "\"" + value + "\"";
        }

        internal static Dictionary<string, string> Columns(SqliteConnection db, string table)
        {
            var result = new Dictionary<string, string>();
            using (var statement = db.Prepare("PRAGMA table_info(" + Ident(table) + ")"))
                while (statement.Step())
                {
                    string name = statement.Text(1);
                    result[name.ToLowerInvariant()] = name;
                }
            return result;
        }

        internal static Dictionary<long, string> Names(SqliteConnection db)
        {
            if (!Columns(db, "Name2Id").ContainsKey("user_name"))
                throw new InvalidDataException("Name2Id格式不同，需要补充适配；未猜测联系人");
            var result = new Dictionary<long, string>();
            using (var statement = db.Prepare("SELECT rowid,user_name FROM Name2Id"))
                while (statement.Step()) result[statement.Int64(0)] = statement.Text(1) ?? "";
            return result;
        }

        internal static Dictionary<string, object> Build(string folder, string indexPath, Action check, Action<Dictionary<string, object>> progress)
        {
            string root = FileSystemPath.FullPath(folder);
            if (File.Exists(FileSystemPath.ForIo(indexPath)))
                throw new InvalidDataException("索引已存在，请新建读取会话");
            using (var db = SqliteConnection.OpenReadWrite(indexPath))
            {
                // One transaction for this private, unpublished index. Disposing on any
                // error/cancellation rolls back, instead of fsync on every inserted row.
                db.Exec("BEGIN IMMEDIATE");
                db.Exec(@"CREATE TABLE contacts(user TEXT PRIMARY KEY,label TEXT);
                  CREATE TABLE messages(chat_hash TEXT,local_id INTEGER,stamp INTEGER,sender TEXT,server_id TEXT);
                  CREATE INDEX msg_key ON messages(chat_hash,local_id,stamp);
                  CREATE INDEX msg_server ON messages(chat_hash,server_id,stamp);
                  CREATE TABLE voices(id TEXT PRIMARY KEY,db TEXT,rid INTEGER,chat TEXT,label TEXT,
                    local_id INTEGER,stamp INTEGER,server_id TEXT,sender TEXT,bytes INTEGER);
                  CREATE INDEX voice_time ON voices(stamp,id); CREATE INDEX voice_chat ON voices(chat,stamp);");
                int total = 0, expected = 0, validMedia = 0;
                var issues = new List<string>();
                foreach (string path in FindFiles(root, "contact.db"))
                {
                    if (check != null) check();
                    using (var source = SqliteConnection.OpenReadOnly(path))
                    {
                        var cols = Columns(source, "contact");
                        if (!cols.ContainsKey("username")) { AddIssue(issues, "联系人表格式不同，使用账号标识显示"); continue; }
                        var extras = new List<string>();
                        foreach (string candidate in new[] { "remark", "nick_name", "alias" }) if (cols.ContainsKey(candidate)) extras.Add(cols[candidate]);
                        var select = new List<string> { cols["username"] };
                        select.AddRange(extras);
                        using (var statement = source.Prepare("SELECT " + string.Join(",", select.Select(Ident)) + " FROM contact"))
                            while (statement.Step())
                            {
                                if (check != null) check();
                                string user = statement.Text(0) ?? "";
                                string label = user;
                                for (int i = 1; i <= extras.Count; i++) { string value = statement.Text(i); if (!string.IsNullOrEmpty(value)) { label = value; break; } }
                                using (var insert = db.Prepare("INSERT OR REPLACE INTO contacts VALUES(?,?)"))
                                {
                                    insert.Bind(1, user); insert.Bind(2, label.Length > 200 ? label.Substring(0, 200) : label); insert.Step();
                                }
                            }
                    }
                }
                var labels = new Dictionary<string, string>();
                using (var statement = db.Prepare("SELECT user,label FROM contacts"))
                    while (statement.Step()) labels[statement.Text(0)] = statement.Text(1);
                foreach (string path in FindFiles(root, "message_*.db"))
                {
                    if (check != null) check();
                    try
                    {
                        using (var source = SqliteConnection.OpenReadOnly(path))
                        {
                            var mapping = Names(source);
                            var tables = new List<string>();
                            using (var statement = source.Prepare("SELECT name FROM sqlite_master WHERE type='table'"))
                                while (statement.Step())
                                {
                                    string name = statement.Text(0);
                                    if (MsgTablePattern.IsMatch(name)) tables.Add(name);
                                }
                            foreach (string table in tables)
                            {
                                if (check != null) check();
                                var cols = Columns(source, table);
                                if (!cols.ContainsKey("local_id") || !cols.ContainsKey("create_time") || !cols.ContainsKey("local_type"))
                                { AddIssue(issues, Path.GetFileName(path) + ": 消息字段不同，发送者可能未知"); continue; }
                                string sender = cols.ContainsKey("real_sender_id") ? Ident(cols["real_sender_id"]) : "NULL";
                                string server = cols.ContainsKey("server_id") ? Ident(cols["server_id"]) : "''";
                                using (var statement = source.Prepare("SELECT local_id,create_time," + sender + "," + server + " FROM " + Ident(table) + " WHERE (local_type & 4294967295)=34"))
                                    while (statement.Step())
                                    {
                                        if (check != null) check();
                                        expected++;
                                        if (expected > 2000000) throw new InvalidDataException("语音消息超过200万条，首版暂不支持");
                                        long senderId = statement.Int64(2);
                                        using (var insert = db.Prepare("INSERT INTO messages VALUES(?,?,?,?,?)"))
                                        {
                                            insert.Bind(1, table.Substring(4).ToLowerInvariant());
                                            insert.Bind(2, statement.Int64(0));
                                            insert.Bind(3, statement.Int64(1));
                                            insert.Bind(4, mapping.ContainsKey(senderId) ? mapping[senderId] : "");
                                            insert.Bind(5, statement.TextOrEmpty(3));
                                            insert.Step();
                                        }
                                    }
                            }
                        }
                    }
                    catch (Exception exc) when (exc is InvalidDataException || exc is IOException)
                    { AddIssue(issues, Path.GetFileName(path) + ": 消息结构不兼容，发送者标为未知"); }
                }
                foreach (string path in FindFiles(root, "media_*.db"))
                {
                    if (check != null) check();
                    using (var source = SqliteConnection.OpenReadOnly(path))
                    {
                        var cols = Columns(source, "VoiceInfo");
                        int schemaFlags = (cols.ContainsKey("chat_name_id") ? 1 : 0) | (cols.ContainsKey("local_id") ? 2 : 0) |
                            (cols.ContainsKey("create_time") ? 4 : 0) | (cols.ContainsKey("voice_data") ? 8 : 0) |
                            (cols.ContainsKey("svr_id") ? 16 : 0) | (cols.Count > 0 ? 64 : 0);
                        WorkerDiagnostics.Event("media_schema", new Dictionary<string, object> { ["database_kind"] = "media", ["schema_flags"] = schemaFlags });
                        if (!cols.ContainsKey("chat_name_id") || !cols.ContainsKey("local_id") || !cols.ContainsKey("create_time") || !cols.ContainsKey("voice_data"))
                        { AddIssue(issues, Path.GetFileName(path) + ": VoiceInfo格式不同，未读取"); continue; }
                        var mapping = Names(source);
                        validMedia++;
                        string server = cols.ContainsKey("svr_id") ? Ident(cols["svr_id"]) : "''";
                        using (var statement = source.Prepare("SELECT rowid,chat_name_id,local_id,create_time," + server + ",length(voice_data) FROM VoiceInfo"))
                            while (statement.Step())
                            {
                                if (check != null) check();
                                total++;
                                if (total > 1000000) throw new InvalidDataException("语音超过100万条，首版暂不支持");
                                long rid = statement.Int64(0), chatId = statement.Int64(1), local = statement.Int64(2), stamp = statement.Int64(3);
                                string chat = mapping.ContainsKey(chatId) ? mapping[chatId] : "";
                                if (string.IsNullOrEmpty(chat)) chat = "未知会话-" + chatId + "-" + Path.GetFileNameWithoutExtension(path);
                                string chatHash = Md5(Encoding.UTF8.GetBytes(chat));
                                // Media and message local IDs need not be the same. Prefer the
                                // exact server ID, still scoped to this conversation and time.
                                string serverId = statement.TextOrEmpty(4);
                                string sender = ResolveSender(db, chatHash, serverId, local, stamp);
                                string relative = path.Substring(root.Length + 1).Replace('\\', '/');
                                string identity = Sha256((relative + "|" + rid)) .Substring(0, 24);
                                string label = labels.ContainsKey(chat) ? labels[chat] : chat;
                                if (label.Length > 200) label = label.Substring(0, 200);
                                string senderLabel = sender == "" ? "未知发送者" : (labels.ContainsKey(sender) ? labels[sender] : sender);
                                using (var insert = db.Prepare("INSERT INTO voices VALUES(?,?,?,?,?,?,?,?,?,?)"))
                                {
                                    insert.Bind(1, identity); insert.Bind(2, relative); insert.Bind(3, rid); insert.Bind(4, chat);
                                    insert.Bind(5, label); insert.Bind(6, local); insert.Bind(7, stamp); insert.Bind(8, serverId);
                                    insert.Bind(9, senderLabel); insert.Bind(10, statement.Int64(5));
                                    insert.Step();
                                }
                                if (total % 200 == 0 && progress != null) progress(new Dictionary<string, object> { ["phase"] = "catalog", ["count"] = total });
                            }
                    }
                }
                if (validMedia == 0)
                    throw EngineErrors.Create("VOICE_SCHEMA_UNSUPPORTED", "语音库副本存在，但未找到支持的 VoiceInfo 字段；请导出诊断包检查表结构。");
                if (check != null) check();
                db.Exec("COMMIT");
                return new Dictionary<string, object> { ["count"] = total, ["message_voices"] = expected, ["issues"] = issues.Take(100).ToList() };
            }
        }

        private static string ResolveSender(SqliteConnection db, string chatHash, string serverId, long local, long stamp)
        {
            var matches = new List<string>();
            if (serverId != "" && serverId.All(char.IsDigit) && long.Parse(serverId) > 0)
            {
                using (var statement = db.Prepare("SELECT DISTINCT sender FROM messages WHERE chat_hash=? AND server_id=? AND stamp=?"))
                {
                    statement.Bind(1, chatHash); statement.Bind(2, serverId); statement.Bind(3, stamp);
                    while (statement.Step()) matches.Add(statement.Text(0) ?? "");
                }
                if (matches.Count == 0)
                    using (var statement = db.Prepare("SELECT DISTINCT sender FROM messages WHERE chat_hash=? AND local_id=? AND stamp=? AND server_id IN ('','0')"))
                    {
                        statement.Bind(1, chatHash); statement.Bind(2, local); statement.Bind(3, stamp);
                        while (statement.Step()) matches.Add(statement.Text(0) ?? "");
                    }
            }
            else
                using (var statement = db.Prepare("SELECT DISTINCT sender FROM messages WHERE chat_hash=? AND local_id=? AND stamp=?"))
                {
                    statement.Bind(1, chatHash); statement.Bind(2, local); statement.Bind(3, stamp);
                    while (statement.Step()) matches.Add(statement.Text(0) ?? "");
                }
            return matches.Count == 1 && matches[0] != "" ? matches[0] : "";
        }

        internal static string QueryFilter(Dictionary<string, object> request, List<object> args)
        {
            var terms = new List<string>();
            if (request.ContainsKey("chat") && request["chat"] != null)
            {
                terms.Add("chat=?"); args.Add(Convert.ToString(request["chat"]));
            }
            foreach (var pair in new[] { new KeyValuePair<string, string>("after", ">="), new KeyValuePair<string, string>("before", "<") })
            {
                if (request.ContainsKey(pair.Key) && request[pair.Key] != null)
                {
                    terms.Add("stamp" + pair.Value + "?"); args.Add(Convert.ToInt64(request[pair.Key]));
                }
            }
            if (request.ContainsKey("after") && request.ContainsKey("before") &&
                request["after"] != null && request["before"] != null && Convert.ToInt64(request["after"]) >= Convert.ToInt64(request["before"]))
                throw new InvalidDataException("开始日期必须早于结束边界");
            if (request.ContainsKey("ids"))
            {
                var ids = request["ids"] as System.Collections.IEnumerable;
                var list = ids == null ? new List<object>() : ids.Cast<object>().ToList();
                if (ids == null || list.Count > 500 || list.Any(x => !IdPattern.IsMatch(Convert.ToString(x))))
                    throw new InvalidDataException("选择项无效或超过500条");
                terms.Add(list.Count == 0 ? "0" : "id IN (" + string.Join(",", Enumerable.Repeat("?", list.Count)) + ")");
                args.AddRange(list);
            }
            return terms.Count == 0 ? "" : " WHERE " + string.Join(" AND ", terms);
        }

        internal static List<Dictionary<string, object>> Records(string indexPath, Dictionary<string, object> request, long? page)
        {
            var result = new List<Dictionary<string, object>>();
            var args = new List<object>();
            string where = QueryFilter(request, args);
            using (var db = SqliteConnection.OpenReadOnly(indexPath))
            {
                string sql;
                if (page == null)
                {
                    sql = "SELECT * FROM voices" + where + " ORDER BY stamp,id";
                }
                else
                {
                    var fields = new Dictionary<string, string> {
                        ["stamp"] = "v.stamp", ["sender"] = "v.sender COLLATE NOCASE", ["label"] = "v.label COLLATE NOCASE",
                        ["bytes"] = "v.bytes", ["local_id"] = "v.local_id", ["transcript"] = "''", ["recognition_status"] = "CAST(2 AS INTEGER)" };
                    string column = request.ContainsKey("sort") ? Convert.ToString(request["sort"]) : "stamp";
                    bool descending = request.ContainsKey("descending") && Convert.ToBoolean(request["descending"]);
                    if (!fields.ContainsKey(column)) throw new InvalidDataException("语音排序条件无效");
                    string join = "";
                    string cachePath = SpeechCache.Path(indexPath);
                    if ((column == "transcript" || column == "recognition_status") && File.Exists(FileSystemPath.ForIo(cachePath)))
                    {
                        try
                        {
                            using (var attach = db.Prepare("ATTACH DATABASE ? AS speech"))
                            {
                                attach.Bind(1, SqliteConnection.FileUri(cachePath, "mode=ro"));
                                attach.Step();
                            }
                            using (var probe = db.Prepare("SELECT id FROM speech.transcripts LIMIT 0")) probe.Step();
                            join = " LEFT JOIN speech.transcripts t ON t.id=v.id AND t.profile=?";
                            args.Add(SpeechCache.Profile);
                            fields["transcript"] = "COALESCE(t.text,'') COLLATE NOCASE";
                            fields["recognition_status"] = "CASE t.status WHEN 'READY' THEN 0 WHEN 'EMPTY' THEN 1 WHEN 'ERROR' THEN 3 ELSE 2 END";
                        }
                        catch (InvalidDataException) { /* cache warnings surface via listing; source rows remain readable */ }
                    }
                    sql = "SELECT v.* FROM (SELECT * FROM voices" + where + ") v" + join +
                        " ORDER BY " + fields[column] + (descending ? " DESC" : " ASC") + ",v.stamp ASC,v.id ASC" +
                        " LIMIT 50 OFFSET ?";
                    args.Add(Math.Max(0, (long)page.Value) * 50);
                }
                using (var statement = db.Prepare(sql))
                {
                    for (int i = 0; i < args.Count; i++) statement.Bind(i + 1, args[i]);
                    while (statement.Step())
                        result.Add(new Dictionary<string, object> {
                            ["id"] = statement.Text(0), ["db"] = statement.Text(1), ["rid"] = statement.Int64(2),
                            ["chat"] = statement.Text(3), ["label"] = statement.Text(4), ["local_id"] = statement.Int64(5),
                            ["stamp"] = statement.Int64(6), ["server_id"] = statement.Text(7), ["sender"] = statement.Text(8),
                            ["bytes"] = statement.Int64(9) });
                }
            }
            return result;
        }

        internal static long Counting(string indexPath, Dictionary<string, object> request)
        {
            var args = new List<object>();
            string where = QueryFilter(request, args);
            using (var db = SqliteConnection.OpenReadOnly(indexPath))
            using (var statement = db.Prepare("SELECT COUNT(*) FROM voices" + where))
            {
                for (int i = 0; i < args.Count; i++) statement.Bind(i + 1, args[i]);
                statement.Step();
                return statement.Int64(0);
            }
        }

        internal static Dictionary<string, object> Listing(string indexPath, Dictionary<string, object> request)
        {
            long count = Counting(indexPath, request);
            var contacts = new List<Dictionary<string, object>>();
            if (!request.ContainsKey("include_contacts") || Convert.ToBoolean(request["include_contacts"]))
                using (var db = SqliteConnection.OpenReadOnly(indexPath))
                using (var statement = db.Prepare("SELECT chat,label,COUNT(*),MIN(stamp),MAX(stamp) FROM voices GROUP BY chat,label ORDER BY label LIMIT 10000"))
                    while (statement.Step())
                        contacts.Add(new Dictionary<string, object> {
                            ["user"] = statement.Text(0), ["label"] = statement.Text(1), ["count"] = statement.Int64(2),
                            ["first_stamp"] = statement.Int64(3), ["latest_stamp"] = statement.Int64(4) });
            long page = request.ContainsKey("page") ? Convert.ToInt64(request["page"]) : 0;
            page = Math.Max(0, page);
            var items = Records(indexPath, request, page);
            bool warning = SpeechCache.Annotate(indexPath, items);
            return new Dictionary<string, object> { ["count"] = count, ["page"] = page, ["items"] = items, ["contacts"] = contacts, ["cache_warning"] = warning };
        }

        internal static byte[] Payload(string folder, Dictionary<string, object> row)
        {
            string root = FileSystemPath.FullPath(folder);
            string path = FileSystemPath.FullPath(Path.Combine(root, Convert.ToString(row["db"])));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && path != root)
                throw new InvalidDataException("快照路径越界");
            if (Convert.ToString(row["db"]).StartsWith("favorite-audio/", StringComparison.Ordinal))
            {
                var file = new FileInfo(FileSystemPath.ForIo(path));
                if (!file.Exists) throw EngineErrors.Create("FAVORITE_AUDIO_MISSING", "收藏语音原件缺失；请在电脑微信打开并播放这条收藏，下载完成后重新点击“收藏语音”");
                if (file.Length <= 0 || file.Length > MaxBlob) throw EngineErrors.Create("FAVORITE_AUDIO_SIZE", "收藏语音副本为空或超过8MiB上限；请重新读取收藏");
                byte[] data = File.ReadAllBytes(FileSystemPath.ForIo(path));
                if (Path.GetFileNameWithoutExtension(path) != Sha256Bytes(data)) throw EngineErrors.Create("FAVORITE_AUDIO_CHANGED", "收藏语音副本校验失败；请重新读取收藏");
                return data;
            }
            using (var db = SqliteConnection.OpenReadOnly(path))
            using (var statement = db.Prepare("SELECT length(voice_data) FROM VoiceInfo WHERE rowid=?"))
            {
                statement.Bind(1, row["rid"]);
                if (!statement.Step() || statement.Int64(0) <= 0 || statement.Int64(0) > MaxBlob)
                    throw new InvalidDataException("语音为空或大于8MiB，原始数据库副本仍保留");
            }
            using (var db = SqliteConnection.OpenReadOnly(path))
            using (var statement = db.Prepare("SELECT voice_data FROM VoiceInfo WHERE rowid=?"))
            {
                statement.Bind(1, row["rid"]);
                statement.Step();
                return statement.Blob(0);
            }
        }

        internal static string Filename(Dictionary<string, object> row)
        {
            string stamp = FormatStamp(row);
            return stamp + "_" + Clean(row.ContainsKey("label") ? Convert.ToString(row["label"]) : "") + "_" + Convert.ToString(row["local_id"]) + "_" + Convert.ToString(row["id"]);
        }

        internal static string WavFilename(Dictionary<string, object> row, string text)
        {
            string stamp = FormatStamp(row);
            string summary = Regex.Replace(text ?? "", @"<\|.*?\|>", "");
            summary = Regex.Replace(summary, @"[<>:""/\\|?*\x00-\x1f\x7f]", "_");
            summary = Regex.Replace(summary, @"\s+", " ").Trim(' ', '.', '_');
            if (summary.Length > 40) summary = summary.Substring(0, 40);
            summary = summary.TrimEnd(' ', '.');
            if (summary == "") summary = "未识别语音";
            return stamp + "_" + Convert.ToString(row["id"]) + "_" + summary + ".wav";
        }

        private static string FormatStamp(Dictionary<string, object> row)
        {
            try
            {
                var offset = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(row["stamp"]));
                return offset.LocalDateTime.ToString("yyyyMMdd-HHmmss");
            }
            catch { return "时间未知"; }
        }

        private static string Clean(string text)
        {
            string value = Regex.Replace(text ?? "", @"[<>:""/\\|?*\x00-\x1f]", "_").Trim(' ', '.');
            if (value.Length > 36) value = value.Substring(0, 36);
            if (value == "") value = "未知";
            if (Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) value = "_" + value;
            return value;
        }

        private static List<string> FindFiles(string root, string pattern)
        {
            var found = new List<string>();
            foreach (string path in FileSystemPath.GetFiles(root, pattern, SearchOption.AllDirectories)) found.Add(FileSystemPath.FullPath(path));
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        private static void AddIssue(List<string> issues, string issue)
        {
            if (!issues.Contains(issue)) issues.Add(issue);
        }

        internal static string Md5(byte[] value)
        {
            using (var md5 = MD5.Create()) return ToHex(md5.ComputeHash(value));
        }

        internal static string Sha256(string value)
        {
            using (var sha = SHA256.Create()) return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }

        internal static string Sha256Bytes(byte[] value)
        {
            using (var sha = SHA256.Create()) return ToHex(sha.ComputeHash(value));
        }

        private static string ToHex(byte[] value)
        {
            var chars = new char[value.Length * 2];
            for (int i = 0; i < value.Length; i++)
            {
                int high = value[i] >> 4, low = value[i] & 15;
                chars[i * 2] = (char)(high < 10 ? '0' + high : 'a' + high - 10);
                chars[i * 2 + 1] = (char)(low < 10 ? '0' + low : 'a' + low - 10);
            }
            return new string(chars);
        }
    }
}
