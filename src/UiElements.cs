using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;
using Rectangle = System.Drawing.Rectangle;

namespace Assist;

/// One on-screen element from Windows UI Automation (accessibility): a button, link, field, row, tab, menu item...
/// Rect is in physical screen pixels.
public sealed record UiElement(int Id, string Type, string Name, string? Value, Rectangle Rect, bool Enabled);

/// The numbered elements of the focused window at capture time. Agents target them with element="N",
/// which is much more precise than reading pixel coordinates off a screenshot.
public sealed class UiMap
{
    public List<UiElement> Items { get; } = new();
    public UiElement? Get(int id) => Items.FirstOrDefault(e => e.Id == id);
    public long ScanMs { get; init; }
}

static class UiElements
{
    const int MaxInteractive = 180, MaxText = 60, MaxNameChars = 70, MaxTabs = 8;

    static readonly (ControlType Type, string Label)[] Interactive =
    {
        (ControlType.Button, "button"), (ControlType.SplitButton, "button"), (ControlType.Hyperlink, "link"),
        (ControlType.Edit, "field"), (ControlType.ComboBox, "dropdown"), (ControlType.CheckBox, "checkbox"),
        (ControlType.RadioButton, "radio"), (ControlType.MenuItem, "menu item"), (ControlType.TabItem, "tab"),
        (ControlType.ListItem, "list item"), (ControlType.TreeItem, "tree item"), (ControlType.DataItem, "row"),
        (ControlType.HeaderItem, "column"), (ControlType.Slider, "slider"), (ControlType.Spinner, "spinner"),
        (ControlType.MenuBar, "menu bar"),
    };

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    /// Scans the focused window (plus visible popups of the same app, e.g. menus and dropdowns) on a background thread.
    /// Null if it fails or takes longer than the timeout.
    public static async Task<UiMap?> ScanAsync(IntPtr hwnd, Rectangle monitor, int timeoutMs = 2500)
    {
        if (hwnd == IntPtr.Zero) return null;
        var work = Task.Run(() => Scan(hwnd, monitor));
        var done = await Task.WhenAny(work, Task.Delay(timeoutMs));
        if (done != work) { Paths.Log("ui elements: timed out"); return null; }
        try { return await work; } catch (Exception ex) { Paths.Log("ui elements: " + ex.Message); return null; }
    }

    static UiMap? Scan(IntPtr hwnd, Rectangle monitor)
    {
        var sw = Stopwatch.StartNew();
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        bool self = pid == (uint)Environment.ProcessId; // Computah itself: skip its window, still scan the taskbar

        // The focused window, plus popups of the same app in FRONT of it (open menus, dropdown lists): windows that come
        // before it in z-order (EnumWindows goes front to back) and are smaller than most of the screen. Other windows of
        // the same app behind it are hidden, so their elements would be wrong targets.
        var windows = self ? new List<IntPtr>() : new List<IntPtr> { hwnd };
        bool reachedFocused = false;
        long monitorArea = (long)monitor.Width * monitor.Height;
        EnumWindows((h, _) =>
        {
            if (h == hwnd) { reachedFocused = true; return false; }
            if (IsWindowVisible(h) && !IsIconic(h))
            {
                Native.GetWindowThreadProcessId(h, out var p);
                if (!self && p == pid && GetWindowRect(h, out var r) && r.R > r.L && r.B > r.T &&
                    (long)(r.R - r.L) * (r.B - r.T) < monitorArea * 6 / 10 &&
                    Rectangle.Intersect(monitor, Rectangle.FromLTRB(r.L, r.T, r.R, r.B)) is { Width: > 0, Height: > 0 } && windows.Count < 5)
                    windows.Insert(0, h); // popups first: they are on top
            }
            return true;
        }, IntPtr.Zero);
        _ = reachedFocused;

        // The taskbar is always on screen: its app buttons and tray icons are common targets ("click Spotify").
        EnumWindows((h, _) =>
        {
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" && h != hwnd && IsWindowVisible(h) &&
                GetWindowRect(h, out var r) && Rectangle.Intersect(monitor, Rectangle.FromLTRB(r.L, r.T, r.R, r.B)) is { Width: > 0, Height: > 0 })
                windows.Add(h);
            return true;
        }, IntPtr.Zero);

        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
        foreach (var prop in new AutomationProperty[]
                 {
                     AutomationElement.NameProperty, AutomationElement.ControlTypeProperty, AutomationElement.BoundingRectangleProperty,
                     AutomationElement.IsOffscreenProperty, AutomationElement.IsEnabledProperty, AutomationElement.HelpTextProperty,
                     AutomationElement.AutomationIdProperty, ValuePattern.ValueProperty,
                 })
            cache.Add(prop);

