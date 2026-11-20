using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using WF = System.Windows.Forms;

namespace Assist;

/// Monitors (numbered 1, 2, … from left to right, then top to bottom) and the visible top-level windows on them.
/// Captures show one monitor at a time; this tells the agent what else there is and lets it look elsewhere
/// (<action type="look" monitor="N"/>) or highlight a window or a whole monitor (window / monitor hints).
static class Monitors
{
    public sealed record Win(IntPtr Handle, string Title, string Process, Rectangle Rect, int Monitor);

    public static List<Rectangle> All() =>
        WF.Screen.AllScreens.Select(s => s.Bounds).OrderBy(b => b.X).ThenBy(b => b.Y).ToList();

    /// 1-based number of the monitor holding most of this rectangle (or its centre).
    public static int NumberOf(Rectangle r)
    {
        var all = All();
        int best = 0; long bestArea = -1;
        for (int i = 0; i < all.Count; i++)
        {
            var x = Rectangle.Intersect(all[i], r);
            long area = x.Width > 0 && x.Height > 0 ? (long)x.Width * x.Height : 0;
            if (area > bestArea) { bestArea = area; best = i; }
        }
        if (bestArea <= 0)
        {
            var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            var idx = all.FindIndex(m => m.Contains(c));
            if (idx >= 0) best = idx;
        }
        return best + 1;
    }

    public static Rectangle? Get(int number) => All() is var all && number >= 1 && number <= all.Count ? all[number - 1] : null;

    public static Rectangle MonitorOf(IntPtr hwnd) =>
        hwnd != IntPtr.Zero ? WF.Screen.FromHandle(hwnd).Bounds : WF.Screen.FromPoint(WF.Cursor.Position).Bounds;

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Native.RECT value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TRANSPARENT = 0x20;
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    /// Visible, titled, non-minimized top-level windows, front to back (Computah's own windows excluded).
    public static List<Win> Windows(int max = 30)
    {
        var list = new List<Win>();
        var all = All();
        EnumWindows((h, _) =>
        {
            if (list.Count >= max) return false;
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true; // other virtual desktops, hidden UWP
            if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TRANSPARENT) != 0) return true; // click-through overlays (GPU overlays, hints)
            var title = Native.WindowTitle(h);
            if (title.Length == 0) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == (uint)Environment.ProcessId) return true;
            var r = Bounds(h);
            if (r.Width < 60 || r.Height < 40) return true;
            if (!all.Any(m => m.IntersectsWith(r))) return true;
            string proc;
            try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { proc = "?"; }
            if (proc is "TextInputHost" or "ShellExperienceHost" or "SearchHost" or "StartMenuExperienceHost") return true;
            if (title == "Program Manager") return true;
            list.Add(new Win(h, title, proc, r, NumberOf(r)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// The window's visible frame (without the invisible resize borders Windows adds).
    public static Rectangle Bounds(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT d, Marshal.SizeOf<Native.RECT>()) == 0 && d.Right > d.Left)
            return Rectangle.FromLTRB(d.Left, d.Top, d.Right, d.Bottom);
        return Native.GetWindowRect(h, out var r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Rectangle.Empty;
    }

    /// A window by (part of) its title and/or process name; without either, the focused window. Front-most match wins.
    public static Win? Find(string? title, string? process)
    {
        var wins = Windows(60);
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(process))
        {
            var fg = Native.GetForegroundWindow();
            return wins.FirstOrDefault(w => w.Handle == fg) ?? wins.FirstOrDefault();
        }
        bool Proc(Win w) => string.IsNullOrWhiteSpace(process) ||
                            w.Process.Equals(process.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
        bool Title(Win w, bool exact) => string.IsNullOrWhiteSpace(title) ||
            (exact ? w.Title.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase)
                   : w.Title.Contains(title.Trim(), StringComparison.OrdinalIgnoreCase));
        return wins.FirstOrDefault(w => Proc(w) && Title(w, true)) ?? wins.FirstOrDefault(w => Proc(w) && Title(w, false));
    }

    /// For the agent, when there is more than one monitor: the monitors, which one this capture shows, where the
    /// pointer and the focused window are, and the main windows on each monitor. Null with a single monitor.
    public static string? Describe(Rectangle shown, IntPtr focused, Point pointer)
    {
        var all = All();
        if (all.Count < 2) return null;
        int shownN = NumberOf(shown);
        var sb = new StringBuilder();
        sb.Append("MONITORS: ");
        sb.Append(string.Join("; ", all.Select((m, i) =>
            $"{i + 1} = {m.Width}x{m.Height} at ({m.X},{m.Y}){(i + 1 == shownN ? " ← this capture" : "")}")));
        sb.AppendLine(".");
        int pointerN = all.FindIndex(m => m.Contains(pointer)) + 1;
        var fgTitle = Native.WindowTitle(focused);
        int fgN = focused != IntPtr.Zero ? NumberOf(Bounds(focused)) : 0;
        sb.Append($"The mouse pointer is on monitor {pointerN}");
        if (fgN > 0 && fgTitle.Length > 0) sb.Append($"; the focused window \"{Clip(fgTitle)}\" is on monitor {fgN}");
        sb.AppendLine(".");
        var byMonitor = Windows(24).GroupBy(w => w.Monitor).OrderBy(g => g.Key);
        foreach (var g in byMonitor)
            sb.AppendLine($"Windows on monitor {g.Key} (front first): " +
                          string.Join(", ", g.Take(6).Select(w => $"\"{Clip(w.Title)}\" ({w.Process})")));
        sb.Append("Only the monitor marked \"this capture\" is in the images and the element list. To see another one, end with " +
                  "<action type=\"look\" monitor=\"N\"/>. To show the user where something is, use <hint type=\"window\" title=\"...\"/> " +
                  "or <hint type=\"monitor\" monitor=\"N\"/>.");
        return sb.ToString();
    }

    static string Clip(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
