using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceEngine
{
    /// <summary>
    /// Bounded WCDB configuration traversal, ported 1:1 from worker/key_reader.py.
    /// Layout facts checked against Moect 3d08fece. No injection, dumps or global
    /// account scan. All candidates must authenticate a page from the explicitly
    /// selected account.
    /// </summary>
    internal static class KeyReader
    {
        internal static readonly byte[] ConfigName = Encoding.ASCII.GetBytes("com.Tencent.WCDB.Config.Cipher");
        internal static readonly byte[] ConfigMask = Hex("d2c7442458020000004889442450488b450048844c2448488944254048584c24");

        private static byte[] Hex(string value)
        {
            var result = new byte[value.Length / 2];
            for (int i = 0; i < result.Length; i++) result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            return result;
        }

        internal sealed class Matcher
        {
            private readonly Dictionary<string, List<KeyValuePair<string, byte[]>>> pages;
            private readonly Action check;
            internal readonly Dictionary<string, string> found = new Dictionary<string, string>();
            private readonly HashSet<string> seen = new HashSet<string>();
            private readonly HashSet<string> masterSeen = new HashSet<string>();
            internal int candidates, configNames, configObjects, masterChecks;
            private readonly int totalPages;

            internal Matcher(Dictionary<string, List<KeyValuePair<string, byte[]>>> pages, Action check)
            {
                this.pages = pages; this.check = check;
                totalPages = 0;
                foreach (var entries in pages.Values) totalPages += entries.Count;
            }

            internal void Accept(byte[] key, byte[] hint, bool master)
            {
                if (check != null) check();
                if (key == null || key.Length != 32) return;
                List<string> salts = new List<string>();
                if (hint != null && hint.Length == 16 && pages.ContainsKey(ToHex(hint))) salts.Add(ToHex(hint));
                else salts.AddRange(pages.Keys);
                foreach (string saltHex in salts)
                {
                    if (check != null) check();
                    var targets = new List<KeyValuePair<string, byte[]>>();
                    foreach (var entry in pages[saltHex]) if (!found.ContainsKey(entry.Key)) targets.Add(entry);
                    if (targets.Count == 0) continue;
                    string identity = ToHex(key) + saltHex;
                    if (!seen.Contains(identity) && seen.Count < 50000)
                    {
                        seen.Add(identity); candidates++;
                        byte[] salt = FromHex(saltHex);
                        foreach (var entry in targets)
                            if (CryptoDb.Authenticated(entry.Value, 1, key, salt))
                                found[entry.Key] = ToHex(key);
                    }
                    if (master && !masterSeen.Contains(identity) && masterSeen.Count < 64)
                    {
                        masterSeen.Add(identity); masterChecks++;
                        byte[] salt = FromHex(saltHex);
                        byte[] derived = CryptoDb.Derive(key, salt);
                        foreach (var entry in targets)
                            if (!found.ContainsKey(entry.Key) && CryptoDb.Authenticated(entry.Value, 1, derived, salt))
                                found[entry.Key] = ToHex(derived);
                    }
                }
            }

            internal bool Done() { return found.Count == totalPages; }

            private static string ToHex(byte[] value)
            {
                var chars = new char[value.Length * 2];
                for (int i = 0; i < value.Length; i++)
                {
                    int high = value[i] >> 4, low = value[i] & 15;
                    chars[i * 2] = (char)(high < 10 ? '0' + high : 'a' + high - 10);
                    chars[i * 2 + 1] = (char)(low < 10 ? '0' + low : 'a' + low - 10);
                }
                return new string(chars);
            }

            private static byte[] FromHex(string value)
            {
                var result = new byte[value.Length / 2];
                for (int i = 0; i < result.Length; i++) result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
                return result;
            }
        }

        /// <summary>1 MiB blocks with a 512-byte tail carry-over across region boundaries.</summary>
        internal static IEnumerable<KeyValuePair<long, byte[]>> Chunks(List<KeyValuePair<long, long>> regions, Func<long, int, byte[]> read, Action check)
        {
            foreach (var region in regions)
            {
                long baseAddress = region.Key; long size = region.Value;
                var tail = new byte[0];
                for (long offset = 0; offset < size; offset += 1024 * 1024)
                {
                    if (check != null) check();
                    int count = (int)Math.Min(size - offset, 1024 * 1024);
                    byte[] block = read(baseAddress + offset, count);
                    if (block == null || block.Length == 0) { tail = new byte[0]; continue; }
                    var merged = new byte[tail.Length + block.Length];
                    Buffer.BlockCopy(tail, 0, merged, 0, tail.Length);
                    Buffer.BlockCopy(block, 0, merged, tail.Length, block.Length);
                    yield return new KeyValuePair<long, byte[]>(baseAddress + offset - tail.Length, merged);
                    tail = block.Length == count ? Merge(tail, block, 512) : new byte[0];
                }
            }
        }

        private static byte[] Merge(byte[] tail, byte[] block, int keep)
        {
            var combined = new byte[tail.Length + block.Length];
            Buffer.BlockCopy(tail, 0, combined, 0, tail.Length);
            Buffer.BlockCopy(block, 0, combined, tail.Length, block.Length);
            if (combined.Length <= keep) return combined;
            var result = new byte[keep];
            Buffer.BlockCopy(combined, combined.Length - keep, result, 0, keep);
            return result;
        }

        internal static void Extract(List<KeyValuePair<long, long>> regions, Func<long, int, byte[]> read, Matcher matcher,
            Func<byte[], List<KeyValuePair<byte[], byte[]>>> candidates, Action check)
        {
            var symbols = new HashSet<long>();
            // First pass handles the 4.0 raw-key path and locates the configuration name.
            foreach (var chunk in Chunks(regions, read, check))
            {
                long baseAddress = chunk.Key;
                byte[] block = chunk.Value;
                foreach (var pair in candidates(block)) matcher.Accept(pair.Key, pair.Value, false);
                if (matcher.Done()) return;
                int start = 0;
                while (symbols.Count < 256)
                {
                    int pos = IndexOf(block, ConfigName, start);
                    if (pos < 0) break;
                    symbols.Add(baseAddress + pos);
                    start = pos + 1;
                }
            }
            matcher.configNames = symbols.Count;
            if (symbols.Count == 0) return;
            var references = new List<byte[]>();
            foreach (long symbol in symbols) references.Add(PackReference(symbol, ConfigName.Length));
            var objects = new HashSet<long>();
            var configCandidates = new List<KeyValuePair<byte[], byte[]>>();
            // Config nodes contain a std::string descriptor followed by a config pointer.
            foreach (var chunk in Chunks(regions, read, check))
            {
                long baseAddress = chunk.Key;
                byte[] block = chunk.Value;
                foreach (byte[] reference in references)
                {
                    int start = 0;
                    while (true)
                    {
                        if (check != null) check();
                        int pos = IndexOf(block, reference, start);
                        if (pos < 0) break;
                        start = pos + 1;
                        byte[] node = read(baseAddress + pos - 16, 80);
                        if (node == null || node.Length < 80) continue;
                        long symbol = Integer(node, 16);
                        long length = Integer(node, 24);
                        if (!symbols.Contains(symbol) || length != ConfigName.Length) continue;
                        long pointer = Integer(node, 40);
                        if (pointer < 0x10000 || pointer >= 0x800000000000L || objects.Contains(pointer)) continue;
                        objects.Add(pointer);
                        if (objects.Count > 4096) return;
                        byte[] descriptor = read(pointer + 0x88, 40);
                        if (descriptor == null || descriptor.Length < 40) continue;
                        long address = Integer(descriptor, 8);
                        long blobLength = Integer(descriptor, 16);
                        if (blobLength <= 0 || blobLength > 1024 || address < 0x10000 || address >= 0x800000000000L) continue;
                        byte[] blob = read(address, (int)blobLength);
                        if (blob == null || blob.Length != blobLength) continue;
                        var decoded = new byte[blob.Length];
                        for (int i = 0; i < blob.Length; i++) decoded[i] = (byte)(blob[i] ^ ConfigMask[i % ConfigMask.Length]);
                        foreach (var pair in candidates(decoded))
                        {
                            matcher.Accept(pair.Key, pair.Value, false);
                            configCandidates.Add(pair);
                        }
                        matcher.configObjects = objects.Count;
                        if (matcher.Done()) return;
                    }
                }
            }
            // Expensive derivation applies only to structurally verified config candidates.
            for (int i = 0; i < configCandidates.Count && i < 64; i++)
            {
                matcher.Accept(configCandidates[i].Key, configCandidates[i].Value, true);
                if (matcher.Done()) return;
            }
        }

        private static byte[] PackReference(long address, int length)
        {
            var result = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes((ulong)address), 0, result, 0, 8);
            Buffer.BlockCopy(BitConverter.GetBytes((ulong)length), 0, result, 8, 8);
            return result;
        }

        private static long Integer(byte[] data, int offset)
        {
            if (data.Length < offset + 8) return 0;
            return (long)BitConverter.ToUInt64(data, offset);
        }

        private static int IndexOf(byte[] data, byte[] pattern, int start)
        {
            for (int i = start; i + pattern.Length <= data.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++) { if (data[i + j] != pattern[j]) { match = false; break; } }
                if (match) return i;
            }
            return -1;
        }
    }
}
