using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assist;

/// A conversation with an agent. Survives its bubble being closed and app restarts.
public sealed class SessionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AgentId { get; set; } = "";
    public string? AgentSessionId { get; set; }          // what `--resume` needs
    public List<Turn> History { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;

    /// Title written by the agent after the first exchange.
    public string? Name { get; set; }

    /// Files and folders the user dropped on Computah for this session; the agent may read them.
    public List<string> SharedPaths { get; set; } = new();

    /// The folders/files this session mainly works on (like a repo). The agent reads them and, when Edit is on, changes them.
    public List<Workspace> Workspaces { get; set; } = new();

    /// The windows where the work runs and gets tested (a browser tab with the app, a program...). Newest first.
    public List<Playground> Playgrounds { get; set; } = new();

    public const int MaxPlaygrounds = 5;

    /// What the agent gets as file access: the shared paths plus the workspaces ("edit:" prefix when edits are allowed).
    [JsonIgnore]
    public IEnumerable<string> AccessPaths =>
        SharedPaths.Concat(Workspaces.Where(w => File.Exists(w.Path) || Directory.Exists(w.Path))
                                     .Select(w => w.Edit ? SharedAccess.EditPrefix + w.Path : w.Path));

    public bool AddWorkspace(string path, bool edit = true)
    {
        path = Path.GetFullPath(path).TrimEnd('\\');
        if (path.Length == 2) path += "\\";
        var existing = Workspaces.FirstOrDefault(w => string.Equals(w.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { if (edit && !existing.Edit) { existing.Edit = true; return true; } return false; }
        Workspaces.Add(new Workspace { Path = path, Edit = edit });
        return true;
    }

    /// Remember the window Computah was called from. Browser tabs are told apart by site, other apps by window title.
    public void TouchPlayground(Playground p)
    {
        var same = Playgrounds.FirstOrDefault(x => x.SameAs(p));
        if (same != null)
        {
            Playgrounds.Remove(same);
            p.Added = same.Added;
            p.Url ??= same.Url;
        }
        p.LastUsed = DateTime.Now;
        Playgrounds.Insert(0, p);
        if (Playgrounds.Count > MaxPlaygrounds) Playgrounds.RemoveRange(MaxPlaygrounds, Playgrounds.Count - MaxPlaygrounds);
    }

    [JsonIgnore] public BubbleWindow? Window { get; set; }
    [JsonIgnore] public bool TitleRequested { get; set; }

    /// How many History turns are already in the database (Save appends the rest).
    [JsonIgnore] public int SavedTurns { get; set; }

    /// What this session has seen of each window: stable element numbers and change-only lists (ScreenMemory.cs).
    [JsonIgnore] public ScreenMemory Screen => screen ??= ScreenMemory.Load(Id);
    ScreenMemory? screen;

    [JsonIgnore]
    public string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name;
            var first = History.FirstOrDefault(t => t.User);
            if (first == null) return "New session";
            var text = first.Text.Length > 0 ? first.Text : first.Attach != null ? "Shared: " + first.Attach : "New session";
            text = text.ReplaceLineEndings(" ");
            return text.Length > 70 ? text[..70] + "…" : text;
        }
    }
}

public sealed class Workspace
{
    public string Path { get; set; } = "";
    public bool Edit { get; set; } = true;
    [JsonIgnore] public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
    [JsonIgnore] public bool IsFolder => Directory.Exists(Path);
}

public sealed class Playground
{
    public string Process { get; set; } = "";       // e.g. chrome
    public string Title { get; set; } = "";         // window title when last used
    public string? Url { get; set; }                // browsers: the page address
    public string? ExePath { get; set; }
    public long Window { get; set; }                // the window handle when last seen (checked before use)
    public DateTime Added { get; set; } = DateTime.Now;
    public DateTime LastUsed { get; set; } = DateTime.Now;

    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc" };
    [JsonIgnore] public bool IsBrowser => Browsers.Contains(Process.ToLowerInvariant());

    /// Site of a browser tab (host + port), e.g. "localhost:4200".
    [JsonIgnore]
    public string? Site => Url != null && Uri.TryCreate(Url.Contains("://") ? Url : "http://" + Url, UriKind.Absolute, out var u)
        ? (u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}") : null;

    /// Short name for chips: the site for browser tabs, else the window title (without the app name suffix).
    [JsonIgnore]
    public string Label
    {
        get
        {
            if (IsBrowser && Site != null) return Site;
            var t = Title;
            foreach (var sep in new[] { " - ", " — ", " – " })
            {
                int i = t.LastIndexOf(sep, StringComparison.Ordinal);
                if (i > 0) { t = t[..i]; break; }
            }
            return t.Length > 0 ? t : Process;
        }
    }

    public bool SameAs(Playground o)
    {
        if (!string.Equals(Process, o.Process, StringComparison.OrdinalIgnoreCase)) return false;
        if (IsBrowser && Site != null && o.Site != null) return Site == o.Site;
        return string.Equals(Label, o.Label, StringComparison.OrdinalIgnoreCase);
    }
}

/// All sessions, which one Ctrl+Alt+A targets, and where the dock button sits. Stored in the database (Db.cs):
/// Save() updates the session rows and appends only the turns that are new since the last save.
public sealed class SessionStore
{
    public List<SessionRecord> Sessions { get; set; } = new();
    public string? FocusedId { get; set; }
    public int? DockX { get; set; }
    public int? DockY { get; set; }

