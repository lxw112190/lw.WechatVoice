using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WechatVoice;

namespace VoiceEngine
{
    /// <summary>
    /// Private per-snapshot transcript cache, ported 1:1 from
    /// worker/speech_cache.py. Never part of diagnostic bundles.
    /// </summary>
    internal static class SpeechCache
    {
        internal const string Profile = "sensevoice-c71f0ce0-sherpa2.0.1-auto-itn-v1";
        private static readonly Regex IdPattern = new Regex(@"^[0-9a-f]{24}$", RegexOptions.Compiled);

        internal static string Path(string index) { return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(index), "transcripts.sqlite"); }

        internal static Dictionary<string, object> Lookup(string index, string identity, string digest)
        {
            var rows = Read(index, new List<string> { identity });
            if (!rows.ContainsKey(identity)) return null;
            var value = rows[identity] as Dictionary<string, object>;
            if (value == null) return null;
            if (Convert.ToString(value["digest"]) == digest &&
                (Convert.ToString(value["status"]) == "READY" || Convert.ToString(value["status"]) == "EMPTY"))
                return value;
            return null;
        }

        internal static Dictionary<string, object> Read(string index, List<string> ids)
        {
            string file = Path(index);
            var result = new Dictionary<string, object>();
            if (!File.Exists(FileSystemPath.ForIo(file)) || ids == null || ids.Count == 0) return result;
            if (ids.Count > 50) throw new InvalidDataException("缓存每次只读取一页");
            using (var db = SqliteConnection.OpenReadOnly(file))
            {
                var sql = "SELECT id,digest,status,text FROM transcripts WHERE profile=? AND id IN (" + string.Join(",", Enumerable.Repeat("?", ids.Count)) + ")";
                using (var statement = db.Prepare(sql))
                {
                    statement.Bind(1, Profile);
                    for (int i = 0; i < ids.Count; i++) statement.Bind(i + 2, ids[i]);
                    while (statement.Step())
                    {
                        string status = statement.Text(2);
                        string text = statement.Text(3);
                        if ((status == "READY" || status == "EMPTY" || status == "ERROR") && text != null && text.Length <= 2000)
                            result[statement.Text(0)] = new Dictionary<string, object> {
                                ["digest"] = statement.Text(1), ["status"] = status, ["text"] = text };
                    }
                }
            }
            return result;
        }

        internal static void Save(string index, string identity, string digest, string status, string text)
        {
            if (status != "READY" && status != "EMPTY" && status != "ERROR") throw new InvalidDataException("识别缓存格式无效");
            if (text == null || text.Length > 2000) throw new InvalidDataException("识别缓存格式无效");
            using (var db = SqliteConnection.OpenReadWrite(Path(index)))
            {
                db.Exec("CREATE TABLE IF NOT EXISTS transcripts(id TEXT PRIMARY KEY,digest TEXT NOT NULL,profile TEXT NOT NULL,status TEXT NOT NULL,text TEXT NOT NULL)");
                using (var statement = db.Prepare("INSERT OR REPLACE INTO transcripts VALUES(?,?,?,?,?)"))
                {
                    statement.Bind(1, identity); statement.Bind(2, digest); statement.Bind(3, Profile);
                    statement.Bind(4, status); statement.Bind(5, text);
                    statement.Step();
                }
            }
        }

        internal static bool Annotate(string index, List<Dictionary<string, object>> items)
        {
            Dictionary<string, object> values;
            bool warning;
            try
            {
                values = Read(index, items.Select(row => Convert.ToString(row["id"])).ToList());
                warning = false;
            }
            catch (Exception exc) when (exc is InvalidDataException || exc is IOException)
            {
                values = new Dictionary<string, object>();
                warning = true;
            }
            foreach (var row in items)
            {
                var value = values.ContainsKey(Convert.ToString(row["id"])) ? values[Convert.ToString(row["id"])] as Dictionary<string, object> : null;
                row["transcript"] = value == null ? "" : Convert.ToString(value["text"]);
                row["recognition_status"] = value == null ? "UNCACHED" : Convert.ToString(value["status"]);
            }
            return warning;
        }
    }
}
