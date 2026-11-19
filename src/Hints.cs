using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Assist;

/// A hint tag from the agent, e.g. &lt;hint type="point" x="10" y="20" label="Here"/&gt;.
public sealed class HintTag
{
    public Dictionary<string, string> Attr { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Type => Attr.TryGetValue("type", out var t) ? t.ToLowerInvariant() : "";
    public string? Label => Attr.TryGetValue("label", out var l) && l.Trim().Length > 0 ? l.Trim() : null;
    public double Num(string key) =>
        Attr.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    public int Image => Attr.TryGetValue("image", out var v) && v.Trim() == "2" ? 2 : 1;
}

/// Streaming filter: removes &lt;hint .../&gt; tags from the agent's text (even when split across chunks)
/// and hands them out as HintTags. Everything else passes through unchanged.
public sealed class HintFilter
{
    static readonly Regex AttrRx = new("(\\w+)\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled);
    const string Open = "<hint";
    readonly StringBuilder hold = new();

    public string Feed(string chunk, List<HintTag> found)
    {
        var output = new StringBuilder(chunk.Length);
        foreach (char ch in chunk)
        {
            if (hold.Length == 0)
            {
                if (ch == '<') hold.Append(ch); else output.Append(ch);
                continue;
            }
            hold.Append(ch);
            if (hold.Length <= Open.Length)
            {
                if (!Open.StartsWith(hold.ToString(), StringComparison.OrdinalIgnoreCase)) { output.Append(hold); hold.Clear(); }
                continue;
            }
            if (ch == '>')
            {
                var tag = new HintTag();
                foreach (Match m in AttrRx.Matches(hold.ToString())) tag.Attr[m.Groups[1].Value] = m.Groups[2].Value;
                found.Add(tag);
                hold.Clear();
            }
            else if (hold.Length > 600) { output.Append(hold); hold.Clear(); } // not a real tag
        }
        return output.ToString();
    }

    public string Flush() { var s = hold.ToString(); hold.Clear(); return s; }
    public void Reset() => hold.Clear();
}

/// Maps screenshot pixel coordinates (Image 1 or the Image 2 crop) back to physical screen pixels.
public sealed class ScreenMap
{
    public System.Drawing.Rectangle Monitor { get; }
    readonly double sx, sy;
    readonly int cropX, cropY;
    readonly bool hasCrop;

    public ScreenMap(CapturedContext c)
    {
        Monitor = c.Monitor;
        sx = (double)c.Monitor.Width / Math.Max(1, c.FullW);
        sy = (double)c.Monitor.Height / Math.Max(1, c.FullH);
        hasCrop = c.CropJpeg != null;
        cropX = c.CropX; cropY = c.CropY;
    }

    public Point ToScreen(double x, double y, int image) =>
        image == 2 && hasCrop
            ? new Point(Monitor.X + cropX + x, Monitor.Y + cropY + y)
            : new Point(Monitor.X + x * sx, Monitor.Y + y * sy);

    public double ScaleLength(double v, int image) => image == 2 && hasCrop ? v : v * sx;
}

/// Transparent, click-through, always-on-top window over one monitor that draws hints:
/// a cartoon pointing hand, a glowing underline, or a glowing frame. Each hint fades out after
/// its time is up or as soon as the mouse reaches it.
public sealed class HintOverlay : Window
{
    static readonly Color Glow = Color.FromRgb(0x5C, 0xE1, 0xFF);
    static readonly Brush GlowBrush = Frozen(Glow);
    static readonly Brush Ink = Frozen(Color.FromRgb(0x1B, 0x1E, 0x2B));
    static readonly Brush Glove = Frozen(Colors.White);
    static readonly Brush Cuff = Frozen(Color.FromRgb(0xE6, 0xEA, 0xF4));

    sealed class Item
    {
        public required FrameworkElement Visual;
        public required Func<Point, bool> Hit;   // cursor (in overlay DIPs) is on the target
        public DateTime Expires;
        public bool Leaving;
    }

    readonly System.Drawing.Rectangle monitor;
    readonly List<Item> items = new();
    readonly List<(HintTag tag, ScreenMap map)> queued = new();
    readonly Canvas canvas = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    double dpi = 1;
    bool ready;