    static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static SessionStore Load()
    {
        var store = new SessionStore();
        try
        {
            Db.With(c =>
            {
                store.FocusedId = Db.Meta(c, "focused");
                store.DockX = int.TryParse(Db.Meta(c, "dock_x"), out var x) ? x : null;
                store.DockY = int.TryParse(Db.Meta(c, "dock_y"), out var y) ? y : null;

                var byId = new Dictionary<string, SessionRecord>();
                using (var cmd = Db.Cmd(c, "SELECT id, agent_id, agent_session_id, name, created, updated, shared_paths, workspaces, playgrounds FROM sessions"))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var s = new SessionRecord
                        {
                            Id = r.GetString(0), AgentId = r.GetString(1),
                            AgentSessionId = r.IsDBNull(2) ? null : r.GetString(2),
                            Name = r.IsDBNull(3) ? null : r.GetString(3),
                            Created = DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                            Updated = DateTime.Parse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                            SharedPaths = JsonSerializer.Deserialize<List<string>>(r.GetString(6), Opts) ?? new(),
                            Workspaces = JsonSerializer.Deserialize<List<Workspace>>(r.GetString(7), Opts) ?? new(),
                            Playgrounds = JsonSerializer.Deserialize<List<Playground>>(r.GetString(8), Opts) ?? new(),
                        };
                        byId[s.Id] = s;
                        store.Sessions.Add(s);
                    }
                using (var cmd = Db.Cmd(c, "SELECT session_id, user, text, attach FROM session_turns ORDER BY session_id, seq"))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        if (byId.TryGetValue(r.GetString(0), out var s))
                            s.History.Add(new Turn(r.GetInt64(1) != 0, r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
                foreach (var s in store.Sessions) s.SavedTurns = s.History.Count;
            });
        }
        catch (Exception ex) { Paths.Log("sessions load: " + ex); }
        return store;
    }

    public void Save()
    {
        try
        {
            Db.With(c => Db.Tx(c, () =>
            {
                Db.SetMeta(c, "focused", FocusedId);
                Db.SetMeta(c, "dock_x", DockX?.ToString());
                Db.SetMeta(c, "dock_y", DockY?.ToString());

                foreach (var s in Sessions)
                {
                    Upsert(c, s);
                    if (s.History.Count < s.SavedTurns) // history got shorter: rewrite it (doesn't happen in normal use)
                    {
                        Db.Exec(c, "DELETE FROM session_turns WHERE session_id=$s", ("$s", s.Id));
                        s.SavedTurns = 0;
                    }
                    for (int i = s.SavedTurns; i < s.History.Count; i++) InsertTurn(c, s.Id, i, s.History[i]);
                    s.SavedTurns = s.History.Count;
                }

                // sessions deleted since the last save: drop their rows, their agent thread and its images
                var gone = new List<(string Id, string? Thread)>();
                using (var cmd = Db.Cmd(c, "SELECT id, agent_session_id FROM sessions"))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        if (!Sessions.Any(s => s.Id == r.GetString(0))) gone.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
                foreach (var (id, thread) in gone)
                {
                    Db.Exec(c, "DELETE FROM sessions WHERE id=$s", ("$s", id));
                    Db.Exec(c, "DELETE FROM session_turns WHERE session_id=$s", ("$s", id));
                    Db.Exec(c, "DELETE FROM window_views WHERE session_id=$s", ("$s", id));
                    if (thread != null) Threads.Delete(c, thread);
                }
            }));
        }
        catch (Exception ex) { Paths.Log("sessions save: " + ex); }
    }

    internal static void Upsert(Microsoft.Data.Sqlite.SqliteConnection c, SessionRecord s) =>
        Db.Exec(c, """
            INSERT INTO sessions(id, agent_id, agent_session_id, name, created, updated, shared_paths, workspaces, playgrounds)
            VALUES($id, $agent, $asid, $name, $created, $updated, $shared, $ws, $pg)
            ON CONFLICT(id) DO UPDATE SET agent_id=excluded.agent_id, agent_session_id=excluded.agent_session_id,
                name=excluded.name, updated=excluded.updated, shared_paths=excluded.shared_paths,
                workspaces=excluded.workspaces, playgrounds=excluded.playgrounds
            """,
            ("$id", s.Id), ("$agent", s.AgentId), ("$asid", s.AgentSessionId), ("$name", s.Name),
            ("$created", s.Created.ToString("o")), ("$updated", s.Updated.ToString("o")),
            ("$shared", JsonSerializer.Serialize(s.SharedPaths)), ("$ws", JsonSerializer.Serialize(s.Workspaces)),
            ("$pg", JsonSerializer.Serialize(s.Playgrounds)));

    internal static void InsertTurn(Microsoft.Data.Sqlite.SqliteConnection c, string sessionId, int seq, Turn t) =>
        Db.Exec(c, "INSERT OR REPLACE INTO session_turns(session_id, seq, user, text, attach) VALUES($s, $q, $u, $t, $a)",
            ("$s", sessionId), ("$q", seq), ("$u", t.User ? 1 : 0), ("$t", t.Text), ("$a", t.Attach));

    [JsonIgnore] public SessionRecord? Focused => Sessions.FirstOrDefault(s => s.Id == FocusedId);

    [JsonIgnore] public IEnumerable<SessionRecord> NewestFirst => Sessions.OrderByDescending(s => s.Updated);

    public SessionRecord Create(string agentId)
    {
        var r = new SessionRecord { AgentId = agentId };
        Sessions.Add(r);
        return r;
    }

    public void Remove(SessionRecord r)
    {
        Sessions.Remove(r);
        if (FocusedId == r.Id) FocusedId = NewestFirst.FirstOrDefault()?.Id;
    }
}
