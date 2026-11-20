using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Assist;

/// What one session has seen of each window, so that:
/// 1. element numbers are STABLE: the same button keeps its number for the whole conversation (new things get new
///    numbers), so a number from an earlier list never silently points at something else;
/// 2. with settings.screenDiff on, a window the agent saw recently is sent as its CHANGES ONLY ("same as snapshot S12
///    except: …") instead of the whole list again.
/// Diffs are never chained: they always compare with the last FULL list of that window, and only while that full list
/// is still in the agent's context (BuiltinSession keeps the last KeepElementLists lists). When much has changed, the
/// full list is sent again and becomes the new base.
public sealed class ScreenMemory
{
    /// How many recent element lists the agent keeps in context (BuiltinSession prunes older ones).
    public const int KeepElementLists = 8;
    /// Every element list starts with this, so it can be recognised (and pruned) in stored conversations.
    public const string Header = "UI ELEMENTS";
    const int MaxWindows = 12;
    const int MoveTolerance = 3; // image pixels

    public static bool IsElementList(string? text) => text != null && text.StartsWith(Header, StringComparison.Ordinal);

    sealed class Seen
    {
        public int Id { get; set; }
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Value { get; set; }
        public bool Enabled { get; set; } = true;
        // physical screen rect
        public int L { get; set; }
        public int T { get; set; }
        public int R { get; set; }
        public int B { get; set; }
        // centre as sent (screen space)
        public int X { get; set; }
        public int Y { get; set; }
    }

    sealed class View
    {
        public string Title { get; set; } = "";
        public string Process { get; set; } = "";
        public bool Taskbar { get; set; }
        public int LastSnapshot { get; set; }
        public List<Seen> Last { get; set; } = new();     // latest elements: numbering continues from these
        public int BaseSnapshot { get; set; }
        public string BaseScale { get; set; } = "";       // screen space size + monitor when the base was sent
        public List<Seen>? Base { get; set; }             // the last FULL list sent for this window
    }

    sealed class State
    {
        public int NextId { get; set; } = 1;
        public int Snapshots { get; set; }
    }

    readonly string sessionId;
    State state = new();
    readonly Dictionary<string, View> views = new();

    ScreenMemory(string sessionId) => this.sessionId = sessionId;

