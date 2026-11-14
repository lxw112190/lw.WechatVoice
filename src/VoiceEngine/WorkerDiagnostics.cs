using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceEngine
{
    /// <summary>
    /// Bounded, allowlisted diagnostic metadata, ported 1:1 from
    /// worker/diagnostics.py. Never serializes request/media contents; rotation
    /// 256 KiB x .1 backup, at most 32 per-request files, no absolute paths,
    /// messages or locals.
    /// </summary>
    internal static class WorkerDiagnostics
    {
        private static readonly HashSet<string> Fields = new HashSet<string>
        {
            "action","phase","count","total","current","success","matched","encrypted","scanned_mb",
            "candidates","config_names","config_objects","master_checks","limit_reached","completed","failed",
            "processed","cached","recognized","recognition_failed","cancelled","version","databases","expected_databases",
            "issue_count","selected_count","transcribe","keep_silk","has_chat","has_dates","elapsed_ms",
            "exception_type","component","errno","winerror","hresult","exit_code","stage","database_kind","module","function","line","error_code","selected_pid",
            "read_calls","read_failures","partial_reads","last_read_error","region_count",
            "snapshot_stage","bytes_current","bytes_total","speed_mb_s",
            "attempt","inner_depth","page_number","sqlite_code","sqlite_version","media_sources","schema_flags","valid_media",
            "physical_pages","logical_pages","declared_pages","free_leaf_pages","wal_committed_pages"
        };
        private static readonly Regex ValuePattern = new Regex("^[A-Za-z0-9_. -]{1,90}$", RegexOptions.Compiled);
        private static string path;
        private static string requestId;
        private static string stage = "request";
        private static readonly Dictionary<string, double> last = new Dictionary<string, double>();

        internal static void Start(string workspace, string id)
        {
            path = null; last.Clear(); stage = "request";
            if (id == null || !Regex.IsMatch(id, "^[0-9a-f]{32}$")) return;
            try
            {
                string directory = Path.Combine(workspace, "diagnostics");
                Directory.CreateDirectory(directory);
                requestId = id;
                path = Path.Combine(directory, "worker-" + id + ".jsonl");
                var files = Directory.GetFiles(directory, "worker-*.jsonl*")
                    .Where(name => Regex.IsMatch(Path.GetFileName(name), @"^worker-[0-9a-f]{32}\.jsonl(?:\.1)?$"))
                    .OrderByDescending(name => File.GetLastWriteTimeUtc(name)).ToList();
                foreach (string old in files.Skip(31))
                    try { File.Delete(old); } catch { }
            }
            catch { path = null; }
        }

        internal static void Event(string name, Dictionary<string, object> values = null)
        {
            if (path == null) return;
            try
            {
                var record = new Dictionary<string, object> { ["utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["request_id"] = requestId, ["event"] = name };
                if (values != null)
                    foreach (var entry in values)
                    {
                        if (!Fields.Contains(entry.Key) || entry.Value == null) continue;
                        if (entry.Value is bool || entry.Value is int || entry.Value is long || entry.Value is double || entry.Value is float)
                            record[entry.Key] = entry.Value;
                        else if (entry.Value is string && ValuePattern.IsMatch((string)entry.Value))
                            record[entry.Key] = entry.Value;
                    }
                if (File.Exists(path) && new FileInfo(path).Length >= 256 * 1024)
                {
                    string backup = path + ".1";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(path, backup);
                }
                File.AppendAllText(path, Json(record) + "\n", Encoding.UTF8);
            }
            catch { }
        }

        internal static void Phase(Dictionary<string, object> value)
        {
            if (value == null || !value.ContainsKey("phase")) return;
            string name = Convert.ToString(value["phase"]);
            stage = name;
            string throttleKey = name == "snapshot_detail" && value.ContainsKey("snapshot_stage") ? name + ":" + Convert.ToString(value["snapshot_stage"]) : name;
            double now = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds;
            bool completed = name == "snapshot_detail" && value.ContainsKey("bytes_current") && value.ContainsKey("bytes_total") && Convert.ToInt64(value["bytes_current"]) == Convert.ToInt64(value["bytes_total"]);
            if (!completed && last.ContainsKey(throttleKey) && now - last[throttleKey] < 2) return;
            last[throttleKey] = now;
            var filtered = new Dictionary<string, object>();
            foreach (var entry in value) if (Fields.Contains(entry.Key)) filtered[entry.Key] = entry.Value;
            Event("progress", filtered);
        }

        internal static void Failure(Exception exc, string failureStage = null, Dictionary<string, object> values = null)
        {
            // Wrapping a snapshot error must not discard its precise validation
            // stage/code. Still never serialize Exception.Message or Data wholesale.
            for (int depth = 0; exc != null && depth < 6; depth++, exc = exc.InnerException)
                FailureDetail(exc, failureStage, values, depth);
        }

        private static void FailureDetail(Exception exc, string failureStage, Dictionary<string, object> values, int depth)
        {
            var record = new Dictionary<string, object> { ["stage"] = failureStage ?? stage, ["exception_type"] = exc.GetType().Name, ["inner_depth"] = depth };
            if (exc is System.ComponentModel.Win32Exception) record["winerror"] = ((System.ComponentModel.Win32Exception)exc).NativeErrorCode;
            record["hresult"] = exc.HResult & 0xffff;
            EngineErrors.Metadata(exc, record);
            if (values != null) foreach (var entry in values) record[entry.Key] = entry.Value;
            Event("failure", record);
            // Function/line only. No exception message, source text, locals or absolute paths.
            var frames = new StackTrace(exc, true).GetFrames();
            if (frames == null) return;
            foreach (var frame in frames.Reverse().Take(10))
            {
                var frameRecord = new Dictionary<string, object>();
                try { frameRecord["module"] = Path.GetFileNameWithoutExtension(frame.GetFileName()); } catch { }
                frameRecord["function"] = frame.GetMethod() == null ? "" : frame.GetMethod().Name;
                frameRecord["line"] = frame.GetFileLineNumber();
                Event("frame", frameRecord);
            }
        }

        private static string Json(object value)
        {
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            return serializer.Serialize(value);
        }
    }
}
