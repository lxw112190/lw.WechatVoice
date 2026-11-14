using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace VoiceEngine
{
    /// <summary>
    /// Bounded SILK decoding, ported 1:1 from worker/voice_codec.py. The codec
    /// core is the same SILK SDK C source pysilk 0.2.8 wraps (vendored under
    /// .deps/silk-sdk, compiled to silk.dll); the per-packet framing semantics
    /// replicate the pinned 0.2.8 Cython wrapper exactly: read packets until
    /// EOF, strip only the validated -1 terminator, no single-packet copying or
    /// PCM truncation. Decoding runs in an isolated time-limited subprocess.
    /// </summary>
    internal static class VoiceCodec
    {
        internal const int Rate = 24000;
        internal const int MaxPcm = Rate * 2 * 90;
        private static readonly byte[] SilkMagic = Encoding.ASCII.GetBytes("#!SILK_V3");
        private static readonly byte[] TencentMagic = { 0x02, (byte)'#', (byte)'!', (byte)'S', (byte)'I', (byte)'L', (byte)'K', (byte)'_', (byte)'V', (byte)'3' };

        [StructLayout(LayoutKind.Sequential)]
        internal struct DecControl
        {
            internal int ApiSampleRate, FrameSize, FramesPerPacket, MoreInternalDecoderFrames, InBandFecOffset;
        }

        [DllImport("silk.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "silk_sdk_get_decoder_size")]
        private static extern int GetDecoderSize(out int sizeBytes);
        [DllImport("silk.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "silk_sdk_init_decoder")]
        private static extern int InitDecoder(IntPtr decState);
        [DllImport("silk.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "silk_sdk_decode")]
        private static extern int DecodeRaw(IntPtr decState, ref DecControl control, int lostFlag,
            byte[] inData, int nBytesIn, byte[] samplesOut, out short nSamplesOut);

        /// <summary>Validated framing; returns (framed bytes, packet count).</summary>
        internal static Tuple<byte[], int> PacketStream(byte[] raw)
        {
            byte[] data = raw != null && raw.Length > 0 && raw[0] == 0x02 ? Slice(raw, 1) : raw;
            if (data == null || !StartsWith(data, SilkMagic))
                throw new InvalidDataException("不是支持的SILK v3语音");
            var starts = new List<int> { 9 };
            if (data.Length > 10 && data[9] == '\r' && data[10] == '\n') starts.Add(11);
            else if (data.Length > 9 && data[9] == '\n') starts.Add(10);
            foreach (int start in starts)
            {
                int at = start;
                var packets = new List<byte[]>();
                while (at < data.Length)
                {
                    int begin = at;
                    if (at + 2 > data.Length) break;
                    short size = ReadInt16(data, at);
                    at += 2;
                    if (size == -1)
                    {
                        if (at != data.Length) break;
                        at = begin;
                        break;
                    }
                    // Native SILK SDK packet buffer capacity; reject before native code.
                    if (size <= 0 || size > 1250 || at + size > data.Length) break;
                    at += size;
                    packets.Add(Slice(data, begin, at - begin));
                    if (packets.Count > 4500) break;
                }
                bool validEnd = at == data.Length || (at + 2 == data.Length && data[at] == 0xff && data[at + 1] == 0xff);
                if (validEnd && packets.Count > 0 && packets.Count <= 4500)
                {
                    int sum = 0;
                    foreach (var packet in packets) sum += packet.Length;
                    if (sum == at - start)
                    {
                        // Pinned pysilk 0.2.8 reads packets until EOF; its decoder does
                        // not consume the signed -1 terminator. Strip only validated framing.
                        var framed = new byte[TencentMagic.Length + sum];
                        Buffer.BlockCopy(TencentMagic, 0, framed, 0, TencentMagic.Length);
                        int offset = TencentMagic.Length;
                        foreach (var packet in packets) { Buffer.BlockCopy(packet, 0, framed, offset, packet.Length); offset += packet.Length; }
                        return Tuple.Create(framed, packets.Count);
                    }
                }
            }
            throw new InvalidDataException("SILK帧长度不完整或不支持，原始文件已保留");
        }

        internal static int ValidatePackets(byte[] raw) { return PacketStream(raw).Item2; }

        private sealed class BoundedBuffer : MemoryStream
        {
            internal BoundedBuffer() : base() { }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Position + count > MaxPcm) throw new InvalidDataException("语音超过90秒解码上限");
                base.Write(buffer, offset, count);
            }
        }

        internal static double Decode(string sourcePath, string targetPath)
        {
            var source = new FileInfo(sourcePath);
            if (source.Length <= 0 || source.Length > 8L * 1024 * 1024) throw new InvalidDataException("语音大小无效");
            byte[] raw = File.ReadAllBytes(sourcePath);
            if (!(StartsWith(raw, TencentMagic) || StartsWith(raw, SilkMagic)))
                throw new InvalidDataException("不是支持的SILK v3语音，已保留原始文件");
            byte[] framed = PacketStream(raw).Item1;
            var pcm = new BoundedBuffer();
            DecodeToPcm(framed, pcm);
            byte[] samples = pcm.ToArray();
            if (samples.Length <= 64 || samples.Length > MaxPcm || samples.Length % 2 != 0)
                throw new InvalidDataException("SILK解码失败，原始文件已保留");
            string temporary = targetPath + ".wav.part";
            try
            {
                WriteWav(temporary, samples);
                using (var check = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] header = new byte[44];
                    ReadFully(check, header);
                    if (BitConverter.ToInt32(header, 16) != 16 || BitConverter.ToInt32(header, 24) != Rate ||
                        BitConverter.ToInt16(header, 22) != 1 ||
                        BitConverter.ToInt32(header, 40) != samples.Length)
                        throw new InvalidDataException("WAV回读验证失败");
                }
                if (File.Exists(targetPath)) throw new InvalidDataException("已有WAV不覆盖");
                File.Move(temporary, targetPath);
                return samples.Length / (double)(Rate * 2);
            }
            finally { if (File.Exists(temporary)) try { File.Delete(temporary); } catch { } }
        }

        private static void DecodeToPcm(byte[] framed, BoundedBuffer output)
        {
            int decSize;
            if (GetDecoderSize(out decSize) != 0) throw new InvalidDataException("SILK解码器初始化失败");
            IntPtr dec = Marshal.AllocHGlobal(decSize);
            try
            {
                if (InitDecoder(dec) != 0) throw new InvalidDataException("SILK解码器初始化失败");
                var control = new DecControl { ApiSampleRate = Rate, FrameSize = 0, FramesPerPacket = 1, MoreInternalDecoderFrames = 0, InBandFecOffset = 0 };
                int frameBytes = Rate / 1000 * 40 * 2; // 1920
                var samplesBuffer = new byte[frameBytes];
                int at = TencentMagic.Length;
                while (at + 2 <= framed.Length)
                {
                    short nBytes = ReadInt16(framed, at);
                    at += 2;
                    if (nBytes > frameBytes) throw new InvalidDataException("SILK包长度无效");
                    if (at + nBytes > framed.Length) throw new InvalidDataException("SILK包数据不完整");
                    short nSamples;
                    int code = DecodeRaw(dec, ref control, 0, Slice(framed, at, nBytes), nBytes, samplesBuffer, out nSamples);
                    if (code != 0) throw new InvalidDataException("SILK解码失败，原始文件已保留");
                    output.Write(samplesBuffer, 0, nSamples * 2);
                    at += nBytes;
                }
            }
            finally { Marshal.FreeHGlobal(dec); }
        }

        private static void WriteWav(string path, byte[] pcm)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var header = new byte[44];
                Encoding.ASCII.GetBytes("RIFF", 0, 4, header, 0);
                BitConverter.GetBytes(36 + pcm.Length).CopyTo(header, 4);
                Encoding.ASCII.GetBytes("WAVEfmt ", 0, 8, header, 8);
                BitConverter.GetBytes(16).CopyTo(header, 16);
                BitConverter.GetBytes((short)1).CopyTo(header, 20);
                BitConverter.GetBytes((short)1).CopyTo(header, 22);
                BitConverter.GetBytes(Rate).CopyTo(header, 24);
                BitConverter.GetBytes(Rate * 2).CopyTo(header, 28);
                BitConverter.GetBytes((short)2).CopyTo(header, 32);
                BitConverter.GetBytes((short)16).CopyTo(header, 34);
                Encoding.ASCII.GetBytes("data", 0, 4, header, 36);
                BitConverter.GetBytes(pcm.Length).CopyTo(header, 40);
                stream.Write(header, 0, header.Length);
                stream.Write(pcm, 0, pcm.Length);
            }
        }

        /// <summary>One bounded decode subprocess per voice; mirrors worker/voice_codec.isolated.</summary>
        internal static double Isolated(string source, string target, Action check)
        {
            string executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var start = new ProcessStartInfo(executable, "--decode")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = Process.Start(start))
            {
                try
                {
                    var serializer = new JavaScriptSerializer();
                    var request = Encoding.UTF8.GetBytes(serializer.Serialize(new Dictionary<string, object> { ["source"] = source, ["target"] = target }) + "\n");
                    process.StandardInput.BaseStream.Write(request, 0, request.Length);
                    process.StandardInput.Close();
                    var timer = Stopwatch.StartNew();
                    while (!process.WaitForExit(40))
                    {
                        if (check != null) check();
                        if (timer.Elapsed.TotalSeconds > 8)
                            throw new InvalidDataException("语音解码超时，原始文件已保留");
                    }
                    string raw = process.StandardOutput.ReadToEnd();
                    if (process.ExitCode != 0) throw new InvalidDataException("语音解码失败，原始文件已保留");
                    var result = serializer.Deserialize<Dictionary<string, object>>(raw);
                    if (result == null || !Convert.ToBoolean(result["ok"]))
                        throw new InvalidDataException(result != null && result.ContainsKey("error") ? Convert.ToString(result["error"]) : "语音转换失败");
                    return Convert.ToDouble(result["duration"]);
                }
                finally
                {
                    // Process.Dispose alone does not terminate a child. Join it before
                    // returning so cancellation cannot create untracked WAVs afterwards.
                    if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
                }
            }
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data == null || data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) if (data[i] != prefix[i]) return false;
            return true;
        }

        private static short ReadInt16(byte[] data, int offset) { return (short)(data[offset] | data[offset + 1] << 8); }

        private static byte[] Slice(byte[] data, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(data, offset, result, 0, count);
            return result;
        }

        private static byte[] Slice(byte[] data, int offset)
        {
            var result = new byte[data.Length - offset];
            Buffer.BlockCopy(data, offset, result, 0, result.Length);
            return result;
        }

        private static void ReadFully(Stream stream, byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length) { int n = stream.Read(buffer, read, buffer.Length - read); if (n == 0) break; read += n; }
            if (read < buffer.Length) throw new InvalidDataException("WAV回读验证失败");
        }
    }
}
