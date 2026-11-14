using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
[assembly: AssemblyFileVersion("4.0.5.16")]
internal static class CaptureCheck
{
    // Compares the C# capture/key_reader port (src/VoiceEngine) against the
    // Python reference on synthetic fixtures (tests/capture_fixtures.py), then
    // runs the full Win32 scan against a synthetic process that merely carries
    // the same memory image — no WeChat involved.
    private static Type cryptoDb, keyReader, matcherType, capture;
    private static MethodInfo extract, scan, databases, saveVault, loadVault, candidatesMethod, validateIdentity;
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static object Invoke(MethodInfo method, params object[] args)
    {
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException exc) { throw exc.InnerException; }
    }
    private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).GetValue(target); }

    private static void ExtractCompare(string root)
    {
        var expected = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(root, "expected.json")));
        byte[] image = File.ReadAllBytes(Path.Combine(root, "memory.bin"));
        string storage = Path.Combine(root, "db_storage");
        var pages = new Dictionary<string, List<KeyValuePair<string, byte[]>>>();
        foreach (string name in new[] { "media_0.db", "media_1.db" })
        {
            var page = new byte[4096];
            using (var stream = File.OpenRead(Path.Combine(storage, name))) stream.Read(page, 0, page.Length);
            string salt = BitConverter.ToString(page, 0, 16).Replace("-", "").ToLowerInvariant();
            if (!pages.ContainsKey(salt)) pages[salt] = new List<KeyValuePair<string, byte[]>>();
            pages[salt].Add(new KeyValuePair<string, byte[]>(name, page));
        }
        var matcher = Activator.CreateInstance(matcherType, BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { pages, null }, null);
        var regions = new List<KeyValuePair<long, long>> { new KeyValuePair<long, long>(0, image.Length) };
        Func<long, int, byte[]> read = (pointer, count) =>
        {
            if (pointer < 0 || pointer >= image.Length) return new byte[0];
            int available = (int)Math.Min(count, image.Length - pointer);
            var result = new byte[available];
            Buffer.BlockCopy(image, (int)pointer, result, 0, available);
            return result;
        };
        Invoke(extract, regions, read, matcher, (Func<byte[], List<KeyValuePair<byte[], byte[]>>>)(block => (List<KeyValuePair<byte[], byte[]>>)Invoke(candidatesMethod, block)), new Action(() => { }));
        var found = Field<Dictionary<string, string>>(matcher, "found");
        var expectedFound = (Dictionary<string, object>)expected["found"];
        Require(found.Count == expectedFound.Count, "Found count differs: " + found.Count + " expected " + expectedFound.Count);
        foreach (var entry in expectedFound)
        {
            string actual = found.ContainsKey(entry.Key) ? found[entry.Key] : "<missing>";
            Require(actual == Convert.ToString(entry.Value), "Found key mismatch for " + entry.Key + ": got " + actual + " want " + Convert.ToString(entry.Value));
        }
        var stats = (Dictionary<string, object>)expected["stats"];
        Require(Field<int>(matcher, "candidates") == Convert.ToInt32(stats["candidates"]), "candidates stat differs");
        Require(Field<int>(matcher, "configNames") == Convert.ToInt32(stats["config_names"]), "config_names stat differs");
        Require(Field<int>(matcher, "configObjects") == Convert.ToInt32(stats["config_objects"]), "config_objects stat differs");
        Require(Field<int>(matcher, "masterChecks") == Convert.ToInt32(stats["master_checks"]), "master_checks stat differs");
        Console.WriteLine("PASS: extract found keys and stats match Python reference on identical bytes.");
    }

    private static void CandidateVectors(string root)
    {
        var expected = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(root, "expected.json")));
        byte[] block = File.ReadAllBytes(Path.Combine(root, "candidates.bin"));
        var actual = (List<KeyValuePair<byte[], byte[]>>)Invoke(candidatesMethod, block);
        var vectors = (System.Collections.IEnumerable)expected["candidates"];
        int index = 0;
        foreach (System.Collections.ArrayList pair in vectors)
        {
            string expectedKey = Convert.ToString(pair[0]), expectedSalt = pair[1] == null ? null : Convert.ToString(pair[1]);
            string actualKey = BitConverter.ToString(actual[index].Key).Replace("-", "").ToLowerInvariant();
            string actualSalt = actual[index].Value == null ? null : BitConverter.ToString(actual[index].Value).Replace("-", "").ToLowerInvariant();
            Require(expectedKey == actualKey && expectedSalt == actualSalt, "candidate vector " + index + " differs");
            index++;
        }
        Require(index == actual.Count, "candidate count differs: " + actual.Count + " vs " + index);
        Console.WriteLine("PASS: literal candidates (ascii/wide/odd/long/offsets) match Python reference.");
    }

    private static void DatabaseChecks(string temp)
    {
        string root = Path.Combine(temp, "account");
        string storage = Path.Combine(root, "db_storage");
        Directory.CreateDirectory(Path.Combine(storage, "sub"));
        foreach (string name in new[] { "contact.db", "media_0.db", "message_0.db", "junk.db", Path.Combine("sub", "x.db") })
            File.WriteAllText(Path.Combine(storage, name), "x");
        var result = (Tuple<string, List<string>>)Invoke(databases, root);
        Require(result.Item1 == Path.GetFullPath(storage), "db_storage root not adopted");
        Require(result.Item2.Count == 3 && result.Item2[0].EndsWith("contact.db") && result.Item2[1].EndsWith("media_0.db") && result.Item2[2].EndsWith("message_0.db"), "database selection differs");
        // multiple accounts rejected
        string multi = Path.Combine(temp, "multi");
        Directory.CreateDirectory(Path.Combine(multi, "a", "db_storage")); Directory.CreateDirectory(Path.Combine(multi, "b", "db_storage"));
        Require(Rejects(databases, "多个/下一级账号", multi), "multi-account root not rejected");
        // missing media rejected
        string noMedia = Path.Combine(temp, "nomedia", "db_storage");
        Directory.CreateDirectory(noMedia); File.WriteAllText(Path.Combine(noMedia, "contact.db"), "x");
        Require(Rejects(databases, "media_", noMedia), "missing media not rejected");
        // per-file size limit (sparse files keep the disk untouched)
        string big = Path.Combine(temp, "big", "db_storage");
        Directory.CreateDirectory(big);
        MakeSparse(Path.Combine(big, "media_0.db"), 33L * 1024 * 1024 * 1024);
        Require(Rejects(databases, "大小超出", big), "oversized db not rejected");
        // count limit
        string many = Path.Combine(temp, "many", "db_storage");
        Directory.CreateDirectory(many);
        for (int i = 0; i < 513; i++) File.WriteAllText(Path.Combine(many, "media_" + i + ".db"), "x");
        Require(Rejects(databases, "512", many), "db count limit not enforced");
        // total limit with sparse files
        string total = Path.Combine(temp, "total", "db_storage");
        Directory.CreateDirectory(total);
        for (int i = 0; i < 3; i++) MakeSparse(Path.Combine(total, "media_" + i + ".db"), 30L * 1024 * 1024 * 1024);
        Require(Rejects(databases, "64GiB", total), "total size limit not enforced");
        Console.WriteLine("PASS: databases selection, limits and rejections.");
    }

    private static bool Rejects(MethodInfo method, string fragment, params object[] args)
    {
        try { Invoke(method, args); return false; }
        catch (InvalidDataException exc) { return exc.Message.Contains(fragment); }
        catch { return false; }
    }

    private static void MakeSparse(string path, long size)
    {
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            int marker = 1;
            uint ignored;
            DeviceIoControl(stream.SafeFileHandle, 0x000900c4, ref marker, 4, IntPtr.Zero, 0, out ignored, IntPtr.Zero);
            stream.SetLength(size);
        }
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint control,
        ref int inBuffer, int inSize, IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    private static void VaultChecks(string temp)
    {
        string path = Path.Combine(temp, "中文 路径", "vault.dpapi");
        string root = Path.Combine(temp, "account 甲");
        var keys = new Dictionary<string, string> { ["media_0.db"] = "ab".PadRight(64, '0') };
        Invoke(saveVault, path, root, keys);
        var loaded = (Dictionary<string, string>)Invoke(loadVault, path, root);
        Require(loaded.Count == 1 && loaded["media_0.db"] == keys["media_0.db"], "vault roundtrip failed");
        Require(Rejects(loadVault, "不一致", path, Path.Combine(temp, "other-account")), "vault root binding not enforced");
        File.WriteAllBytes(path, new byte[300 * 1024]);
        Require(Rejects(loadVault, "过大", path, root), "vault size limit not enforced");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        try { Invoke(loadVault, path, root); Require(false, "garbage vault accepted"); } catch (InvalidDataException) { }
        Console.WriteLine("PASS: DPAPI vault roundtrip, root binding, size and format rejection.");
    }

    private static void IdentityChecks()
    {
        Identity(true, "weixin.exe", "4.0.5.16"); Identity(true, "WeChat.exe", "4.1.0.30");
        Identity(false, "wechat.exe", "3.9.12.5"); Identity(false, "other.exe", "4.0.5.16");
        Identity(false, "weixin.exe", "4.0"); Identity(false, "weixin.exe", null);
        Console.WriteLine("PASS: process name/version validation matrix.");
    }

    private static void Identity(bool accepted, string name, string version)
    {
        bool ok;
        try { Invoke(validateIdentity, name, version); ok = true; } catch (InvalidDataException) { ok = false; }
        Require(ok == accepted, "identity gate wrong for " + name + " " + version);
    }

    private static void LiveScan(string root, string temp, string ownPath)
    {
        string processPath = Path.Combine(temp, "Weixin.exe");
        File.Copy(ownPath, processPath, true);
        string marker = Path.Combine(temp, "ready.txt");
        File.Delete(marker);
        var start = new ProcessStartInfo(processPath, "\"--synthetic\" \"" + Path.Combine(root, "memory.bin") + "\" \"" + marker + "\"")
        { UseShellExecute = false, CreateNoWindow = true };
        using (var process = Process.Start(start))
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(marker) && DateTime.UtcNow < deadline) Thread.Sleep(100);
            Require(File.Exists(marker), "synthetic process did not pin its image in time");
            Thread.Sleep(500); // let the heap settle into committed regions
            var result = Invoke(scan, process.Id, Path.Combine(root, "db_storage"), new Action(() => { }), null);
            var expected = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(root, "expected.json")));
            var found = Field<Dictionary<string, string>>(result, "Found");
            var expectedFound = (Dictionary<string, object>)expected["found"];
            Require(found.Count == expectedFound.Count, "live scan found " + found.Count + " keys, expected " + expectedFound.Count);
            foreach (var entry in expectedFound) Require(found.ContainsKey(entry.Key) && found[entry.Key] == Convert.ToString(entry.Value), "live scan key mismatch for " + entry.Key);
            var report = Field<Dictionary<string, object>>(result, "Report");
            Require(Convert.ToString(report["version"]) == "4.0.5.16", "live scan version report differs");
            Require(Convert.ToInt32(report["matched"]) == 2 && Convert.ToInt32(report["encrypted"]) == 2, "live scan matched/encrypted differs");
            Require(Convert.ToInt32(report["config_names"]) == 1 && Convert.ToInt32(report["config_objects"]) == 1 && Convert.ToInt32(report["master_checks"]) == 1, "live scan config stats differ");
            Require(Convert.ToInt32(report["region_count"]) > 0, "live scan enumerated no regions");
            Require(!Convert.ToBoolean(report["limit_reached"]), "live scan hit limits");
            process.Kill(); process.WaitForExit(5000);
            Console.WriteLine("PASS: full Win32 scan against a synthetic 4.0.5.16 process found both keys.");
        }
    }

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--synthetic")
        {
            // Pin the synthetic memory image, rewrite the embedded pointers to
            // their real addresses (node symbol, config object, blob) and hold
            // the process alive for the scan.
            byte[] image = File.ReadAllBytes(args[1]);
            GCHandle pinned = GCHandle.Alloc(image, GCHandleType.Pinned);
            long real = pinned.AddrOfPinnedObject().ToInt64();
            Marshal.WriteInt64(pinned.AddrOfPinnedObject() + 0x10010, real + 0x18000); // node+16: symbol address
            Marshal.WriteInt64(pinned.AddrOfPinnedObject() + 0x10028, real + 0x14000); // node+40: config object
            Marshal.WriteInt64(pinned.AddrOfPinnedObject() + 0x14090, real + 0x16000); // object+0x88+8: blob address
            File.WriteAllText(args[2], "ready");
            Thread.Sleep(120000);
            pinned.Free();
            return 0;
        }
        if (args.Length != 3) { Console.WriteLine("usage: CaptureCheck.exe <VoiceEngine.exe> <capture-fixtures-dir> <temp-dir>"); return 1; }
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        cryptoDb = assembly.GetType("VoiceEngine.CryptoDb", true);
        keyReader = assembly.GetType("VoiceEngine.KeyReader", true);
        matcherType = keyReader.GetNestedType("Matcher", BindingFlags.NonPublic);
        capture = assembly.GetType("VoiceEngine.Capture", true);
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        extract = keyReader.GetMethod("Extract", flags);
        candidatesMethod = capture.GetMethod("Candidates", flags);
        databases = capture.GetMethod("Databases", flags);
        saveVault = capture.GetMethod("SaveVault", flags);
        loadVault = capture.GetMethod("LoadVault", flags);
        scan = capture.GetMethod("Scan", flags);
        validateIdentity = capture.GetMethod("ValidateProcessIdentity", flags);
        string root = Path.GetFullPath(args[1]);
        string temp = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(temp);
        try
        {
            ExtractCompare(root);
            CandidateVectors(root);
            DatabaseChecks(temp);
            VaultChecks(temp);
            IdentityChecks();
            LiveScan(root, temp, Assembly.GetExecutingAssembly().Location);
        }
        catch (Exception exc)
        {
            Console.WriteLine("FAIL: harness error: " + exc);
            return 1;
        }
        Console.WriteLine("PASS: C# capture/key_reader port matches Python reference and scans a synthetic process end to end.");
        File.WriteAllText(Path.Combine(root, "capture-check.txt"), "PASS");
        return 0;
    }
}
