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
    /// "hint", "action" or "request-control".
    public string Name { get; set; } = "hint";
    public Dictionary<string, string> Attr { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Get(string key) => Attr.TryGetValue(key, out var v) ? v : null;
    public string Type => Attr.TryGetValue("type", out var t) ? t.ToLowerInvariant() : "";
    public string? Label => Attr.TryGetValue("label", out var l) && l.Trim().Length > 0 ? l.Trim() : null;
    public double Num(string key) =>
        Attr.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    public int Image => Attr.TryGetValue("image", out var v) && v.Trim() == "2" ? 2 : 1;
}

/// Streaming filter: removes Computah tags (&lt;hint/&gt;, &lt;action/&gt;, &lt;request-control/&gt;) from the
/// agent's text, even when split across chunks, and hands them out. Everything else passes through unchanged.
public sealed class HintFilter
{
    static readonly Regex AttrRx = new("([\\w-]+)\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled);
    static readonly string[] Names = { "hint", "action", "request-control", "request-access" };
    readonly StringBuilder hold = new();
    string? name; // tag name once recognised
    bool inQuote;

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
            if (name == null)
            {
                var typed = hold.ToString(1, hold.Length - 1);
                var exact = Names.FirstOrDefault(n => typed.Length == n.Length + 1 && typed.StartsWith(n, StringComparison.OrdinalIgnoreCase)
                                                      && (char.IsWhiteSpace(ch) || ch == '/' || ch == '>'));
                if (exact != null) name = exact;
                else if (!Names.Any(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase))) { output.Append(hold); hold.Clear(); continue; }
                else continue;
            }
            if (ch == '"') inQuote = !inQuote;
            if (ch == '>' && !inQuote)
            {
                var tag = new HintTag { Name = name };
                foreach (Match m in AttrRx.Matches(hold.ToString()))
                    tag.Attr[m.Groups[1].Value] = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value);
                found.Add(tag);
                hold.Clear();
                name = null;
            }
            else if (hold.Length > 2000) { output.Append(hold); hold.Clear(); name = null; inQuote = false; } // not a real tag
        }
        return output.ToString();
    }

    public string Flush() { var s = hold.ToString(); hold.Clear(); name = null; inQuote = false; return s; }
    public void Reset() { hold.Clear(); name = null; inQuote = false; }
}

/// Maps screenshot pixel coordinates (Image 1 or the Image 2 crop) back to physical screen pixels.
public sealed class ScreenMap
{
    public System.Drawing.Rectangle Monitor { get; }
    readonly double sx, sy;
    readonly int cropX, cropY;
    readonly bool hasCrop;

    ScreenMap(System.Drawing.Rectangle monitor)
    {
        Monitor = monitor;
        sx = sy = 1;
    }

    /// Coordinates are already physical pixels relative to the monitor's top-left corner.
    public static ScreenMap Identity(System.Drawing.Rectangle monitor) => new(monitor);

    public ScreenMap(CapturedContext c)
    {
        Monitor = c.Monitor;
        sx = (double)c.Monitor.Width / Math.Max(1, c.FullW);
        sy = (double)c.Monitor.Height / Math.Max(1, c.FullH);
        hasCrop = c.CropJpeg != null;
        cropX = c.CropX; cropY = c.CropY;
        Elements = c.Elements;
    }

    /// The focused window's numbered elements at capture time (element="N" in hints and actions).
    public UiMap? Elements { get; init; }

