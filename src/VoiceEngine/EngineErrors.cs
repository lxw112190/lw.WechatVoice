using System;
using System.Collections.Generic;
using System.IO;

namespace VoiceEngine
{
    internal static class EngineErrors
    {
        private static readonly HashSet<string> Codes = new HashSet<string> {
            "PAGE_AUTH_FAILED", "KEY_MISSING", "DB_SIZE_INVALID", "SOURCE_CHANGED", "COPY_INCOMPLETE",
            "WAL_SIZE_INVALID", "WAL_FORMAT_UNSUPPORTED", "WAL_HEADER_CHECKSUM", "WAL_FRAME_BOUNDARY",
            "WAL_FRAME_CHECKSUM", "WAL_PAGE_AUTH_FAILED", "WAL_FRAME_TRUNCATED", "SNAPSHOT_RETRIES_EXHAUSTED",
            "SQLITE_OPEN_FAILED", "SQLITE_QUICK_CHECK_PREPARE", "SQLITE_QUICK_CHECK_READ", "SQLITE_QUICK_CHECK_REJECTED",
            "MEDIA_SNAPSHOT_MISSING", "VOICE_SCHEMA_UNSUPPORTED", "FREELIST_INVALID", "DB_HEADER_UNSUPPORTED" };

        internal static InvalidDataException Create(string code, string message, Exception inner = null, int page = 0, int sqlite = -1)
        {
            if (!Codes.Contains(code)) throw new ArgumentException("Unknown engine error code");
            var error = new InvalidDataException(message, inner);
            error.Data["error_code"] = code;
            if (page > 0) error.Data["page_number"] = page;
            if (sqlite >= 0) error.Data["sqlite_code"] = sqlite;
            return error;
        }

        internal static string Code(Exception error)
        {
            string result = "";
            for (int i = 0; error != null && i < 8; i++, error = error.InnerException)
            {
                var value = error.Data["error_code"] as string;
                if (value != null && Codes.Contains(value)) result = value;
            }
            return result;
        }

        internal static void Metadata(Exception error, Dictionary<string, object> target)
        {
            var code = error.Data["error_code"] as string;
            if (code != null && Codes.Contains(code)) target["error_code"] = code;
            foreach (string field in new[] { "page_number", "sqlite_code" })
                if (error.Data[field] is int && (int)error.Data[field] >= 0) target[field] = error.Data[field];
        }
    }
}
