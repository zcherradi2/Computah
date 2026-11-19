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
