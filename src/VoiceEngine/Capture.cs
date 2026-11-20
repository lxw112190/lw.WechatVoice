using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using WechatVoice;

namespace VoiceEngine
{
    /// <summary>
    /// Read-only, selected-PID WeChat 4.x key matching for one local account,
    /// ported 1:1 from worker/capture.py. Synthetic-process tests
    /// (tests/CaptureCheck.cs) exercise the full Win32 path without WeChat.
    /// </summary>
    internal static class Capture
    {
        internal const string TargetVersion = "4.x";
        internal const string VerifiedVersion = "4.0.5.16";

        private static readonly Regex Pattern = new Regex(@"[xX]'([0-9a-fA-F]{64,192})'", RegexOptions.Compiled);
        private static readonly Regex WidePattern = new Regex(@"[xX]\x00'\x00((?:[0-9a-fA-F]\x00){64,192})'\x00", RegexOptions.Compiled);
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        internal sealed class ScanResult
        {
            internal string Root;
            internal Dictionary<string, string> Found = new Dictionary<string, string>();
            internal Dictionary<string, object> Report = new Dictionary<string, object>();
        }

        internal static Tuple<string, List<string>> Databases(string folder)
        { return DatabaseSet(folder, false); }

        internal static Tuple<string, List<string>> DatabasesForFavorites(string folder)
        { return DatabaseSet(folder, true); }

