using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Assist;

/// Computah's data: %LOCALAPPDATA%\Computah\computah.db (SQLite). Writes are small row inserts/updates instead of
/// rewriting whole JSON files. Images are not stored in the database: they are JPEG files under images\{thread}\.
/// One shared connection, used under a lock (the UI thread and the built-in agent's threads both write).
static class Db
{
    public static string DataDir => Path.GetDirectoryName(Paths.Workspace)!;
    public static string FilePath => Path.Combine(DataDir, "computah.db");
    public static string ImagesDir => Path.Combine(DataDir, "images");

    static readonly object Gate = new();
    static SqliteConnection? conn;

    const string Schema = """
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS sessions(
            id TEXT PRIMARY KEY, agent_id TEXT NOT NULL, agent_session_id TEXT, name TEXT,
            created TEXT NOT NULL, updated TEXT NOT NULL,
            shared_paths TEXT NOT NULL DEFAULT '[]', workspaces TEXT NOT NULL DEFAULT '[]', playgrounds TEXT NOT NULL DEFAULT '[]');
        -- what the bubble shows (user / agent text), per session
        CREATE TABLE IF NOT EXISTS session_turns(
            session_id TEXT NOT NULL, seq INTEGER NOT NULL, user INTEGER NOT NULL, text TEXT NOT NULL, attach TEXT,
            PRIMARY KEY(session_id, seq));
        -- the built-in agent's full conversation (what the model sees), per thread; blocks as JSON, images as files
        CREATE TABLE IF NOT EXISTS thread_turns(
            thread_id TEXT NOT NULL, seq INTEGER NOT NULL, role TEXT NOT NULL, blocks TEXT NOT NULL,
            PRIMARY KEY(thread_id, seq));
        -- the UI elements each session last saw per window (stable element numbers, change-only lists)
        CREATE TABLE IF NOT EXISTS window_views(
            session_id TEXT NOT NULL, window_key TEXT NOT NULL, data TEXT NOT NULL, updated TEXT NOT NULL,
            PRIMARY KEY(session_id, window_key));
        """;

    /// Runs `work` with the open connection, under the lock. Opens (and migrates) on first use.
    public static T With<T>(Func<SqliteConnection, T> work)
    {
        lock (Gate)
        {
            if (conn == null)
            {
                Directory.CreateDirectory(DataDir);
                conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
                conn.Open();
                Exec(conn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=OFF;");
                Exec(conn, Schema);
                ImportJson(conn);
            }
            return work(conn);
        }
    }

    public static void With(Action<SqliteConnection> work) => With(c => { work(c); return 0; });

    /// Runs `work` in one SQLite transaction (plain BEGIN/COMMIT, so commands need no transaction object).
    public static void Tx(SqliteConnection c, Action work)
    {
        Exec(c, "BEGIN");
        try { work(); Exec(c, "COMMIT"); }
        catch { try { Exec(c, "ROLLBACK"); } catch { } throw; }
    }

    public static int Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static SqliteCommand Cmd(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd;
    }

    public static string? Meta(SqliteConnection c, string key)
    {
        using var cmd = Cmd(c, "SELECT value FROM meta WHERE key=$k", ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    public static void SetMeta(SqliteConnection c, string key, string? value) =>
        Exec(c, "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));

    // ---------- one-time import of the old JSON files ----------

    /// sessions.json and threads\*.json → the database, once. The old files are renamed to *.bak, not deleted.
    static void ImportJson(SqliteConnection c)
    {
        if (Meta(c, "imported_json") != null) return;
        var sessionsFile = Path.Combine(DataDir, "sessions.json");
        var threadsDir = Path.Combine(DataDir, "threads");
        try
        {
            Tx(c, () => {
            if (File.Exists(sessionsFile))
            {
                var old = JsonSerializer.Deserialize<LegacyStore>(File.ReadAllText(sessionsFile),
                              new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new LegacyStore();
                foreach (var s in old.Sessions)
                {
                    SessionStore.Upsert(c, s);
                    for (int i = 0; i < s.History.Count; i++) SessionStore.InsertTurn(c, s.Id, i, s.History[i]);
                }
                SetMeta(c, "focused", old.FocusedId);
                SetMeta(c, "dock_x", old.DockX?.ToString());
                SetMeta(c, "dock_y", old.DockY?.ToString());
            }
            if (Directory.Exists(threadsDir))
                foreach (var f in Directory.GetFiles(threadsDir, "*.json"))
                {
                    var turns = JsonSerializer.Deserialize<List<BuiltinSession.Turn>>(File.ReadAllText(f));
                    if (turns == null) continue;
                    var id = Path.GetFileNameWithoutExtension(f);
                    for (int i = 0; i < turns.Count; i++) Threads.InsertTurn(c, id, i, turns[i]);
                }
            SetMeta(c, "imported_json", DateTime.Now.ToString("o"));
            });
        }
        catch (Exception ex)
        {
            Paths.Log("import of sessions.json/threads failed, old files left as they are: " + ex);
            return;
        }
        try
        {
            if (File.Exists(sessionsFile)) File.Move(sessionsFile, sessionsFile + ".bak", overwrite: true);
            if (Directory.Exists(threadsDir) && !Directory.Exists(threadsDir + ".bak")) Directory.Move(threadsDir, threadsDir + ".bak");
        }
        catch (Exception ex) { Paths.Log("renaming imported json: " + ex.Message); }
    }

    sealed class LegacyStore
    {
        public List<SessionRecord> Sessions { get; set; } = new();
        public string? FocusedId { get; set; }
        public int? DockX { get; set; }
        public int? DockY { get; set; }
    }
}