        var typeConds = Interactive.Select(i => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, i.Type))
                                   .Append(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                                   .ToArray();
        var cond = new AndCondition(new OrCondition(typeConds), new PropertyCondition(AutomationElement.IsOffscreenProperty, false));

        var raw = new List<(string Type, string Name, string? Value, Rectangle Rect, bool Enabled, bool IsText)>();
        foreach (var w in windows)
        {
            Rectangle winRect = GetWindowRect(w, out var wr) ? Rectangle.FromLTRB(wr.L, wr.T, wr.R, wr.B) : monitor;
            var visible = Rectangle.Intersect(winRect, monitor);
            if (visible.Width <= 0 || visible.Height <= 0) continue;
            AutomationElementCollection found;
            try
            {
                var root = AutomationElement.FromHandle(w); // live root first; the cache applies to what FindAll returns
                using (cache.Activate())
                    found = root.FindAll(TreeScope.Descendants, cond);
            }
            catch (Exception ex) { Paths.Log("ui scan window: " + ex.Message); continue; }

            foreach (AutomationElement e in found)
            {
                try
                {
                    var br = (System.Windows.Rect)e.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
                    if (br.IsEmpty || br.Width < 2 || br.Height < 2 || double.IsInfinity(br.Width)) continue;
                    var rect = Rectangle.FromLTRB((int)br.Left, (int)br.Top, (int)br.Right, (int)br.Bottom);
                    var cx = rect.X + rect.Width / 2; var cy = rect.Y + rect.Height / 2;
                    if (!visible.Contains(cx, cy)) continue; // centre must be on screen and inside its window

                    var ct = (ControlType)e.GetCachedPropertyValue(AutomationElement.ControlTypeProperty);
                    bool isText = ct == ControlType.Text;
                    var name = (e.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "").Trim();
                    if (name.Length == 0) name = (e.GetCachedPropertyValue(AutomationElement.HelpTextProperty) as string ?? "").Trim();
                    if (name.Length == 0 && !isText) name = (e.GetCachedPropertyValue(AutomationElement.AutomationIdProperty) as string ?? "").Trim();
                    if (isText && name.Length == 0) continue;
                    string? value = null;
                    if (ct == ControlType.Edit || ct == ControlType.ComboBox)
                        value = e.GetCachedPropertyValue(ValuePattern.ValueProperty) as string;
                    var enabled = e.GetCachedPropertyValue(AutomationElement.IsEnabledProperty) is bool b ? b : true;
                    var label = isText ? "text" : Interactive.First(i => i.Type == ct).Label;
                    raw.Add((label, Clip(name), value is { Length: > 0 } ? Clip(value) : null, rect, enabled, isText));
                }
                catch { }
            }
        }

        // drop duplicates (same kind of thing at the same place), keep what fits, number in reading order
        var unique = raw.GroupBy(r => (r.Rect, r.Name)).Select(g => g.OrderBy(r => r.IsText).First()).ToList();
        // text that just repeats the name of a control it sits in adds nothing
        unique = unique.Where(r => !r.IsText || !unique.Any(o => !o.IsText && o.Name == r.Name && o.Rect.Contains(r.Rect.X + r.Rect.Width / 2, r.Rect.Y + r.Rect.Height / 2))).ToList();
        // a browser's tab strip can hold dozens of tabs: keep a few (the agent switches tabs with playground focus anyway)
        var tabs = unique.Where(r => r.Type == "tab").ToList();
        if (tabs.Count > MaxTabs) unique = unique.Except(tabs.OrderBy(t => t.Rect.X).Skip(MaxTabs)).ToList();
        var chosen = unique.Where(r => !r.IsText).Take(MaxInteractive).Concat(unique.Where(r => r.IsText).Take(MaxText))
                           .OrderBy(r => r.Rect.Y / 12).ThenBy(r => r.Rect.X).ToList();

        var map = new UiMap { ScanMs = sw.ElapsedMilliseconds };
        int id = 1;
        foreach (var r in chosen) map.Items.Add(new UiElement(id++, r.Type, r.Name, r.Value, r.Rect, r.Enabled));
        return map.Items.Count > 0 ? map : null;
    }

    static string Clip(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length > MaxNameChars ? s[..MaxNameChars] + "…" : s;
    }

    /// The list as the agent reads it: positions are centres in Image 1 (the scaled screenshot).
    public static string Describe(UiMap map, Rectangle monitor, int imageW, int imageH)
    {
        double sx = (double)Math.Max(1, imageW) / monitor.Width, sy = (double)Math.Max(1, imageH) / monitor.Height;
        var sb = new StringBuilder();
        sb.AppendLine($"UI ELEMENTS on screen ({map.Items.Count}, from Windows accessibility; (x,y) = centre in Image 1). " +
                      "To click, point at or frame one of these, use element=\"N\" instead of x/y: it is exact.");
        foreach (var e in map.Items)
        {
            int x = (int)((e.Rect.X + e.Rect.Width / 2.0 - monitor.X) * sx), y = (int)((e.Rect.Y + e.Rect.Height / 2.0 - monitor.Y) * sy);
            sb.Append('[').Append(e.Id).Append("] ").Append(e.Type);
            if (e.Name.Length > 0) sb.Append(" \"").Append(e.Name).Append('"');
            if (e.Value != null && e.Value != e.Name) sb.Append(" value=\"").Append(e.Value).Append('"');
            if (!e.Enabled) sb.Append(" (disabled)");
            sb.Append(" (").Append(x).Append(',').Append(y).Append(")\n");
        }
        return sb.ToString().TrimEnd();
    }
}
