using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

internal static class SnapshotCheck
{
    private static MethodInfo detailed;
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Snapshot(string source, string target, byte[] key, Action check, Action<int, int> retry, Action<Dictionary<string, object>> progress)
    {
        try { detailed.Invoke(null, new object[] { source, target, key, check, retry, progress }); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static string Hash(string file)
    {
        using (var hash = SHA256.Create()) using (var stream = File.OpenRead(file)) return BitConverter.ToString(hash.ComputeHash(stream));
    }
    private static byte[] Key(object value)
    {
        if (value == null) return null;
        string hex = Convert.ToString(value); var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
        return bytes;
    }
    private static int Main(string[] args)
    {
        try
        {
            detailed = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("VoiceEngine.CryptoDb").GetMethod("LiveSnapshotDetailed", BindingFlags.NonPublic | BindingFlags.Static);
            var errors = detailed.DeclaringType.Assembly.GetType("VoiceEngine.EngineErrors");
            var errorCode = errors.GetMethod("Code", BindingFlags.NonPublic | BindingFlags.Static);
            string fixtures = Path.GetFullPath(args[1]), root = Path.GetFullPath(args[2]); Directory.CreateDirectory(root);
            foreach (string folder in Directory.GetDirectories(fixtures).Where(p => File.Exists(Path.Combine(p, "case.json"))))
            {
                var meta = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(folder, "case.json")));
                string name = Path.GetFileName(folder), source = Path.Combine(folder, "source.db"), target = Path.Combine(root, name + ".db");
                string wal = source + "-wal"; var files = File.Exists(wal) ? new[] { source, wal } : new[] { source };
                var hashes = files.Select(Hash).ToArray(); var times = files.Select(File.GetLastWriteTimeUtc).ToArray();
                var names = Directory.GetFiles(folder).OrderBy(p => p).ToArray();
                var held = new List<FileStream>(); bool succeeded = false;
                var stages = new HashSet<string>();
                try
                {
                    // Deny writes while reading. A source write/checkpoint attempt
                    // would fail the test instead of silently changing the fixture.
                    foreach (string file in files) held.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));
                    try
                    {
                        Snapshot(source, target, Key(meta["key"]), null, null, progress => {
                            string stage = Convert.ToString(progress["snapshot_stage"]); stages.Add(stage);
                            long done = Convert.ToInt64(progress["bytes_current"]), total = Convert.ToInt64(progress["bytes_total"]);
                            Require(done >= 0 && done <= total, "invalid progress bounds");
                            Require(!progress.ContainsKey("source") && !progress.ContainsKey("key"), "private snapshot progress field");
                        });
                        succeeded = true;
                    }
                    catch (InvalidDataException error)
                    {
                        Require(Convert.ToString(meta["expect"]) == "error", name + " rejected unexpectedly");
                        Require(!string.IsNullOrEmpty((string)errorCode.Invoke(null, new object[] { error })), name + " lost its validation error code");
                    }
                }
                finally { foreach (var file in held) file.Dispose(); }
                Require(succeeded == (Convert.ToString(meta["expect"]) == "success"), name + " acceptance differs");
                for (int i = 0; i < files.Length; i++)
                    Require(Hash(files[i]) == hashes[i] && File.GetLastWriteTimeUtc(files[i]) == times[i], name + " source was changed");
                Require(names.SequenceEqual(Directory.GetFiles(folder).OrderBy(p => p)), name + " created a source sidecar");
                if (succeeded)
                {
                    Require(Hash(target) == Hash(Path.Combine(folder, "expected.db")), name + " bytes differ");
                    Require(stages.Contains("copy_main") && stages.Contains("verify_db"), name + " missing stage progress");
                    if (Key(meta["key"]) == null) Require(!stages.Contains("decrypt"), "plain copy was needlessly rewritten");
                }
                else Require(!File.Exists(target), name + " published a failed snapshot");
                Require(Directory.GetDirectories(root, "live-*").Length == 0, name + " left a frozen directory behind");
            }
            Console.WriteLine("PASS: all snapshot fixtures under write-denying source handles; source bytes/times/names unchanged, exact outputs and bounded stages.");

            string encrypted = Path.Combine(fixtures, "encrypted-many-pages", "source.db");
            foreach (string stage in new[] { "copy_main", "decrypt", "verify_db" })
            {
                bool stop = false, cancelled = false; string target = Path.Combine(root, "cancel-" + stage + ".db");
                try
                {
                    Snapshot(encrypted, target, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
                        () => { if (stop) throw new OperationCanceledException(); }, null,
                        progress => { if (Convert.ToString(progress["snapshot_stage"]) == stage && (stage == "verify_db" || Convert.ToInt64(progress["bytes_current"]) > 0)) stop = true; });
                }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && !File.Exists(target), stage + " cancellation published a snapshot");
                Require(Directory.GetDirectories(root, "live-*").Length == 0 && Directory.GetFiles(root, "*.part").Length == 0, "cancellation left temporary files");
            }
            Console.WriteLine("PASS: cancellation during copy, decryption and integrity-check stages leaves no published/temporary snapshot.");

            string changing = Path.Combine(root, "changing.db"), retried = Path.Combine(root, "retried.db");
            File.Copy(Path.Combine(fixtures, "plain-no-wal", "source.db"), changing);
            bool changed = false; int retries = 0;
            Snapshot(changing, retried, null, null, (a, b) => retries++, progress => {
                if (!changed && Convert.ToString(progress["snapshot_stage"]) == "copy_main" && Convert.ToInt64(progress["bytes_current"]) == Convert.ToInt64(progress["bytes_total"]))
                { changed = true; File.SetLastWriteTimeUtc(changing, File.GetLastWriteTimeUtc(changing).AddSeconds(2)); }
            });
            Require(changed && retries == 1 && Hash(changing) == Hash(retried), "changed source was not retried before publication");
            Console.WriteLine("PASS: source change during freeze still triggers bounded retry before use.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL: " + ex); return 1; }
    }
}
