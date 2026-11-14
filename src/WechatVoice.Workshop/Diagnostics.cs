using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
namespace WechatVoice.Workshop
{
    internal static class Diagnostics
    {
        internal const string Version="1.0.0-preview18";
        private static readonly object Gate=new object();
        private static readonly HashSet<string> Fields=new HashSet<string>(("utc request_id event physical_pages logical_pages declared_pages free_leaf_pages wal_committed_pages attempt inner_depth page_number sqlite_code sqlite_version media_sources schema_flags valid_media snapshot_stage bytes_current bytes_total speed_mb_s error_code selected_pid read_calls read_failures partial_reads last_read_error region_count action phase count total current success matched encrypted scanned_mb candidates config_names config_objects master_checks limit_reached completed failed processed cached recognized recognition_failed cancelled version databases expected_databases issue_count selected_count transcribe keep_silk has_chat has_dates elapsed_ms exception_type component errno winerror hresult stage database_kind module function line exit_code os_version clr_version process64 cpu_count engine_present asr_present model_present tokens_present ort_present dml_present capi_present").Split(' '));
        internal static Dictionary<string,object> Clean(Dictionary<string,object> values)
        {
            var result=new Dictionary<string,object>();
            foreach(var pair in values)
            {
                if(!Fields.Contains(pair.Key) || pair.Value==null)continue;
                if(pair.Value is bool || pair.Value is int || pair.Value is long || pair.Value is double || pair.Value is decimal)result[pair.Key]=pair.Value;
                else if(pair.Value is string && Regex.IsMatch((string)pair.Value,@"\A[A-Za-z0-9_. :+\-TZ]{1,90}\z"))result[pair.Key]=pair.Value;
            }
            return result;
        }
        internal static void Write(string workspace,string name,string requestId=null,Dictionary<string,object> values=null)
        {
            if(string.IsNullOrEmpty(workspace))return;
            lock(Gate)try
            {
                string directory=Path.Combine(workspace,"diagnostics");Directory.CreateDirectory(directory);string path=Path.Combine(directory,"ui.jsonl");
                if(File.Exists(path) && new FileInfo(path).Length>=256*1024)
                {
                    for(int i=2;i>=0;i--){string from=i==0?path:path+"."+i,to=path+"."+(i+1);if(File.Exists(from)){if(File.Exists(to))File.Delete(to);File.Move(from,to);}}
                }
                var record=Clean(values??new Dictionary<string,object>());record["utc"]=DateTime.UtcNow.ToString("o");record["event"]=name;
                if(requestId!=null && Regex.IsMatch(requestId,@"\A[0-9a-f]{32}\z"))record["request_id"]=requestId;
                File.AppendAllText(path,new JavaScriptSerializer().Serialize(record)+Environment.NewLine,new UTF8Encoding(false));
            }catch{ }
        }
        internal static Dictionary<string,object> EnvironmentInfo()
        {
            string app=AppDomain.CurrentDomain.BaseDirectory;
            return new Dictionary<string,object>{{"version",Version},{"os_version",Environment.OSVersion.Version.ToString()},{"clr_version",Environment.Version.ToString()},{"process64",Environment.Is64BitProcess},{"cpu_count",Environment.ProcessorCount},{"engine_present",File.Exists(Path.Combine(app,"engine","VoiceWorker.exe"))},{"asr_present",File.Exists(Path.Combine(app,"asr","AsrWorker.exe"))},{"model_present",File.Exists(Path.Combine(app,"asr","models","sensevoice","model.int8.onnx"))},{"tokens_present",File.Exists(Path.Combine(app,"asr","models","sensevoice","tokens.txt"))},{"ort_present",File.Exists(Path.Combine(app,"asr","onnxruntime.dll"))},{"dml_present",File.Exists(Path.Combine(app,"asr","DirectML.dll"))},{"capi_present",File.Exists(Path.Combine(app,"asr","sherpa-onnx-c-api.dll"))}};
        }
        internal static void ExportZip(string workspace,string target)
        {
            string temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var zip=ZipFile.Open(temporary,ZipArchiveMode.Create))
                {
                    Action<string,string> add=(name,text)=>{var entry=zip.CreateEntry(name,CompressionLevel.Optimal);using(var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false)))writer.Write(text);};
                    add("environment.json",new JavaScriptSerializer().Serialize(EnvironmentInfo()));
                    add("说明.txt","仅含运行环境、请求编号、阶段、计数、异常类型与代码位置。未包含聊天、账号目录、密钥、录音、识别全文、旧日志或数据库。请补充问题发生时间与操作步骤。\r\n");
                    string directory=Path.Combine(workspace,"diagnostics");
                    if(Directory.Exists(directory))foreach(string file in Directory.GetFiles(directory).Where(f=>Regex.IsMatch(Path.GetFileName(f),@"\A(?:ui\.jsonl(?:\.[1-3])?|worker-[0-9a-f]{32}\.jsonl(?:\.1)?)\z")).OrderByDescending(File.GetLastWriteTimeUtc).Take(40))
                    {
                        var text=new StringBuilder();
                        // Bound each snapshot, allow an active worker to keep writing.
                        using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                        {
                            byte[] buffer=new byte[512*1024];int length=0,read;
                            while(length<buffer.Length && (read=stream.Read(buffer,length,buffer.Length-length))>0)length+=read;
                            foreach(string line in Encoding.UTF8.GetString(buffer,0,length).Split('\n'))
                            {
                                if(line.Length>8192)continue;
                                try{var record=new JavaScriptSerializer{MaxJsonLength=8192}.Deserialize<Dictionary<string,object>>(line);text.AppendLine(new JavaScriptSerializer().Serialize(Clean(record)));}catch{ }
                            }
                        }
                        add("logs/"+Path.GetFileName(file),text.ToString());
                    }
                }
                if(File.Exists(target))File.Replace(temporary,target,null);else File.Move(temporary,target);
            }
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
}
