using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Assist;

/// Ghost tags above the floating button showing the commands/tools the agent is running (newest at the bottom).
/// At most 5. Hovering one expands it to the whole command; clicking copies it.
public sealed class CommandFan : Window
{
    const int Max = 5;
    static readonly TimeSpan LingerAfterDone = TimeSpan.FromSeconds(8);

    static readonly Color Cyan = Color.FromRgb(0x5C, 0xE1, 0xFF);
    static readonly Brush Fg = Frozen(Color.FromRgb(0xEC, 0xEC, 0xEE));
    static readonly Brush Muted = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA6));
    static readonly Brush CardBg = Frozen(Color.FromArgb(0xF4, 0x1C, 0x1C, 0x21));
    static readonly Brush GhostBg = Frozen(Color.FromArgb(0xB8, 0x1C, 0x1C, 0x21));
    static readonly Brush CyanBrush = Frozen(Cyan);
    static readonly Brush GhostBorder = Frozen(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    sealed class Cmd
    {
        public required SessionRecord Rec;
        public required Border Pill;
        public required TextBlock Line;
        public required StackPanel Details;
        public required Ellipse Dot;
        public bool Running = true;
        public DateTime Expires = DateTime.MaxValue;
        public bool Leaving;
    }

    readonly DockWindow dock;
    readonly List<Cmd> cmds = new();
    readonly StackPanel stack = new() { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right };
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(400) };

    public bool IsEmpty => cmds.All(c => c.Leaving);

    public CommandFan(DockWindow dock)
    {
        this.dock = dock;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = null; // empty areas let clicks through
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        // Fixed size, never resized: tags grow inside it on hover, so nothing jumps under the mouse.
        // Empty (transparent) areas don't take clicks or hover.
        Width = 640; Height = 600;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        stack.VerticalAlignment = VerticalAlignment.Bottom;
        stack.Margin = new Thickness(0, 0, 6, 2);
        Content = new Grid { Children = { stack } };

        SourceInitialized += (_, _) =>
        {
            Native.MakeNoActivate(new WindowInteropHelper(this).Handle);
            Place();
        };
        tick.Tick += (_, _) => Expire();
        tick.Start();
        Closed += (_, _) => tick.Stop();
    }

    public void Add(SessionRecord rec, string tool, string detail)
    {
        foreach (var c in cmds.Where(c => c.Rec == rec)) SetRunning(c, false); // the previous one finished
        var cmd = Make(rec, tool, detail);
        cmds.Add(cmd);
        stack.Children.Add(cmd.Pill);
        cmd.Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.9, TimeSpan.FromMilliseconds(180)));
        ((TranslateTransform)((TransformGroup)cmd.Pill.RenderTransform).Children[1])
            .BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase() });

        var live = cmds.Where(c => !c.Leaving).ToList();
        foreach (var old in live.Take(Math.Max(0, live.Count - Max))) Remove(old);
        Ghosts();
    }

    /// The session's turn ended: its commands linger a few seconds, then fade.
    public void Done(SessionRecord rec)
    {
        foreach (var c in cmds.Where(c => c.Rec == rec && !c.Leaving))
        {
            SetRunning(c, false);
            c.Expires = DateTime.Now + LingerAfterDone;
        }
    }

    void Expire()
    {
        var now = DateTime.Now;
        foreach (var c in cmds.Where(c => !c.Leaving && now >= c.Expires && !c.Pill.IsMouseOver).ToList()) Remove(c);
        // safety: a session that vanished without finishing
        foreach (var c in cmds.Where(c => !c.Leaving && c.Running && c.Rec.Window == null).ToList()) Remove(c);
        if (cmds.Count == 0) Close();
    }

    void Remove(Cmd c)
    {
        c.Leaving = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { stack.Children.Remove(c.Pill); cmds.Remove(c); if (cmds.Count == 0) Close(); };
        c.Pill.BeginAnimation(OpacityProperty, fade);
    }

    /// Older tags are fainter; the newest is nearly solid.
    void Ghosts()
    {
        var live = cmds.Where(c => !c.Leaving).ToList();
        for (int i = 0; i < live.Count; i++)
        {
            var c = live[i];
            if (c.Pill.IsMouseOver) continue;
            int age = live.Count - 1 - i; // 0 = newest
            double o = age == 0 ? 0.9 : 0.68 - 0.08 * age;
            c.Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(o, TimeSpan.FromMilliseconds(160)));
        }
    }

    Cmd Make(SessionRecord rec, string tool, string detail)
    {
        bool shell = tool is "Bash" or "PowerShell" or "BashOutput";
        bool json = detail.StartsWith('{');
        string oneLine = json ? tool : string.Join(" ⏎ ", detail.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

        var dot = new Ellipse { Width = 6, Height = 6, Fill = CyanBrush, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { Color = Cyan, BlurRadius = 8, ShadowDepth = 0, Opacity = 1 } };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

        var icon = new TextBlock
        {
            Text = tool switch
            {
                "Bash" or "PowerShell" or "BashOutput" => "",           // command prompt
                "Read" or "NotebookRead" => "",                        // document
                "Write" or "Edit" or "MultiEdit" or "NotebookEdit" => "", // edit
                "Grep" or "Glob" => "",                                // search
                "WebFetch" or "WebSearch" or "web_search" or "web_fetch" => "", // globe
                _ => "",                                                // tool
            },
            FontFamily = Icons, FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0),
        };
        var line = new TextBlock
        {
            Text = oneLine, Foreground = Fg, FontSize = shell ? 12 : 12.5, FontFamily = shell ? Mono : FontFamily,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300, VerticalAlignment = VerticalAlignment.Center,
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(dot);
        head.Children.Add(icon);
        head.Children.Add(line);

        // shown on hover: which session/tool, then the whole command
        var details = new StackPanel { Visibility = Visibility.Collapsed, MaxWidth = 560 };
        details.Children.Add(new TextBlock { Text = $"{tool}  ·  {rec.Title}", Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 5), TextTrimming = TextTrimming.CharacterEllipsis });
        details.Children.Add(new TextBlock
        {
            Text = detail.Length > 4000 ? detail[..4000] + "…" : detail, Foreground = Fg, FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
        details.Children.Add(new TextBlock { Text = "Click to copy  ·  right-click to dismiss", Foreground = Muted, FontSize = 10.5, Margin = new Thickness(0, 6, 0, 0) });

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(details);

        var pill = new Border
        {
            Child = body,
            Background = GhostBg,
            BorderBrush = GhostBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(11, 6, 13, 7),
            Margin = new Thickness(0, 3, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = 0,
            Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(1, 1),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 10, ShadowDepth = 2, Opacity = 0.4 },
        };
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(1, 1));
        tg.Children.Add(new TranslateTransform());
        pill.RenderTransform = tg;

        var cmd = new Cmd { Rec = rec, Pill = pill, Line = line, Details = details, Dot = dot };
        pill.MouseEnter += (_, _) =>
        {
            if (cmd.Leaving) return;
            pill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
            pill.Background = CardBg;
            pill.BorderBrush = CyanBrush;
            head.Visibility = Visibility.Collapsed;
            details.Visibility = Visibility.Visible;
        };
        pill.MouseLeave += (_, _) =>
        {
            if (cmd.Leaving) return;
            pill.Background = GhostBg;
            pill.BorderBrush = GhostBorder;
            head.Visibility = Visibility.Visible;
            details.Visibility = Visibility.Collapsed;
            ((TextBlock)details.Children[2]).Text = "Click to copy  ·  right-click to dismiss";
            if (cmd.Expires != DateTime.MaxValue && cmd.Expires < DateTime.Now + TimeSpan.FromSeconds(2))
                cmd.Expires = DateTime.Now + TimeSpan.FromSeconds(2); // don't vanish the instant the mouse leaves
            Ghosts();
        };
        pill.MouseLeftButtonUp += (_, _) =>
        {
            try { Clipboard.SetText(detail); ((TextBlock)details.Children[2]).Text = "Copied ✓"; } catch { }
        };
        // right-click: dismiss all the ghosts
        pill.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            foreach (var c in cmds.Where(c => !c.Leaving).ToList()) Remove(c);
        };
        return cmd;
    }

    void SetRunning(Cmd c, bool on)
    {
        c.Running = on;
        if (on) return;
        c.Dot.BeginAnimation(OpacityProperty, null);
        c.Dot.Fill = Muted;
        c.Dot.Effect = null;
        c.Dot.Opacity = 0.6;
    }

    /// Right-aligned with the button, bottom just above it (or below if there is no room).
    public void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var d = dock.ScreenRect;
        var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((d.Left + d.Right) / 2, (d.Top + d.Bottom) / 2)).WorkingArea;
        int x = Math.Clamp(d.Right - w - 4, wa.Left, Math.Max(wa.Left, wa.Right - w));
        int y = d.Top - h + 14;
        bool below = d.Top - wa.Top < 160; // no room above the button: hang the tags below it
        if (below) y = d.Bottom - 8;
        stack.VerticalAlignment = below ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
