using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
internal static class ExportCheck
{
    // Compares the C# export pipeline (src/VoiceEngine/Export.cs) against the
    // Python reference on the synthetic session built by tests/export_fixtures.py:
    // manifest fields, CSV bytes, WAV/SILK hashes, cancellation partial path and
    // (when ASR assets are staged) one real-model recognition export.
    private static MethodInfo run;
    private static Type cancelledType;
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static object Invoke(MethodInfo method, params object[] args)
    {
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException exc) { throw exc.InnerException; }
    }
    private static Dictionary<string, object> Parse(string json) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); }
    private static string Sha256(string path)
    {
        using (var sha = SHA256.Create())
        {
            var bytes = sha.ComputeHash(File.ReadAllBytes(path));
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int high = bytes[i] >> 4, low = bytes[i] & 15;
                chars[i * 2] = (char)(high < 10 ? '0' + high : 'a' + high - 10);
                chars[i * 2 + 1] = (char)(low < 10 ? '0' + low : 'a' + low - 10);
            }
            return new string(chars);
        }
    }
    private static Dictionary<string, string> Digests(string folder)
    {
        var result = new Dictionary<string, string>();
        foreach (string path in Directory.GetFiles(folder))
        {
            string name = Path.GetFileName(path);
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".wav" || extension == ".silk") result[name] = Sha256(path);
        }
        return result;
    }
    private static Dictionary<string, object> Fields(Dictionary<string, object> final)
    {
        var result = new Dictionary<string, object>();
        foreach (string key in new[] { "completed", "failed", "cancelled", "total", "processed", "remaining",
            "recognized", "recognition_failed", "naming", "note", "source_issues" })
            result[key] = final[key];
        return result;
    }
    private static void CompareFields(Dictionary<string, object> want, Dictionary<string, object> got, string context)
    {
        var wantJson = new JavaScriptSerializer().Serialize(want);
        var gotJson = new JavaScriptSerializer().Serialize(got);
        if (wantJson != gotJson)
            throw new Exception(context + " fields differ:\n  want " + wantJson + "\n  got  " + gotJson);
    }
    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    private static string Destination(string outputRoot)
    {
        string[] dirs = Directory.GetDirectories(outputRoot, "微信语音-*");
        if (dirs.Length != 1) throw new Exception("expected exactly one destination under " + outputRoot + ", got " + dirs.Length);
        return dirs[0];
    }

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.WriteLine("usage: ExportCheck.exe <VoiceEngine.exe> <export-fixtures-dir>"); return 1; }
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var type = assembly.GetType("VoiceEngine.Export", true);
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        run = type.GetMethod("Run", flags);
        cancelledType = type.GetNestedType("CancelledException", BindingFlags.NonPublic);
        string root = Path.GetFullPath(args[1]);
        string workspace = root;
        try
        {
            var expected = Parse(File.ReadAllText(Path.Combine(root, "expected.json")));
            string session = Convert.ToString(expected["session"]);
            var wantExport = (Dictionary<string, object>)expected["export"];

            // plain export
            string csOut = Path.Combine(root, "cs-out");
            Directory.CreateDirectory(csOut);
            var request = new Dictionary<string, object> {
                ["session"] = session, ["output"] = csOut, ["keep_silk"] = true, ["transcribe"] = false, ["chat"] = "chat" };
            var final = (Dictionary<string, object>)Invoke(run, workspace, request, new Action(() => { }), null);
            CompareFields((Dictionary<string, object>)wantExport["fields"], Fields(final), "export");
            string destination = Convert.ToString(final["output"]);
            var wantFiles = (Dictionary<string, object>)wantExport["files"];
            var gotFiles = Digests(destination);
            Require(wantFiles.Count == gotFiles.Count, "export file count differs: " + gotFiles.Count);
            foreach (var entry in wantFiles)
                Require(gotFiles.ContainsKey(entry.Key) && gotFiles[entry.Key] == Convert.ToString(entry.Value), "export file hash differs: " + entry.Key);
            // CSV compared as raw bytes: JavaScriptSerializer normalizes line
            // endings, so the reference is read from the Python output directory.
            string pyDestination = Destination(Path.Combine(root, "py-out"));
            byte[] wantCsv = File.ReadAllBytes(Path.Combine(pyDestination, "语音清单.csv"));
            byte[] gotCsv = File.ReadAllBytes(Path.Combine(destination, "语音清单.csv"));
            Require(BytesEqual(wantCsv, gotCsv), "export CSV bytes differ (want " + wantCsv.Length + ", got " + gotCsv.Length + ")");
            Require(File.ReadAllLines(Path.Combine(destination, "progress.ndjson")).Length == Convert.ToInt32(wantExport["journal_lines"]), "journal line count differs");
            Require(Directory.GetFiles(destination, "*.wav").Select(Path.GetFileName).OrderBy(n => n).SequenceEqual(
                ((System.Collections.IEnumerable)wantExport["wav_names"]).Cast<object>().Select(Convert.ToString)), "wav name set differs");
            Console.WriteLine("PASS: export manifest, CSV bytes, WAV/SILK hashes, journal and names match.");

            // cancellation once the destination exists
            string csCancel = Path.Combine(root, "cs-cancel");
            Directory.CreateDirectory(csCancel);
            var cancelRequest = new Dictionary<string, object>(request) { ["output"] = csCancel };
            Action cancelCheck = () =>
            {
                if (Directory.GetDirectories(csCancel, "微信语音-*").Length > 0)
                    throw (Exception)Activator.CreateInstance(cancelledType);
            };
            var cancelledFinal = (Dictionary<string, object>)Invoke(run, workspace, cancelRequest, cancelCheck, null);
            var wantCancel = (Dictionary<string, object>)expected["cancel"];
            CompareFields((Dictionary<string, object>)wantCancel["fields"], Fields(cancelledFinal), "cancel");
            string cancelDestination = Convert.ToString(cancelledFinal["output"]);
            Require(File.Exists(Path.Combine(cancelDestination, "partial.json")) == Convert.ToBoolean(wantCancel["has_partial"]), "cancel partial.json presence differs");
            Require(File.Exists(Path.Combine(cancelDestination, "manifest.json")) == Convert.ToBoolean(wantCancel["has_manifest"]), "cancel manifest presence differs");
            Require(Directory.GetFiles(cancelDestination, "*.wav").Length == Convert.ToInt32(wantCancel["wav_count"]), "cancel wav count differs");
            Require(File.ReadAllLines(Path.Combine(cancelDestination, "语音清单.csv"), Encoding.UTF8).Length == 1 == Convert.ToBoolean(wantCancel["csv_has_header_only"]), "cancel csv header-only differs");
            Console.WriteLine("PASS: cancellation partial path matches.");

            // real-model recognition when ASR assets are staged
            if (expected.ContainsKey("asr"))
            {
                var wantAsr = (Dictionary<string, object>)expected["asr"];
                string csAsr = Path.Combine(root, "cs-asr");
                Directory.CreateDirectory(csAsr);
                var asrRequest = new Dictionary<string, object> {
                    ["session"] = session, ["output"] = csAsr, ["keep_silk"] = false, ["transcribe"] = true,
                    ["ids"] = new List<string> { Convert.ToString(wantAsr["id"]) } };
                var asrFinal = (Dictionary<string, object>)Invoke(run, workspace, asrRequest, new Action(() => { }), null);
                Require(Convert.ToInt32(asrFinal["completed"]) == Convert.ToInt32(wantAsr["completed"]), "asr completed differs");
                Require(Convert.ToInt32(asrFinal["recognized"]) == Convert.ToInt32(wantAsr["recognized"]), "asr recognized count differs: " + asrFinal["recognized"]);
                Require(Convert.ToInt32(asrFinal["recognition_failed"]) == Convert.ToInt32(wantAsr["recognition_failed"]), "asr recognition_failed differs");
                string asrDestination = Convert.ToString(asrFinal["output"]);
                string pyAsrDestination = Destination(Path.Combine(root, "py-asr"));
                byte[] wantAsrCsv = File.ReadAllBytes(Path.Combine(pyAsrDestination, "语音清单.csv"));
                byte[] gotAsrCsv = File.ReadAllBytes(Path.Combine(asrDestination, "语音清单.csv"));
                Require(BytesEqual(wantAsrCsv, gotAsrCsv), "asr CSV bytes differ");
                Console.WriteLine("PASS: real-model recognition export matches (text and naming).");
            }
            else Console.WriteLine("SKIP: ASR assets not staged; recognition export comparison omitted.");

            File.WriteAllText(Path.Combine(root, "export-check.txt"), "PASS");
            Console.WriteLine("PASS: C# export pipeline matches Python reference end to end.");
            return 0;
        }
        catch (Exception exc)
        {
            Console.WriteLine("FAIL: harness error: " + exc);
            return 1;
        }
    }
}
