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

/// All sessions, which one Ctrl+Alt+A targets, and where the dock button sits.
public sealed class SessionStore
{
    public List<SessionRecord> Sessions { get; set; } = new();
    public string? FocusedId { get; set; }
    public int? DockX { get; set; }
    public int? DockY { get; set; }

    static string FilePath => Path.Combine(Path.GetDirectoryName(Paths.Workspace)!, "sessions.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static SessionStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SessionStore>(File.ReadAllText(FilePath), Opts) ?? new SessionStore();
        }
        catch (Exception ex) { Paths.Log("sessions.json unreadable: " + ex.Message); }
        return new SessionStore();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts)); }
        catch (Exception ex) { Paths.Log("sessions save: " + ex.Message); }
    }

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
