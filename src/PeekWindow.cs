using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace Assist;

/// A file or folder the agent "shows" the user: a card popping up on screen with the name, the path, and the
/// content scrolled to the exact spot (a glowing highlight on the line/characters). Scrollable, selectable,
/// expandable, copyable. Folders list their entries; double-click opens a sub-folder or a file in place.
public sealed class PeekWindow : Window
{
    // ---------- look ----------
    static readonly Color Cyan = Color.FromRgb(0x5C, 0xE1, 0xFF);
    static readonly Brush CyanBrush = Frozen(Cyan);
    static readonly Brush Fg = Frozen(Color.FromRgb(0xEC, 0xEC, 0xEE));
    static readonly Brush Muted = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA6));
    static readonly Brush CardBg = Frozen(Color.FromArgb(0xFA, 0x1C, 0x1C, 0x21));
    static readonly Brush CodeBg = Frozen(Color.FromRgb(0x15, 0x15, 0x19));
    static readonly Brush Hover = Frozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    static readonly Brush HotRow = Frozen(Color.FromArgb(0x30, 0x5C, 0xE1, 0xFF));
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    const long MaxTextBytes = 8_000_000;
    static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff", ".webp" };
    static readonly List<PeekWindow> Open = new();

    // ---------- what to show ----------
    sealed record Target(string Path, int Line, int Col, int EndLine, int EndCol, string[] Select);

    readonly Rect workArea; // DIPs
    readonly Stack<Target> back = new();
    Target current;
    readonly string? label;
    object? owner;          // the conversation that opened it
    Target opened = null!;  // what the agent originally showed (the user may have navigated since)

    readonly TextBlock title = new() { Foreground = Fg, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock pathText = new() { Foreground = Muted, FontFamily = Mono, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand };
    readonly TextBlock icon = new() { FontFamily = Icons, FontSize = 18, Foreground = CyanBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 12, 0) };
    readonly TextBlock footer = new() { Foreground = Muted, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly Border body = new() { Background = CodeBg, CornerRadius = new CornerRadius(8), ClipToBounds = true };
    readonly Button backBtn, expandBtn, copyBtn;
    TextEditor? editor;
    RangeHighlighter? highlighter;
    readonly List<(Border Row, string Path)> rows = new();
    string? selectedEntry;
    bool expanded;
    Rect compactBounds;

    /// Show a `file` or `folder` hint. Returns false when the path doesn't exist.
    public static bool Show(HintTag t, Rect workAreaDips, object? owner = null)
    {
        var raw = t.Get("path") ?? t.Get("file") ?? t.Get("folder");
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var path = Resolve(raw);
        if (path == null) { Paths.Log($"peek: not found: {raw}"); return false; }

        int Int(params string[] keys)
        {
            foreach (var k in keys) { var n = t.Num(k); if (!double.IsNaN(n)) return (int)Math.Round(n); }
            return 0;
        }
        int line = Int("line", "from-line", "startline"), col = Int("col", "char", "column", "startcol");
        int endLine = Int("endline", "end-line", "toline"), endCol = Int("endcol", "endchar", "end-col", "tocol");
        if (t.Get("lines") is { } span && span.Split('-') is { Length: 2 } ab && int.TryParse(ab[0], out var l1) && int.TryParse(ab[1], out var l2))
        { line = l1; endLine = l2; }
        var select = (t.Get("select") ?? "").Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // the same file already open: just move to the new spot
        var same = Open.FirstOrDefault(w => string.Equals(w.current.Path, path, StringComparison.OrdinalIgnoreCase));
        var target = new Target(path, line, col, endLine, endCol, select);
        if (same != null) { same.back.Clear(); same.owner = owner ?? same.owner; same.opened = target; same.Load(target); same.Pop(); return true; }

        var w = new PeekWindow(target, t.Label, workAreaDips) { owner = owner };
        w.Show();
        return true;
    }

    static readonly List<string> RecentlyClosed = new();

    /// What's open on screen right now, for the agent: each card's file/folder, the highlighted spot, the part in view,
    /// and what the user selected in it. Also cards closed since the last call. Null when there's nothing to say.
    public static string? DescribeOpen(object? asker)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var w in Open.Where(w => w.IsVisible))
            sb.Append("- ").Append(w.Describe(asker)).Append('\n');
        List<string> closed;
        lock (RecentlyClosed) { closed = RecentlyClosed.Distinct(StringComparer.OrdinalIgnoreCase).ToList(); RecentlyClosed.Clear(); }
        closed.RemoveAll(c => Open.Any(w => string.Equals(w.current.Path, c, StringComparison.OrdinalIgnoreCase)));
        if (sb.Length == 0 && closed.Count == 0) return null;

        var text = "";
        if (sb.Length > 0)
            text = "[Computah] File/folder cards open on the user's screen right now (\"this file\", \"that folder\", \"here\", " +
                   "\"the selected line\" most likely mean these):\n" + sb;
        if (closed.Count > 0)
            text += (text.Length > 0 ? "" : "[Computah] ") + "Cards the user closed since their last message: " + string.Join(", ", closed) + "\n";
        return text.TrimEnd();
    }

    string Describe(object? asker)
    {
        var parts = new List<string>();
        bool folder = Directory.Exists(current.Path);
        parts.Add($"{(folder ? "Folder" : "File")} card: {current.Path}");
        if (!string.Equals(opened.Path, current.Path, StringComparison.OrdinalIgnoreCase))
            parts.Add($"the user navigated here from {opened.Path}");
        if (owner != null && asker != null && !ReferenceEquals(owner, asker)) parts.Add("opened in another conversation");

        if (editor != null)
        {
            var doc = editor.Document;
            if (highlighter != null)
            {
                var a = doc.GetLocation(highlighter.Start);
                var b = doc.GetLocation(Math.Max(highlighter.Start, highlighter.End - 1));
                parts.Add(highlighter.Line == highlighter.EndLine && current.Col == 0 ? $"highlighted line {highlighter.Line}"
                        : current.Col == 0 ? $"highlighted lines {highlighter.Line}-{highlighter.EndLine}"
                        : $"highlighted line {a.Line} char {a.Column} to line {b.Line} char {b.Column}");
            }
            var tv = editor.TextArea.TextView;
            try
            {
                tv.EnsureVisualLines();
                if (tv.VisualLinesValid && tv.VisualLines.Count > 0)
                    parts.Add($"lines {tv.VisualLines[0].FirstDocumentLine.LineNumber}-{tv.VisualLines[^1].LastDocumentLine.LineNumber} of {doc.LineCount} in view");
            }
            catch { }
            if (editor.SelectionLength > 0)
            {
                var a = doc.GetLocation(editor.SelectionStart);
                var b = doc.GetLocation(editor.SelectionStart + editor.SelectionLength - 1);
                var sel = editor.SelectedText;
                if (sel.Length > 1500) sel = sel[..1500] + $"… ({editor.SelectionLength} chars)";
                parts.Add($"the user SELECTED line {a.Line} char {a.Column} to line {b.Line} char {b.Column}:\n  \"\"\"\n  {sel.Replace("\n", "\n  ")}\n  \"\"\"");
            }
        }
        else if (folder)
        {
            if (current.Select.Length > 0) parts.Add("highlighted: " + string.Join(", ", current.Select));
            if (selectedEntry != null) parts.Add($"the user SELECTED: {Path.GetFileName(selectedEntry)}{(Directory.Exists(selectedEntry) ? " (folder)" : "")}");
        }
        else parts.Add("shown as a preview (image or non-text file)");
        if (expanded) parts.Add("enlarged");
        return string.Join("; ", parts);
    }

    static string? Resolve(string raw)
    {
        var p = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
        if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(p, UriKind.Absolute, out var u)) p = u.LocalPath;
        if (p.StartsWith("~")) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..];
        p = p.Replace('/', '\\');
        if (!Path.IsPathRooted(p)) p = Path.Combine(Paths.Workspace, p);
        try { p = Path.GetFullPath(p); } catch { return null; }
        return File.Exists(p) || Directory.Exists(p) ? p.TrimEnd('\\') is { Length: > 2 } x ? x : p : null;
    }

    PeekWindow(Target target, string? label, Rect workAreaDips)
    {
        current = target;
        opened = target;
        this.label = label;
        workArea = workAreaDips;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false; // don't steal the keyboard from what the user is doing
        MinWidth = 360; MinHeight = 220;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Title = "Computah";
        Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), SlimScrollBar.Value);

        // ----- header -----
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(title);
        names.Children.Add(pathText);
        pathText.MouseLeftButtonUp += (_, _) => RevealInExplorer(current.Path);
        pathText.ToolTip = "Show in File Explorer";

        backBtn = IconButton("", "Back", () => { if (back.Count > 0) Load(back.Pop()); });
        copyBtn = IconButton("", "Copy", Copy);
        var openBtn = IconButton("", "Open with its app", () => OpenExternally(selectedEntry ?? current.Path));
        expandBtn = IconButton("", "Bigger", ToggleExpand);
        var closeBtn = IconButton("", "Close (Esc)", Close);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        foreach (var b in new[] { copyBtn, openBtn, expandBtn, closeBtn }) tools.Children.Add(b);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Background = Brushes.Transparent, LastChildFill = true };
        DockPanel.SetDock(tools, Dock.Right);
        DockPanel.SetDock(backBtn, Dock.Left);
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(tools);
        header.Children.Add(backBtn);
        header.Children.Add(icon);
        header.Children.Add(names);
        header.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) ToggleExpand(); else try { DragMove(); } catch { } };

        var labelBlock = new Border
        {
            Visibility = label == null ? Visibility.Collapsed : Visibility.Visible,
            Child = new TextBlock { Text = label, Foreground = CyanBrush, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
            Margin = new Thickness(2, 0, 0, 8),
        };

        var foot = new DockPanel { Margin = new Thickness(2, 8, 18, 0) };
        foot.Children.Add(footer);

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(labelBlock, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(labelBlock);
        layout.Children.Add(foot);
        layout.Children.Add(body);

        var card = new Border
        {
            Child = layout,
            Background = CardBg,
            BorderBrush = Frozen(Color.FromArgb(0x90, 0x5C, 0xE1, 0xFF)),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12, 14, 10),
            Margin = new Thickness(16),
            Effect = new DropShadowEffect { Color = Cyan, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.35 },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        Content = card;

        // ----- size and place: on the bubble's monitor, upper middle, cascading -----
        double w = Math.Min(760, workArea.Width - 40), h = Math.Min(460, workArea.Height - 40);
        int n = Open.Count;
        Width = w; Height = h;
        Left = workArea.Left + (workArea.Width - w) / 2 + n * 28;
        Top = workArea.Top + workArea.Height * 0.12 + n * 28;
        Open.Add(this);
        Closed += (_, _) =>
        {
            Open.Remove(this);
            lock (RecentlyClosed) RecentlyClosed.Add(current.Path);
        };

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };

        Loaded += (_, _) => { Load(current); Pop(); };
    }

    // ---------- content ----------

    void Load(Target t)
    {
        current = t;
        selectedEntry = null;
        backBtn.Visibility = back.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        title.Text = Path.GetFileName(t.Path) is { Length: > 0 } nm ? nm : t.Path;
        pathText.Text = t.Path;
        editor = null; highlighter = null; rows.Clear();

        if (Directory.Exists(t.Path)) { ShowFolder(t); return; }

        var ext = Path.GetExtension(t.Path).ToLowerInvariant();
        var size = new FileInfo(t.Path).Length;
        if (ImageExt.Contains(ext)) { ShowImage(t, size); return; }
        if (size > MaxTextBytes) { ShowMessage("", $"This file is too big to preview ({Size(size)}).", t.Path); return; }

        string text;
        try
        {
            var bytes = File.ReadAllBytes(t.Path);
            if (bytes.Take(8000).Contains((byte)0)) { ShowMessage("", $"Binary file, {Size(size)}.", t.Path); return; }
            using var sr = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            text = sr.ReadToEnd();
        }
        catch (Exception ex) { ShowMessage("", "Can't read this file: " + ex.Message, t.Path); return; }
        ShowText(t, text);
    }

    void ShowText(Target t, string text)
    {
        icon.Text = "";
        var ed = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = true,
            FontFamily = Mono,
            FontSize = 13,
            Background = CodeBg,
            Foreground = Fg,
            LineNumbersForeground = Frozen(Color.FromRgb(0x5E, 0x60, 0x6E)),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(4, 6, 4, 6),
            WordWrap = false,
        };
        ed.Resources[SystemColors.ControlBrushKey] = Brushes.Transparent; // the corner between the scrollbars
        ed.Options.EnableHyperlinks = false;
        ed.Options.EnableEmailHyperlinks = false;
        ed.Options.HighlightCurrentLine = false;
        ed.TextArea.SelectionBrush = Frozen(Color.FromArgb(0x55, 0x5C, 0xE1, 0xFF));
        ed.TextArea.SelectionForeground = null;
        ed.TextArea.SelectionBorder = null;
        ed.TextArea.TextView.LinkTextForegroundBrush = CyanBrush;
        ed.Document = new TextDocument(text);
        var def = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(t.Path));
        if (def != null) ed.TextArea.TextView.LineTransformers.Add(new DarkColorizer(def));

        editor = ed;
        body.Child = ed;

        // the exact spot
        var doc = ed.Document;
        int lines = doc.LineCount;
        int line = Math.Clamp(t.Line, 0, lines);
        if (line > 0)
        {
            int endLine = Math.Clamp(t.EndLine > 0 ? t.EndLine : line, line, lines);
            var first = doc.GetLineByNumber(line);
            var last = doc.GetLineByNumber(endLine);
            int start = first.Offset, end = last.EndOffset;
            if (t.Col > 0)
            {
                start = first.Offset + Math.Min(t.Col - 1, first.Length);
                int ec = t.EndCol > 0 ? t.EndCol : (endLine == line ? t.Col : last.Length);
                end = last.Offset + Math.Clamp(ec, 0, last.Length); // end column inclusive
                if (end <= start) end = Math.Min(start + 1, last.EndOffset);
            }
            highlighter = new RangeHighlighter(line, endLine, start, end);
            ed.TextArea.TextView.BackgroundRenderers.Add(highlighter);
            ed.TextArea.Caret.Offset = start;
            footer.Text = SpotText(t, line, endLine) + "  ·  " + $"{lines} lines";
            // center the spot once the editor has laid out
            ed.Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CenterOn(line, start));
        }
        else footer.Text = $"{lines} lines  ·  {Size(new FileInfo(t.Path).Length)}";
        copyBtn.ToolTip = highlighter != null ? "Copy the highlighted part (or your selection)" : "Copy the selection, or the whole file";
    }

    static string SpotText(Target t, int line, int endLine)
    {
        var s = endLine > line ? $"Lines {line}–{endLine}" : $"Line {line}";
        if (t.Col > 0) s += t.EndCol > 0 && endLine == line ? $", characters {t.Col}–{t.EndCol}" : $", character {t.Col}";
        return s;
    }

    void CenterOn(int line, int offset)
    {
        if (editor == null) return;
        editor.TextArea.TextView.EnsureVisualLines();
        double top = editor.TextArea.TextView.GetVisualTopByDocumentLine(line);
        editor.ScrollToVerticalOffset(Math.Max(0, top - editor.TextArea.TextView.ActualHeight / 3));
        // bring a far-right column into view too
        var loc = editor.Document.GetLocation(offset);
        var pos = editor.TextArea.TextView.GetVisualPosition(new TextViewPosition(loc), VisualYPosition.LineTop);
        double vw = editor.TextArea.TextView.ActualWidth;
        if (pos.X > vw - 40) editor.ScrollToHorizontalOffset(Math.Max(0, pos.X - vw / 3));
    }

    void ShowImage(Target t, long size)
    {
        icon.Text = "";
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(t.Path);
            bmp.EndInit();
            body.Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Image { Source = bmp, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(8) },
            };
            footer.Text = $"{bmp.PixelWidth} × {bmp.PixelHeight}  ·  {Size(size)}";
        }
        catch (Exception ex) { ShowMessage("", "Can't show this image: " + ex.Message, t.Path); }
    }

    void ShowMessage(string glyph, string text, string path)
    {
        icon.Text = glyph;
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = text, Foreground = Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        var open = new TextBlock { Text = "Open with its app", Foreground = CyanBrush, FontSize = 13, Cursor = Cursors.Hand, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        open.MouseLeftButtonUp += (_, _) => OpenExternally(path);
        sp.Children.Add(open);
        body.Child = sp;
        footer.Text = "";
    }

    void ShowFolder(Target t)
    {
        icon.Text = "";
        var list = new StackPanel { Margin = new Thickness(4) };
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        body.Child = scroll;

        List<FileSystemInfo> entries;
        try
        {
            var di = new DirectoryInfo(t.Path);
            entries = di.EnumerateDirectories().Cast<FileSystemInfo>().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Concat(di.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)).Take(3000).ToList();
        }
        catch (Exception ex) { ShowMessage("", "Can't open this folder: " + ex.Message, t.Path); return; }

        Border? firstHot = null;
        foreach (var e in entries)
        {
            bool isDir = e is DirectoryInfo;
            bool hot = t.Select.Any(s => string.Equals(s.Trim().TrimEnd('\\', '/'), e.Name, StringComparison.OrdinalIgnoreCase));
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = new TextBlock { Text = isDir ? "" : "", FontFamily = Icons, FontSize = 13, Foreground = isDir ? Frozen(Color.FromRgb(0xF2, 0xC2, 0x4B)) : Muted, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var nm = new TextBlock { Text = e.Name, Foreground = Fg, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontWeight = hot ? FontWeights.SemiBold : FontWeights.Normal };
            var sz = new TextBlock { Text = e is FileInfo f ? Size(f.Length) : "", Foreground = Muted, FontSize = 11.5, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 64, TextAlignment = TextAlignment.Right };
            var dt = new TextBlock { Text = e.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), Foreground = Muted, FontSize = 11.5, Margin = new Thickness(16, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(nm, 1); Grid.SetColumn(sz, 2); Grid.SetColumn(dt, 3);
            g.Children.Add(ic); g.Children.Add(nm); g.Children.Add(sz); g.Children.Add(dt);

            var row = new Border
            {
                Child = g, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent,
                BorderThickness = new Thickness(1), BorderBrush = hot ? CyanBrush : Brushes.Transparent,
            };
            if (hot)
            {
                row.Background = HotRow;
                row.Effect = new DropShadowEffect { Color = Cyan, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.6 };
                firstHot ??= row;
            }
            var path = e.FullName;
            row.MouseEnter += (_, _) => { if (selectedEntry != path && !hot) row.Background = Hover; };
            row.MouseLeave += (_, _) => { if (selectedEntry != path) row.Background = hot ? HotRow : Brushes.Transparent; };
            row.MouseLeftButtonDown += (_, ev) =>
            {
                if (ev.ClickCount == 2) { Navigate(path); return; }
                selectedEntry = path;
                foreach (var (r, p) in rows) if (r != row && r.BorderBrush != CyanBrush) r.Background = Brushes.Transparent;
                row.Background = Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
                footer.Text = $"{e.Name}  ·  double-click to open";
            };
            rows.Add((row, path));
            list.Children.Add(row);
        }

        int dirs = entries.Count(x => x is DirectoryInfo);
        footer.Text = entries.Count == 0 ? "Empty folder" : $"{dirs} folders, {entries.Count - dirs} files";
        if (firstHot != null)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var p = firstHot.TranslatePoint(new Point(0, 0), list);
                scroll.ScrollToVerticalOffset(Math.Max(0, p.Y - scroll.ViewportHeight / 3));
            });
        copyBtn.ToolTip = "Copy the selected item's path (or this folder's)";
    }

    void Navigate(string path)
    {
        back.Push(current);
        Load(new Target(path, 0, 0, 0, 0, Array.Empty<string>()));
    }

    // ---------- buttons ----------

    void Copy()
    {
        string? text = null;
        if (editor != null)
        {
            if (editor.SelectionLength > 0) text = editor.SelectedText;
            else if (highlighter != null) text = editor.Document.GetText(highlighter.Start, highlighter.End - highlighter.Start);
            else text = editor.Text;
        }
        else text = selectedEntry ?? current.Path;
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { return; }
        var tb = (TextBlock)copyBtn.Content;
        tb.Text = "";
        tb.Foreground = CyanBrush;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        t.Tick += (_, _) => { t.Stop(); tb.Text = ""; tb.Foreground = Fg; };
        t.Start();
    }

    void ToggleExpand()
    {
        expanded = !expanded;
        ((TextBlock)expandBtn.Content).Text = expanded ? "" : "";
        expandBtn.ToolTip = expanded ? "Smaller" : "Bigger";
        Rect to;
        if (expanded)
        {
            compactBounds = new Rect(Left, Top, Width, Height);
            double w = workArea.Width * 0.8, h = workArea.Height * 0.85;
            to = new Rect(workArea.Left + (workArea.Width - w) / 2, workArea.Top + (workArea.Height - h) / 2, w, h);
        }
        else to = compactBounds;
        Left = to.Left; Top = to.Top; Width = to.Width; Height = to.Height;
        if (highlighter != null && editor != null)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CenterOn(highlighter.Line, highlighter.Start));
    }

    static void OpenExternally(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Paths.Log("peek open: " + ex.Message); }
    }

    static void RevealInExplorer(string path)
    {
        try { Process.Start("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\""); }
        catch (Exception ex) { Paths.Log("peek reveal: " + ex.Message); }
    }

    void Pop()
    {
        var card = (Border)Content;
        var sc = (ScaleTransform)card.RenderTransform;
        var ease = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    static Button IconButton(string glyph, string tip, Action click)
    {
        var tb = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 13, Foreground = Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Button { Content = tb, ToolTip = tip, Width = 30, Height = 30, Margin = new Thickness(2, 0, 0, 0), Cursor = Cursors.Hand, Focusable = false };
        var bg = new FrameworkElementFactory(typeof(Border));
        bg.Name = "bg";
        bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        bg.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        bg.AppendChild(cp);
        var tpl = new ControlTemplate(typeof(Button)) { VisualTree = bg };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Hover, "bg"));
        tpl.Triggers.Add(hover);
        b.Template = tpl;
        b.Click += (_, _) => click();
        return b;
    }

    /// Thin dark scrollbars (the default ones are light grey blocks).
    static readonly Lazy<Style> SlimScrollBar = new(() => (Style)System.Windows.Markup.XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollBar">
          <Setter Property="Background" Value="Transparent"/>
          <Setter Property="Width" Value="10"/>
          <Setter Property="MinWidth" Value="10"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ScrollBar">
                <Border Background="Transparent">
                  <Track x:Name="PART_Track" IsDirectionReversed="True">
                    <Track.Thumb>
                      <Thumb>
                        <Thumb.Template>
                          <ControlTemplate TargetType="Thumb">
                            <Border x:Name="t" CornerRadius="3" Background="#40FFFFFF" Margin="2"/>
                            <ControlTemplate.Triggers>
                              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="t" Property="Background" Value="#805CE1FF"/></Trigger>
                              <Trigger Property="IsDragging" Value="True"><Setter TargetName="t" Property="Background" Value="#B05CE1FF"/></Trigger>
                            </ControlTemplate.Triggers>
                          </ControlTemplate>
                        </Thumb.Template>
                      </Thumb>
                    </Track.Thumb>
                  </Track>
                </Border>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
          <Style.Triggers>
            <Trigger Property="Orientation" Value="Horizontal">
              <Setter Property="Width" Value="Auto"/>
              <Setter Property="MinWidth" Value="0"/>
              <Setter Property="Height" Value="10"/>
              <Setter Property="MinHeight" Value="10"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="ScrollBar">
                    <Border Background="Transparent">
                      <Track x:Name="PART_Track" IsDirectionReversed="False">
                        <Track.Thumb>
                          <Thumb>
                            <Thumb.Template>
                              <ControlTemplate TargetType="Thumb">
                                <Border x:Name="t" CornerRadius="3" Background="#40FFFFFF" Margin="2"/>
                                <ControlTemplate.Triggers>
                                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="t" Property="Background" Value="#805CE1FF"/></Trigger>
                                </ControlTemplate.Triggers>
                              </ControlTemplate>
                            </Thumb.Template>
                          </Thumb>
                        </Track.Thumb>
                      </Track>
                    </Border>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Trigger>
          </Style.Triggers>
        </Style>
        """));

    static string Size(long b) => b < 1024 ? $"{b} B" : b < 1024 * 1024 ? $"{b / 1024.0:0.#} KB" : b < 1024L * 1024 * 1024 ? $"{b / 1048576.0:0.#} MB" : $"{b / 1073741824.0:0.##} GB";
    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // ---------- editor helpers ----------

    /// Glowing highlight: a faint band on the lines, a bright frame on the exact characters.
    sealed class RangeHighlighter : IBackgroundRenderer
    {
        public readonly int Line, EndLine, Start, End;
        static readonly Brush Band = Frozen(Color.FromArgb(0x1C, 0x5C, 0xE1, 0xFF));
        static readonly Brush Fill = Frozen(Color.FromArgb(0x48, 0x5C, 0xE1, 0xFF));
        static readonly Pen Glow = MakePen(Color.FromArgb(0x50, 0x5C, 0xE1, 0xFF), 5);
        static readonly Pen Edge = MakePen(Cyan, 1.4);

        public RangeHighlighter(int line, int endLine, int start, int end) { Line = line; EndLine = endLine; Start = start; End = end; }
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView view, DrawingContext dc)
        {
            if (view.Document == null) return;
            view.EnsureVisualLines();
            for (int l = Line; l <= EndLine; l++)
            {
                var dl = view.Document.GetLineByNumber(l);
                foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(view, dl, true))
                    dc.DrawRectangle(Band, null, new Rect(0, r.Top, Math.Max(view.ActualWidth, r.Right) + view.HorizontalOffset, r.Height));
            }
            var b = new BackgroundGeometryBuilder { CornerRadius = 3, AlignToWholePixels = true };
            b.AddSegment(view, new TextSegment { StartOffset = Start, EndOffset = End });
            var geo = b.CreateGeometry();
            if (geo != null)
            {
                dc.DrawGeometry(null, Glow, geo);
                dc.DrawGeometry(Fill, Edge, geo);
            }
        }

        static Pen MakePen(Color c, double w) { var p = new Pen(Frozen(c), w) { LineJoin = PenLineJoin.Round }; p.Freeze(); return p; }
    }

    /// AvalonEdit's built-in colors are made for white backgrounds; lift the dark ones so they read on dark.
    sealed class DarkColorizer : HighlightingColorizer
    {
        readonly Dictionary<HighlightingColor, HighlightingColor> cache = new();
        public DarkColorizer(IHighlightingDefinition def) : base(def) { }

        protected override void ApplyColorToElement(VisualLineElement element, HighlightingColor color)
        {
            if (!cache.TryGetValue(color, out var adj))
            {
                adj = color.Clone();
                if (color.Foreground?.GetColor(null) is Color c) adj.Foreground = new SimpleHighlightingBrush(Lift(c));
                adj.Background = null; // backgrounds in the light themes look like blotches here
                cache[color] = adj;
            }
            base.ApplyColorToElement(element, adj);
        }

        static Color Lift(Color c)
        {
            double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
            if (lum >= 0.6) return c;
            double k = Math.Clamp((0.68 - lum) / (1 - lum), 0, 0.8);
            byte Mix(byte v) => (byte)(v + (255 - v) * k);
            return Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
        }
    }
}
