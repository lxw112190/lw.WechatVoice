using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceEngine
{
    /// <summary>
    /// Thin winsqlite3 wrapper (Windows 10+ built-in SQLite, zero deployment
    /// footprint) covering what the Python sqlite3 stdlib module provided:
    /// readonly URI opens, exec, prepared statements with UTF-8 parameters and
    /// typed column reads. UTF-8 filenames are passed as raw bytes so Chinese
    /// and space paths survive without ANSI codepage mangling.
    /// </summary>
    internal sealed class SqliteConnection : IDisposable
    {
        private const string Dll = "winsqlite3";
        internal const int SqliteRow = 100, SqliteDone = 101;
        private const int OpenReadonly = 0x00000001, OpenReadwrite = 0x00000002, OpenCreate = 0x00000004, OpenUri = 0x00000040;
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_libversion_number")]
        internal static extern int LibraryVersion();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
        private static extern int Open(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close")]
        private static extern int Close(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_exec")]
        private static extern int ExecRaw(IntPtr db, byte[] sql, IntPtr callback, IntPtr userdata, out IntPtr error);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_free")]
        private static extern void Free(IntPtr pointer);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errmsg")]
        private static extern IntPtr ErrorMessage(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
        private static extern int Prepare(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_text")]
        private static extern int BindText(IntPtr statement, int index, byte[] value, int bytes, IntPtr destructor);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_int64")]
        private static extern int BindInt64(IntPtr statement, int index, long value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_null")]
        private static extern int BindNull(IntPtr statement, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
        private static extern int StepRaw(IntPtr statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
        private static extern IntPtr ColumnText(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_int64")]
        private static extern long ColumnInt64(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_type")]
        private static extern int ColumnType(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_bytes")]
        private static extern int ColumnBytes(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_blob")]
        private static extern IntPtr ColumnBlob(IntPtr statement, int column);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
        private static extern int FinalizeStatement(IntPtr statement);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_reset")]
        private static extern int Reset(IntPtr statement);

        internal readonly IntPtr Handle;
        private bool disposed;

        internal SqliteConnection(IntPtr handle) { Handle = handle; }

        internal static string FileUri(string path)
        {
            // Escape literal #, %, ? and non-ASCII characters before adding URI options.
            return new Uri(Path.GetFullPath(path)).AbsoluteUri;
        }

        internal static SqliteConnection OpenReadWrite(string path)
        {
            IntPtr handle;
            if (Open(Utf8Z(FileUri(path) + "?mode=rwc"), out handle, OpenReadwrite | OpenCreate | OpenUri, IntPtr.Zero) != 0)
            {
                string error = handle == IntPtr.Zero ? "" : Describe(handle);
                if (handle != IntPtr.Zero) Close(handle);
                throw new InvalidDataException("无法打开索引数据库" + error);
            }
            return new SqliteConnection(handle);
        }

        internal static SqliteConnection OpenReadOnly(string path)
        {
            IntPtr handle;
            if (Open(Utf8Z(FileUri(path) + "?mode=ro&immutable=1"), out handle, OpenReadonly | OpenUri, IntPtr.Zero) != 0)
            {
                if (handle != IntPtr.Zero) Close(handle);
                throw new InvalidDataException("无法打开数据库副本");
            }
            return new SqliteConnection(handle);
        }

        internal void Exec(string sql)
        {
            IntPtr error;
            if (ExecRaw(Handle, Utf8Z(sql), IntPtr.Zero, IntPtr.Zero, out error) != 0)
            {
                string message;
                try { message = Utf8FromPointer(error); }
                finally { if (error != IntPtr.Zero) Free(error); }
                throw new InvalidDataException("SQL执行失败：" + message);
            }
        }

        internal SqliteStatement Prepare(string sql)
        {
            IntPtr statement;
            if (Prepare(Handle, Utf8Z(sql), -1, out statement, IntPtr.Zero) != 0)
            {
                if (statement != IntPtr.Zero) FinalizeStatement(statement);
                throw new InvalidDataException("SQL准备失败：" + Describe(Handle));
            }
            return new SqliteStatement(statement, Handle);
        }

        private static string Describe(IntPtr db) { return Utf8FromPointer(ErrorMessage(db)); }

        private static string Utf8FromPointer(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return "";
            int length = 0;
            while (Marshal.ReadByte(pointer, length) != 0) length++;
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static byte[] Utf8Z(string value) { return Encoding.UTF8.GetBytes(value + "\0"); }

        public void Dispose()
        {
            if (!disposed && Handle != IntPtr.Zero) { Close(Handle); disposed = true; }
        }

        internal sealed class SqliteStatement : IDisposable
        {
            private IntPtr handle;
            private readonly IntPtr db;

            internal SqliteStatement(IntPtr handle, IntPtr db) { this.handle = handle; this.db = db; }

            internal void Bind(int index, object value)
            {
                if (value == null) BindNull(handle, index);
                else if (value is string) BindText(handle, index, Utf8Z((string)value), -1, new IntPtr(-1));
                else if (value is byte[]) BindText(handle, index, (byte[])value, ((byte[])value).Length, new IntPtr(-1));
                else BindInt64(handle, index, Convert.ToInt64(value));
            }

            internal bool Step() { int code = StepRaw(handle); if (code != SqliteRow && code != SqliteDone) throw new InvalidDataException("SQL读取失败：" + Describe(db)); return code == SqliteRow; }

            internal string Text(int column) { IntPtr pointer = ColumnText(handle, column); return pointer == IntPtr.Zero ? null : Utf8FromPointer(pointer); }

            internal long Int64(int column) { return ColumnInt64(handle, column); }

            /// <summary>Python str(value or ''): NULL and integer 0 become "", other integers stringify, text passes through.</summary>
            internal string TextOrEmpty(int column)
            {
                int type = ColumnType(handle, column);
                if (type == 5) return "";
                if (type == 1 || type == 2) { long value = ColumnInt64(handle, column); return value == 0 ? "" : value.ToString(); }
                return Text(column) ?? "";
            }

            internal byte[] Blob(int column)
            {
                int length = ColumnBytes(handle, column);
                IntPtr pointer = ColumnBlob(handle, column);
                if (pointer == IntPtr.Zero || length == 0) return new byte[0];
                var result = new byte[length];
                Marshal.Copy(pointer, result, 0, length);
                return result;
            }

            internal void Execute(object[] args)
            {
                for (int i = 0; i < args.Length; i++) Bind(i + 1, args[i]);
                Step();
            }

            public void Dispose() { if (handle != IntPtr.Zero) { FinalizeStatement(handle); handle = IntPtr.Zero; } }
        }
    }
}
