using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
internal static class CryptoDbCheck
{
    // Compares the C# CryptoDb port (src/VoiceEngine) against the Python
    // reference implementation on the fixtures built by tests/crypto_fixtures.py.
    // Success cases must produce byte-identical snapshots; error cases must be
    // rejected with the same outcome. Synthetic data only.
    private static int failures;
    private static MethodInfo snapshot, liveSnapshot, firstPageKey, derive, macKey;
    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    private static object Invoke(MethodInfo method, params object[] args)
    {
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException exc) { throw exc.InnerException; }
    }
    private static void VectorChecks(string root, object cryptoType)
    {
        var vectors = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(root, "crypto-vectors.json")));
        byte[] key = Hex(Convert.ToString(vectors["key"])), salt = Hex(Convert.ToString(vectors["salt"]));
        byte[] derived = (byte[])Invoke(derive, HexString("passphrase"), HexString("0123456789abcdef"));
        Require(Convert.ToString(vectors["derive"]) == ToHex(derived), "Derive mismatch");
        Require(Convert.ToString(vectors["mac_key"]) == ToHex((byte[])Invoke(macKey, key, salt)), "MacKey mismatch");
        Require(Convert.ToString(vectors["first_page_key_ok"]) == ToHex((byte[])Invoke(firstPageKey, Path.Combine(root, "encrypted-no-wal", "source.db"), key, false)), "FirstPageKey ok mismatch");
        Require(vectors["first_page_key_bad"] == null && Invoke(firstPageKey, Path.Combine(root, "encrypted-no-wal", "source.db"), HexString(new string('9', 64)), false) == null, "FirstPageKey should reject wrong key");
        Require(Convert.ToString(vectors["first_page_key_master"]) == ToHex((byte[])Invoke(firstPageKey, Path.Combine(root, "enc", "master.db"), HexString("master"), true)), "FirstPageKey master derivation mismatch");
        Require(vectors["first_page_key_master_raw_rejected"] == null && Invoke(firstPageKey, Path.Combine(root, "enc", "master.db"), HexString("master"), false) == null, "Master key accepted as raw key");
        Console.WriteLine("PASS: derive/mac_key/first_page_key vectors match Python reference.");
    }
    private static void Case(string root, string name)
    {
        string dir = Path.Combine(root, name);
        if (!File.Exists(Path.Combine(dir, "case.json")))
        {
            if (File.Exists(Path.Combine(dir, "skip.txt"))) { Console.WriteLine("SKIP: " + name); return; }
            throw new Exception("case.json missing: " + name);
        }
        var meta = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(dir, "case.json")));
        byte[] key = meta["key"] == null ? null : Hex(Convert.ToString(meta["key"]));
        string source = Path.Combine(dir, "source.db");
        string dest = Path.Combine(dir, "out-" + Guid.NewGuid().ToString("N") + ".db");
        bool expectSuccess = Convert.ToString(meta["expect"]) == "success";
        try
        {
            Invoke(snapshot, source, dest, key, null, null);
            if (!expectSuccess) { failures++; Console.WriteLine("FAIL: " + name + " (expected rejection, got snapshot)"); return; }
            byte[] expected = File.ReadAllBytes(Path.Combine(dir, "expected.db"));
            byte[] actual = File.ReadAllBytes(dest);
            if (!BytesEqual(expected, actual)) { failures++; Console.WriteLine("FAIL: " + name + " (snapshot bytes differ)"); return; }
            File.Delete(dest);
            Console.WriteLine("PASS: " + name);
        }
        catch (Exception exc)
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            if (expectSuccess) { failures++; Console.WriteLine("FAIL: " + name + " (" + exc.GetType().Name + ": " + exc.Message + ")"); return; }
            if (!(exc is InvalidDataException || exc is IOException || exc is UnauthorizedAccessException)) { failures++; Console.WriteLine("FAIL: " + name + " (wrong rejection type " + exc.GetType().Name + ")"); return; }
            Console.WriteLine("PASS: " + name + " (rejected as expected)");
        }
    }
    private static void LiveChecks(string root)
    {
        // Static plain-wal fixture through the online freeze path.
        string dir = Path.Combine(root, "plain-wal");
        string dest = Path.Combine(dir, "live-out-" + Guid.NewGuid().ToString("N") + ".db");
        Invoke(liveSnapshot, Path.Combine(dir, "source.db"), dest, null, null, null);
        Require(BytesEqual(File.ReadAllBytes(dest), File.ReadAllBytes(Path.Combine(dir, "expected.db"))), "LiveSnapshot bytes differ");
        File.Delete(dest);
        // Truncated WAL must refuse after bounded retries, never produce output.
        dir = Path.Combine(root, "plain-wal-truncated");
        dest = Path.Combine(dir, "live-bad-" + Guid.NewGuid().ToString("N") + ".db");
        int progress = 0;
        bool refused = false;
        try { Invoke(liveSnapshot, Path.Combine(dir, "source.db"), dest, null, null, new Action<int, int>((a, t) => progress++)); }
        catch (Exception exc) { refused = exc is InvalidDataException && exc.Message.Contains("三次"); }
        Require(refused, "LiveSnapshot did not refuse truncated WAL");
        Require(progress == 2 && !File.Exists(dest), "LiveSnapshot retry progress/output mismatch");
        Console.WriteLine("PASS: live snapshot freeze, bounded retry and refusal.");
    }
    private static byte[] Hex(string value)
    {
        var result = new byte[value.Length / 2];
        for (int i = 0; i < result.Length; i++) result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
        return result;
    }
    private static byte[] HexString(string value) { return System.Text.Encoding.ASCII.GetBytes(value); }
    private static string ToHex(byte[] value)
    {
        var chars = new char[value.Length * 2];
        for (int i = 0; i < value.Length; i++) { int b = value[i]; chars[i * 2] = HexDigit(b >> 4); chars[i * 2 + 1] = HexDigit(b & 15); }
        return new string(chars);
    }
    private static char HexDigit(int value) { return (char)(value < 10 ? '0' + value : 'a' + value - 10); }
    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.WriteLine("usage: CryptoDbCheck.exe <VoiceEngine.exe> <crypto-fixtures-dir>"); return 1; }
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var type = assembly.GetType("VoiceEngine.CryptoDb", true);
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        snapshot = type.GetMethod("Snapshot", flags);
        liveSnapshot = type.GetMethod("LiveSnapshot", flags);
        firstPageKey = type.GetMethod("FirstPageKey", flags);
        derive = type.GetMethod("Derive", flags);
        macKey = type.GetMethod("MacKey", flags);
        string root = Path.GetFullPath(args[1]);
        try
        {
            VectorChecks(root, type);
            foreach (string name in Directory.GetDirectories(root))
                if (File.Exists(Path.Combine(name, "case.json"))) Case(root, Path.GetFileName(name));
            LiveChecks(root);
        }
        catch (Exception exc)
        {
            Console.WriteLine("FAIL: harness error: " + exc);
            return 1;
        }
        string summary = failures == 0 ? "PASS: C# CryptoDb snapshot port matches Python reference byte-for-byte on all fixtures." : "FAIL: " + failures + " crypto port mismatches.";
        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(root, "crypto-check.txt"), summary);
        return failures == 0 ? 0 : 1;
    }
}
