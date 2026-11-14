using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;

internal static class EngineRobustnessCheck
{
    private static readonly BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static object Call(MethodInfo method, object target, params object[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException exc) { throw exc.InnerException; }
    }

    // The same test binary acts as a controlled ASR child when copied to AsrWorker.exe.
    // No model, account, microphone or external process memory is involved.
    private static void FakeRecognizer()
    {
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false), true));
        string line;
        while ((line = Console.ReadLine()) != null)
        {
            var request = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(line);
            string wav = Convert.ToString(request["wav"]);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(wav), "active.pid"), Process.GetCurrentProcess().Id.ToString());
            string name = Path.GetFileNameWithoutExtension(wav);
            if (name == "slow") Thread.Sleep(10000);
            if (name == "exit") return;
            if (name == "oversize") { Console.Write(new string('x', 100000)); Console.Out.Flush(); Thread.Sleep(10000); }
            if (name == "stderr") { Console.Error.Write(new string('w', 200000)); Console.Error.Flush(); }
            Console.WriteLine(new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["ok"] = true, ["text"] = name }));
        }
    }

    private static bool Running(int pid)
    {
        try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static int Main(string[] args)
    {
        if (args.Length == 0) { FakeRecognizer(); return 0; }
        try
        {
            Console.InputEncoding = System.Text.Encoding.UTF8;
            var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            string root = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(root);
            var sqlite = assembly.GetType("VoiceEngine.SqliteConnection", true);
            string special = Path.Combine(root, "中文 #百分%20's");
            Directory.CreateDirectory(special);
            string path = Path.Combine(special, "test.sqlite");
            using (var db = (IDisposable)Call(sqlite.GetMethod("OpenReadWrite", Static), null, path))
                Call(sqlite.GetMethod("Exec", Instance), db, "CREATE TABLE sample(x INTEGER); INSERT INTO sample VALUES(42)");
            Require(File.Exists(path), "URI created the database at a different path");
            using (var db = (IDisposable)Call(sqlite.GetMethod("OpenReadOnly", Static), null, path))
            using (var statement = (IDisposable)Call(sqlite.GetMethod("Prepare", Instance), db, "SELECT x FROM sample"))
            {
                Require((bool)Call(statement.GetType().GetMethod("Step", Instance), statement), "special path read failed");
                Require((long)Call(statement.GetType().GetMethod("Int64", Instance), statement, 0) == 42, "special path read wrong DB");
            }
            var integrity = assembly.GetType("VoiceEngine.NativeSqliteConnection", true);
            using (var db = (IDisposable)Activator.CreateInstance(integrity, true))
                Call(integrity.GetMethod("OpenReadOnly", Instance), db, path);

            // Cache ATTACH must be parameterized too: apostrophes cannot break SQL.
            string index = Path.Combine(special, "index.sqlite");
            using (var db = (IDisposable)Call(sqlite.GetMethod("OpenReadWrite", Static), null, index))
                Call(sqlite.GetMethod("Exec", Instance), db, @"CREATE TABLE voices(id TEXT,db TEXT,rid INTEGER,chat TEXT,label TEXT,local_id INTEGER,stamp INTEGER,server_id TEXT,sender TEXT,bytes INTEGER);
                    INSERT INTO voices VALUES('aaaaaaaaaaaaaaaaaaaaaaaa','media_0.db',1,'chat','chat',1,1,'','',1);
                    INSERT INTO voices VALUES('bbbbbbbbbbbbbbbbbbbbbbbb','media_0.db',2,'chat','chat',2,2,'','',1);");
            var cache = assembly.GetType("VoiceEngine.SpeechCache", true);
            Call(cache.GetMethod("Save", Static), null, index, "aaaaaaaaaaaaaaaaaaaaaaaa", "digest", "READY", "Z");
            Call(cache.GetMethod("Save", Static), null, index, "bbbbbbbbbbbbbbbbbbbbbbbb", "digest", "READY", "A");
            var library = assembly.GetType("VoiceEngine.Library", true);
            var rows = (List<Dictionary<string, object>>)Call(library.GetMethod("Records", Static), null, index,
                new Dictionary<string, object> { ["sort"] = "transcript" }, (long?)0);
            Require(Convert.ToString(rows[0]["id"]) == "bbbbbbbbbbbbbbbbbbbbbbbb", "cache sort silently fell back on special path");
            Console.WriteLine("PASS: special-character SQLite URI, snapshot integrity and transcript ATTACH sorting.");

            // A cancelled private index must roll back its schema and inserted rows.
            string storage = Path.Combine(root, "storage"); Directory.CreateDirectory(storage);
            using (var db = (IDisposable)Call(sqlite.GetMethod("OpenReadWrite", Static), null, Path.Combine(storage, "contact.db")))
                Call(sqlite.GetMethod("Exec", Instance), db, "CREATE TABLE contact(username TEXT,remark TEXT); INSERT INTO contact VALUES('a','A'); INSERT INTO contact VALUES('b','B')");
            string cancelledIndex = Path.Combine(root, "cancelled-index.sqlite");
            int checks = 0; bool cancelled = false;
            try { Call(library.GetMethod("Build", Static), null, storage, cancelledIndex, new Action(() => { if (++checks == 3) throw new OperationCanceledException(); }), null); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "index cancellation was not exercised");
            using (var db = (IDisposable)Call(sqlite.GetMethod("OpenReadOnly", Static), null, cancelledIndex))
            using (var statement = (IDisposable)Call(sqlite.GetMethod("Prepare", Instance), db, "SELECT count(*) FROM sqlite_master WHERE type='table'"))
            {
                Call(statement.GetType().GetMethod("Step", Instance), statement);
                Require((long)Call(statement.GetType().GetMethod("Int64", Instance), statement, 0) == 0, "cancelled index transaction was committed");
            }
            Console.WriteLine("PASS: cancelled catalogue transaction rolls back all writes.");

            string fake = Path.Combine(root, "fake-asr"); Directory.CreateDirectory(fake);
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(fake, "AsrWorker.exe"));
            var recognizerType = assembly.GetType("VoiceEngine.LocalRecognizer", true);
            var recognize = recognizerType.GetMethod("Recognize", Instance);
            var recognizer = (IDisposable)Activator.CreateInstance(recognizerType, Instance, null, new object[] { fake, null, 2000 }, null);
            using (recognizer)
            {
                bool timeout = false;
                try { Call(recognize, recognizer, Path.Combine(root, "slow.wav")); }
                catch (InvalidDataException ex) { timeout = ex.Message.Contains("超时"); }
                Require(timeout, "slow ASR did not time out");
                Require(!Running(int.Parse(File.ReadAllText(Path.Combine(root, "active.pid")))), "timed-out child still running");
                Require((string)Call(recognize, recognizer, Path.Combine(root, "second.wav")) == "second", "stale ASR reply assigned to next voice");
                foreach (string name in new[] { "exit", "oversize" })
                {
                    bool failed = false; var timer = Stopwatch.StartNew();
                    try { Call(recognize, recognizer, Path.Combine(root, name + ".wav")); }
                    catch (InvalidDataException) { failed = true; }
                    Require(failed && timer.ElapsedMilliseconds < 1900, "invalid ASR reply waited for the full timeout: " + name);
                    Require((string)Call(recognize, recognizer, Path.Combine(root, "retry.wav")) == "retry", "ASR restart failed after " + name);
                }
                Require((string)Call(recognize, recognizer, Path.Combine(root, "stderr.wav")) == "stderr", "stderr pipe blocked ASR");
            }
            bool cancelRequested = false;
            Action check = () => { if (cancelRequested) throw new OperationCanceledException(); cancelRequested = true; };
            using (var cancelRecognizer = (IDisposable)Activator.CreateInstance(recognizerType, Instance, null, new object[] { fake, check, 2000 }, null))
            {
                bool cancelledAsr = false;
                try { Call(recognize, cancelRecognizer, Path.Combine(root, "slow.wav")); }
                catch (OperationCanceledException) { cancelledAsr = true; }
                Require(cancelledAsr, "ASR cancellation swallowed");
            }
            Console.WriteLine("PASS: ASR timeout/restart, no stale replies, EOF, bounded response, stderr drain and cancellation.");
            return 0;
        }
        catch (Exception exc) { Console.WriteLine("FAIL: " + exc); return 1; }
    }
}