    /// Turns element="N" into the coordinates the hint or action uses (Image 1): the element's centre, its box for
    /// frames, its bottom edge for underlines. False when the tag names an element that isn't in the list.
    public bool ResolveElement(HintTag t, string kind)
    {
        var raw = t.Get("element") ?? t.Get("el");
        if (raw == null) return true;
        if (!int.TryParse(raw.Trim().TrimStart('#').Trim('[', ']'), out var id) || Elements?.Get(id) is not { } e) return false;
        double X(double px) => (px - Monitor.X) / sx;
        double Y(double py) => (py - Monitor.Y) / sy;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string F(double v) => Math.Round(v, 1).ToString(inv);
        var r = e.Rect;
        t.Attr["image"] = "1";
        switch (kind)
        {
            case "box" or "frame" or "highlight" or "rect":
                t.Attr["x"] = F(X(r.Left)); t.Attr["y"] = F(Y(r.Top)); t.Attr["w"] = F(r.Width / sx); t.Attr["h"] = F(r.Height / sy);
                break;
            case "underline" or "line":
                t.Attr["x1"] = F(X(r.Left)); t.Attr["x2"] = F(X(r.Right)); t.Attr["y1"] = t.Attr["y2"] = F(Y(r.Bottom + 2));
                break;
            default:
                t.Attr["x"] = F(X(r.X + r.Width / 2.0)); t.Attr["y"] = F(Y(r.Y + r.Height / 2.0));
                break;
        }
        return true;
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
        public Window? CloseButton;   // drawings: a small clickable × (the overlay itself lets clicks through)
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
        if (!map.ResolveElement(tag, tag.Type)) { Paths.Log($"hint: element {tag.Get("element")} not found"); return; }
        bool drawing = tag.Type is "draw" or "drawing" or "sketch" or "svg";
        double seconds = Math.Clamp(double.IsNaN(tag.Num("seconds")) ? 10 : tag.Num("seconds"), 2, drawing ? 120 : 30);
        Item? item = tag.Type switch
        {
            "point" or "pointer" or "click" => PointAt(tag, map),
            "underline" or "line" => Underline(tag, map),
            "box" or "frame" or "highlight" or "rect" => Box(tag, map),
            "draw" or "drawing" or "sketch" or "svg" => Draw(tag, map),
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
        tg.Children.Add(new ScaleTransform(flipX ^ flipY ? -0.9 : 0.9, 0.9));
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

    /// A drawing from the agent: SVG path data (`d`, in a `viewbox`, default 0 0 100 100), drawn as a glowing line that
    /// traces itself in. Placed in the x/y/w/h box (screenshot pixels) if given, else in the middle of the screen.
    Item? Draw(HintTag t, ScreenMap map)
    {
        var d = t.Get("d") ?? t.Get("path") ?? t.Get("svg");
        if (string.IsNullOrWhiteSpace(d)) return null;
        Geometry src;
        try { src = Geometry.Parse(d.Trim()); }
        catch (Exception ex) { Paths.Log("draw: bad path: " + ex.Message); return null; }

        // the drawing's own coordinate box
        Rect view = src.Bounds;
        if (t.Get("viewbox") is { } vb)
        {
            var n = vb.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
            if (n.Length == 4 && n.All(v => !double.IsNaN(v)) && n[2] > 0 && n[3] > 0) view = new Rect(n[0], n[1], n[2], n[3]);
        }
        if (view.IsEmpty || view.Width <= 0 || view.Height <= 0) return null;

        // where it goes on screen
        double sw = monitor.Width / dpi, sh = monitor.Height / dpi;
        Rect box;
        double x = t.Num("x"), y = t.Num("y"), w = t.Num("w"), h = t.Num("h");
        if (!double.IsNaN(x) && !double.IsNaN(y) && !double.IsNaN(w) && !double.IsNaN(h) && w > 0 && h > 0)
            box = new Rect(Local(x, y, t.Image, map), Local(x + w, y + h, t.Image, map));
        else
        {
            double size = Math.Min(sw, sh) * 0.5;
            box = new Rect((sw - size) / 2, (sh - size) / 2 - sh * 0.04, size, size);
        }
        double scale = Math.Min(box.Width / view.Width, box.Height / view.Height);
        double ox = box.X + (box.Width - view.Width * scale) / 2 - view.X * scale;
        double oy = box.Y + (box.Height - view.Height * scale) / 2 - view.Y * scale;
        var geo = src.Clone();
        geo.Transform = new MatrixTransform(scale, 0, 0, scale, ox, oy);

        var color = Glow;
        if (t.Get("color") is { Length: > 0 } cs)
            try { color = (Color)ColorConverter.ConvertFromString(cs.Trim()); } catch { }
        var brush = Frozen(color);
        double thick = Math.Clamp(double.IsNaN(t.Num("stroke")) ? 4 : t.Num("stroke"), 1, 20);

        // total line length, for the trace-in animation
        double length = 0;
        foreach (var fig in geo.GetFlattenedPathGeometry().Figures)
        {
            var prev = fig.StartPoint;
            foreach (var seg in fig.Segments)
            {
                IEnumerable<Point> pts = seg switch
                {
                    PolyLineSegment pl => pl.Points,
                    LineSegment ls => new[] { ls.Point },
                    _ => Array.Empty<Point>(),
                };
                foreach (var p in pts) { length += (p - prev).Length; prev = p; }
            }
            if (fig.IsClosed) length += (fig.StartPoint - prev).Length;
        }
        double units = Math.Max(1, length / thick);
        var drawTime = TimeSpan.FromMilliseconds(Math.Clamp(length * 1.6, 700, 3200));

        var root = new Canvas();
        if (t.Get("fill") is { Length: > 0 } fs && fs.Trim().ToLowerInvariant() != "none")
        {
            Color fc = color;
            try { fc = (Color)ColorConverter.ConvertFromString(fs.Trim()); } catch { }
            var fill = new Path { Data = geo, Fill = Frozen(Color.FromArgb(0x55, fc.R, fc.G, fc.B)), Opacity = 0 };
            fill.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { BeginTime = drawTime });
            root.Children.Add(fill);
        }
        var glow = new Path
        {
            Data = geo, Stroke = brush, StrokeThickness = thick * 3, Opacity = 0.3, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Effect = new BlurEffect { Radius = thick * 3 },
        };
        var core = new Path
        {
            Data = geo, Stroke = brush, StrokeThickness = thick, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeDashArray = new DoubleCollection { units, units }, StrokeDashOffset = units,
            Effect = new DropShadowEffect { Color = color, BlurRadius = thick * 3, ShadowDepth = 0, Opacity = 0.9 },
        };
        core.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(units, 0, drawTime) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        // the glow follows once the line is drawn, then breathes
        glow.Opacity = 0;
        glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 0.4, TimeSpan.FromMilliseconds(1100))
        { BeginTime = drawTime, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        root.Children.Add(glow);
        root.Children.Add(core);

        if (t.Label != null)
        {
            var label = MakeLabel(t.Label);
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var b = geo.Bounds;
            Canvas.SetLeft(label, Math.Clamp(b.X + (b.Width - label.DesiredSize.Width) / 2, 4, Math.Max(4, sw - label.DesiredSize.Width - 4)));
            Canvas.SetTop(label, Math.Clamp(b.Bottom + 14, 4, Math.Max(4, sh - label.DesiredSize.Height - 4)));
            root.Children.Add(label);
        }
        var item = new Item { Visual = root, Hit = _ => false }; // stays until its time is up or the user closes it
        var gb = geo.Bounds;
        item.CloseButton = MakeCloseButton(new Point(Math.Min(gb.Right + 6, sw - 34), Math.Max(gb.Top - 30, 4)), () => { if (!item.Leaving) Leave(item); });
        return item;
    }

    /// A white cartoon glove pointing up, fingertip at (0,0).
    static Canvas MakeHand()
    {
        var c = new Canvas { Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 3, Direction = 300, Opacity = 0.45, Color = Colors.Black } };
        static Geometry R(double x, double y, double w, double h, double r, double angle = 0)
        {
            var g = new RectangleGeometry(new Rect(x, y, w, h), r, r);
            if (angle != 0) g.Transform = new RotateTransform(angle, x + w / 2, y + h / 2);
            return g;
        }
        // One merged silhouette so there are no inner outlines where the parts overlap.
        Geometry body = R(-8, 0, 16, 54, 8);                                  // index finger
        foreach (var part in new[]
        {
            R(-13, 34, 50, 40, 13),                                          // palm
            R(8, 31, 10, 18, 5), R(18, 32, 10, 18, 5), R(28, 34, 9.5, 16, 4.75), // curled fingers (knuckles)
            R(-23, 40, 15, 28, 7.5, -28),                                    // thumb
        })
            body = Geometry.Combine(body, part, GeometryCombineMode.Union, null);
        c.Children.Add(new Path { Data = body, Fill = Glove, Stroke = Ink, StrokeThickness = 2.4, StrokeLineJoin = PenLineJoin.Round });

        // creases: between the finger and the knuckles, between knuckles, and the thumb's edge
        var creases = new StreamGeometry();
        using (var g = creases.Open())
        {
            foreach (var x in new[] { 8.0, 18, 28 }) { g.BeginFigure(new Point(x, x == 8 ? 36 : 35), false, false); g.LineTo(new Point(x, 43), true, false); }
            g.BeginFigure(new Point(-9, 49), false, false);
            g.QuadraticBezierTo(new Point(-2, 60), new Point(8, 61), true, false);
        }
        c.Children.Add(new Path { Data = creases, Stroke = Ink, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });

        var cuff = new Rectangle { Width = 46, Height = 14, RadiusX = 4, RadiusY = 4, Fill = Cuff, Stroke = Ink, StrokeThickness = 2.4 };
        Canvas.SetLeft(cuff, -10); Canvas.SetTop(cuff, 71);
        c.Children.Add(cuff);

        var shine = new Rectangle { Width = 4, Height = 18, RadiusX = 2, RadiusY = 2, Fill = Frozen(Color.FromArgb(0x38, 0x5C, 0xE1, 0xFF)) };
        Canvas.SetLeft(shine, -3.5); Canvas.SetTop(shine, 6);
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
        try { it.CloseButton?.Close(); } catch { }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => { canvas.Children.Remove(it.Visual); items.Remove(it); };
        it.Visual.BeginAnimation(OpacityProperty, fade);
    }

