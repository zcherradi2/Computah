using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace Assist;

/// Playgrounds: the windows a session works in, recorded when Computah is called from them.
public static class Playgrounds
{
    static readonly string[] Ignored = { "computah", "explorer_desktop", "shellexperiencehost", "searchhost", "startmenuexperiencehost", "lockapp" };

    /// The window Computah was called from, as a playground (null for the desktop, the taskbar, Computah itself...).
    public static Playground? From(CapturedContext ctx)
    {
        var proc = ctx.ProcessName.Trim();
        var title = ctx.WindowTitle.Trim();
        if (proc.Length == 0 || title.Length == 0 || title == "Program Manager") return null;
        if (Ignored.Contains(proc.ToLowerInvariant())) return null;
        return new Playground { Process = proc, Title = title, ExePath = ctx.ProcessPath.Length > 0 ? ctx.ProcessPath : null, Window = (long)ctx.ForegroundHwnd };
    }

    /// The address shown in a browser window's address bar, read through UI Automation. Null when not found in time.
    public static async Task<string?> BrowserUrlAsync(IntPtr hwnd, int timeoutMs = 2500)
    {
        if (hwnd == IntPtr.Zero) return null;
        var work = Task.Run(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                foreach (AutomationElement e in edits)
                {
                    if (!e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) continue;
                    var v = ((ValuePattern)vp).Current.Value?.Trim();
                    if (string.IsNullOrEmpty(v) || v.Contains(' ')) continue;
                    if (v.Contains("://") || v.StartsWith("localhost") || v.Contains('.') && v.IndexOf('.') < v.Length - 2) return v;
                }
            }
            catch (Exception ex) { Paths.Log("browser url: " + ex.Message); }
            return null;
        });
        var done = await Task.WhenAny(work, Task.Delay(timeoutMs));
        return done == work ? work.Result : null;
    }

    // ---------- bringing a playground back ----------

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);

    static string ProcessOf(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out var pid);
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return ""; }
    }

    static string TitleOf(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(512);
        Native.GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// Visible top-level windows of a program (all Chrome windows, for example).
    static List<IntPtr> WindowsOf(string process)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && TitleOf(h).Length > 0 && string.Equals(ProcessOf(h), process, StringComparison.OrdinalIgnoreCase)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// The tab title inside a browser window title ("Axiobat - Bons de réception - Google Chrome" → "Axiobat - Bons de réception").
    static string TabTitle(Playground p)
    {
        var t = p.Title;
        foreach (var sep in new[] { " - ", " — ", " – " })
        {
            int i = t.LastIndexOf(sep, StringComparison.Ordinal);
            if (i > 0) return t[..i];
        }
        return t;
    }

    /// Bring a playground to the front: its window (restored if minimized) and, for browsers, its tab.
    /// Returns null when done, or what went wrong.
    public static Task<string?> FocusAsync(Playground p) => Task.Run<string?>(() =>
    {
        var stored = (IntPtr)p.Window;
        var windows = WindowsOf(p.Process);
        if (stored != IntPtr.Zero && IsWindow(stored) && windows.Contains(stored)) { windows.Remove(stored); windows.Insert(0, stored); }
        if (windows.Count == 0) return $"{p.Process} isn't running";

        IntPtr target = IntPtr.Zero;
        AutomationElement? tab = null;
        if (p.IsBrowser)
        {
            var want = TabTitle(p);
            var first = want.Split(new[] { " - ", " — ", " | " }, StringSplitOptions.None)[0].Trim();
            AutomationElement? loose = null; IntPtr looseWin = IntPtr.Zero;
            foreach (var w in windows)
            {
                try
                {
                    var tabs = AutomationElement.FromHandle(w).FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                    foreach (AutomationElement t in tabs)
                    {
                        var name = t.Current.Name ?? "";
                        if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) { tab = t; target = w; break; }
                        if (loose == null && first.Length >= 3 && name.StartsWith(first, StringComparison.OrdinalIgnoreCase)) { loose = t; looseWin = w; }
                    }
                }
                catch (Exception ex) { Paths.Log("focus tabs: " + ex.Message); }
                if (tab != null) break;
            }
            if (tab == null && loose != null) { tab = loose; target = looseWin; }
            if (tab == null) target = windows[0];
        }
        else
        {
            // the stored window, else one whose title still looks like it
            target = windows.FirstOrDefault(w => w == stored)
                  is var s && s != IntPtr.Zero ? s
                  : windows.FirstOrDefault(w => TitleOf(w).Contains(p.Label, StringComparison.OrdinalIgnoreCase)) is var m && m != IntPtr.Zero ? m
                  : windows[0];
        }

        if (IsIconic(target)) ShowWindow(target, 9); // SW_RESTORE
        Native.ForceForeground(target);
        p.Window = (long)target;
        if (tab != null)
        {
            try
            {
                if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) ((SelectionItemPattern)sp).Select();
                else if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) ((InvokePattern)ip).Invoke();
            }
            catch (Exception ex) { return "found the window but couldn't switch to the tab: " + ex.Message; }
        }
        else if (p.IsBrowser) return $"brought {p.Process} to the front, but no open tab matches \"{TabTitle(p)}\" any more";
        return null;
    });

    /// The session's workspaces and playgrounds, written for the agent. Null when it has none.
    public static string? Describe(SessionRecord s)
    {
        var ws = s.Workspaces.Where(w => System.IO.Directory.Exists(w.Path) || System.IO.File.Exists(w.Path)).ToList();
        if (ws.Count == 0 && s.Playgrounds.Count == 0) return null;
        var sb = new System.Text.StringBuilder("[Computah] This session's setup:");
        if (ws.Count > 0)
        {
            sb.Append("\nWORKSPACES (what this session works on, like a repo; focus your work here):");
            foreach (var w in ws)
                sb.Append($"\n- {w.Path}  ({(w.IsFolder ? "folder" : "file")}, {(w.Edit ? "you can read and edit it" : "read-only")})");
        }
        else sb.Append("\nNo workspace yet. If you find the folder or repo this session is about, propose it with <request-access path=\"...\" mode=\"workspace\" reason=\"...\"/>.");
        if (s.Playgrounds.Count > 0)
        {
            sb.Append("\nPLAYGROUNDS (windows where the user runs and tests the work, newest first; screenshots usually come from these.");
            sb.Append(" Bring one to the front with <action type=\"focus\" playground=\"N\"/>, which also switches the browser to that tab):");
            int n = 1;
            foreach (var p in s.Playgrounds)
            {
                var where = p.IsBrowser
                    ? $"{p.Process} tab \"{TabTitle(p)}\"" + (p.Url != null ? $" at {p.Url}" : "")
                    : $"{p.Process} window \"{p.Title}\"";
                sb.Append($"\n{n++}. {where}");
            }
        }
        return sb.ToString();
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} d ago";
    }
}
