using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WechatVoice.Asr
{
    /// <summary>Upstream 2.0.1 configuration layouts, explicit UTF-8 strings and checked handles.
    /// Framework LPStr uses the system ANSI codepage; passing Chinese Windows paths through it corrupts filenames.
    /// This scope preserves native ABI offsets while owning every UTF-8 allocation until Create returns.</summary>
    internal sealed class Utf8Config : IDisposable
    {
        private readonly List<IntPtr> memory = new List<IntPtr>();
        public IntPtr Pointer { get; private set; }
        public Utf8Config(object config)
        {
            try { Pointer = Allocate(Marshal.SizeOf(config.GetType())); Write(config, Pointer); }
            catch { Dispose(); throw; }
        }
        private IntPtr Allocate(int count)
        {
            IntPtr p = Marshal.AllocHGlobal(count); memory.Add(p); Marshal.Copy(new byte[count], 0, p, count); return p;
        }
        private void Write(object value, IntPtr address)
        {
            foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                IntPtr p = IntPtr.Add(address, checked((int)Marshal.OffsetOf(value.GetType(), field.Name)));
                object v = field.GetValue(value); Type t = field.FieldType;
                if (t == typeof(string))
                {
                    byte[] b = Encoding.UTF8.GetBytes((string)v ?? ""); IntPtr str = Allocate(b.Length + 1); Marshal.Copy(b, 0, str, b.Length); Marshal.WriteIntPtr(p, str);
                }
                else if (t == typeof(int)) Marshal.WriteInt32(p, (int)v);
                else if (t == typeof(float)) Marshal.Copy(new[] { (float)v }, 0, p, 1);
                else if (t == typeof(double)) Marshal.Copy(new[] { (double)v }, 0, p, 1);
                else if (t.IsValueType && !t.IsPrimitive && !t.IsEnum) Write(v, p);
                else throw new NotSupportedException("不支持的 sherpa 配置字段：" + field.Name);
            }
        }
        public void Dispose() { for (int i = memory.Count - 1; i >= 0; i--) Marshal.FreeHGlobal(memory[i]); memory.Clear(); Pointer = IntPtr.Zero; }
    }
    internal static class NativeSpeechApi
    {
        private const string Dll = "sherpa-onnx-c-api.dll";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Callback(IntPtr samples, int count);
        [StructLayout(LayoutKind.Sequential)] internal struct GeneratedAudio { public IntPtr samples; public int count; public int sampleRate; }
        public static string Utf8(IntPtr p, int maximum = 65536)
        {
            if (p == IntPtr.Zero) throw new InvalidOperationException("语音引擎返回空指针。");
            int size = 0; while (size < maximum && Marshal.ReadByte(p, size) != 0) size++;
            if (size == maximum) throw new InvalidOperationException("语音引擎文本超过限制。");
            byte[] bytes = new byte[size]; Marshal.Copy(p, bytes, 0, size); return new UTF8Encoding(false, true).GetString(bytes);
        }
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr SherpaOnnxGetVersionStr();
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr SherpaOnnxCreateOfflineRecognizer(IntPtr config);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxDestroyOfflineRecognizer(IntPtr handle);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr SherpaOnnxCreateOfflineStream(IntPtr handle);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxDestroyOfflineStream(IntPtr handle);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxAcceptWaveformOffline(IntPtr stream, int rate, float[] samples, int count);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxDecodeOfflineStream(IntPtr recognizer, IntPtr stream);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr SherpaOnnxGetOfflineStreamResultAsJson(IntPtr stream);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxDestroyOfflineStreamResultJson(IntPtr json);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr SherpaOnnxCreateOfflineTts(IntPtr config);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxDestroyOfflineTts(IntPtr handle);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int SherpaOnnxOfflineTtsSampleRate(IntPtr handle);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int SherpaOnnxOfflineTtsNumSpeakers(IntPtr handle);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr SherpaOnnxOfflineTtsGenerateWithCallback(IntPtr handle, byte[] text, int speaker, float speed, Callback callback);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxDestroyOfflineTtsGeneratedAudio(IntPtr audio);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr SherpaOnnxCreateKeywordSpotter(IntPtr config);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxDestroyKeywordSpotter(IntPtr handle);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr SherpaOnnxCreateKeywordStream(IntPtr handle);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxDestroyOnlineStream(IntPtr handle);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxOnlineStreamAcceptWaveform(IntPtr stream, int rate, float[] samples, int count);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int SherpaOnnxIsKeywordStreamReady(IntPtr spotter, IntPtr stream);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxDecodeKeywordStream(IntPtr spotter, IntPtr stream);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr SherpaOnnxGetKeywordResultAsJson(IntPtr spotter, IntPtr stream);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxFreeKeywordResultJson(IntPtr json);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern IntPtr SherpaOnnxCreateVoiceActivityDetector(IntPtr config, float bufferSeconds);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxDestroyVoiceActivityDetector(IntPtr handle);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxVoiceActivityDetectorAcceptWaveform(IntPtr handle, float[] samples, int count);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int SherpaOnnxVoiceActivityDetectorDetected(IntPtr handle);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern int SherpaOnnxVoiceActivityDetectorEmpty(IntPtr handle);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxVoiceActivityDetectorPop(IntPtr handle);
        [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] internal static extern void SherpaOnnxVoiceActivityDetectorReset(IntPtr handle);
    }
}
