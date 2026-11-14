using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

internal static class FailureDiagnosticsCheck
{
    private static readonly BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
    private static object Call(MethodInfo method, params object[] args)
    {
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            string fixtures = Path.GetFullPath(args[1]), root = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(root);
            var diagnostics = assembly.GetType("VoiceEngine.WorkerDiagnostics");
            var errors = assembly.GetType("VoiceEngine.EngineErrors");
            Call(diagnostics.GetMethod("Start", Static), root, Guid.NewGuid().ToString("N"));
            var cause = (Exception)Call(errors.GetMethod("Create", Static), "PAGE_AUTH_FAILED", "PRIVATE_MESSAGE", null, 123, -1);
            cause.Data["account"] = "PRIVATE_ACCOUNT";
            var wrapper = new InvalidDataException("PRIVATE_PATH", cause);
            wrapper.Data["error_code"] = "PRIVATE_UNRECOGNIZED_CODE";
            Call(diagnostics.GetMethod("Failure", Static), wrapper, "snapshot", null);
            Require((string)Call(errors.GetMethod("Code", Static), wrapper) == "PAGE_AUTH_FAILED", "inner code lost");
            string source = Path.Combine(root, "source", "db_storage"), workspace = Path.Combine(root, "work");
            Directory.CreateDirectory(source);
            File.Copy(Path.Combine(fixtures, "encrypted-late-tamper", "source.db"), Path.Combine(source, "media_0.db"));
            var program = assembly.GetType("VoiceEngine.Program");
            program.GetField("progress", Static).SetValue(null, new Action<Dictionary<string, object>>(value => { }));
            string hash = (string)Call(program.GetMethod("Hash", Static), source);
            var keys = new Dictionary<string, string> { ["media_0.db"] = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f" };
            Call(assembly.GetType("VoiceEngine.Capture").GetMethod("SaveVault", Static), Path.Combine(workspace, "keys", hash + ".dpapi"), source, keys);
            bool rejected = false;
            try
            {
                Call(program.GetMethod("Execute", Static), new Dictionary<string, object> { ["action"] = "catalog", ["source"] = source, ["consent"] = true }, workspace, new JavaScriptSerializer());
            }
            catch (InvalidDataException error)
            {
                rejected = error.Message.Contains("语音库副本创建失败") && !error.Message.Contains("未找到支持");
                Require((string)Call(errors.GetMethod("Code", Static), error) == "PAGE_AUTH_FAILED", "catalogue masked underlying page failure");
                Call(diagnostics.GetMethod("Failure", Static), error, "catalog", null);
            }
            Require(rejected, "missing media snapshot was reported as a table/schema problem");
            string logs = "";
            foreach (string file in Directory.GetFiles(Path.Combine(root, "diagnostics"))) logs += File.ReadAllText(file);
            Require(!logs.Contains("PRIVATE_") && !logs.Contains(keys["media_0.db"]), "diagnostics leaked private data");
            Require(logs.Contains("PAGE_AUTH_FAILED") && logs.Contains("MEDIA_SNAPSHOT_MISSING") && logs.Contains("page_number") && logs.Contains("inner_depth") && logs.Contains("snapshot_attempt"), "diagnostics lack precise snapshot evidence");
            Console.WriteLine("PASS: nested failure codes/page metadata retained, messages/paths/keys excluded, absent media snapshot is not a schema error.");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL: " + error); return 1; }
    }
}
