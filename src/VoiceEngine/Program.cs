using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using WechatVoice;

namespace VoiceEngine
{
    /// <summary>
    /// C# engine main: one bounded JSON request on stdin, NDJSON progress and a
    /// single result on stdout — the exact protocol of the Python worker the
    /// GUI (WorkerClient.cs) already speaks. Also serves as the isolated
    /// --decode subprocess.
    /// </summary>
    internal static class Program
    {
        private static string baseDir;
        private static string cancelPath;
        private static Action<Dictionary<string, object>> progress;

        private static int Main(string[] args)
        {
            // Redirected stdin/stdout default to the OEM codepage; the protocol
            // is UTF-8 (Chinese paths and transcripts must survive).
            try { Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            // .NET Framework's redirected StandardInput writer can emit a BOM
            // under codepage 65001. Console.In does not detect that preamble;
            // consume it once at the transport boundary before parsing NDJSON.
            Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), true));
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var serializer = new JavaScriptSerializer();
            var started = DateTime.UtcNow;
            try
            {
                if (args.Length > 0 && args[0] == "--decode")
                {
                    string line = Console.In.ReadLine();
                    if (line == null || line.Length > 262144) throw new InvalidDataException("请求超过256KiB");
                    var request = serializer.Deserialize<Dictionary<string, object>>(line);
                    double duration = VoiceCodec.Decode(Convert.ToString(request["source"]), Convert.ToString(request["target"]));
                    Emit(new Dictionary<string, object> { ["ok"] = true, ["duration"] = Export.PyFloat(duration) });
                    return 0;
                }
                string input = Console.In.ReadLine();
                if (input.Length > 262144) throw new InvalidDataException("请求超过256KiB");
                var payload = serializer.Deserialize<Dictionary<string, object>>(input);
                string workspace = FileSystemPath.FullPath(payload.ContainsKey("workspace") && payload["workspace"] != null
                    ? Convert.ToString(payload["workspace"]) : Path.Combine(baseDir, "workspace"));
                Directory.CreateDirectory(FileSystemPath.ForIo(workspace));
                cancelPath = payload.ContainsKey("cancel") && payload["cancel"] != null ? FileSystemPath.FullPath(Convert.ToString(payload["cancel"])) : null;
                if (cancelPath != null && !cancelPath.StartsWith(FileSystemPath.FullPath(workspace) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("取消标记必须位于工作目录内");
                if (payload.ContainsKey("request_id")) WorkerDiagnostics.Start(workspace, Convert.ToString(payload["request_id"]));
                var begin = new Dictionary<string, object> {
                    ["action"] = payload.ContainsKey("action") ? payload["action"] : null,
                    ["selected_count"] = payload.ContainsKey("ids") && payload["ids"] is System.Collections.IEnumerable ? ((System.Collections.IEnumerable)payload["ids"]).Cast<object>().Count() : 0,
                    ["has_chat"] = payload.ContainsKey("chat") && payload["chat"] != null,
                    ["has_dates"] = payload.ContainsKey("after") || payload.ContainsKey("before"),
                    ["transcribe"] = payload.ContainsKey("transcribe") && Convert.ToBoolean(payload["transcribe"]),
                    ["favorites"] = payload.ContainsKey("favorites") && Convert.ToBoolean(payload["favorites"]),
                    ["keep_silk"] = !payload.ContainsKey("keep_silk") || Convert.ToBoolean(payload["keep_silk"]) };
                WorkerDiagnostics.Event("begin", begin);
                progress = value => { if (value.ContainsKey("phase")) WorkerDiagnostics.Phase(value); Emit(new Dictionary<string, object>(value) { ["type"] = "progress" }); };
                var result = Execute(payload, workspace, serializer);
                var complete = new Dictionary<string, object> { ["elapsed_ms"] = (long)(DateTime.UtcNow - started).TotalMilliseconds };
                foreach (string key in new[] { "count", "total", "completed", "failed", "recognized", "recognition_failed", "matched", "encrypted", "databases", "expected_databases", "cancelled" })
                    if (result.ContainsKey(key)) complete[key] = result[key];
                WorkerDiagnostics.Event("complete", complete);
                if (result.ContainsKey("issues") && result["issues"] is System.Collections.IEnumerable)
                    WorkerDiagnostics.Event("catalog_summary", new Dictionary<string, object> { ["issue_count"] = ((System.Collections.IEnumerable)result["issues"]).Cast<object>().Count() });
                if (result.ContainsKey("key_report") && result["key_report"] is Dictionary<string, object>)
                {
                    var keyReport = (Dictionary<string, object>)result["key_report"];
                    var summary = new Dictionary<string, object>();
                    foreach (string key in new[] { "matched", "encrypted", "scanned_mb", "candidates", "config_names", "config_objects", "master_checks", "limit_reached", "version" })
                        if (keyReport.ContainsKey(key)) summary[key] = keyReport[key];
                    WorkerDiagnostics.Event("key_summary", summary);
                }
                Emit(new Dictionary<string, object> { ["type"] = "result", ["ok"] = true, ["data"] = result });
                return 0;
            }
            catch (Export.CancelledException)
            {
                WorkerDiagnostics.Event("cancelled", new Dictionary<string, object> { ["elapsed_ms"] = (long)(DateTime.UtcNow - started).TotalMilliseconds });
                Emit(new Dictionary<string, object> { ["type"] = "result", ["ok"] = false, ["cancelled"] = true, ["error"] = "操作已取消，已生成的副本和文件保留在工作目录" });
                return 2;
            }
            catch (Exception exc)
            {
                WorkerDiagnostics.Failure(exc);
                // No traceback, database content or request payload in stdout/stderr.
                string message = exc.Message ?? "";
                if (message.Length > 1000) message = message.Substring(0, 1000);
                Emit(new Dictionary<string, object> { ["type"] = "result", ["ok"] = false, ["error"] = message, ["error_code"] = EngineErrors.Code(exc) });
                return 1;
            }
        }

        private static Dictionary<string, object> Execute(Dictionary<string, object> request, string workspace, JavaScriptSerializer serializer)
        {
            Action check = () =>
            {
                if (cancelPath != null && File.Exists(FileSystemPath.ForIo(cancelPath))) throw new Export.CancelledException();
            };
            string action = request.ContainsKey("action") ? Convert.ToString(request["action"]) : "";
            Dictionary<string, object> connectionReport = null;
            if (action == "connect")
            {
                // One explicit GUI action prepares only the selected PID/account and
                // then freezes copies while WeChat stays logged in.
                connectionReport = Execute(Clone(request, "keys"), workspace, serializer);
                action = "catalog";
            }
            if (action == "keys") return Keys(request, workspace, check);
            if (action == "catalog") return Catalog(request, workspace, check, connectionReport, serializer);
            if (action == "self-test")
                return new Dictionary<string, object> {
                    ["worker"] = "1.1.2", ["codec"] = "0.2.8-sdk-507be6bc",
                    ["target_wechat"] = Capture.TargetVersion, ["verified_wechat_version"] = Capture.VerifiedVersion,
                    ["native_tests"] = false };
            var paths = Export.SessionPaths(workspace, request.ContainsKey("session") ? request["session"] : null);
            if (action == "list") return Library.Listing(paths.Item3, request);
            if (action == "count") return new Dictionary<string, object> { ["count"] = Library.Counting(paths.Item3, request) };
            if (action == "export") return Export.Run(workspace, request, check, progress);
            if (action == "recognize_page") return RecognizePage(workspace, request, check);
            if (action == "preview") return Preview(workspace, request, check);
            throw new InvalidDataException("不支持的操作");
        }

        private static Dictionary<string, object> Keys(Dictionary<string, object> request, string workspace, Action check)
        {
            if (!request.ContainsKey("consent") || !Convert.ToBoolean(request["consent"]))
                throw new InvalidDataException("请先确认本人或已获授权账号");
            bool favorites = request.ContainsKey("favorites") && Convert.ToBoolean(request["favorites"]);
            var databases = favorites ? Capture.DatabasesForFavorites(Convert.ToString(request["source"])) : Capture.Databases(Convert.ToString(request["source"]));
            string root = databases.Item1;
            Export.EnsureOutside(workspace, root);
            var scanStarted = DateTime.UtcNow;
            var scan = favorites ? Capture.ScanFavorites(Convert.ToInt32(request["pid"]), root, check, progress) : Capture.Scan(Convert.ToInt32(request["pid"]), root, check, progress);
            var report = scan.Report;
            WorkerDiagnostics.Event("key_scan_result", new Dictionary<string, object> {
                ["selected_pid"] = Convert.ToInt32(request["pid"]),
                ["version"] = report.ContainsKey("version") ? report["version"] : "unknown",
                ["elapsed_ms"] = (long)(DateTime.UtcNow - scanStarted).TotalMilliseconds,
                ["limit_reached"] = report.ContainsKey("limit_reached") && Convert.ToBoolean(report["limit_reached"]),
                ["matched"] = scan.Found.Count, ["encrypted"] = Convert.ToInt32(report["encrypted"]),
                ["scanned_mb"] = report.ContainsKey("scanned_mb") ? report["scanned_mb"] : 0,
                ["candidates"] = report.ContainsKey("candidates") ? report["candidates"] : 0,
                ["config_names"] = report.ContainsKey("config_names") ? report["config_names"] : 0,
                ["config_objects"] = report.ContainsKey("config_objects") ? report["config_objects"] : 0,
                ["master_checks"] = report.ContainsKey("master_checks") ? report["master_checks"] : 0,
                ["read_calls"] = report.ContainsKey("read_calls") ? report["read_calls"] : 0,
                ["read_failures"] = report.ContainsKey("read_failures") ? report["read_failures"] : 0,
                ["partial_reads"] = report.ContainsKey("partial_reads") ? report["partial_reads"] : 0,
                ["last_read_error"] = report.ContainsKey("last_read_error") ? report["last_read_error"] : 0 });
            if (scan.Found.Count == 0 && Convert.ToInt32(report["encrypted"]) > 0)
                throw new InvalidDataException("连接失败：未获得通过页认证的密钥。扫描" + Convert.ToString(report.ContainsKey("scanned_mb") ? report["scanned_mb"] : 0) +
                    "MB，候选" + Convert.ToString(report.ContainsKey("candidates") ? report["candidates"] : 0) +
                    "，配置对象" + Convert.ToString(report.ContainsKey("config_objects") ? report["config_objects"] : 0) +
                    "。请确认主进程及当前登录账号，播放目标语音后重试；可展开高级设置选择进程。");
            string vault = Path.Combine(workspace, "keys", Hash(root) + ".dpapi");
            var previous = Capture.LoadVault(vault, root);
            var valid = new Dictionary<string, string>();
            foreach (var entry in previous)
            {
                string path = FileSystemPath.FullPath(Path.Combine(root, entry.Key));
                if (path.StartsWith(FileSystemPath.FullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(FileSystemPath.ForIo(path)) && CryptoDb.FirstPageKey(path, FromHex(entry.Value), false) != null)
                    valid[entry.Key] = entry.Value;
            }
            foreach (var entry in scan.Found) valid[entry.Key] = entry.Value;
            Capture.SaveVault(vault, root, valid);
            report["matched"] = valid.Count;
            return report;
        }

        private static Dictionary<string, object> Catalog(Dictionary<string, object> request, string workspace, Action check,
            Dictionary<string, object> connectionReport, JavaScriptSerializer serializer)
        {
            if (!request.ContainsKey("consent") || !Convert.ToBoolean(request["consent"]))
                throw new InvalidDataException("请先确认本人或已获授权账号");
            bool favorites = request.ContainsKey("favorites") && Convert.ToBoolean(request["favorites"]);
            var databases = favorites ? Capture.DatabasesForFavorites(Convert.ToString(request["source"])) : Capture.Databases(Convert.ToString(request["source"]));
            string root = databases.Item1;
            var files = databases.Item2;
            Export.EnsureOutside(workspace, root);
            string vault = Path.Combine(workspace, "keys", Hash(root) + ".dpapi");
            var vaultKeys = Capture.LoadVault(vault, root);
            string session = Guid.NewGuid().ToString("N");
            var paths = Export.SessionPaths(workspace, session);
            string folder = paths.Item1, snapshots = paths.Item2, index = paths.Item3;
            Directory.CreateDirectory(FileSystemPath.ForIo(folder));
            var issues = new List<string>();
            WorkerDiagnostics.Event("sqlite_environment", new Dictionary<string, object> { ["sqlite_version"] = SqliteConnection.LibraryVersion() });
            int success = 0;
            int mediaSuccess = 0;
            Exception mediaFailure = null;
            for (int position = 0; position < files.Count; position++)
            {
                string path = files[position];
                check();
                string relative = path.Substring(root.Length + 1);
                try
                {
                    string wal = path + "-wal";
                    if (!FileSystemPath.FullPath(wal).StartsWith(FileSystemPath.FullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("WAL链接越过源目录");
                    byte[] key = vaultKeys.ContainsKey(relative) ? FromHex(vaultKeys[relative]) : null;
                    CryptoDb.LiveSnapshotDetailed(path, Path.Combine(snapshots, relative), key, check,
                        (attempt, total) => progress(new Dictionary<string, object> { ["phase"] = "snapshot_retry", ["attempt"] = attempt, ["total"] = total }),
                        detail => { detail["current"] = position + 1; detail["total"] = files.Count; progress(detail); });
                    success++;
                    if (Path.GetFileName(path).StartsWith("media_", StringComparison.OrdinalIgnoreCase)) mediaSuccess++;
                }
                catch (Export.CancelledException) { throw; }
                catch (Exception exc)
                {
                    WorkerDiagnostics.Failure(exc, "snapshot", new Dictionary<string, object> { ["database_kind"] = Path.GetFileNameWithoutExtension(path).Split('_')[0] });
                    issues.Add(Path.GetFileName(path) + ": " + exc.Message);
                    if (mediaFailure == null && Path.GetFileName(path).StartsWith("media_", StringComparison.OrdinalIgnoreCase)) mediaFailure = exc;
                }
                progress(new Dictionary<string, object> { ["phase"] = "snapshot", ["current"] = position + 1, ["total"] = files.Count, ["success"] = success });
            }
            Dictionary<string, object> report;
            if (!favorites && mediaSuccess == 0)
                throw EngineErrors.Create("MEDIA_SNAPSHOT_MISSING", "语音库副本创建失败，尚未读取 VoiceInfo；不能将其判定为表结构不支持。" +
                    (mediaFailure == null ? "请导出诊断包检查。" : "\r\n语音库原因：" + mediaFailure.Message + "\r\n诊断代码：" + EngineErrors.Code(mediaFailure)), mediaFailure);
            try { report = favorites ? Favorites.Build(snapshots, index, root, check, progress) : Library.Build(snapshots, index, check, progress); }
            catch (InvalidDataException exc)
            {
                if (issues.Count > 0) throw new InvalidDataException(exc.Message + "；副本详情：" + string.Join("；", issues.Take(8)), exc);
                throw;
            }
            foreach (object issue in (System.Collections.IEnumerable)report["issues"]) issues.Add(Convert.ToString(issue));
            report["session"] = session;
            report["issues"] = issues;
            report["source"] = root;
            report["databases"] = success;
            report["expected_databases"] = files.Count;
            report["snapshot_mode"] = "online-per-database";
            report["snapshot_note"] = "各库分别冻结并验证，不代表跨库同一时刻；需要近期语音时可重新连接刷新。";
            if (connectionReport != null) report["key_report"] = connectionReport;
            Export.WriteJson(Path.Combine(folder, "session.json"), report);
            return report;
        }

        private static Dictionary<string, object> RecognizePage(string workspace, Dictionary<string, object> request, Action check)
        {
            var ids = request.ContainsKey("ids") ? request["ids"] as System.Collections.IEnumerable : null;
            var list = ids == null ? new List<object>() : ids.Cast<object>().ToList();
            if (ids == null || list.Count < 1 || list.Count > 50 || list.Select(Convert.ToString).Distinct().Count() != list.Count)
                throw new InvalidDataException("请指定本页1至50条不重复语音");
            var paths = Export.SessionPaths(workspace, request.ContainsKey("session") ? request["session"] : null);
            string folder = paths.Item1, snapshots = paths.Item2, index = paths.Item3;
            var rows = Library.Records(index, request, null);
            if (rows.Count != list.Count) throw new InvalidDataException("本页语音范围已改变，请刷新后重试");
            string asrRoot = Path.GetDirectoryName(baseDir.TrimEnd('\\', '/'));
            int processed = 0, cached = 0, recognized = 0, failed = 0;
            bool cancelled = false;
            string temporary = Path.Combine(folder, "recognition-tmp");
            Directory.CreateDirectory(FileSystemPath.ForIo(temporary));
            try
            {
                using (var recognizer = new LocalRecognizer(Path.Combine(asrRoot, "asr"), check))
                {
                    foreach (var row in rows)
                    {
                        check();
                        string raw = Path.Combine(temporary, Guid.NewGuid().ToString("N") + ".silk");
                        string wav = raw + ".wav";
                        try
                        {
                            byte[] data = Library.Payload(snapshots, row);
                            string digest = Library.Sha256Bytes(data);
                            var cachedValue = SpeechCache.Lookup(index, Convert.ToString(row["id"]), digest);
                            if (cachedValue != null) cached++;
                            else
                            {
                                File.WriteAllBytes(FileSystemPath.ForIo(raw), data);
                                VoiceCodec.Isolated(raw, wav, check);
                                string text = recognizer.Recognize(wav);
                                check();
                                SpeechCache.Save(index, Convert.ToString(row["id"]), digest, text != "" ? "READY" : "EMPTY", text);
                                recognized++;
                            }
                        }
                        catch (Export.CancelledException) { throw; }
                        catch (Exception exc)
                        {
                            failed++;
                            WorkerDiagnostics.Failure(exc, "page_recognition");
                            try { SpeechCache.Save(index, Convert.ToString(row["id"]), "", "ERROR", ""); }
                            catch { }
                        }
                        finally
                        {
                            if (File.Exists(FileSystemPath.ForIo(raw))) try { File.Delete(FileSystemPath.ForIo(raw)); } catch { }
                            if (File.Exists(FileSystemPath.ForIo(wav))) try { File.Delete(FileSystemPath.ForIo(wav)); } catch { }
                        }
                        processed++;
                        Emit(new Dictionary<string, object> {
                            ["type"] = "progress", ["phase"] = "page_recognition", ["processed"] = processed,
                            ["total"] = rows.Count, ["cached"] = cached, ["recognized"] = recognized, ["failed"] = failed });
                    }
                }
            }
            catch (Export.CancelledException) { cancelled = true; }
            return new Dictionary<string, object> {
                ["processed"] = processed, ["total"] = rows.Count, ["cached"] = cached,
                ["recognized"] = recognized, ["failed"] = failed, ["cancelled"] = cancelled };
        }

        private static Dictionary<string, object> Preview(string workspace, Dictionary<string, object> request, Action check)
        {
            var paths = Export.SessionPaths(workspace, request.ContainsKey("session") ? request["session"] : null);
            string folder = paths.Item1, snapshots = paths.Item2, index = paths.Item3;
            var idRequest = new Dictionary<string, object> { ["ids"] = new List<string> { Convert.ToString(request["id"]) } };
            var rows = Library.Records(index, idRequest, null);
            if (rows.Count != 1) throw new InvalidDataException("该语音未找到");
            string cache = Path.Combine(folder, "preview");
            Directory.CreateDirectory(FileSystemPath.ForIo(cache));
            string raw = Path.Combine(cache, Convert.ToString(rows[0]["id"]) + ".silk");
            string wav = raw + ".wav";
            if (!File.Exists(FileSystemPath.ForIo(wav)))
            {
                File.WriteAllBytes(FileSystemPath.ForIo(raw), Library.Payload(snapshots, rows[0]));
                VoiceCodec.Isolated(raw, wav, check);
            }
            return new Dictionary<string, object> { ["path"] = wav };
        }

        private static Dictionary<string, object> Clone(Dictionary<string, object> source, string action)
        {
            var result = new Dictionary<string, object>(source) { ["action"] = action };
            return result;
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return Library.Sha256Bytes(Encoding.UTF8.GetBytes(value));
        }

        private static byte[] FromHex(string value)
        {
            var result = new byte[value.Length / 2];
            for (int i = 0; i < result.Length; i++) result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            return result;
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) if (data[i] != prefix[i]) return false;
            return true;
        }

        private static void Emit(Dictionary<string, object> value)
        {
            Console.WriteLine(new JavaScriptSerializer().Serialize(value));
            Console.Out.Flush();
        }
    }
}