    public HintOverlay(System.Drawing.Rectangle monitor)
    {
        this.monitor = monitor;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000; Width = 10; Height = 10;
        Content = canvas;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeClickThrough(hwnd);
            Native.SetWindowPos(hwnd, IntPtr.Zero, monitor.X, monitor.Y, monitor.Width, monitor.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        };
        Loaded += (_, _) =>
        {
            dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            ready = true;
            foreach (var (t, m) in queued) AddNow(t, m);
            queued.Clear();
        };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    public System.Drawing.Rectangle MonitorRect => monitor;

    public void Add(HintTag tag, ScreenMap map)
    {
        if (ready) AddNow(tag, map); else queued.Add((tag, map));
    }

    // ---------- building hints ----------

    void AddNow(HintTag tag, ScreenMap map)
    {
        double seconds = Math.Clamp(double.IsNaN(tag.Num("seconds")) ? 10 : tag.Num("seconds"), 2, 30);
        Item? item = tag.Type switch
        {
            "point" or "pointer" or "click" => PointAt(tag, map),
            "underline" or "line" => Underline(tag, map),
            "box" or "frame" or "highlight" or "rect" => Box(tag, map),
            _ => null,
        };
        if (item == null) return;
        item.Expires = DateTime.Now.AddSeconds(seconds);
        item.Visual.Opacity = 0;
        canvas.Children.Add(item.Visual);
        item.Visual.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        items.Add(item);
    }

    Point Local(double x, double y, int image, ScreenMap map)
    {
        var p = map.ToScreen(x, y, image);
        return new Point((p.X - monitor.X) / dpi, (p.Y - monitor.Y) / dpi);
    }

    Item? PointAt(HintTag t, ScreenMap map)
    {
        double x = t.Num("x"), y = t.Num("y");
        if (double.IsNaN(x) || double.IsNaN(y)) return null;
        var p = Local(x, y, t.Image, map);
        double w = monitor.Width / dpi, h = monitor.Height / dpi;
        bool flipX = p.X > w - 150, flipY = p.Y > h - 170;

        var root = new Canvas();

        // pulsing target ring + dot
        var ring = new Ellipse { Width = 26, Height = 26, Stroke = GlowBrush, StrokeThickness = 3, RenderTransformOrigin = new Point(0.5, 0.5) };
        var rs = new ScaleTransform(0.5, 0.5);
        ring.RenderTransform = rs;
        Canvas.SetLeft(ring, p.X - 13); Canvas.SetTop(ring, p.Y - 13);
        var pulse = new DoubleAnimation(0.5, 1.9, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever };
        rs.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        rs.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        ring.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever });
        var dot = new Ellipse { Width = 8, Height = 8, Fill = GlowBrush, Effect = new DropShadowEffect { Color = Glow, BlurRadius = 12, ShadowDepth = 0, Opacity = 1 } };
        Canvas.SetLeft(dot, p.X - 4); Canvas.SetTop(dot, p.Y - 4);
        root.Children.Add(ring);
        root.Children.Add(dot);

        // the hand, fingertip at (0,0) pointing up, then tilted/flipped so it stays on screen
        var hand = MakeHand();
        var bob = new TranslateTransform(0, 10);
        double angle = flipY ? (flipX ? 208 : 152) : (flipX ? 28 : -28);
        var tg = new TransformGroup();
        tg.Children.Add(new TranslateTransform(0, 6));                 // keep the tip just off the target
        tg.Children.Add(bob);
        tg.Children.Add(new ScaleTransform(flipX ^ flipY ? -1.15 : 1.15, 1.15));
        tg.Children.Add(new RotateTransform(angle));
        tg.Children.Add(new TranslateTransform(p.X, p.Y));
        hand.RenderTransform = tg;
        bob.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(420))
        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        root.Children.Add(hand);

        if (t.Label != null)
        {
            var label = MakeLabel(t.Label);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double lx = flipX ? p.X - label.DesiredSize.Width - 70 : p.X + 70;
            double ly = flipY ? p.Y - 120 : p.Y + 92;
            Canvas.SetLeft(label, Math.Clamp(lx, 4, Math.Max(4, w - label.DesiredSize.Width - 4)));
            Canvas.SetTop(label, Math.Clamp(ly, 4, Math.Max(4, h - label.DesiredSize.Height - 4)));
            root.Children.Add(label);
        }

        return new Item { Visual = root, Hit = c => (c - p).Length < 30 };
    }

