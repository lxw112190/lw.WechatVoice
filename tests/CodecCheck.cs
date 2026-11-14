using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
internal static class CodecCheck
{
    // Compares the C# VoiceCodec port (same SILK SDK C core, compiled to
    // silk.dll) against the pinned pysilk 0.2.8 reference: identical WAV bytes,
    // packet counts and rejections on the fixtures from tests/codec_fixtures.py.
    private static MethodInfo decode, validatePackets, isolated;
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static object Invoke(MethodInfo method, params object[] args)
    {
        try { return method.Invoke(null, args); }
        catch (TargetInvocationException exc) { throw exc.InnerException; }
    }
    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.WriteLine("usage: CodecCheck.exe <VoiceEngine.exe> <codec-fixtures-dir>"); return 1; }
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var type = assembly.GetType("VoiceEngine.VoiceCodec", true);
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        decode = type.GetMethod("Decode", flags);
        validatePackets = type.GetMethod("ValidatePackets", flags);
        isolated = type.GetMethod("Isolated", flags);
        string root = Path.GetFullPath(args[1]);
        try
        {
            foreach (string dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                var meta = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(dir, "case.json")));
                string source = Path.Combine(dir, "source.silk");
                string target = Path.Combine(dir, "out-" + Guid.NewGuid().ToString("N") + ".wav");
                bool expectSuccess = Convert.ToString(meta["expect"]) == "success";
                try
                {
                    int packets = (int)Invoke(validatePackets, File.ReadAllBytes(source));
                    Require(packets == Convert.ToInt32(meta["packets"]), name + ": packet count differs: " + packets);
                    double duration = (double)Invoke(decode, source, target);
                    if (!expectSuccess) throw new Exception(name + ": expected rejection, got WAV");
                    Require(Math.Abs(duration - Convert.ToDouble(meta["duration"])) < 1e-9, name + ": duration differs: " + duration);
                    Require(BytesEqual(File.ReadAllBytes(target), File.ReadAllBytes(Path.Combine(dir, "expected.wav"))), name + ": WAV bytes differ");
                    File.Delete(target);
                    Console.WriteLine("PASS: " + name);
                }
                catch (TargetInvocationException) { throw; }
                catch (Exception exc)
                {
                    if (File.Exists(target)) try { File.Delete(target); } catch { }
                    if (expectSuccess) { Console.WriteLine("FAIL: " + name + ": " + exc.Message); return 1; }
                    if (!(exc is InvalidDataException)) { Console.WriteLine("FAIL: " + name + ": wrong rejection type " + exc.GetType().Name); return 1; }
                    Console.WriteLine("PASS: " + name + " (rejected as expected)");
                }
            }
            // isolated subprocess: decode via the engine's own --decode child
            string multi = Path.Combine(root, "multi");
            string isolatedTarget = Path.Combine(root, "isolated-" + Guid.NewGuid().ToString("N") + ".wav");
            double isolatedDuration = (double)Invoke(isolated, Path.Combine(multi, "source.silk"), isolatedTarget, new Action(() => { }));
            Require(BytesEqual(File.ReadAllBytes(isolatedTarget), File.ReadAllBytes(Path.Combine(multi, "expected.wav"))), "isolated decode bytes differ");
            Require(Math.Abs(isolatedDuration - 1.0) < 1e-9, "isolated duration differs");
            File.Delete(isolatedTarget);
            // cancellation: a throwing check aborts the child and leaves no target
            string cancelledTarget = Path.Combine(root, "isolated-cancel-" + Guid.NewGuid().ToString("N") + ".wav");
            bool aborted = false;
            try { Invoke(isolated, Path.Combine(multi, "source.silk"), cancelledTarget, new Action(() => { throw new InvalidDataException("已取消"); })); }
            catch (InvalidDataException) { aborted = true; }
            Require(aborted && !File.Exists(cancelledTarget), "isolated cancellation handling differs");
            System.Threading.Thread.Sleep(600);
            Require(!File.Exists(cancelledTarget), "cancelled child created an untracked WAV after return");
            Console.WriteLine("PASS: isolated decode subprocess, cancellation and cleanup.");
            File.WriteAllText(Path.Combine(root, "codec-check.txt"), "PASS");
            Console.WriteLine("PASS: C# SILK codec port matches pysilk 0.2.8 reference byte-for-byte.");
            return 0;
        }
        catch (Exception exc)
        {
            Console.WriteLine("FAIL: harness error: " + exc);
            return 1;
        }
    }
    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
