using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Assist;

/// While files are dragged over the floating button: a fan of session tags above it.
/// The target session is solid; other sessions and "New session" are ghosts. Each tag is a drop zone.
public sealed class DropFan : Window
{
    static readonly Color Cyan = Color.FromRgb(0x5C, 0xE1, 0xFF);
    static readonly Brush Fg = Frozen(Color.FromRgb(0xEC, 0xEC, 0xEE));
    static readonly Brush Muted = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA6));
    static readonly Brush CardBg = Frozen(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x21));
    static readonly Brush GhostBg = Frozen(Color.FromArgb(0xB0, 0x1C, 0x1C, 0x21));
    static readonly Brush CyanBrush = Frozen(Cyan);
    static readonly Brush GhostBorder = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// One drop zone. Record == null and IsNew == false means "the target session".
    public sealed record Zone(Border Pill, SessionRecord? Record, bool IsNew, bool IsTarget);

    readonly DockWindow dock;
    readonly List<Zone> zones = new();
    readonly StackPanel stack = new() { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right };
    Zone? hot;

    public event Action<string[], Zone>? Dropped;
    public event Action? DragEntered;
    public event Action? DragLeft;

    public DropFan(DockWindow dock, SessionStore store)
    {
        this.dock = dock;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Content = new Border { Child = stack, Padding = new Thickness(14, 14, 6, 4), Background = Brushes.Transparent };

        // Top to bottom: New session, other sessions (ghosts), then the target right above the button.
        var target = store.Focused;
        zones.Add(MakeZone(null, isNew: true, isTarget: false));
        foreach (var s in store.NewestFirst.Where(s => s != target).Take(5).Reverse()) zones.Add(MakeZone(s, false, false));
        if (target != null) zones.Add(MakeZone(target, false, true));
        foreach (var z in zones) stack.Children.Add(z.Pill);

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeNoActivate(hwnd);
            FileDropTarget.Register(hwnd, new DropHandlers
            {
                Enter = p => { DragEntered?.Invoke(); Highlight(ZoneAt(p)); },
                Over = p => Highlight(ZoneAt(p)),
                Leave = () => { Highlight(null); DragLeft?.Invoke(); },
                Drop = (files, p) => { var z = ZoneAt(p); if (z != null) Dropped?.Invoke(files, z); },
            });
        };
        Closed += (_, _) => FileDropTarget.Unregister(new WindowInteropHelper(this).Handle);
        SizeChanged += (_, _) => Place();
        Loaded += (_, _) =>
        {
            Place();
            int i = 0;
            foreach (var z in zones.AsEnumerable().Reverse())
            {
                var delay = TimeSpan.FromMilliseconds(35 * i++);
                z.Pill.BeginAnimation(OpacityProperty, new DoubleAnimation(0, z.IsTarget ? 1 : 0.55, TimeSpan.FromMilliseconds(160)) { BeginTime = delay });
                ((TranslateTransform)((TransformGroup)z.Pill.RenderTransform).Children[1])
                    .BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(200)) { BeginTime = delay, EasingFunction = new CubicEase() });
            }
        };
    }

    /// The target session's zone (dropping on the button itself means this).
    public Zone? TargetZone => zones.FirstOrDefault(z => z.IsTarget) ?? zones.FirstOrDefault(z => z.IsNew);

    public void Highlight(Zone? z)
    {
        if (z == hot) return;
        if (hot != null) SetHot(hot, false);
        hot = z;
        if (hot != null) SetHot(hot, true);
    }

    Zone? ZoneAt(Point screen)
    {
        foreach (var z in zones)
        {
            var local = z.Pill.PointFromScreen(screen);
            var r = new Rect(0, -3, z.Pill.ActualWidth, z.Pill.ActualHeight + 6); // include the small gaps
            if (r.Contains(local)) return z;
        }
        return null;
    }

    Zone MakeZone(SessionRecord? s, bool isNew, bool isTarget)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = isNew ? "" : isTarget ? "" : "", FontFamily = Icons, FontSize = 12,
            Foreground = isTarget ? CyanBrush : Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0),
        });
        var title = isNew ? "New session" : s!.Title;
        content.Children.Add(new TextBlock
        {
            Text = title.Length > 38 ? title[..36] + "…" : title, Foreground = Fg, FontSize = 13,
            FontWeight = isTarget ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
        });
        if (isTarget)
            content.Children.Add(new Border
            {
                Child = new TextBlock { Text = "TARGET", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = CyanBrush },
                BorderBrush = Frozen(Color.FromArgb(0x80, 0x5C, 0xE1, 0xFF)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });

        var pill = new Border
        {
            Child = content,
            Background = isTarget ? CardBg : GhostBg,
            BorderBrush = isTarget ? CyanBrush : GhostBorder,
            BorderThickness = new Thickness(isTarget ? 1.5 : 1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 7, 14, 8),
            Margin = new Thickness(0, 3, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = 0,
            RenderTransformOrigin = new Point(1, 0.5),
            Effect = new DropShadowEffect { Color = isTarget ? Cyan : Colors.Black, BlurRadius = isTarget ? 16 : 10, ShadowDepth = isTarget ? 0 : 2, Opacity = isTarget ? 0.55 : 0.4 },
        };
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(1, 1));
        tg.Children.Add(new TranslateTransform());
        pill.RenderTransform = tg;
        return new Zone(pill, s, isNew, isTarget);
    }

    void SetHot(Zone z, bool on)
    {
        var p = z.Pill;
        p.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : (z.IsTarget ? 1 : 0.55), TimeSpan.FromMilliseconds(110)));
        p.BorderBrush = on ? CyanBrush : (z.IsTarget ? CyanBrush : GhostBorder);
        p.Background = on || z.IsTarget ? CardBg : GhostBg;
        var sc = (ScaleTransform)((TransformGroup)p.RenderTransform).Children[0];
        var a = new DoubleAnimation(on ? 1.07 : 1, TimeSpan.FromMilliseconds(110));
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// Right-aligned with the button, its bottom just above it (or below if there is no room).
    void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var d = dock.ScreenRect;
        var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((d.Left + d.Right) / 2, (d.Top + d.Bottom) / 2)).WorkingArea;
        int x = Math.Clamp(d.Right - w - 4, wa.Left, Math.Max(wa.Left, wa.Right - w));
        int y = d.Top - h + 18;
        if (y < wa.Top) y = d.Bottom - 10;
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