    public static ScreenMemory Load(string sessionId)
    {
        var m = new ScreenMemory(sessionId);
        try
        {
            Db.With(c =>
            {
                using var cmd = Db.Cmd(c, "SELECT window_key, data FROM window_views WHERE session_id=$s", ("$s", sessionId));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.GetString(0) == "_state") m.state = JsonSerializer.Deserialize<State>(r.GetString(1)) ?? new();
                    else if (JsonSerializer.Deserialize<View>(r.GetString(1)) is { } v) m.views[r.GetString(0)] = v;
                }
            });
        }
        catch (Exception ex) { Paths.Log("screen memory load: " + ex.Message); }
        return m;
    }

    /// Renumbers the capture's elements (stable numbers; ScreenMap then resolves element="N" with them) and returns the
    /// element list text to send, or null when there is no list. Call once per capture that is actually sent.
    public string? Describe(CapturedContext ctx, bool diff)
    {
        if (ctx.Elements is not { Items.Count: > 0 } map) return null;
        int snap = ++state.Snapshots;
        var mon = ctx.Monitor;
        double sx = (double)Math.Max(1, ctx.FullW) / mon.Width, sy = (double)Math.Max(1, ctx.FullH) / mon.Height;
        string scale = $"{ctx.FullW}x{ctx.FullH}@{mon.X},{mon.Y},{mon.Width}x{mon.Height}";

        var sb = new StringBuilder();
        sb.AppendLine($"{Header}, snapshot S{snap} (Windows accessibility; (x,y) = centre in screen space, as in Image 1; " +
                      "aim with element=\"N\", it is exact; numbers are stable across snapshots" +
                      (diff ? "; \"same as in Sn except\" lists only changes since that full list)" : ")"));

        var renumbered = new List<UiElement>();
        var touched = new List<string>();
        var order = map.Windows.Select(w => w.Handle).Concat(map.Items.Select(e => e.Window)).Distinct();
        foreach (var handle in order)
        {
            var items = map.Items.Where(e => e.Window == handle).ToList();
            if (items.Count == 0) continue;
            var win = map.Windows.FirstOrDefault(w => w.Handle == handle) ?? new UiWindow(handle, "", ctx.ProcessName, false);
            var key = (win.Taskbar ? "taskbar:" : win.Process + ":") + handle;
            views.TryGetValue(key, out var view);
            view ??= new View();

            // stable numbers: match against what this session last saw in this window
            var seen = Number(items, view.Last);
            foreach (var s in seen)
            {
                s.X = (int)(((s.L + s.R) / 2.0 - mon.X) * sx);
                s.Y = (int)(((s.T + s.B) / 2.0 - mon.Y) * sy);
            }
            for (int i = 0; i < items.Count; i++) renumbered.Add(items[i] with { Id = seen[i].Id });

            var label = win.Taskbar ? "Taskbar" : $"Window \"{Clip(win.Title)}\" ({win.Process})";
            bool canDiff = diff && view.Base != null && view.BaseScale == scale && snap - view.BaseSnapshot < KeepElementLists;
            var changes = canDiff ? Changes(view.Base!, seen) : null;
            if (changes != null && changes.Count == 0)
                sb.AppendLine($"{label}: unchanged since snapshot S{view.BaseSnapshot} ({seen.Count} elements).");
            else if (changes != null && changes.Count * 2 <= view.Base!.Count)
            {
                sb.AppendLine($"{label}: same as in snapshot S{view.BaseSnapshot} except these {changes.Count} change(s):");
                foreach (var line in changes) sb.AppendLine(line);
            }
            else
            {
                sb.AppendLine($"{label}, full list ({seen.Count}):");
                foreach (var s in seen) sb.AppendLine(Line(s));
                view.Base = seen;
                view.BaseSnapshot = snap;
                view.BaseScale = scale;
            }

            view.Title = win.Title;
            view.Process = win.Process;
            view.Taskbar = win.Taskbar;
            view.Last = seen;
            view.LastSnapshot = snap;
            views[key] = view;
            touched.Add(key);
        }
        map.Items = renumbered;
        Save(touched);
        return sb.ToString().TrimEnd();
    }

    /// Gives each scanned element the number it had last time (same type and name; nearest position wins when several
    /// match), or a new number.
    List<Seen> Number(List<UiElement> items, List<Seen> last)
    {
        var result = items.Select(e => new Seen
        {
            Type = e.Type, Name = e.Name, Value = e.Value, Enabled = e.Enabled,
            L = e.Rect.Left, T = e.Rect.Top, R = e.Rect.Right, B = e.Rect.Bottom,
        }).ToList();
        var free = last.ToList();

        // 1) same thing at the same place
        foreach (var s in result)
        {
            var m = free.FirstOrDefault(o => o.Type == s.Type && o.Name == s.Name && Math.Abs(o.L - s.L) <= 2 && Math.Abs(o.T - s.T) <= 2 &&
                                             Math.Abs(o.R - s.R) <= 2 && Math.Abs(o.B - s.B) <= 2);
            if (m != null) { s.Id = m.Id; free.Remove(m); }
        }
        // 2) same thing that moved (scrolled, resized): nearest first
        var pairs = (from s in result where s.Id == 0
                     from o in free where o.Type == s.Type && o.Name == s.Name
                     select (s, o, d: Dist(s, o))).OrderBy(p => p.d).ToList();
        foreach (var (s, o, _) in pairs)
            if (s.Id == 0 && free.Contains(o)) { s.Id = o.Id; free.Remove(o); }
        // 3) new numbers, always above anything handed out before
        if (last.Count > 0) state.NextId = Math.Max(state.NextId, last.Max(o => o.Id) + 1);
        foreach (var s in result) if (s.Id == 0) s.Id = state.NextId++;
        return result;
    }

    static double Dist(Seen a, Seen b)
    {
        double dx = (a.L + a.R - b.L - b.R) / 2.0, dy = (a.T + a.B - b.T - b.B) / 2.0;
        return dx * dx + dy * dy;
    }

    /// Differences from the base (the last full list): added, gone, changed (value, enabled, position).
    static List<string> Changes(List<Seen> baseList, List<Seen> now)
    {
        var lines = new List<string>();
        var before = baseList.ToDictionary(s => s.Id);
        var ids = now.Select(s => s.Id).ToHashSet();
        foreach (var s in now)
        {
            if (!before.TryGetValue(s.Id, out var o)) { lines.Add("+ " + Line(s)); continue; }
            var what = new List<string>();
            if (o.Value != s.Value) what.Add("value");
            if (o.Enabled != s.Enabled) what.Add(s.Enabled ? "enabled" : "disabled");
            if (Math.Abs(o.X - s.X) > MoveTolerance || Math.Abs(o.Y - s.Y) > MoveTolerance) what.Add("moved");
            if (what.Count > 0) lines.Add("~ " + Line(s) + "  [" + string.Join(", ", what) + "]");
        }
        foreach (var o in baseList)
            if (!ids.Contains(o.Id)) lines.Add($"- [{o.Id}] {o.Type}{(o.Name.Length > 0 ? $" \"{o.Name}\"" : "")}  [gone]");
        return lines;
    }

    static string Line(Seen e)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(e.Id).Append("] ").Append(e.Type);
        if (e.Name.Length > 0) sb.Append(" \"").Append(e.Name).Append('"');
        if (e.Value != null && e.Value != e.Name) sb.Append(" value=\"").Append(e.Value).Append('"');
        if (!e.Enabled) sb.Append(" (disabled)");
        sb.Append(" (").Append(e.X).Append(',').Append(e.Y).Append(')');
        return sb.ToString();
    }

    static string Clip(string s) => s.Length > 80 ? s[..80] + "…" : s;

    void Save(List<string> touched)
    {
        // forget the windows not seen for the longest time
        foreach (var old in views.OrderByDescending(v => v.Value.LastSnapshot).Skip(MaxWindows).Select(v => v.Key).ToList())
            views.Remove(old);
        try
        {
            Db.With(c => Db.Tx(c, () =>
            {
                var now = DateTime.Now.ToString("o");
                void Put(string key, string json) =>
                    Db.Exec(c, "INSERT INTO window_views(session_id, window_key, data, updated) VALUES($s,$k,$d,$u) " +
                               "ON CONFLICT(session_id, window_key) DO UPDATE SET data=excluded.data, updated=excluded.updated",
                        ("$s", sessionId), ("$k", key), ("$d", json), ("$u", now));
                Put("_state", JsonSerializer.Serialize(state));
                foreach (var k in touched) if (views.TryGetValue(k, out var v)) Put(k, JsonSerializer.Serialize(v));
                var keep = string.Join(",", views.Keys.Append("_state").Select((_, i) => "$k" + i));
                Db.Exec(c, $"DELETE FROM window_views WHERE session_id=$s AND window_key NOT IN ({keep})",
                    views.Keys.Append("_state").Select((k, i) => ("$k" + i, (object?)k)).Append(("$s", sessionId)).ToArray());
            }));
        }
        catch (Exception ex) { Paths.Log("screen memory save: " + ex.Message); }
    }
}
