using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
namespace WechatVoice.Workshop
{
    internal static class WorkerClient
    {
        internal static async Task<Dictionary<string, object>> RunAsync(string path, Dictionary<string, object> request, Action<Dictionary<string, object>> progress)
        {
            request=new Dictionary<string,object>(request);
            string requestId=Guid.NewGuid().ToString("N"),workspace=Get(request,"workspace");request["request_id"]=requestId;
            var timer=Stopwatch.StartNew();
            Diagnostics.Write(workspace,"begin",requestId,new Dictionary<string,object>{{"action",Get(request,"action")}});
            try{
            return await Task.Run(() =>
            {
                if (!File.Exists(FileSystemPath.ForIo(path))) throw new FileNotFoundException("缺少导出引擎，请完整解压部署包。", path);
                var json = new JavaScriptSerializer { MaxJsonLength = 1048576 };
                var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(path) };
                using (var process = Process.Start(start))
                {
                    var stderr = process.StandardError.ReadToEndAsync();
                    var input = new UTF8Encoding(false).GetBytes(json.Serialize(request) + "\n");
                    process.StandardInput.BaseStream.Write(input, 0, input.Length); process.StandardInput.BaseStream.Flush(); process.StandardInput.Close();
                    Dictionary<string, object> final = null;
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        if (line.Length > 1048576) throw new InvalidDataException("引擎响应过大。");
                        var message = json.Deserialize<Dictionary<string, object>>(line);
                        if (Get(message, "type") == "progress") progress(message); else final = message;
                    }
                    process.WaitForExit(); stderr.GetAwaiter().GetResult();
                    Diagnostics.Write(workspace,"worker_exit",requestId,new Dictionary<string,object>{{"exit_code",process.ExitCode},{"elapsed_ms",timer.ElapsedMilliseconds}});
                    if (final == null) throw new InvalidOperationException("导出引擎提前结束，退出码 " + process.ExitCode + "。请确认完整解压，勿混用其他版本文件。");
                    if (final.ContainsKey("cancelled") && Convert.ToBoolean(final["cancelled"])) throw new OperationCanceledException(Get(final, "error"));
                    if (!final.ContainsKey("ok") || !Convert.ToBoolean(final["ok"])) throw new InvalidOperationException(Get(final, "error"));
                    return (Dictionary<string, object>)final["data"];
                }
            });
            }
            catch(Exception ex){ex.Data["request_id"]=requestId;Diagnostics.Write(workspace,"failure",requestId,new Dictionary<string,object>{{"exception_type",ex.GetType().Name},{"elapsed_ms",timer.ElapsedMilliseconds}});throw;}
        }
        internal static string Get(Dictionary<string, object> value, string key) { return value.ContainsKey(key) ? Convert.ToString(value[key]) : ""; }
        internal static object[] Array(Dictionary<string, object> value, string key)
        {
            var items = value.ContainsKey(key) ? value[key] as System.Collections.IEnumerable : null;
            return items == null || items is string ? new object[0] : items.Cast<object>().ToArray();
        }
    }
}