    Item? Underline(HintTag t, ScreenMap map)
    {
        double x1 = t.Num("x1"), y1 = t.Num("y1"), x2 = t.Num("x2"), y2 = t.Num("y2");
        if (new[] { x1, y1, x2, y2 }.Any(double.IsNaN)) return null;
        var a = Local(x1, y1, t.Image, map);
        var b = Local(x2, y2, t.Image, map);
        double len = Math.Max(1, (b - a).Length);

        var root = new Canvas();
        var glow = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = GlowBrush, StrokeThickness = 12, Opacity = 0.35,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Effect = new BlurEffect { Radius = 10 } };
        var core = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = GlowBrush, StrokeThickness = 3.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Effect = new DropShadowEffect { Color = Glow, BlurRadius = 10, ShadowDepth = 0, Opacity = 1 } };
        // draw-in animation
        double units = len / core.StrokeThickness;
        core.StrokeDashArray = new DoubleCollection { units, units };
        core.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(units, 0, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase() });
        glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 0.5, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        root.Children.Add(glow);
        root.Children.Add(core);

        if (t.Label != null)
        {
            var label = MakeLabel(t.Label);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Max(4, Math.Min(a.X, b.X)));
            Canvas.SetTop(label, Math.Max(4, Math.Min(a.Y, b.Y) - label.DesiredSize.Height - 10));
            root.Children.Add(label);
        }

        return new Item { Visual = root, Hit = c => DistToSegment(c, a, b) < 18 };
    }

    Item? Box(HintTag t, ScreenMap map)
    {
        double x = t.Num("x"), y = t.Num("y"), bw = t.Num("w"), bh = t.Num("h");
        if (new[] { x, y, bw, bh }.Any(double.IsNaN) || bw <= 0 || bh <= 0) return null;
        var a = Local(x, y, t.Image, map);
        var b = Local(x + bw, y + bh, t.Image, map);
        var r = new Rect(a, b);
        r.Inflate(4, 4);

        var root = new Canvas();
        var glow = new Rectangle { Width = r.Width, Height = r.Height, RadiusX = 10, RadiusY = 10, Stroke = GlowBrush, StrokeThickness = 10,
            Opacity = 0.35, Effect = new BlurEffect { Radius = 12 } };
        var frame = new Rectangle { Width = r.Width, Height = r.Height, RadiusX = 10, RadiusY = 10, Stroke = GlowBrush, StrokeThickness = 3,
            Fill = Frozen(Color.FromArgb(0x16, 0x5C, 0xE1, 0xFF)), Effect = new DropShadowEffect { Color = Glow, BlurRadius = 12, ShadowDepth = 0, Opacity = 1 } };
        foreach (var s in new Shape[] { glow, frame }) { Canvas.SetLeft(s, r.X); Canvas.SetTop(s, r.Y); root.Children.Add(s); }
        frame.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.55, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

        if (t.Label != null)
        {
            var label = MakeLabel(t.Label);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Math.Max(4, r.X));
            double top = r.Y - label.DesiredSize.Height - 8;
            Canvas.SetTop(label, top >= 4 ? top : r.Bottom + 8);
            root.Children.Add(label);
        }

        var hit = r; hit.Inflate(6, 6);
        return new Item { Visual = root, Hit = c => hit.Contains(c) };
    }

    /// A white cartoon glove pointing up, fingertip at (0,0).
    static Canvas MakeHand()
    {
        var c = new Canvas { Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 3, Direction = 300, Opacity = 0.45, Color = Colors.Black } };
        Rectangle Part(double x, double y, double w, double h, double r, Brush? fill = null, double angle = 0)
        {
            var s = new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Fill = fill ?? Glove, Stroke = Ink, StrokeThickness = 2.4 };
            if (angle != 0) { s.RenderTransformOrigin = new Point(0.5, 0.5); s.RenderTransform = new RotateTransform(angle); }
            Canvas.SetLeft(s, x); Canvas.SetTop(s, y);
            c.Children.Add(s);
            return s;
        }
        Part(-7, 0, 14, 48, 7);                 // index finger
        Part(-17, 44, 14, 26, 7, angle: -22);   // thumb
        Part(-10, 32, 42, 40, 14);              // fist
        Part(9, 35, 27, 12, 6);                 // folded fingers
        Part(9, 46, 27, 12, 6);
        Part(9, 57, 25, 12, 6);
        Part(-12, 68, 46, 14, 4, Cuff);         // glove cuff
        var shine = new Rectangle { Width = 4, Height = 16, RadiusX = 2, RadiusY = 2, Fill = Frozen(Color.FromArgb(0x30, 0x5C, 0xE1, 0xFF)) };
        Canvas.SetLeft(shine, -3); Canvas.SetTop(shine, 6);
        c.Children.Add(shine);
        return c;
    }

    static Border MakeLabel(string text) => new()
    {
        Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13.5, FontWeight = FontWeights.SemiBold, MaxWidth = 280, TextWrapping = TextWrapping.Wrap },
        Background = Frozen(Color.FromArgb(0xEE, 0x1C, 0x1C, 0x21)),
        BorderBrush = GlowBrush, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(10),
        Padding = new Thickness(10, 5, 10, 6),
        Effect = new DropShadowEffect { Color = Glow, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.6 },
    };

    // ---------- lifetime ----------

    void Tick()
    {
        if (!ready) return;
        Native.GetCursorPos(out var cp);
        var cursor = new Point((cp.X - monitor.X) / dpi, (cp.Y - monitor.Y) / dpi);
        var now = DateTime.Now;
        foreach (var it in items.Where(i => !i.Leaving).ToList())
        {
            if (now >= it.Expires || it.Hit(cursor)) Leave(it);
        }
        if (items.Count == 0 && queued.Count == 0) Close();
    }

    void Leave(Item it)
    {
        it.Leaving = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => { canvas.Children.Remove(it.Visual); items.Remove(it); };
        it.Visual.BeginAnimation(OpacityProperty, fade);
    }

    static double DistToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double t = ab.LengthSquared == 0 ? 0 : Math.Clamp(((p - a) * ab) / ab.LengthSquared, 0, 1);
        return (p - (a + t * ab)).Length;
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
