using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace VoiceEngine
{
    /// <summary>
    /// One cancellable native ASR process per export, ported 1:1 from
    /// worker/transcriber.py. The recognizer binary is the existing AsrWorker
    /// (C#/.NET, P/Invoke sherpa-onnx) — the Python layer only drove the
    /// subprocess, so the port keeps the same bounded UTF-8 line protocol.
    /// </summary>
    internal sealed class LocalRecognizer : IDisposable
    {
        private readonly string root;
        private readonly Action check;
        private Process process;
        private readonly int timeoutMilliseconds;
        private BlockingCollection<byte[]> responses;
        private Thread outputReader, errorReader;

        internal LocalRecognizer(string root, Action check) : this(root, check, 90000) { }
        internal LocalRecognizer(string root, Action check, int timeoutMilliseconds)
        {
            this.root = root; this.check = check;
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            this.timeoutMilliseconds = timeoutMilliseconds;
        }

        private void Start()
        {
            string executable = Path.Combine(root, "AsrWorker.exe");
            if (!File.Exists(executable)) throw new InvalidDataException("缺少本地识别组件，WAV仍正常导出");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = root
            };
            process = Process.Start(start);
            // Each process owns its queue; no late response from a failed generation
            // can become the next voice's transcript.
            var queue = new BlockingCollection<byte[]>(2);
            responses = queue;
            var output = process.StandardOutput.BaseStream;
            var errors = process.StandardError.BaseStream;
            outputReader = new Thread(() =>
            {
                try
                {
                    using (var pending = new MemoryStream())
                    {
                        int value;
                        while ((value = output.ReadByte()) >= 0)
                        {
                            pending.WriteByte((byte)value);
                            if (pending.Length > 65537) return;
                            if (value != '\n') continue;
                            byte[] line = pending.ToArray();
                            pending.SetLength(0);
                            if (!queue.TryAdd(line)) return;
                        }
                    }
                }
                catch { }
                finally { queue.CompleteAdding(); }
            });
            outputReader.IsBackground = true;
            outputReader.Start();
            // Native warnings must not fill the redirected pipe and block ASR.
            // Drain without persisting potentially private free-form output.
            errorReader = new Thread(() =>
            {
                try { var buffer = new byte[4096]; while (errors.Read(buffer, 0, buffer.Length) > 0) { } }
                catch { }
            });
            errorReader.IsBackground = true;
            errorReader.Start();
        }

        internal string Recognize(string wav)
        {
            try { return RecognizeCore(wav); }
            catch { Close(true); throw; }
        }

        private string RecognizeCore(string wav)
        {
            if (check != null) check();
            if (process == null) Start();
            var serializer = new JavaScriptSerializer();
            var line = Encoding.UTF8.GetBytes(serializer.Serialize(new Dictionary<string, object> { ["wav"] = Path.GetFullPath(wav) }) + "\n");
            try
            {
                process.StandardInput.BaseStream.Write(line, 0, line.Length);
                process.StandardInput.BaseStream.Flush();
            }
            catch (Exception) { throw new InvalidDataException("本地识别组件提前退出，WAV已保留"); }
            var timer = Stopwatch.StartNew();
            byte[] response = null;
            while (true)
            {
                if (check != null) check();
                if (timer.ElapsedMilliseconds >= timeoutMilliseconds) throw new InvalidDataException("本地识别超时，WAV已保留");
                if (responses.TryTake(out response, 100)) break;
                if (responses.IsCompleted) throw new InvalidDataException("本地识别组件提前退出或响应过大，WAV已保留");
            }
            if (response == null || response.Length == 0)
            {
                try { process.Kill(); } catch { }
                throw new InvalidDataException("本地识别组件提前退出，WAV已保留");
            }
            string text = Encoding.UTF8.GetString(response).Trim('\r', '\n');
            if (text.Length > 0 && text[0] == 0xFEFF) text = text.Substring(1);
            var result = serializer.Deserialize<Dictionary<string, object>>(text);
            if (result == null || !Convert.ToBoolean(result["ok"]))
            {
                var message = new Dictionary<string, object>();
                if (result != null)
                    foreach (string key in new[] { "exception_type", "component", "hresult", "winerror" })
                        if (result.ContainsKey(key)) message[key] = result[key];
                WorkerDiagnostics.Event("asr_error", message);
                throw new InvalidDataException(result != null && result.ContainsKey("error") ? Convert.ToString(result["error"]) : "识别失败");
            }
            string recognized = result.ContainsKey("text") ? Convert.ToString(result["text"]) : "";
            if (recognized == null || recognized.Length > 2000) throw new InvalidDataException("识别文字格式无效");
            return recognized.Trim();
        }

        internal void Close() { Close(false); }

        private void Close(bool abort)
        {
            if (process == null) return;
            var closing = process;
            process = null;
            try
            {
                if (abort && !closing.HasExited) closing.Kill();
                try { closing.StandardInput.Close(); } catch { }
                if (!closing.WaitForExit(2000)) { closing.Kill(); closing.WaitForExit(); }
            }
            finally
            {
                try { closing.StandardOutput.Close(); } catch { }
                try { closing.StandardError.Close(); } catch { }
                if (outputReader != null) outputReader.Join();
                if (errorReader != null) errorReader.Join();
                if (responses != null) responses.Dispose();
                responses = null;
                outputReader = null; errorReader = null;
                closing.Dispose();
            }
        }

        public void Dispose() { Close(); }
    }
}
