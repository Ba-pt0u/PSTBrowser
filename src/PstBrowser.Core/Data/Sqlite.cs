// Minimal SQLite wrapper (P/Invoke on the bundled e_sqlite3 native library).
// Kept deliberately small and dependency-free so it can be audited easily.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PstBrowser.Core.Data
{
    internal static class Native
    {
        private const string Lib = "e_sqlite3";
        public const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101, SQLITE_BUSY = 5;
        public const int SQLITE_OPEN_READWRITE = 0x2, SQLITE_OPEN_CREATE = 0x4, SQLITE_OPEN_FULLMUTEX = 0x10000, SQLITE_OPEN_READONLY = 0x1;
        public const int SQLITE_INTEGER = 1, SQLITE_FLOAT = 2, SQLITE_TEXT = 3, SQLITE_BLOB = 4, SQLITE_NULL = 5;
        public static readonly IntPtr SQLITE_TRANSIENT = new IntPtr(-1);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_errmsg16(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_prepare16_v2(IntPtr db, [MarshalAs(UnmanagedType.LPWStr)] string sql, int nBytes, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_reset(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_clear_bindings(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_bind_int64(IntPtr stmt, int idx, long v);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_bind_double(IntPtr stmt, int idx, double v);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_bind_null(IntPtr stmt, int idx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_bind_text16(IntPtr stmt, int idx, [MarshalAs(UnmanagedType.LPWStr)] string v, int nBytes, IntPtr destructor);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_bind_parameter_index(IntPtr stmt, byte[] name);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_column_name16(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_column_type(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sqlite3_column_int64(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern double sqlite3_column_double(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_column_text16(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_column_bytes16(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long sqlite3_last_insert_rowid(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_changes(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sqlite3_interrupt(IntPtr db);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_libversion();
    }

    public sealed class SqliteException : Exception
    {
        public int Code { get; }
        public SqliteException(int code, string message) : base(message) { Code = code; }
    }

    /// <summary>One SQLite connection. Not thread-safe: use one connection per thread.</summary>
    public sealed class SqliteDb : IDisposable
    {
        private IntPtr _db;
        private readonly Dictionary<string, SqliteStmt> _cache = new Dictionary<string, SqliteStmt>();

        public string Path { get; }

        public SqliteDb(string path, bool readOnly = false)
        {
            Path = path;
            int flags = readOnly ? Native.SQLITE_OPEN_READONLY : (Native.SQLITE_OPEN_READWRITE | Native.SQLITE_OPEN_CREATE);
            flags |= Native.SQLITE_OPEN_FULLMUTEX;
            var rc = Native.sqlite3_open_v2(Utf8z(path), out _db, flags, IntPtr.Zero);
            if (rc != Native.SQLITE_OK)
            {
                string msg = _db != IntPtr.Zero ? ErrMsg() : "cannot open database";
                if (_db != IntPtr.Zero) Native.sqlite3_close_v2(_db);
                _db = IntPtr.Zero;
                throw new SqliteException(rc, $"{msg} ({path})");
            }
            Native.sqlite3_busy_timeout(_db, 30000);
        }

        public static string Version => Marshal.PtrToStringAnsi(Native.sqlite3_libversion());

        internal static byte[] Utf8z(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            Array.Resize(ref b, b.Length + 1);
            return b;
        }

        internal IntPtr Handle => _db != IntPtr.Zero ? _db : throw new ObjectDisposedException(nameof(SqliteDb));
        internal string ErrMsg() => Marshal.PtrToStringUni(Native.sqlite3_errmsg16(_db));

        internal void Check(int rc, string context = null)
        {
            if (rc != Native.SQLITE_OK && rc != Native.SQLITE_ROW && rc != Native.SQLITE_DONE)
                throw new SqliteException(rc, ErrMsg() + (context != null ? " — " + context : ""));
        }

        /// <summary>Prepared statement, cached by SQL text. Do not dispose cached statements.</summary>
        public SqliteStmt Prepare(string sql)
        {
            if (_cache.TryGetValue(sql, out var st))
            {
                st.Reset();
                return st;
            }
            st = new SqliteStmt(this, sql);
            _cache[sql] = st;
            return st;
        }

        /// <summary>Uncached statement: caller must dispose.</summary>
        public SqliteStmt PrepareOnce(string sql) => new SqliteStmt(this, sql);

        /// <summary>Executes one or more statements without results.</summary>
        public void Exec(string sql, params object[] args)
        {
            if (args != null && args.Length > 0)
            {
                var st = Prepare(sql);
                st.BindAll(args);
                st.Step();
                st.Reset();
                return;
            }
            // Multiple statements: split by prepare tail handled via simple loop
            foreach (var part in SplitStatements(sql))
            {
                using var st = new SqliteStmt(this, part);
                while (st.Step()) { }
            }
        }

        public long ExecScalarLong(string sql, params object[] args)
        {
            var st = Prepare(sql);
            st.BindAll(args);
            long v = st.Step() ? st.GetLong(0) : 0;
            st.Reset();
            return v;
        }

        public string ExecScalarString(string sql, params object[] args)
        {
            var st = Prepare(sql);
            st.BindAll(args);
            string v = st.Step() ? st.GetString(0) : null;
            st.Reset();
            return v;
        }

        public long LastInsertRowId => Native.sqlite3_last_insert_rowid(Handle);
        public int Changes => Native.sqlite3_changes(Handle);
        public void Interrupt() { if (_db != IntPtr.Zero) Native.sqlite3_interrupt(_db); }

        public void Begin() => Exec("BEGIN IMMEDIATE");
        public void Commit() => Exec("COMMIT");
        public void Rollback() { try { Exec("ROLLBACK"); } catch { } }

        private static IEnumerable<string> SplitStatements(string sql)
        {
            // Our schema scripts never contain ';' inside literals, so a plain split is sufficient.
            foreach (var p in sql.Split(';'))
            {
                var t = p.Trim();
                if (t.Length > 0) yield return t;
            }
        }

        public void Dispose()
        {
            foreach (var st in _cache.Values) st.Dispose();
            _cache.Clear();
            if (_db != IntPtr.Zero)
            {
                Native.sqlite3_close_v2(_db);
                _db = IntPtr.Zero;
            }
        }
    }

    public sealed class SqliteStmt : IDisposable
    {
        private readonly SqliteDb _db;
        private IntPtr _st;
        private Dictionary<string, int> _cols;
        public string Sql { get; }

        internal SqliteStmt(SqliteDb db, string sql)
        {
            _db = db;
            Sql = sql;
            var rc = Native.sqlite3_prepare16_v2(db.Handle, sql, -1, out _st, IntPtr.Zero);
            if (rc != Native.SQLITE_OK)
                throw new SqliteException(rc, db.ErrMsg() + " — SQL: " + sql);
        }

        public SqliteStmt Reset()
        {
            Native.sqlite3_reset(_st);
            Native.sqlite3_clear_bindings(_st);
            return this;
        }

        public SqliteStmt BindAll(params object[] args)
        {
            if (args == null) return this;
            for (int i = 0; i < args.Length; i++) Bind(i + 1, args[i]);
            return this;
        }

        public SqliteStmt Bind(int idx, object v)
        {
            int rc;
            switch (v)
            {
                case null: rc = Native.sqlite3_bind_null(_st, idx); break;
                case string s: rc = Native.sqlite3_bind_text16(_st, idx, s, -1, Native.SQLITE_TRANSIENT); break;
                case bool b: rc = Native.sqlite3_bind_int64(_st, idx, b ? 1 : 0); break;
                case int i: rc = Native.sqlite3_bind_int64(_st, idx, i); break;
                case long l: rc = Native.sqlite3_bind_int64(_st, idx, l); break;
                case uint u: rc = Native.sqlite3_bind_int64(_st, idx, u); break;
                case double d: rc = Native.sqlite3_bind_double(_st, idx, d); break;
                case DateTime dt: rc = Native.sqlite3_bind_int64(_st, idx, ToUnixMs(dt)); break;
                default: rc = Native.sqlite3_bind_text16(_st, idx, v.ToString(), -1, Native.SQLITE_TRANSIENT); break;
            }
            _db.Check(rc, Sql);
            return this;
        }

        public SqliteStmt Bind(string name, object v)
        {
            int idx = Native.sqlite3_bind_parameter_index(_st, SqliteDb.Utf8z(name));
            if (idx == 0) throw new ArgumentException("Unknown parameter " + name);
            return Bind(idx, v);
        }

        public static long ToUnixMs(DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Unspecified) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return new DateTimeOffset(dt.ToUniversalTime()).ToUnixTimeMilliseconds();
        }

        /// <summary>Advances; returns true while a row is available.</summary>
        public bool Step()
        {
            int rc = Native.sqlite3_step(_st);
            if (rc == Native.SQLITE_ROW) return true;
            if (rc == Native.SQLITE_DONE) return false;
            string msg = _db.ErrMsg();
            Native.sqlite3_reset(_st);
            throw new SqliteException(rc, msg + " — SQL: " + Sql);
        }

        public void Run() { Step(); Native.sqlite3_reset(_st); }

        public int ColumnCount => Native.sqlite3_column_count(_st);
        public string ColumnName(int i) => Marshal.PtrToStringUni(Native.sqlite3_column_name16(_st, i));
        public int Ordinal(string name)
        {
            if (_cols == null)
            {
                _cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < ColumnCount; i++) _cols[ColumnName(i)] = i;
            }
            return _cols[name];
        }

        public bool IsNull(int i) => Native.sqlite3_column_type(_st, i) == Native.SQLITE_NULL;
        public long GetLong(int i) => Native.sqlite3_column_int64(_st, i);
        public int GetInt(int i) => (int)Native.sqlite3_column_int64(_st, i);
        public double GetDouble(int i) => Native.sqlite3_column_double(_st, i);
        public long? GetLongOrNull(int i) => IsNull(i) ? (long?)null : GetLong(i);
        public string GetString(int i)
        {
            if (IsNull(i)) return null;
            var p = Native.sqlite3_column_text16(_st, i);
            int n = Native.sqlite3_column_bytes16(_st, i);
            return p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p, n / 2);
        }
        public long GetLong(string c) => GetLong(Ordinal(c));
        public int GetInt(string c) => GetInt(Ordinal(c));
        public string GetString(string c) => GetString(Ordinal(c));
        public long? GetLongOrNull(string c) => GetLongOrNull(Ordinal(c));
        public bool IsNull(string c) => IsNull(Ordinal(c));

        public void Dispose()
        {
            if (_st != IntPtr.Zero)
            {
                Native.sqlite3_finalize(_st);
                _st = IntPtr.Zero;
            }
        }
    }
}
