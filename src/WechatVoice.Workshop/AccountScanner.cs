using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WechatVoice.Workshop
{
    internal sealed class AccountInfo
    {
        internal string Path;      // account folder that contains db_storage
        internal string DirName;   // raw folder name, e.g. wxid_x_1a2b
        internal string Wxid;      // folder name without the trailing 4-hex suffix
        internal DateTime Recent;  // newest file mtime inside db_storage (MinValue = unknown)
    }

    /// <summary>
    /// Moect-style account discovery: locate the WeChat 4.x data root (xwechat_files)
    /// from WeChat's own config/registry entries, then list every account folder that
    /// contains db_storage, most recently used first. Only folder names are read; no
    /// keys, databases or chats. Manual folder browsing remains available for
    /// locations the scan cannot see.
    /// </summary>
    internal static class AccountScanner
    {
        private static readonly Regex WxidSuffix = new Regex(@"^(?<wxid>.+?)_(?<suffix>[0-9a-fA-F]{4})$", RegexOptions.Compiled);
        private const int MtimeFileCap = 20000;

        internal static string DocumentsDirectory()
        {
            foreach (string key in new[] {
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders" })
            {
                try
                {
                    using (var hive = Registry.CurrentUser.OpenSubKey(key))
                    {
                        string value = hive == null ? null : hive.GetValue("Personal") as string;
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            string expanded = Environment.ExpandEnvironmentVariables(value).Trim();
                            if (Directory.Exists(expanded)) return Path.GetFullPath(expanded);
                        }
                    }
                }
                catch { }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        }

        /// <summary>Folders where WeChat 4.x stores data; each may contain an xwechat_files subfolder.</summary>
        internal static List<string> SaveRoots()
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = path =>
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                path = Environment.ExpandEnvironmentVariables(path).Trim();
                if (!Directory.Exists(path)) return;
                path = Path.GetFullPath(path);
                if (seen.Add(path)) roots.Add(path);
            };
            // %APPDATA%\Tencent\xwechat\config\*.ini — "MyDocument:" or an absolute path
            string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tencent", "xwechat", "config");
            if (Directory.Exists(configDir))
                foreach (string ini in SafeFiles(configDir, "*.ini"))
                {
                    string raw = null;
                    try
                    {
                        var buffer = new byte[1024];
                        using (var stream = new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            int read = stream.Read(buffer, 0, buffer.Length);
                            raw = Encoding.UTF8.GetString(buffer, 0, read);
                        }
                    }
                    catch { }
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    add(ResolveSaveValue(raw));
                }
            foreach (string hive in new[] { @"Software\Tencent\Weixin", @"Software\Tencent\WeChat" })
            {
                string value = null;
                try { using (var key = Registry.CurrentUser.OpenSubKey(hive)) if (key != null) value = key.GetValue("FileSavePath") as string; } catch { }
                add(ResolveSaveValue(value));
            }
            add(DocumentsDirectory());
            // Shallow drive fallback for custom locations without config entries
            foreach (string drive in DriveLetters())
                foreach (string baseDir in new[] { drive,
                    Path.Combine(drive, "Users"), Path.Combine(drive, "Documents"),
                    Path.Combine(drive, "WeChat"), Path.Combine(drive, "Tencent"), Path.Combine(drive, "ProgramData") })
                {
                    if (!Directory.Exists(baseDir)) continue;
                    add(Path.Combine(baseDir, "xwechat_files"));
                    foreach (string child in SafeDirectories(baseDir)) add(Path.Combine(child, "xwechat_files"));
                }
            return roots;
        }

        internal static List<string> XwechatRoots()
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string baseDir in SaveRoots())
            {
                string root = Path.GetFileName(baseDir).Equals("xwechat_files", StringComparison.OrdinalIgnoreCase)
                    ? baseDir : Path.Combine(baseDir, "xwechat_files");
                if (!Directory.Exists(root)) continue;
                root = Path.GetFullPath(root);
                if (seen.Add(root)) roots.Add(root);
            }
            return roots;
        }

        internal static List<AccountInfo> ListAccounts() { return ListAccountsFromRoots(XwechatRoots()); }

        internal static List<AccountInfo> ListAccountsFromRoots(IEnumerable<string> roots)
        {
            var accounts = new List<AccountInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roots == null) return accounts;
            foreach (string root in roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                foreach (string dir in SafeDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (name.Equals("all_users", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Directory.Exists(Path.Combine(dir, "db_storage"))) continue;
                    string full = Path.GetFullPath(dir);
                    if (!seen.Add(full)) continue;
                    accounts.Add(new AccountInfo { Path = full, DirName = name, Wxid = SplitWxid(name), Recent = RecentFileTime(Path.Combine(dir, "db_storage")) });
                }
            }
            accounts.Sort(delegate (AccountInfo a, AccountInfo b)
            {
                int compare = b.Recent.CompareTo(a.Recent);
                return compare != 0 ? compare : StringComparer.OrdinalIgnoreCase.Compare(a.DirName, b.DirName);
            });
            return accounts;
        }

        /// <summary>Account folder or its db_storage, for a preferred path the scan may not have covered.</summary>
        internal static AccountInfo FromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
            string full = Path.GetFullPath(path.Trim());
            string root = Path.GetFileName(full).Equals("db_storage", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(full) : full;
            if (root == null || !Directory.Exists(Path.Combine(root, "db_storage"))) return null;
            string name = Path.GetFileName(root);
            return new AccountInfo { Path = Path.GetFullPath(root), DirName = name, Wxid = SplitWxid(name), Recent = RecentFileTime(Path.Combine(root, "db_storage")) };
        }

        internal static string SplitWxid(string dirName)
        {
            Match match = WxidSuffix.Match(dirName ?? "");
            return match.Success ? match.Groups["wxid"].Value : dirName;
        }

        private static string ResolveSaveValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = Environment.ExpandEnvironmentVariables(value).Trim();
            if (value.TrimEnd('\\', '/').Equals("MyDocument:", StringComparison.OrdinalIgnoreCase)) return DocumentsDirectory();
            return Directory.Exists(value) ? value : null;
        }

        private static DateTime RecentFileTime(string storage)
        {
            DateTime latest = DateTime.MinValue;
            int visited = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(storage, "*", SearchOption.AllDirectories))
                {
                    if (++visited > MtimeFileCap) break;
                    try { DateTime stamp = File.GetLastWriteTime(file); if (stamp > latest) latest = stamp; } catch { }
                }
            }
            catch { }
            return latest;
        }

        private static IEnumerable<string> DriveLetters()
        {
            DriveInfo[] drives = null;
            try { drives = DriveInfo.GetDrives(); } catch { }
            if (drives == null) yield break;
            foreach (DriveInfo drive in drives)
                if ((drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable) && drive.IsReady)
                    yield return drive.RootDirectory.FullName;
        }

        internal static List<string> SafeDirectories(string path)
        {
            var found = new List<string>();
            try { foreach (string dir in Directory.EnumerateDirectories(path)) found.Add(dir); }
            catch { }
            return found;
        }

        private static List<string> SafeFiles(string path, string pattern)
        {
            var found = new List<string>();
            try { foreach (string file in Directory.EnumerateFiles(path, pattern)) found.Add(file); }
            catch { }
            return found;
        }
    }
}
