using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WechatVoice
{
    internal static class FileSystemPath
    {
        private const string ExtendedPrefix = @"\\?\";
        private const string UncPrefix = @"\\?\UNC\";

        internal static string Ordinary(string path)
        {
            if (path == null) return null;
            if (path.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(UncPrefix.Length);
            return path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) ? path.Substring(ExtendedPrefix.Length) : path;
        }

        internal static string FullPath(string path) => Path.GetFullPath(Ordinary(path));

        // The prefix is for I/O only. Logical paths stay unprefixed in IPC and manifests.
        internal static string ForIo(string path)
        {
            if (path == null || path.Length < 240 || path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)) return path;
            string full = Path.GetFullPath(path);
            return full.StartsWith(@"\\", StringComparison.Ordinal)
                ? UncPrefix + full.Substring(2) : ExtendedPrefix + full;
        }

        internal static string[] GetFiles(string path, string pattern, SearchOption options = SearchOption.TopDirectoryOnly) =>
            EnumerateFiles(path, pattern, options).ToArray();

        internal static string[] GetFiles(string path) => GetFiles(path, "*");

        internal static string[] GetDirectories(string path) => EnumerateDirectories(path).ToArray();

        internal static IEnumerable<string> EnumerateFiles(string path, string pattern,
            SearchOption options = SearchOption.TopDirectoryOnly) => Directory.EnumerateFiles(ForIo(path), pattern, options).Select(Ordinary);

        internal static IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(ForIo(path)).Select(Ordinary);

        internal static IEnumerable<string> EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(ForIo(path)).Select(Ordinary);
    }
}
