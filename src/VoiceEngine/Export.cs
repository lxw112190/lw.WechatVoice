using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace VoiceEngine
{
    /// <summary>
    /// Export pipeline ported 1:1 from worker/cli.py (export + helpers):
    /// per-row SILK copy, isolated decode, optional cached ASR naming, CSV /
    /// NDJSON journal, manifest or partial manifest on cancellation. WAV files
    /// are byte-compared against the Python reference in tests/ExportCheck.cs.
    /// </summary>
    internal static class Export
    {
        internal sealed class CancelledException : Exception { }

        private static readonly string[] Fields = { "id", "chat", "label", "sender", "stamp", "local_time", "local_id", "server_id", "silk", "wav", "duration", "sha256", "text", "asr_status", "status" };

        internal static Tuple<string, string, string> SessionPaths(string workspace, object session)
        {
            if (!(session is string) || !Regex.IsMatch((string)session, "^[0-9a-f]{32}$"))
                throw new InvalidDataException("读取会话无效，请重新读取目录");
            string folder = Path.Combine(workspace, "sessions", (string)session);
            return Tuple.Create(folder, Path.Combine(folder, "snapshots"), Path.Combine(folder, "index.sqlite"));
        }

        internal static void EnsureOutside(string output, string source)
        {
            string outFull = Path.GetFullPath(output).TrimEnd('\\', '/');
            string sourceFull = Path.GetFullPath(source).TrimEnd('\\', '/');
            if (outFull.Equals(sourceFull, StringComparison.OrdinalIgnoreCase) ||
                outFull.StartsWith(sourceFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                outFull.StartsWith(sourceFull + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("工作/输出目录不能放在微信源账号目录内");
        }

        internal static void WriteJson(string path, object value)
        {
            string full = Path.GetFullPath(path);
            string temporary = full + ".json.part";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
                if (File.Exists(full)) File.Delete(full);
                File.Move(temporary, full);
            }
            finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch { } }
        }

        internal static Dictionary<string, object> Run(string workspace, Dictionary<string, object> request, Action check, Action<Dictionary<string, object>> progress)
        {
            var paths = SessionPaths(workspace, request.ContainsKey("session") ? request["session"] : null);
            string folder = paths.Item1, snapshots = paths.Item2, index = paths.Item3;
            var serializer = new JavaScriptSerializer();
            var state = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(folder, "session.json"), Encoding.UTF8));
            long total = Library.Counting(index, request);
            if (check != null) check();
            string output = Path.GetFullPath(Convert.ToString(request["output"]));
            EnsureOutside(output, Convert.ToString(state["source"]));
            Directory.CreateDirectory(output);
            string destination = Path.Combine(output, "微信语音-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(destination);
            int completed = 0, failed = 0, recognized = 0, recognitionFailed = 0;
            bool cancelled = false;
            bool useAsr = request.ContainsKey("transcribe") && Convert.ToBoolean(request["transcribe"]);
            string baseDir = Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'));
            var started = DateTime.UtcNow;
            Action<string> report = phase =>
            {
                double elapsed = Math.Max(0, (DateTime.UtcNow - started).TotalSeconds);
                int processed = completed + failed;
                var value = new Dictionary<string, object>
                {
                    ["type"] = "progress", ["phase"] = phase, ["completed"] = completed, ["failed"] = failed,
                    ["processed"] = processed, ["total"] = total, ["recognized"] = recognized,
                    ["recognition_failed"] = recognitionFailed,
                    ["elapsed_seconds"] = PyFloat(Math.Round(elapsed, 1)),
                    ["remaining_seconds"] = processed > 0 ? PyFloat(Math.Round(elapsed / processed * (total - processed), 1)) : null,
                    ["output"] = destination
                };
                if (progress != null) progress(value);
            };
            try
            {
                report("export");
                using (var recognizer = useAsr ? new LocalRecognizer(Path.Combine(baseDir, "asr"), check) : null)
                using (var journal = new StreamWriter(Path.Combine(destination, "progress.ndjson"), false, new UTF8Encoding(false)))
                using (var csv = new StreamWriter(Path.Combine(destination, "语音清单.csv"), false, new UTF8Encoding(true)))
                {
                    WriteCsvHeader(csv);
                    foreach (var row in Library.Records(index, request, null))
                    {
                        if (check != null) check();
                        var result = new Dictionary<string, object>();
                        foreach (string field in Fields) if (row.ContainsKey(field)) result[field] = row[field];
                        try { result["local_time"] = LocalTime(row); }
                        catch { result["local_time"] = "未知"; }
                        string wavPath = null;
                        try
                        {
                            byte[] data = Library.Payload(snapshots, row);
                            string name = Library.Filename(row);
                            string rawPath = Path.Combine(destination, name + ".silk");
                            wavPath = Path.Combine(destination, name + ".wav");
                            using (var raw = new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) raw.Write(data, 0, data.Length);
                            result["silk"] = name + ".silk";
                            result["sha256"] = Library.Sha256Bytes(data);
                            result["duration"] = PyFloat(VoiceCodec.Isolated(rawPath, wavPath, check));
                            string text = "";
                            result["asr_status"] = "未启用";
                            if (useAsr)
                            {
                                report("recognize");
                                try
                                {
                                    Dictionary<string, object> cached = null;
                                    try { cached = SpeechCache.Lookup(index, Convert.ToString(row["id"]), Convert.ToString(result["sha256"])); }
                                    catch (Exception) { }
                                    text = cached != null ? Convert.ToString(cached["text"]) : recognizer.Recognize(wavPath);
                                    if (cached == null)
                                    {
                                        try { SpeechCache.Save(index, Convert.ToString(row["id"]), Convert.ToString(result["sha256"]), text != "" ? "READY" : "EMPTY", text); }
                                        catch (Exception) { }
                                    }
                                    if (text != "") { recognized++; result["asr_status"] = "已识别"; }
                                    else { recognitionFailed++; result["asr_status"] = "未识别到文字"; }
                                }
                                catch (CancelledException) { throw; }
                                catch (Exception exc)
                                {
                                    recognitionFailed++;
                                    result["asr_status"] = exc.Message.Length > 200 ? exc.Message.Substring(0, 200) : exc.Message;
                                }
                            }
                            result["text"] = text;
                            string renamed = Path.Combine(destination, Library.WavFilename(row, text));
                            if (File.Exists(renamed)) throw new InvalidDataException("已有WAV不覆盖");
                            File.Move(wavPath, renamed);
                            if (!(!request.ContainsKey("keep_silk") || Convert.ToBoolean(request["keep_silk"])))
                            {
                                File.Delete(rawPath);
                                result["silk"] = "";
                            }
                            result["wav"] = Path.GetFileName(renamed);
                            result["status"] = "已导出WAV";
                            completed++;
                        }
                        catch (CancelledException)
                        {
                            // The current decoded WAV may exist even though this row did not finish.
                            // Record it in the partial journal without reporting it as completed.
                            result["status"] = "已取消；本条保留已生成文件";
                            result["asr_status"] = useAsr ? "已取消" : "未启用";
                            if (result.ContainsKey("duration") && wavPath != null && File.Exists(wavPath)) result["wav"] = Path.GetFileName(wavPath);
                            WriteCsvRow(csv, result);
                            journal.WriteLine(serializer.Serialize(result));
                            journal.Flush(); csv.Flush();
                            throw;
                        }
                        catch (Exception exc)
                        {
                            failed++;
                            result["status"] = exc.Message.Length > 300 ? exc.Message.Substring(0, 300) : exc.Message;
                        }
                        WriteCsvRow(csv, result);
                        journal.WriteLine(serializer.Serialize(result));
                        journal.Flush(); csv.Flush();
                        report("export");
                    }
                }
            }
            catch (CancelledException) { cancelled = true; }
            var final = new Dictionary<string, object>
            {
                ["completed"] = completed, ["failed"] = failed, ["cancelled"] = cancelled, ["output"] = destination,
                ["total"] = total, ["processed"] = completed + failed, ["remaining"] = total - completed - failed,
                ["elapsed_seconds"] = PyFloat(Math.Round((DateTime.UtcNow - started).TotalSeconds, 1)),
                ["recognized"] = recognized, ["recognition_failed"] = recognitionFailed,
                ["naming"] = "时间_唯一编号_识别文字摘要.wav",
                ["source_issues"] = state.ContainsKey("issues") ? state["issues"] : new object[0],
                ["note"] = "本地缓存可能不含全部历史语音；未知发送者不做猜测。"
            };
            WriteJson(Path.Combine(destination, cancelled ? "partial.json" : "manifest.json"), final);
            return final;
        }

        private static void WriteCsvHeader(StreamWriter csv)
        {
            var quoted = new List<string>();
            foreach (string field in Fields) quoted.Add(CsvQuote(field));
            csv.Write(string.Join(",", quoted));
            csv.Write("\r\n");
        }

        private static void WriteCsvRow(StreamWriter csv, Dictionary<string, object> result)
        {
            var quoted = new List<string>();
            foreach (string field in Fields)
                quoted.Add(CsvQuote(FormulaSafe(result.ContainsKey(field) ? result[field] : "")));
            csv.Write(string.Join(",", quoted));
            csv.Write("\r\n");
        }

        private static object FormulaSafe(object value)
        {
            if (value is string)
            {
                string text = (string)value;
                if (text.Length > 0 && (text[0] == '=' || text[0] == '+' || text[0] == '-' || text[0] == '@' || text[0] == '\t' || text[0] == '\r'))
                    return "'" + text;
            }
            return value;
        }

        private static string CsvQuote(object value)
        {
            string text = value is double || value is float ? PyFloat(Convert.ToDouble(value)) : Convert.ToString(value);
            if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            return text;
        }

        private static string LocalTime(Dictionary<string, object> row)
        {
            try
            {
                var offset = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(row["stamp"]));
                return offset.LocalDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss");
            }
            catch { return "未知"; }
        }

        /// <summary>Python str(float) formatting: shortest round-trip, integral values keep .0.</summary>
        internal static string PyFloat(double value)
        {
            string text = value.ToString("R", CultureInfo.InvariantCulture);
            if (!text.Contains(".") && !text.Contains("e") && !text.Contains("E")) text += ".0";
            return text;
        }
    }
}
