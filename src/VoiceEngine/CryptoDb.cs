using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VoiceEngine
{
    /// <summary>
    /// Authenticated SQLCipher4 snapshot logic, ported 1:1 from worker/cryptodb.py.
    /// Never opens the source database for writing. Synthetic comparison tests
    /// (tests/CryptoDbCheck.cs) require byte-identical output with the Python
    /// reference implementation on the same fixtures.
    /// </summary>
    internal static class CryptoDb
    {
        internal const int Page = 4096;
        internal const int Reserve = 80;
        internal const long MaxDb = 32L * 1024 * 1024 * 1024;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SQLite format 3\0");
        private const int IoBuffer = 1024 * 1024;

        // One crypto context per private database, never shared between threads.
        // The page MAC subkey depends on key/salt, not on the page number.
        private sealed class PageCipher : IDisposable
        {
            private readonly HMACSHA512 mac;
            private readonly byte[] key;
            private readonly byte[] numberBytes = new byte[4], iv = new byte[16];
            private static readonly byte[] Empty = new byte[0];
            private Aes aes;

            internal PageCipher(byte[] key, byte[] salt)
            {
                this.key = (byte[])key.Clone();
                byte[] macKey = MacKey(key, salt);
                try { mac = new HMACSHA512(macKey); }
                finally { Array.Clear(macKey, 0, macKey.Length); }
            }

            internal bool Authenticate(byte[] page, int offset, int number)
            {
                if (page == null || offset < 0 || page.Length - offset < Page) return false;
                int start = number == 1 ? 16 : 0;
                uint value = (uint)number;
                for (int i = 0; i < 4; i++) numberBytes[i] = (byte)(value >> (i * 8));
                mac.Initialize();
                mac.TransformBlock(page, offset + start, Page - 64 - start, null, 0);
                mac.TransformFinalBlock(numberBytes, 0, 4);
                return ConstantTimeEquals(mac.Hash, page, offset + Page - 64);
            }

            internal void Decrypt(byte[] page, int offset, int number, byte[] output)
            {
                if (!Authenticate(page, offset, number))
                    throw EngineErrors.Create("PAGE_AUTH_FAILED", "数据库第 " + number + " 页认证失败；未使用该副本", page: number);
                int start = number == 1 ? 16 : 0, length = Page - Reserve - start;
                Buffer.BlockCopy(page, offset + Page - Reserve, iv, 0, 16);
                if (aes == null) { aes = Aes.Create(); aes.Key = key; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None; }
                aes.IV = iv;
                using (var decryptor = aes.CreateDecryptor())
                {
                    int written = decryptor.TransformBlock(page, offset + start, length, output, start);
                    byte[] final = decryptor.TransformFinalBlock(Empty, 0, 0);
                    if (written != length || final.Length != 0) throw new InvalidDataException("数据库页解密长度无效");
                }
                if (start != 0) Buffer.BlockCopy(Magic, 0, output, 0, 16);
                Buffer.BlockCopy(page, offset + Page - Reserve, output, Page - Reserve, Reserve);
            }

            public void Dispose()
            {
                mac.Dispose();
                if (aes != null) aes.Dispose();
                Array.Clear(key, 0, key.Length);
            }
        }

        private sealed class SnapshotReporter
        {
            private readonly Action<Dictionary<string, object>> callback;
            private readonly Stopwatch timer = Stopwatch.StartNew();
            private string stage;
            private long start, last;
            internal string Stage { get { return stage ?? "copy_main"; } }
            internal SnapshotReporter(Action<Dictionary<string, object>> callback) { this.callback = callback; }
            internal void Report(string name, long current, long total)
            {
                if (callback == null) return;
                long now = timer.ElapsedMilliseconds;
                if (name != stage) { stage = name; start = now; last = -1000; }
                if (current != total && now - last < 200) return;
                last = now;
                long elapsed = now - start;
                callback(new Dictionary<string, object> {
                    ["phase"] = "snapshot_detail", ["snapshot_stage"] = name,
                    ["bytes_current"] = current, ["bytes_total"] = total, ["elapsed_ms"] = elapsed,
                    ["speed_mb_s"] = elapsed > 0 ? Math.Round(current / 1048576.0 * 1000 / elapsed, 1) : 0 });
            }
        }

        private sealed class PageLayout
        {
            internal int Pages, FreeLeaves;
            internal BitArray Leaves;
        }

        private static PageLayout ReadLayout(FileStream input, PageCipher cipher, long physicalPages, int committedPages, Action check)
        {
            var raw = new byte[Page]; var decoded = new byte[Page];
            input.Position = 0;
            if (!ReadInto(input, raw)) throw EngineErrors.Create("DB_SIZE_INVALID", "数据库首页不完整");
            cipher.Decrypt(raw, 0, 1, decoded); // authenticate header before trusting allocation metadata
            if ((decoded[16] << 8 | decoded[17]) != Page || decoded[20] != Reserve)
                throw EngineErrors.Create("DB_HEADER_UNSUPPORTED", "数据库页大小或保留区不受支持");
            uint declared = ReadUInt32(decoded, 28, false);
            bool validSize = declared > 0 && ReadUInt32(decoded, 24, false) == ReadUInt32(decoded, 92, false);
            long pages = committedPages > 0 ? committedPages : (validSize ? declared : physicalPages);
            if (pages <= 0 || pages > physicalPages || pages * Page > MaxDb)
                throw EngineErrors.Create("DB_SIZE_INVALID", "数据库逻辑页数超出已冻结文件范围");
            var layout = new PageLayout { Pages = (int)pages, Leaves = new BitArray((int)pages + 1) };
            uint trunk = ReadUInt32(decoded, 32, false), expected = ReadUInt32(decoded, 36, false);
            if (expected >= pages || (expected == 0) != (trunk == 0))
                throw EngineErrors.Create("FREELIST_INVALID", "空闲页链与数据库首页计数不一致");
            var seen = new BitArray((int)pages + 1); int count = 0;
            while (trunk != 0)
            {
                if (check != null) check();
                if (trunk <= 1 || trunk > pages || seen[(int)trunk])
                    throw EngineErrors.Create("FREELIST_INVALID", "空闲页链越界、重复或循环");
                seen[(int)trunk] = true; count++;
                input.Position = ((long)trunk - 1) * Page;
                if (!ReadInto(input, raw)) throw EngineErrors.Create("DB_SIZE_INVALID", "空闲页主链读取不完整");
                cipher.Decrypt(raw, 0, (int)trunk, decoded); // every trunk is authenticated too
                uint next = ReadUInt32(decoded, 0, false), leaves = ReadUInt32(decoded, 4, false);
                if (leaves > (Page - Reserve - 8) / 4 || (long)count + leaves > expected)
                    throw EngineErrors.Create("FREELIST_INVALID", "空闲页主链长度无效");
                for (int i = 0; i < leaves; i++)
                {
                    uint leaf = ReadUInt32(decoded, 8 + i * 4, false);
                    if (leaf <= 1 || leaf > pages || seen[(int)leaf])
                        throw EngineErrors.Create("FREELIST_INVALID", "空闲叶页越界或重复");
                    seen[(int)leaf] = true; layout.Leaves[(int)leaf] = true;
                    count++; layout.FreeLeaves++;
                }
                trunk = next;
            }
            if (count != expected) throw EngineErrors.Create("FREELIST_INVALID", "空闲页总数不匹配");
            WorkerDiagnostics.Event("snapshot_layout", new Dictionary<string, object> {
                ["physical_pages"] = physicalPages, ["logical_pages"] = pages,
                ["declared_pages"] = (long)declared, ["free_leaf_pages"] = layout.FreeLeaves,
                ["wal_committed_pages"] = committedPages });
            input.Position = 0;
            return layout;
        }

        internal static byte[] MagicBytes() { return Magic; }

        internal static byte[] Derive(byte[] master, byte[] salt)
        {
            using (var derive = new Rfc2898DeriveBytes(master, salt, 256000, HashAlgorithmName.SHA512))
                return derive.GetBytes(32);
        }

        internal static byte[] MacKey(byte[] key, byte[] salt)
        {
            var xored = new byte[salt.Length];
            for (int i = 0; i < salt.Length; i++) xored[i] = (byte)(salt[i] ^ 0x3a);
            using (var derive = new Rfc2898DeriveBytes(key, xored, 2, HashAlgorithmName.SHA512))
                return derive.GetBytes(32);
        }

        internal static bool Authenticated(byte[] page, int number, byte[] key, byte[] salt)
        {
            if (page == null || page.Length != Page || key == null || salt == null) return false;
            int start = number == 1 ? 16 : 0;
            byte[] mac;
            using (var hmac = new HMACSHA512(MacKey(key, salt)))
            {
                hmac.TransformBlock(page, start, page.Length - 64 - start, null, 0);
                // .NET Framework: TransformFinalBlock returns the input block; the
                // digest comes from Hash afterwards.
                hmac.TransformFinalBlock(BitConverter.GetBytes((uint)number), 0, 4);
                mac = hmac.Hash;
            }
            return ConstantTimeEquals(mac, page, page.Length - 64);
        }

        internal static byte[] DecryptPage(byte[] page, int number, byte[] key, byte[] salt)
        {
            if (page == null || page.Length != Page || key == null || salt == null)
                throw new InvalidDataException("数据库页认证失败；密钥不匹配或文件损坏，未使用该副本");
            var result = new byte[Page];
            using (var cipher = new PageCipher(key, salt)) cipher.Decrypt(page, 0, number, result);
            return result;
        }

        internal static byte[] FirstPageKey(string path, byte[] candidate, bool master)
        {
            using (var stream = OpenRead(path, Page))
            {
                var page = ReadExactly(stream, Page);
                if (page == null) return null;
                var salt = new byte[16];
                Buffer.BlockCopy(page, 0, salt, 0, 16);
                byte[] key = master ? Derive(candidate, salt) : candidate;
                return key != null && key.Length == 32 && Authenticated(page, 1, key, salt) ? key : null;
            }
        }

        internal static void Checksum(byte[] data, int offset, int length, bool little, ref uint a, ref uint b)
        {
            if ((length & 7) != 0) throw new InvalidDataException("WAL校验长度无效");
            for (int at = offset; at < offset + length; at += 8)
            {
                uint x = ReadUInt32(data, at, little), y = ReadUInt32(data, at + 4, little);
                unchecked { a = a + x + b; b = b + y + a; }
            }
        }

        internal static void ApplyWal(string walPath, string targetPath, byte[] key, byte[] salt, Action check)
        {
            using (var cipher = key == null ? null : new PageCipher(key, salt))
                ApplyWalCore(walPath, targetPath, cipher, check, new SnapshotReporter(null));
        }

        private static int ApplyWalCore(string walPath, string targetPath, PageCipher cipher, Action check, SnapshotReporter reporter, bool encryptedOutput = false)
        {
            if (!File.Exists(walPath) || new FileInfo(walPath).Length == 0) return 0;
            const int frameSize = Page + 24;
            long size = new FileInfo(walPath).Length;
            if (size > MaxDb * 2 || size < 32) throw EngineErrors.Create("WAL_SIZE_INVALID", "WAL大小无效，未使用该副本");
            using (var stream = OpenRead(walPath))
            {
                var header = ReadExactly(stream, 32);
                if (header == null) throw EngineErrors.Create("WAL_SIZE_INVALID", "WAL大小无效，未使用该副本");
                uint magic = ReadUInt32(header, 0, false), version = ReadUInt32(header, 4, false), pageSize = ReadUInt32(header, 8, false);
                if ((magic != 0x377f0682 && magic != 0x377f0683) || version != 3007000 || pageSize != Page)
                    throw EngineErrors.Create("WAL_FORMAT_UNSUPPORTED", "WAL格式不受支持，未忽略近期数据");
                bool little = magic == 0x377f0682;
                uint a = 0, b = 0;
                Checksum(header, 0, 24, little, ref a, ref b);
                if (a != ReadUInt32(header, 24, false) || b != ReadUInt32(header, 28, false))
                    throw EngineErrors.Create("WAL_HEADER_CHECKSUM", "WAL文件头校验失败");
                int lastCommit = -1, committedPages = 0;
                bool logicalEnd = false;
                var frame = new byte[frameSize];
                reporter.Report("verify_wal", 0, size);
                for (int index = 0; index < (size - 32) / frameSize; index++)
                {
                    if (check != null) check();
                    if (!ReadInto(stream, frame)) throw EngineErrors.Create("WAL_FRAME_TRUNCATED", "WAL帧未完整落盘，未使用该副本");
                    uint number = ReadUInt32(frame, 0, false), pages = ReadUInt32(frame, 4, false);
                    // WCDB keeps a preallocated WAL after checkpoint. Old-generation
                    // frames and arbitrary physical tail bytes are not current frames.
                    // SQLite defines the salt change as the logical end of this WAL.
                    if (!BytesEqual(frame, 8, header, 16, 8)) { logicalEnd = true; break; }
                    if (number == 0 || (long)number * Page > MaxDb || (long)pages * Page > MaxDb)
                        throw EngineErrors.Create("WAL_FRAME_BOUNDARY", "WAL帧边界或salt无效");
                    Checksum(frame, 0, 8, little, ref a, ref b);
                    Checksum(frame, 24, frameSize - 24, little, ref a, ref b);
                    if (a != ReadUInt32(frame, 16, false) || b != ReadUInt32(frame, 20, false))
                        throw EngineErrors.Create("WAL_FRAME_CHECKSUM", "WAL帧校验失败，请退出微信后重新准备");
                    if (cipher != null)
                    {
                        if (!cipher.Authenticate(frame, 24, (int)number))
                            throw EngineErrors.Create("WAL_PAGE_AUTH_FAILED", "WAL加密页认证失败");
                    }
                    if (pages != 0) { lastCommit = index; committedPages = (int)pages; }
                    reporter.Report("verify_wal", 32L + (index + 1L) * frameSize, size);
                }
                if (!logicalEnd && (size - 32) % frameSize != 0)
                    throw EngineErrors.Create("WAL_FRAME_TRUNCATED", "当前代际WAL帧未完整落盘，未使用该副本");
                if (lastCommit < 0) return 0;
                stream.Seek(32, SeekOrigin.Begin);
                var page = new byte[Page];
                long replayBytes = (lastCommit + 1L) * frameSize;
                reporter.Report("replay_wal", 0, replayBytes);
                using (var output = new FileStream(targetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    for (int index = 0; index <= lastCommit; index++)
                    {
                        if (check != null) check();
                        if (!ReadInto(stream, frame)) throw EngineErrors.Create("WAL_FRAME_TRUNCATED", "WAL帧未完整落盘，未使用该副本");
                        int number = (int)ReadUInt32(frame, 0, false);
                        if (cipher != null && !encryptedOutput) cipher.Decrypt(frame, 24, number, page);
                        else Buffer.BlockCopy(frame, 24, page, 0, Page);
                        output.Seek((long)(number - 1) * Page, SeekOrigin.Begin);
                        output.Write(page, 0, page.Length);
                        reporter.Report("replay_wal", (index + 1L) * frameSize, replayBytes);
                    }
                    output.SetLength((long)committedPages * Page);
                }
                return committedPages;
            }
        }

        private struct Signature { internal long Size; internal long Mtime; }

        private static Signature? Sign(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                return new Signature { Size = info.Length, Mtime = info.LastWriteTimeUtc.ToFileTime() };
            }
            catch { return null; }
        }

        internal static void Snapshot(string sourcePath, string destinationPath, byte[] key, Action check = null, Action<int, int> progress = null)
        {
            LiveSnapshot(sourcePath, destinationPath, key, check, progress);
        }

        private static void SnapshotCore(string sourcePath, string destinationPath, byte[] key, Action check, SnapshotReporter reporter, int committedPages = 0)
        {
            string walPath = sourcePath + "-wal";
            var before = new Tuple<Signature?, Signature?>(Sign(sourcePath), Sign(walPath));
            if (before.Item1 == null || before.Item1.Value.Size <= 0 || before.Item1.Value.Size > MaxDb || before.Item1.Value.Size % Page != 0)
                throw EngineErrors.Create("DB_SIZE_INVALID", "数据库大小或页大小不受支持");
            string destinationDir = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
            Directory.CreateDirectory(destinationDir);
            string temporary = Path.Combine(destinationDir, Guid.NewGuid().ToString("N") + ".snapshot.part");
            PageCipher cipher = null;
            try
            {
                bool plain;
                var salt = new byte[16];
                using (var input = OpenRead(sourcePath))
                {
                    var first = ReadExactly(input, Page);
                    if (first == null) throw new InvalidDataException("数据库页读取不完整");
                    plain = StartsWith(first, Magic);
                    Buffer.BlockCopy(first, 0, salt, 0, 16);
                    if (!plain && key == null)
                        throw EngineErrors.Create("KEY_MISSING", "该数据库尚无验证通过的密钥，请先在微信中打开对应会话并准备密钥");
                    if (!plain) cipher = new PageCipher(key, salt);
                    reporter.Report("analyse_pages", 0, before.Item1.Value.Size);
                    PageLayout layout = plain ? null : ReadLayout(input, cipher, before.Item1.Value.Size / Page, committedPages, check);
                    long pages = layout == null ? before.Item1.Value.Size / Page : layout.Pages;
                    reporter.Report("analyse_pages", pages * Page, pages * Page);
                    input.Seek(0, SeekOrigin.Begin);
                    reporter.Report("decrypt", 0, pages * Page);
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, IoBuffer, FileOptions.SequentialScan))
                    {
                        var page = new byte[Page];
                        var written = new byte[Page];
                        for (int number = 1; number <= pages; number++)
                        {
                            if (check != null) check();
                            if (layout != null && layout.Leaves[number])
                            {
                                // The authenticated header + full authenticated trunk
                                // chain explicitly mark this page as unused. SQLite
                                // never reads its contents; export a zero leaf page.
                                input.Seek(Page, SeekOrigin.Current); Array.Clear(written, 0, written.Length);
                                output.Write(written, 0, written.Length);
                            }
                            else
                            {
                                if (!ReadInto(input, page)) throw new InvalidDataException("数据库页读取不完整");
                                if (plain) output.Write(page, 0, page.Length);
                                else { cipher.Decrypt(page, 0, number, written); output.Write(written, 0, written.Length); }
                            }
                            if ((number & 255) == 0 || number == pages)
                                reporter.Report("decrypt", (long)number * Page, pages * Page);
                        }
                    }
                }
                ApplyWalCore(walPath, temporary, cipher, check, reporter);
                var after = new Tuple<Signature?, Signature?>(Sign(sourcePath), Sign(walPath));
                if (!SignatureEquals(before, after))
                    throw EngineErrors.Create("SOURCE_CHANGED", "源数据库仍在变化，请完全退出微信后重试");
                PublishSnapshot(temporary, destinationPath, check, reporter);
            }
            finally
            {
                if (cipher != null) cipher.Dispose();
                if (File.Exists(temporary)) try { File.Delete(temporary); } catch { }
            }
        }

        internal static void LiveSnapshot(string sourcePath, string destinationPath, byte[] key, Action check = null, Action<int, int> progress = null)
        {
            LiveSnapshotDetailed(sourcePath, destinationPath, key, check, progress, null);
        }

        internal static void LiveSnapshotDetailed(string sourcePath, string destinationPath, byte[] key, Action check,
            Action<int, int> progress, Action<Dictionary<string, object>> detail)
        {
            if (File.Exists(destinationPath)) throw new InvalidDataException("已有数据库副本不覆盖");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
            string walPath = sourcePath + "-wal";
            var reporter = new SnapshotReporter(detail);
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (check != null) check();
                string tempDir = Path.Combine(Path.GetDirectoryName(destinationPath), "live-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(tempDir);
                    string frozen = Path.Combine(tempDir, "source.db");
                    var before = new Tuple<Signature?, Signature?>(Sign(sourcePath), Sign(walPath));
                    CopyFrozen(sourcePath, frozen, MaxDb, check, reporter, "copy_main");
                    CopyFrozen(walPath, frozen + "-wal", MaxDb * 2, check, reporter, "copy_wal");
                    var after = new Tuple<Signature?, Signature?>(Sign(sourcePath), Sign(walPath));
                    if (!SignatureEquals(before, after))
                        throw EngineErrors.Create("SOURCE_CHANGED", "数据库正在变化，重试在线快照");
                    // The original can change now: decryption and WAL verification
                    // consume only this immutable pair, including committed frames.
                    byte[] first;
                    using (var input = OpenRead(frozen, Page)) first = ReadExactly(input, Page);
                    if (first != null && StartsWith(first, Magic))
                    {
                        // This is our stable, private copy. A plain SQLite database
                        // needs WAL replay + quick_check, not a second full copy.
                        long size = new FileInfo(frozen).Length;
                        if (size <= 0 || size > MaxDb || size % Page != 0)
                            throw EngineErrors.Create("DB_SIZE_INVALID", "数据库大小或页大小不受支持");
                        ApplyWalCore(frozen + "-wal", frozen, null, check, reporter);
                        PublishSnapshot(frozen, destinationPath, check, reporter);
                    }
                    else
                    {
                        string frozenWal = frozen + "-wal";
                        int committedPages = 0;
                        if (key != null && first != null && File.Exists(frozenWal))
                        {
                            // Resolve the committed encrypted view before decrypting
                            // base pages. SQLite reads the last committed WAL version;
                            // an obsolete base page cannot block a valid replacement.
                            var salt = new byte[16]; Buffer.BlockCopy(first, 0, salt, 0, 16);
                            using (var cipher = new PageCipher(key, salt))
                                committedPages = ApplyWalCore(frozenWal, frozen, cipher, check, reporter, true);
                            // Retain the original database salt: a WAL page-one prefix
                            // is outside that frame's authenticated page payload.
                            using (var output = new FileStream(frozen, FileMode.Open, FileAccess.Write, FileShare.None))
                                output.Write(salt, 0, salt.Length);
                            File.Delete(frozenWal); // private, already validated frozen file only
                        }
                        SnapshotCore(frozen, destinationPath, key, check, reporter, committedPages);
                    }
                    return;
                }
                catch (Exception exc) when (exc is InvalidDataException || exc is IOException || exc is UnauthorizedAccessException)
                {
                    last = exc;
                    WorkerDiagnostics.Failure(exc, "snapshot_attempt", new Dictionary<string, object> { ["attempt"] = attempt + 1, ["snapshot_stage"] = reporter.Stage });
                    if (exc is InvalidDataException && exc.Message.StartsWith("已有数据库副本不覆盖"))
                        throw;
                    if (attempt == 2)
                        throw EngineErrors.Create("SNAPSHOT_RETRIES_EXHAUSTED", "在线快照三次未通过：" + exc.Message, exc);
                    if (progress != null) progress(attempt + 1, 3);
                    if (check != null) check();
                    System.Threading.Thread.Sleep(200);
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
            throw EngineErrors.Create("SNAPSHOT_RETRIES_EXHAUSTED", "在线快照三次未通过：" + (last == null ? "" : last.Message), last);
        }

        private static void PublishSnapshot(string temporary, string destination, Action check, SnapshotReporter reporter)
        {
            if (check != null) check();
            long size = new FileInfo(temporary).Length;
            reporter.Report("verify_db", 0, size);
            using (var connection = new NativeSqliteConnection()) connection.OpenReadOnly(temporary);
            reporter.Report("verify_db", size, size);
            if (check != null) check();
            if (File.Exists(destination)) throw new InvalidDataException("已有数据库副本不覆盖，请重新开始一个会话");
            File.Move(temporary, destination);
        }

        private static void CopyFrozen(string source, string target, long limit, Action check, SnapshotReporter reporter, string stage)
        {
            var info = Sign(source);
            if (info == null)
            {
                if (source.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("源数据库不存在");
                return;
            }
            if (info.Value.Size > limit) throw new InvalidDataException("数据库或WAL超过大小限制");
            reporter.Report(stage, 0, info.Value.Size);
            using (var reader = OpenRead(source))
            using (var writer = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, IoBuffer, FileOptions.SequentialScan))
            {
                long remaining = info.Value.Size;
                var buffer = new byte[1024 * 1024];
                while (remaining > 0)
                {
                    if (check != null) check();
                    int read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read == 0) throw EngineErrors.Create("COPY_INCOMPLETE", "数据库正在变化，快照未完整复制");
                    writer.Write(buffer, 0, read);
                    remaining -= read;
                    reporter.Report(stage, info.Value.Size - remaining, info.Value.Size);
                }
            }
        }

        private static FileStream OpenRead(string path, int bufferSize = IoBuffer)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, FileOptions.SequentialScan);
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            var buffer = new byte[count];
            return ReadInto(stream, buffer) ? buffer : null;
        }

        private static bool ReadInto(Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0) return false;
                total += read;
            }
            return true;
        }

        private static uint ReadUInt32(byte[] data, int offset, bool little)
        {
            return little
                ? (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24)
                : (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
        }

        private static bool ConstantTimeEquals(byte[] expected, byte[] actual, int actualOffset)
        {
            if (expected.Length != 64) return false;
            int diff = 0;
            for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ actual[actualOffset + i];
            return diff == 0;
        }

        private static bool BytesEqual(byte[] a, int aOffset, byte[] b, int bOffset, int count)
        {
            for (int i = 0; i < count; i++) if (a[aOffset + i] != b[bOffset + i]) return false;
            return true;
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) if (data[i] != prefix[i]) return false;
            return true;
        }

        private static bool SignatureEquals(Tuple<Signature?, Signature?> a, Tuple<Signature?, Signature?> b)
        {
            return a.Item1.Equals(b.Item1) && a.Item2.Equals(b.Item2);
        }
    }

    /// <summary>Minimal read-only winsqlite3 access (Windows 10+ built-in SQLite) for quick_check.</summary>
    internal sealed class NativeSqliteConnection : IDisposable
    {
        private const string Dll = "winsqlite3";
        private IntPtr handle;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
        private static extern int Open(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
        private static extern int Prepare(IntPtr db, string sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
        private static extern int Step(IntPtr statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
        private static extern IntPtr ColumnText(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
        private static extern int Finalize(IntPtr statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close")]
        private static extern int Close(IntPtr db);

        internal void OpenReadOnly(string path)
        {
            const int SQLITE_OPEN_READONLY = 0x00000001;
            const int SQLITE_OPEN_URI = 0x00000040;
            string uri = SqliteConnection.FileUri(path) + "?mode=ro&immutable=1";
            // winsqlite3 expects UTF-8 filenames; pass raw bytes so Chinese/space
            // paths survive without ANSI codepage mangling.
            int openCode = Open(Encoding.UTF8.GetBytes(uri + "\0"), out handle, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, IntPtr.Zero);
            if (openCode != 0)
            {
                Dispose();
                throw EngineErrors.Create("SQLITE_OPEN_FAILED", "数据库副本完整性检查失败（无法打开）", sqlite: openCode);
            }
            try
            {
                IntPtr statement;
                int prepareCode = Prepare(handle, "PRAGMA quick_check", -1, out statement, IntPtr.Zero);
                if (prepareCode != 0)
                    throw EngineErrors.Create("SQLITE_QUICK_CHECK_PREPARE", "数据库副本完整性检查失败（无法查询）", sqlite: prepareCode);
                try
                {
                    int stepCode = Step(statement);
                    if (stepCode != 100) throw EngineErrors.Create("SQLITE_QUICK_CHECK_READ", "数据库副本完整性检查失败", sqlite: stepCode);
                    string value = Marshal.PtrToStringAnsi(ColumnText(statement, 0));
                    if (value != "ok") throw EngineErrors.Create("SQLITE_QUICK_CHECK_REJECTED", "数据库副本完整性检查失败");
                }
                finally { Finalize(statement); }
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero) { Close(handle); handle = IntPtr.Zero; }
        }
    }
}