        private static Tuple<string, List<string>> DatabaseSet(string folder, bool favorites)
        {
            string root = FileSystemPath.FullPath(folder);
            if (Directory.Exists(FileSystemPath.ForIo(Path.Combine(root, "db_storage"))))
                root = FileSystemPath.FullPath(Path.Combine(root, "db_storage"));
            else if (FileSystemPath.GetDirectories(root).Any(child => Directory.Exists(FileSystemPath.ForIo(Path.Combine(child, "db_storage")))))
                throw new InvalidDataException("此目录含多个/下一级账号，请先选择一个具体账号，未混合读取");
            var files = new List<string>();
            foreach (string path in FileSystemPath.GetFiles(root, "*.db", SearchOption.AllDirectories))
            {
                string full = FileSystemPath.FullPath(path);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("源目录包含越界链接，未读取");
                string name = Path.GetFileName(path);
                bool wanted = name.Equals("contact.db", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(name, @"^(?:media|message)_\d+\.db$", RegexOptions.IgnoreCase);
                if (favorites && name.Equals("favorite.db", StringComparison.OrdinalIgnoreCase)) wanted = true;
                if (!wanted) continue;
                long size = new FileInfo(FileSystemPath.ForIo(path)).Length;
                if (size <= 0 || size > 32L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("数据库大小超出支持范围");
                files.Add(full);
                if (files.Count > 512)
                    throw new InvalidDataException("数据库数量超过512个，请只选择一个账号");
            }
            if (favorites && !files.Any(path => Path.GetFileName(path).Equals("favorite.db", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("没有找到收藏库；请在电脑微信打开收藏，再选择账号目录或 db_storage");
            if (!favorites && !files.Any(path => Path.GetFileName(path).StartsWith("media_", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("没有找到media_*.db；请选择一个微信账号目录或db_storage目录");
            if (files.Sum(path => new FileInfo(FileSystemPath.ForIo(path)).Length) > 64L * 1024 * 1024 * 1024)
                throw new InvalidDataException("所选数据库超过64GiB，首版暂不支持");
            files.Sort(StringComparer.OrdinalIgnoreCase);
            return Tuple.Create(root, files);
        }

        internal static void ValidateProcessIdentity(string name, string version)
        {
            if (name == null || !(name.Equals("weixin.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("wechat.exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("所选进程不是微信，未读取其内存");
            if (version == null || !Regex.IsMatch(version, @"^4\.\d+\.\d+\.\d+$"))
                throw new InvalidDataException("支持 Windows 微信4.x；检测到 " + version + "，未读取其内存");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FixedFileInfo
        {
            internal uint Signature, StrucVersion, FileVersionMS, FileVersionLS, ProductVersionMS, ProductVersionLS;
            internal uint FileFlagsMask, FileFlags, FileOS, FileType, FileSubtype, FileDateMS, FileDateLS;
        }

        internal static Dictionary<string, object> ProcessDetails(int pid)
        {
            IntPtr handle = NativeMethods.OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero)
                throw new InvalidDataException("无法打开所选微信进程，Win32=" + Marshal.GetLastWin32Error());
            try
            {
                var buffer = new StringBuilder(32768);
                uint length = 32768;
                if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref length))
                    throw new InvalidDataException("无法读取进程路径，Win32=" + Marshal.GetLastWin32Error());
                string executable = buffer.ToString();
                uint ignored;
                uint size = NativeMethods.GetFileVersionInfoSize(executable, out ignored);
                if (size == 0) throw new InvalidDataException("无法读取微信文件版本");
                var data = new byte[size];
                if (!NativeMethods.GetFileVersionInfo(executable, 0, size, data))
                    throw new InvalidDataException("无法读取微信版本信息，Win32=" + Marshal.GetLastWin32Error());
                IntPtr pointer;
                uint infoLength;
                if (!NativeMethods.VerQueryValue(data, "\\", out pointer, out infoLength) || infoLength < 52 || pointer == IntPtr.Zero)
                    throw new InvalidDataException("微信版本信息不完整");
                var info = (FixedFileInfo)Marshal.PtrToStructure(pointer, typeof(FixedFileInfo));
                if (info.Signature != 0xFEEF04BD)
                    throw new InvalidDataException("微信文件版本结构无效，未读取其内存");
                string version = ((info.FileVersionMS >> 16) & 0xffff) + "." + (info.FileVersionMS & 0xffff) + "." +
                    ((info.FileVersionLS >> 16) & 0xffff) + "." + (info.FileVersionLS & 0xffff);
                ValidateProcessIdentity(Path.GetFileName(executable), version);
                var result = new Dictionary<string, object> { ["pid"] = pid, ["version"] = version, ["executable"] = executable };
                return result;
            }
            finally { NativeMethods.CloseHandle(handle); }
        }

        internal static List<KeyValuePair<byte[], byte[]>> Candidates(byte[] block)
        {
            string text = Latin1.GetString(block);
            var result = new List<KeyValuePair<byte[], byte[]>>();
            foreach (var pair in new[] { new KeyValuePair<Regex, bool>(Pattern, false), new KeyValuePair<Regex, bool>(WidePattern, true) })
            {
                foreach (Match match in pair.Key.Matches(text))
                {
                    string value = pair.Value ? match.Groups[1].Value.Replace("\0", "") : match.Groups[1].Value;
                    if (value.Length % 2 != 0) continue;
                    var offsets = new SortedSet<int> { 0 };
                    if (value.Length > 96)
                    {
                        for (int offset = 0; offset < value.Length - 63; offset += 32) offsets.Add(offset);
                        offsets.Add(value.Length - 64);
                    }
                    foreach (int offset in offsets)
                    {
                        byte[] key = FromHex(value.Substring(offset, 64));
                        byte[] salt = offset + 96 <= value.Length ? FromHex(value.Substring(offset + 64, 32)) : null;
                        result.Add(new KeyValuePair<byte[], byte[]>(key, salt));
                    }
                }
            }
            return result;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            internal IntPtr BaseAddress, AllocationBase;
            internal uint AllocationProtect, Alignment;
            internal UIntPtr RegionSize;
            internal uint State, Protect, Type;
        }

        internal static ScanResult Scan(int pid, string folder, Action check, Action<Dictionary<string, object>> progress)
        { return ScanCore(pid, folder, check, progress, false); }

        internal static ScanResult ScanFavorites(int pid, string folder, Action check, Action<Dictionary<string, object>> progress)
        { return ScanCore(pid, folder, check, progress, true); }

        private static ScanResult ScanCore(int pid, string folder, Action check, Action<Dictionary<string, object>> progress, bool favorites)
        {
            var details = ProcessDetails(pid);
            var databases = favorites ? DatabasesForFavorites(folder) : Databases(folder);
            string root = databases.Item1;
            var files = databases.Item2;
            var wanted = new Dictionary<string, List<string>>();
            foreach (string path in files)
            {
                var first = new byte[CryptoDb.Page];
                using (var stream = new FileStream(FileSystemPath.ForIo(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int read = 0;
                    while (read < first.Length) { int n = stream.Read(first, read, first.Length - read); if (n == 0) break; read += n; }
                }
                if (first.Length >= CryptoDb.Page && !StartsWith(first, CryptoDb.MagicBytes()))
                {
                    string salt = ToHex(first, 0, 16);
                    if (!wanted.ContainsKey(salt)) wanted[salt] = new List<string>();
                    wanted[salt].Add(path);
                }
            }
            if (wanted.Count == 0)
            {
                var empty = new ScanResult { Root = root };
                empty.Report["matched"] = 0; empty.Report["encrypted"] = 0; empty.Report["version"] = details["version"];
                return empty;
            }
            IntPtr handle = NativeMethods.OpenProcess(0x0400 | 0x0010, false, pid);
            if (handle == IntPtr.Zero)
                throw new InvalidDataException("无法只读打开选定微信进程，Win32=" + Marshal.GetLastWin32Error() + "；请确认微信仍存在，并以相同用户/权限运行");
            var pages = new Dictionary<string, List<KeyValuePair<string, byte[]>>>();
            foreach (var entry in wanted)
            {
                pages[entry.Key] = new List<KeyValuePair<string, byte[]>>();
                foreach (string path in entry.Value)
                {
                    var page = new byte[CryptoDb.Page];
                    using (var stream = new FileStream(FileSystemPath.ForIo(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        int read = 0;
                        while (read < page.Length) { int n = stream.Read(page, read, page.Length - read); if (n == 0) break; read += n; }
                    }
                    pages[entry.Key].Add(new KeyValuePair<string, byte[]>(FileSystemPath.FullPath(path).Substring(root.Length + 1), page));
                }
            }
            var started = DateTime.UtcNow;
            long scanned = 0;
            bool limitReached = false;
            int readCalls = 0, readFailures = 0, partialReads = 0, lastReadError = 0, regionCount = 0;
            Action limitedCheck = () =>
            {
                if (check != null) check();
                if ((DateTime.UtcNow - started).TotalSeconds >= 120 || scanned >= 2L * 1024 * 1024 * 1024)
                    throw new LimitReachedException();
            };
            var matcher = new KeyReader.Matcher(pages, limitedCheck);
            try
            {
                var processName = new StringBuilder(32768);
                uint nameLength = 32768;
                if (!NativeMethods.QueryFullProcessImageName(handle, 0, processName, ref nameLength) ||
                    !processName.ToString().Equals(Convert.ToString(details["executable"]), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("微信进程在准备期间改变，未读取内存，请刷新进程");
                var regions = new List<KeyValuePair<int, KeyValuePair<long, long>>>();
                long address = 0;
                while (address < 0x7fffffffffffL)
                {
                    limitedCheck();
                    var region = new MemoryBasicInformation();
                    if (NativeMethods.VirtualQueryEx(handle, new IntPtr(address), out region, (IntPtr)Marshal.SizeOf(typeof(MemoryBasicInformation))) == IntPtr.Zero)
                        break;
                    long baseAddress = region.BaseAddress.ToInt64();
                    ulong size = region.RegionSize.ToUInt64();
                    if (size == 0 || baseAddress + (long)size <= address) break;
                    address = baseAddress + (long)size;
                    if (region.State != 0x1000 || (region.Protect & (0x100 | 0x01)) != 0 || (region.Protect & (0x02 | 0x04 | 0x08 | 0x40 | 0x80)) == 0)
                        continue;
                    int priority = region.Type == 0x20000 && ((region.Protect & 0xff) == 4 || (region.Protect & 0xff) == 8 ||
                        (region.Protect & 0xff) == 64 || (region.Protect & 0xff) == 128) ? 0 : 1;
                    regions.Add(new KeyValuePair<int, KeyValuePair<long, long>>(priority, new KeyValuePair<long, long>(baseAddress, (long)size)));
                    if (regions.Count > 65536) throw new InvalidDataException("进程内存区域数量超限");
                }
                regionCount = regions.Count;
                var ordered = regions.OrderBy(r => r.Key).Select(r => r.Value).ToList();
                DateTime lastProgress = DateTime.UtcNow;
                Func<long, int, byte[]> readMemory = (pointer, count) =>
                {
                    limitedCheck();
                    if (pointer < 0 || pointer >= 0x800000000000L || count <= 0 || count > 1024 * 1024) return new byte[0];
                    var buffer = new byte[count];
                    UIntPtr amount;
                    bool ok = NativeMethods.ReadProcessMemory(handle, new IntPtr(pointer), buffer, (IntPtr)count, out amount);
                    readCalls++;
                    if (!ok)
                    {
                        lastReadError = Marshal.GetLastWin32Error();
                        if (amount.ToUInt64() != 0) partialReads++; else readFailures++;
                    }
                    scanned += (long)amount.ToUInt64();
                    if ((DateTime.UtcNow - lastProgress).TotalSeconds > 0.5 && progress != null)
                    {
                        progress(BuildProgress("keys", matcher, wanted, scanned, regionCount, readCalls, readFailures, partialReads, lastReadError));
                        lastProgress = DateTime.UtcNow;
                    }
                    var result = new byte[amount.ToUInt64()];
                    Buffer.BlockCopy(buffer, 0, result, 0, result.Length);
                    return result;
                };
                KeyReader.Extract(ordered, readMemory, matcher, Candidates, limitedCheck);
            }
            catch (LimitReachedException) { limitReached = true; }
            finally { NativeMethods.CloseHandle(handle); }
            var report = BuildProgress(null, matcher, wanted, scanned, regionCount, readCalls, readFailures, partialReads, lastReadError);
            report["limit_reached"] = limitReached;
            report["version"] = details["version"];
            report["matched"] = matcher.found.Count;
            report["encrypted"] = wanted.Values.Sum(list => list.Count);
            var scan = new ScanResult { Root = root };
            foreach (var entry in matcher.found) scan.Found[entry.Key] = entry.Value;
            scan.Report = report;
            return scan;
        }

        private static Dictionary<string, object> BuildProgress(string phase, KeyReader.Matcher matcher, Dictionary<string, List<string>> wanted,
            long scanned, int regionCount, int readCalls, int readFailures, int partialReads, int lastReadError)
        {
            var value = new Dictionary<string, object>();
            if (phase != null) value["phase"] = phase;
            value["matched"] = matcher.found.Count;
            value["encrypted"] = wanted.Values.Sum(list => list.Count);
            value["scanned_mb"] = scanned / 1048576;
            value["candidates"] = matcher.candidates;
            value["config_names"] = matcher.configNames;
            value["config_objects"] = matcher.configObjects;
            value["master_checks"] = matcher.masterChecks;
            value["read_calls"] = readCalls;
            value["read_failures"] = readFailures;
            value["partial_reads"] = partialReads;
            value["last_read_error"] = lastReadError;
            value["region_count"] = regionCount;
            return value;
        }

        // ---------------------------------------------------------------------
        // DPAPI vault, bound to the selected account root.
        // ---------------------------------------------------------------------

        internal static void SaveVault(string path, string root, Dictionary<string, string> keys)
        {
            var serializer = new JavaScriptSerializer();
            var payload = new Dictionary<string, object> { ["schema"] = 1, ["root"] = FileSystemPath.FullPath(root), ["keys"] = keys };
            byte[] protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(serializer.Serialize(payload)), null, DataProtectionScope.CurrentUser);
            string full = FileSystemPath.FullPath(path);
            Directory.CreateDirectory(FileSystemPath.ForIo(Path.GetDirectoryName(full)));
            string temporary = full + ".part";
            try
            {
                File.WriteAllBytes(FileSystemPath.ForIo(temporary), protectedBytes);
                if (File.Exists(FileSystemPath.ForIo(full))) File.Delete(FileSystemPath.ForIo(full));
                File.Move(FileSystemPath.ForIo(temporary), FileSystemPath.ForIo(full));
            }
            finally { if (File.Exists(FileSystemPath.ForIo(temporary))) try { File.Delete(FileSystemPath.ForIo(temporary)); } catch { } }
        }

        internal static Dictionary<string, string> LoadVault(string path, string root)
        {
            string full = FileSystemPath.FullPath(path);
            if (!File.Exists(FileSystemPath.ForIo(full))) return new Dictionary<string, string>();
            if (new FileInfo(FileSystemPath.ForIo(full)).Length > 256 * 1024)
                throw new InvalidDataException("本机密钥文件过大");
            byte[] unprotected;
            try { unprotected = ProtectedData.Unprotect(File.ReadAllBytes(FileSystemPath.ForIo(full)), null, DataProtectionScope.CurrentUser); }
            catch { throw new InvalidDataException("本机密钥文件无法解密"); }
            var serializer = new JavaScriptSerializer();
            Dictionary<string, object> value;
            try { value = serializer.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(unprotected)); }
            catch { throw new InvalidDataException("本机密钥文件格式无效"); }
            if (value == null || !value.ContainsKey("schema") || Convert.ToInt32(value["schema"]) != 1 ||
                !value.ContainsKey("root") || Convert.ToString(value["root"]) != FileSystemPath.FullPath(root))
                throw new InvalidDataException("密钥文件与当前所选账号不一致");
            var keys = value.ContainsKey("keys") ? value["keys"] as Dictionary<string, object> : null;
            if (keys == null || keys.Count > 512 || keys.Values.Any(v => !Regex.IsMatch(Convert.ToString(v), "^[0-9a-f]{64}$")))
                throw new InvalidDataException("本机密钥文件格式无效");
            var result = new Dictionary<string, string>();
            foreach (var entry in keys) result[entry.Key] = Convert.ToString(entry.Value);
            return result;
        }

        private static byte[] FromHex(string value)
        {
            var result = new byte[value.Length / 2];
            for (int i = 0; i < result.Length; i++) result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            return result;
        }

        private static string ToHex(byte[] value, int offset, int count)
        {
            var chars = new char[count * 2];
            for (int i = 0; i < count; i++)
            {
                int high = value[offset + i] >> 4, low = value[offset + i] & 15;
                chars[i * 2] = (char)(high < 10 ? '0' + high : 'a' + high - 10);
                chars[i * 2 + 1] = (char)(low < 10 ? '0' + low : 'a' + low - 10);
            }
            return new string(chars);
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++) if (data[i] != prefix[i]) return false;
            return true;
        }

        private sealed class LimitReachedException : Exception { }

        private static class NativeMethods
        {
            internal const string Kernel = "kernel32";
            internal const string Version = "version";

            [DllImport(Kernel, SetLastError = true)]
            internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
            [DllImport(Kernel, SetLastError = true)]
            internal static extern bool CloseHandle(IntPtr handle);
            [DllImport(Kernel, SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder path, ref uint length);
            [DllImport(Kernel, SetLastError = true)]
            internal static extern IntPtr VirtualQueryEx(IntPtr handle, IntPtr address, out MemoryBasicInformation info, IntPtr length);
            [DllImport(Kernel, SetLastError = true)]
            internal static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, IntPtr count, out UIntPtr read);
            [DllImport(Version, SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern uint GetFileVersionInfoSize(string path, out uint handle);
            [DllImport(Version, SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern bool GetFileVersionInfo(string path, uint handle, uint length, byte[] data);
            [DllImport(Version, SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern bool VerQueryValue(byte[] data, string subBlock, out IntPtr pointer, out uint length);
        }
    }
}