    /// A round × button in its own small clickable window, at a point of this overlay (DIPs).
    Window MakeCloseButton(Point at, Action close)
    {
        const double size = 28;
        var glyph = new TextBlock
        {
            Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11,
            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var dot = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = glyph,
            Background = Frozen(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x21)), BorderBrush = GlowBrush, BorderThickness = new Thickness(1.2),
            Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Close the drawing",
            Effect = new DropShadowEffect { Color = Glow, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.6 },
        };
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
            ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, Width = size + 8, Height = size + 8,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            Content = new Grid { Children = { dot } }, Opacity = 0,
        };
        dot.MouseEnter += (_, _) => dot.Background = Frozen(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x33));
        dot.MouseLeave += (_, _) => dot.Background = Frozen(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x21));
        dot.MouseLeftButtonUp += (_, _) => close();
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            Native.MakeNoActivate(hwnd);
            Native.SetWindowPos(hwnd, IntPtr.Zero, monitor.X + (int)Math.Round((at.X - 4) * dpi), monitor.Y + (int)Math.Round((at.Y - 4) * dpi), 0, 0,
                                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        };
        w.Show();
        w.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)) { BeginTime = TimeSpan.FromMilliseconds(400) });
        Closed += (_, _) => { try { w.Close(); } catch { } }; // the overlay goes away: so does the button
        return w;
    }

    static double DistToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double t = ab.LengthSquared == 0 ? 0 : Math.Clamp(((p - a) * ab) / ab.LengthSquared, 0, 1);
        return (p - (a + t * ab)).Length;
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
