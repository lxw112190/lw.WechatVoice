using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using WechatVoice.Workshop;
internal static class IpcCheck
{
    private static int Main(string[] args)
    {
        try
        {
            Console.InputEncoding = System.Text.Encoding.UTF8; // CI-style BOM on redirected child stdin
            var result = WorkerClient.RunAsync(args[0], new Dictionary<string, object> { ["action"]="catalog", ["source"]=args[1], ["consent"]=true, ["workspace"]=args[2] }, p => Console.WriteLine(WorkerClient.Get(p,"phase"))).GetAwaiter().GetResult();
            foreach(var item in result) Console.WriteLine(item.Key+"="+item.Value+" ("+(item.Value==null?"null":item.Value.GetType().FullName)+")");
            var listing=WorkerClient.RunAsync(args[0], new Dictionary<string, object> { ["action"]="list",["session"]=result["session"],["workspace"]=args[2] },p=>{}).GetAwaiter().GetResult();
            int expected=args.Length>3?int.Parse(args[3]):3,contacts=args.Length>4?int.Parse(args[4]):1;
            if (WorkerClient.Array(listing,"items").Length!=expected || WorkerClient.Array(listing,"contacts").Length!=contacts) throw new Exception("NDJSON array round trip failed");
            var count=WorkerClient.RunAsync(args[0],new Dictionary<string,object>{["action"]="count",["session"]=result["session"],["workspace"]=args[2],["chat"]="chat"},p=>{}).GetAwaiter().GetResult();
            if(Convert.ToInt32(count["count"])!=3)throw new Exception("Scoped count IPC failed");
            string cancel=Path.Combine(Path.GetFullPath(args[2]),"cancel-ipc-"+Guid.NewGuid().ToString("N"));File.WriteAllText(cancel,"cancel");bool cancelled=false;
            try{WorkerClient.RunAsync(args[0],new Dictionary<string,object>{["action"]="catalog",["source"]=args[1],["workspace"]=args[2],["consent"]=true,["cancel"]=cancel},p=>{}).GetAwaiter().GetResult();}
            catch(OperationCanceledException){cancelled=true;}
            finally{File.Delete(cancel);}
            if(!cancelled)throw new Exception("Cancellation incorrectly reported as generic failure");
            Diagnostics.Write(args[2],"key_scan_result",null,new Dictionary<string,object>{{"error_code","KEY_NOT_FOUND"},{"selected_pid",12345},{"read_calls",100},{"read_failures",2},{"last_read_error",299}});
            Diagnostics.Write(args[2],"progress",null,new Dictionary<string,object>{{"phase","snapshot_detail"},{"snapshot_stage","copy_main"},{"bytes_current",1048576L},{"bytes_total",33554432L},{"speed_mb_s",123.5},{"source","PRIVATE_ACCOUNT"}});
            Diagnostics.Write(args[2],"failure",null,new Dictionary<string,object>{{"error_code","PAGE_AUTH_FAILED"},{"page_number",123},{"inner_depth",1},{"sqlite_code",11},{"sqlite_version",3040001}});
            Diagnostics.Write(args[2],"snapshot_layout",null,new Dictionary<string,object>{{"physical_pages",10000L},{"logical_pages",9000L},{"free_leaf_pages",100},{"wal_committed_pages",9000}});
            Diagnostics.Write(args[2],"favorite_summary",null,new Dictionary<string,object>{{"favorites",true},{"favorite_missing",3},{"favorite_cache_scanned",42},{"favorite_cache_hits",2},{"favorite_server_hits",1},{"error_code","FAVORITE_AUDIO_MISSING"},{"source","PRIVATE_FAVORITE_PATH"},{"md5","PRIVATE_FAVORITE_HASH"}});
            bool rejected=false;
            try{WorkerClient.RunAsync(args[0],new Dictionary<string,object>{["action"]="invalid-operation",["workspace"]=args[2],["source"]="PRIVATE_ACCOUNT",["text"]="PRIVATE_TRANSCRIPT",["key"]="PRIVATE_KEY"},p=>{}).GetAwaiter().GetResult();}
            catch(InvalidOperationException ex){rejected=Convert.ToString(ex.Data["request_id"]).Length==32;}
            if(!rejected)throw new Exception("Failure has no correlation ID");
            File.WriteAllText(Path.Combine(args[2],"diagnostic.log"),"PRIVATE_LEGACY_LOG");
            File.WriteAllText(Path.Combine(args[2],"diagnostics","worker-"+new string('f',32)+".jsonl"),"{\"event\":\"failure\",\"error\":\"PRIVATE_MESSAGE\",\"text\":\"PRIVATE_TRANSCRIPT\",\"winerror\":1114}",System.Text.Encoding.UTF8);
            string archive=Path.Combine(args[2],"diagnostic-export.zip");Diagnostics.ExportZip(args[2],archive);
            using(var zip=ZipFile.OpenRead(archive))
            {
                string logs=string.Join("\n",zip.Entries.Select(entry=>{using(var reader=new StreamReader(entry.Open()))return reader.ReadToEnd();}));
                if(logs.Contains("PRIVATE_") || !logs.Contains("1114") || !logs.Contains("failure") || !logs.Contains("request_id"))throw new Exception("Diagnostics leak private fields or lack failure evidence");
                if(!logs.Contains("KEY_NOT_FOUND")||!logs.Contains("last_read_error")||!logs.Contains("299"))throw new Exception("Diagnostic export lost final key scan/error metadata");
                if(!logs.Contains("snapshot_stage") || !logs.Contains("bytes_total") || !logs.Contains("speed_mb_s"))throw new Exception("Diagnostic export lost snapshot timing/progress metadata");
                if(!logs.Contains("PAGE_AUTH_FAILED") || !logs.Contains("page_number") || !logs.Contains("sqlite_version"))throw new Exception("Diagnostic export lost precise failure metadata");
                if(!logs.Contains("logical_pages") || !logs.Contains("free_leaf_pages"))throw new Exception("Diagnostic export lost page-layout metadata");
                if(!logs.Contains("favorite_missing") || !logs.Contains("favorite_cache_scanned") || !logs.Contains("FAVORITE_AUDIO_MISSING"))throw new Exception("Diagnostic export lost favorite matching evidence");
                if(zip.Entries.Any(entry=>entry.FullName.Contains("workspace")||entry.Name=="diagnostic.log"))throw new Exception("Diagnostics copied private workspace");
            }
            Console.WriteLine("PASS: correlated packaged worker failure and allowlisted diagnostic ZIP without account/transcript/legacy log.");
            Console.WriteLine("PASS: UTF-8 process IPC, list arrays, scoped count, cancellation exception mapping");
            return 0;
        }
        catch(Exception error) {Console.WriteLine(error);return 1;}
    }
}
