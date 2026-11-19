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
using ICSharpCode.AvalonEdit.Editing;
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
    sealed record Target(string Path, int Line, int Col, int EndLine, int EndCol, string[] Select, FileChange? Change = null);

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
    readonly Button backBtn, expandBtn, copyBtn, undoBtn, redoBtn;
    readonly Border cardBorder, labelBox;
    readonly TextBlock labelText = new() { Foreground = CyanBrush, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    List<DiffLine>? diff;          // change mode: the unified view shown
    Action<string, bool>? onUndo;  // tells the conversation the user undid (true) or redid (false) a change
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
        var same = Open.FirstOrDefault(w => w.tabs.Count == 0 && string.Equals(w.current.Path, path, StringComparison.OrdinalIgnoreCase));
        var target = new Target(path, line, col, endLine, endCol, select);
        if (same != null) { same.back.Clear(); same.owner = owner ?? same.owner; same.opened = target; same.Load(target); same.Pop(); return true; }

        var w = new PeekWindow(target, t.Label, workAreaDips) { owner = owner };
        w.Show();
        return true;
    }

    // ---------- change cards ----------

    static readonly Color Green = Color.FromRgb(0x4A, 0xDE, 0x80);
    static readonly Brush GreenBrush = Frozen(Green);

    /// One edit the agent made, shown as a tab of the change card.
    sealed class ChangeTab
    {
        public required FileChange Change;
        public bool Undone;
        public DateTime At = DateTime.Now;
        public Border Pill = null!;
    }

    readonly List<ChangeTab> tabs = new();
    ChangeTab? activeTab;
    readonly StackPanel tabPanel = new() { Orientation = Orientation.Horizontal };
    readonly ScrollViewer tabStrip = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed,
    };

    /// Undo state of the tab being shown.
    bool undone
    {
        get => activeTab?.Undone ?? false;
        set { if (activeTab != null) activeTab.Undone = value; }
    }

    /// The agent changed a file: pop up a card showing the change (removed lines red, added lines green).
    /// Every edit of the conversation becomes a tab of one change card, newest selected.
    public static void ShowChange(FileChange c, Rect workAreaDips, object? owner, Action<string, bool>? onUndo)
    {
        var w = Open.FirstOrDefault(x => x.tabs.Count > 0 && ReferenceEquals(x.owner, owner))
             // a plain card of that file is open: it becomes the change card
             ?? Open.FirstOrDefault(x => x.tabs.Count == 0 && x.current.Change == null &&
                                         string.Equals(x.current.Path, c.Path, StringComparison.OrdinalIgnoreCase));
        if (w == null)
        {
            w = new PeekWindow(new Target(c.Path, 0, 0, 0, 0, Array.Empty<string>(), c), null, workAreaDips) { owner = owner };
            w.Show();
        }
        w.owner ??= owner;
        w.onUndo = onUndo;
        w.AddTab(new ChangeTab { Change = c });
        w.Pop();
    }

    void AddTab(ChangeTab t)
    {
        tabs.Add(t);
        t.Pill = MakeTabPill(t, tabs.Count);
        tabPanel.Children.Add(t.Pill);
        SelectTab(t);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => tabStrip.ScrollToRightEnd());
    }

    void SelectTab(ChangeTab t)
    {
        activeTab = t;
        back.Clear();
        var target = new Target(t.Change.Path, 0, 0, 0, 0, Array.Empty<string>(), t.Change);
        opened = target;
        if (IsLoaded) Load(target); else current = target; // not shown yet: Loaded will load it
        tabStrip.Visibility = tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        RefreshTabs();
    }

    Border MakeTabPill(ChangeTab t, int number)
    {
        var name = Path.GetFileName(t.Change.Path);
        int sameFileBefore = tabs.Count(x => x != t && tabs.IndexOf(x) < tabs.IndexOf(t) &&
                                              string.Equals(x.Change.Path, t.Change.Path, StringComparison.OrdinalIgnoreCase));
        var lines = Diff.Lines(t.Change.Before, t.Change.After);
        int add = lines.Count(l => l.Kind == DiffKind.Added), del = lines.Count(l => l.Kind == DiffKind.Removed);

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = $"{number}", Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = name + (sameFileBefore > 0 ? $" ({sameFileBefore + 1})" : ""), Foreground = Fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = $"+{add}", Foreground = GreenBrush, FontSize = 11, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = $"−{del}", Foreground = Frozen(Color.FromRgb(0xF8, 0x71, 0x71)), FontSize = 11, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        var pill = new Border
        {
            Child = sp, CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 4, 10, 5), Margin = new Thickness(0, 0, 5, 0),
            BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
            ToolTip = $"{t.Change.Path}\nChanged at {t.At:HH:mm:ss}" + (t.Change.Before == null ? " (new file)" : ""),
        };
        pill.MouseLeftButtonUp += (_, _) => { if (activeTab != t) SelectTab(t); };
        pill.MouseEnter += (_, _) => { if (activeTab != t) pill.Background = Hover; };
        pill.MouseLeave += (_, _) => RefreshTabs();
        return pill;
    }

    /// Selected tab: solid with a colored edge; undone tabs: grey and struck through.
    void RefreshTabs()
    {
        foreach (var t in tabs)
        {
            bool on = t == activeTab;
            t.Pill.Background = on ? Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
            t.Pill.BorderBrush = on ? (t.Undone ? Muted : GreenBrush) : Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            t.Pill.Opacity = t.Undone ? 0.6 : 1;
            var name = (TextBlock)((StackPanel)t.Pill.Child).Children[1];
            name.TextDecorations = t.Undone ? TextDecorations.Strikethrough : null;
        }
    }

    void Accent(bool change)
    {
        var color = change ? Green : Cyan;
        cardBorder.BorderBrush = Frozen(Color.FromArgb(0x90, color.R, color.G, color.B));
        ((DropShadowEffect)cardBorder.Effect).Color = color;
        icon.Foreground = change ? GreenBrush : CyanBrush;
        labelText.Foreground = change ? GreenBrush : CyanBrush;
        undoBtn.Visibility = change && !undone ? Visibility.Visible : Visibility.Collapsed;
        redoBtn.Visibility = change && undone ? Visibility.Visible : Visibility.Collapsed;
        if (change && undone)
        {
            cardBorder.BorderBrush = Frozen(Color.FromArgb(0x70, 0x9A, 0x9A, 0xA6));
            ((DropShadowEffect)cardBorder.Effect).Color = Color.FromRgb(0x9A, 0x9A, 0xA6);
            icon.Foreground = labelText.Foreground = Muted;
        }
        if (!change && labelText.Text is "" or null) labelBox.Visibility = Visibility.Collapsed;
    }

    void ShowDiff(FileChange c)
    {
        icon.Text = c.Before == null ? "" : ""; // new file / edit
        if (body.Child is TextEditor old && diff != null && old.Document.Text == string.Join("\n", Diff.Lines(c.Before, c.After).Select(l => l.Text)))
        {
            // same change, only its undone/redone state moved: keep the view (and the scroll position)
            labelText.Text = ChangeLabel(c);
            old.Opacity = undone ? 0.55 : 1;
            editor = old;
            return;
        }        var lines = diff = Diff.Lines(c.Before, c.After);
        int added = lines.Count(l => l.Kind == DiffKind.Added), removed = lines.Count(l => l.Kind == DiffKind.Removed);
        labelText.Text = ChangeLabel(c);
        labelBox.Visibility = Visibility.Visible;

        var ed = NewEditor(string.Join("\n", lines.Select(l => l.Text)), c.Path, showLineNumbers: false);
        ed.TextArea.LeftMargins.Insert(0, new DiffMargin(lines));
        ed.TextArea.TextView.BackgroundRenderers.Add(new DiffRenderer(lines));
        ed.TextArea.TextView.LineTransformers.Add(new DiffColorizer(lines));
        ed.Opacity = undone ? 0.55 : 1;
        editor = ed;
        body.Child = ed;

        int first = lines.FindIndex(l => l.Kind != DiffKind.Same) + 1;
        footer.Text = (c.Before == null ? $"New file · {added} lines" : $"+{added}  −{removed} lines") +
                      $"  ·  {(activeTab?.At ?? DateTime.Now):HH:mm}  ·  Ctrl+Z undo, Ctrl+Y redo";
        if (first > 0)
            ed.Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CenterOn(first, ed.Document.GetLineByNumber(first).Offset));
        copyBtn.ToolTip = "Copy the new text (or your selection)";
    }

    string ChangeLabel(FileChange c)
    {
        var what = undone ? "Change undone · the file is back as it was"
                 : c.Before == null ? $"Created by {c.AgentName}" : $"Changed by {c.AgentName}";
        return tabs.Count > 1 && activeTab != null ? $"Change {tabs.IndexOf(activeTab) + 1} of {tabs.Count} · {what}" : what;
    }

    void Undo() => Flip(toBefore: true);
    void Redo() => Flip(toBefore: false);

    /// Put the file back as it was before the change (undo), or as the agent left it (redo).
    /// Refuses when the file was changed by something else in between.
    void Flip(bool toBefore)
    {
        var c = current.Change;
        if (c == null || undone == toBefore) return;
        string? from = toBefore ? c.After : c.Before, to = toBefore ? c.Before : c.After;
        byte[]? toBytes = toBefore ? c.BeforeBytes : c.AfterBytes;
        try
        {
            string? now = null;
            if (File.Exists(c.Path))
                using (var sr = new StreamReader(c.Path, detectEncodingFromByteOrderMarks: true)) now = sr.ReadToEnd();
            if (now != from)
            {
                // changes to one file stack up: undo the newest first, redo the oldest first
                int me = activeTab != null ? tabs.IndexOf(activeTab) : -1;
                var blocking = tabs.Select((t, i) => (t, i)).Where(x =>
                        string.Equals(x.t.Change.Path, c.Path, StringComparison.OrdinalIgnoreCase) &&
                        (toBefore ? x.i > me && !x.t.Undone : x.i < me && x.t.Undone))
                    .Select(x => (x.i + 1).ToString()).ToList();
                labelText.Text = blocking.Count > 0
                    ? $"{(toBefore ? "Undo" : "Redo")} change {string.Join(", ", blocking)} to this file first: {(toBefore ? "later" : "earlier")} changes build on {(toBefore ? "this one" : "it")}."
                    : $"The file was changed since, so it can't be {(toBefore ? "undone" : "redone")} safely. Open it to fix it by hand.";
                return;
            }
            if (to == null)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(c.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                                                   Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else if (toBytes != null) File.WriteAllBytes(c.Path, toBytes);
            else File.WriteAllText(c.Path, to);
        }
        catch (Exception ex) { labelText.Text = $"Couldn't {(toBefore ? "undo" : "redo")}: " + ex.Message; return; }

        undone = toBefore;
        onUndo?.Invoke($"change {(activeTab != null ? tabs.IndexOf(activeTab) + 1 : 1)} (to {c.Path})", toBefore);
        RefreshTabs();
        Accent(true);
        ShowDiff(c);
    }

    /// Line numbers of the diff view: the new number for kept/added lines, the old one (dimmed) for removed lines, and a +/− sign.
    sealed class DiffMargin : AbstractMargin
    {
        readonly List<DiffLine> lines;
        static readonly Typeface Face = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static readonly Brush Num = Frozen(Color.FromRgb(0x5E, 0x60, 0x6E));
        static readonly Brush Red = Frozen(Color.FromRgb(0xF8, 0x71, 0x71));
        public DiffMargin(List<DiffLine> lines) { this.lines = lines; }

        protected override Size MeasureOverride(Size availableSize) => new(64, 0);

        protected override void OnRender(DrawingContext dc)
        {
            var view = TextView;
            if (view == null || !view.VisualLinesValid) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (var vl in view.VisualLines)
            {
                int i = vl.FirstDocumentLine.LineNumber - 1;
                if (i < 0 || i >= lines.Count) continue;
                var l = lines[i];
                double y = vl.GetTextLineVisualYPosition(vl.TextLines[0], VisualYPosition.TextTop) - view.VerticalOffset;
                var (no, brush, sign) = l.Kind switch
                {
                    DiffKind.Added => (l.NewNo, GreenBrush, "+"),
                    DiffKind.Removed => (l.OldNo, Red, "−"),
                    _ => (l.NewNo, Num, ""),
                };
                var ft = new FormattedText(no.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12, l.Kind == DiffKind.Same ? Num : brush, dpi);
                dc.DrawText(ft, new Point(40 - ft.Width, y + 1));
                if (sign.Length > 0)
                    dc.DrawText(new FormattedText(sign, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 13, brush, dpi), new Point(48, y));
            }
        }

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView != null) oldTextView.VisualLinesChanged -= Redraw;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView != null) newTextView.VisualLinesChanged += Redraw;
        }
        void Redraw(object? s, EventArgs e) => InvalidateVisual();
    }

    /// Green band on added lines, red band on removed ones; the exact changed characters glow brighter.
    sealed class DiffRenderer : IBackgroundRenderer
    {
        readonly List<DiffLine> lines;
        static readonly Brush AddBand = Frozen(Color.FromArgb(0x26, 0x4A, 0xDE, 0x80));
        static readonly Brush DelBand = Frozen(Color.FromArgb(0x26, 0xF8, 0x71, 0x71));
        static readonly Brush AddChars = Frozen(Color.FromArgb(0x5A, 0x4A, 0xDE, 0x80));
        static readonly Brush DelChars = Frozen(Color.FromArgb(0x55, 0xF8, 0x71, 0x71));
        static readonly Pen AddEdge = MakePen(Color.FromArgb(0xC0, 0x4A, 0xDE, 0x80));
        public DiffRenderer(List<DiffLine> lines) { this.lines = lines; }
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView view, DrawingContext dc)
        {
            if (view.Document == null || !view.VisualLinesValid) return;
            foreach (var vl in view.VisualLines)
            {
                int n = vl.FirstDocumentLine.LineNumber;
                if (n - 1 >= lines.Count) continue;
                var l = lines[n - 1];
                if (l.Kind == DiffKind.Same) continue;
                var dl = vl.FirstDocumentLine;
                foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(view, dl, true))
                    dc.DrawRectangle(l.Kind == DiffKind.Added ? AddBand : DelBand, null,
                                     new Rect(0, r.Top, Math.Max(view.ActualWidth, r.Right) + view.HorizontalOffset, r.Height));
                if (l.Length > 0)
                {
                    var b = new BackgroundGeometryBuilder { CornerRadius = 2, AlignToWholePixels = true };
                    b.AddSegment(view, new TextSegment { StartOffset = dl.Offset + l.Start, Length = l.Length });
                    if (b.CreateGeometry() is { } g) dc.DrawGeometry(l.Kind == DiffKind.Added ? AddChars : DelChars, l.Kind == DiffKind.Added ? AddEdge : null, g);
                }
            }
        }
        static Pen MakePen(Color c) { var p = new Pen(Frozen(c), 1); p.Freeze(); return p; }
    }

    /// Removed lines: dimmed red and struck through (just the changed characters when known).
    sealed class DiffColorizer : DocumentColorizingTransformer
    {
        readonly List<DiffLine> lines;
        static readonly Brush DelText = Frozen(Color.FromRgb(0xC9, 0x8A, 0x8A));
        public DiffColorizer(List<DiffLine> lines) { this.lines = lines; }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.LineNumber - 1 >= lines.Count) return;
            var l = lines[line.LineNumber - 1];
            if (l.Kind != DiffKind.Removed || line.Length == 0) return;
            ChangeLinePart(line.Offset, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(DelText));
            if (l.Length == 0) return; // text was only inserted on this line: nothing of it was removed
            int s = l.Length > 0 ? line.Offset + l.Start : line.Offset + (l.Text.Length - l.Text.TrimStart().Length);
            int end = l.Length > 0 ? s + l.Length : line.EndOffset;
            if (end > s) ChangeLinePart(s, end, e => e.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough));
        }
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
        parts.Add($"{(diff != null ? "Change" : folder ? "Folder" : "File")} card: {current.Path}");
        if (!string.Equals(opened.Path, current.Path, StringComparison.OrdinalIgnoreCase))
            parts.Add($"the user navigated here from {opened.Path}");
        if (owner != null && asker != null && !ReferenceEquals(owner, asker)) parts.Add("opened in another conversation");

        if (editor != null && diff != null)
        {
            // the view mixes old and new lines: report positions as file lines
            string Where(int docLine)
            {
                var l = diff[Math.Clamp(docLine - 1, 0, diff.Count - 1)];
                return l.Kind == DiffKind.Removed ? $"removed line (was line {l.OldNo})" : $"line {l.NewNo}";
            }
            if (tabs.Count > 1)
            {
                var list = tabs.Select((t, i) =>
                {
                    var d = Diff.Lines(t.Change.Before, t.Change.After);
                    return $"\n    {i + 1}. {t.Change.Path} +{d.Count(l => l.Kind == DiffKind.Added)} −{d.Count(l => l.Kind == DiffKind.Removed)}" +
                           (t.Undone ? " (UNDONE)" : "") + (t == activeTab ? "  <- the tab the user is viewing" : "");
                });
                parts.Add($"one tab per change you made, in order:{string.Concat(list)}\n  ");
            }
            parts.Add($"the tab shown is your change: +{diff.Count(l => l.Kind == DiffKind.Added)} −{diff.Count(l => l.Kind == DiffKind.Removed)} lines{(undone ? " (UNDONE by the user: the file is currently as it was before the change)" : "")}");
            if (editor.SelectionLength > 0)
            {
                var a = editor.Document.GetLocation(editor.SelectionStart);
                var b = editor.Document.GetLocation(editor.SelectionStart + editor.SelectionLength - 1);
                var sel = editor.SelectedText;
                if (sel.Length > 1500) sel = sel[..1500] + "…";
                parts.Add($"the user SELECTED from {Where(a.Line)} char {a.Column} to {Where(b.Line)} char {b.Column}:\n  \"\"\"\n  {sel.Replace("\n", "\n  ")}\n  \"\"\"");
            }
        }
        else if (editor != null)
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
        undoBtn = IconButton("\uE7A7", "Undo this change (Ctrl+Z)", Undo);
        undoBtn.Visibility = Visibility.Collapsed;
        redoBtn = IconButton("\uE7A6", "Redo the change (Ctrl+Y)", Redo);
        redoBtn.Visibility = Visibility.Collapsed;
        var closeBtn = IconButton("", "Close (Esc)", Close);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        foreach (var b in new[] { undoBtn, redoBtn, copyBtn, openBtn, expandBtn, closeBtn }) tools.Children.Add(b);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Background = Brushes.Transparent, LastChildFill = true };
        DockPanel.SetDock(tools, Dock.Right);
        DockPanel.SetDock(backBtn, Dock.Left);
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(tools);
        header.Children.Add(backBtn);
        header.Children.Add(icon);
        header.Children.Add(names);
        header.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) ToggleExpand(); else try { DragMove(); } catch { } };

        labelText.Text = label;
        var labelBlock = labelBox = new Border
        {
            Visibility = label == null ? Visibility.Collapsed : Visibility.Visible,
            Child = labelText,
            Margin = new Thickness(2, 0, 0, 8),
        };

        var foot = new DockPanel { Margin = new Thickness(2, 8, 18, 0) };
        foot.Children.Add(footer);

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        tabStrip.Content = tabPanel;
        DockPanel.SetDock(tabStrip, Dock.Top);
        DockPanel.SetDock(labelBlock, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(tabStrip);
        layout.Children.Add(labelBlock);
        layout.Children.Add(foot);
        layout.Children.Add(body);

        var card = cardBorder = new Border
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

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
            if (current.Change == null || Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.Z) { Undo(); e.Handled = true; }
            else if (e.Key == Key.Y) { Redo(); e.Handled = true; }
        };

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
        editor = null; highlighter = null; rows.Clear(); diff = null;
        Accent(t.Change != null);
        if (t.Change != null) { ShowDiff(t.Change); return; }

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

    TextEditor NewEditor(string text, string path, bool showLineNumbers)
    {
        var ed = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = showLineNumbers,
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
        var def = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));
        if (def != null) ed.TextArea.TextView.LineTransformers.Add(new DarkColorizer(def));
        return ed;
    }

    void ShowText(Target t, string text)
    {
        icon.Text = "";
        var ed = NewEditor(text, t.Path, showLineNumbers: true);

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
            else if (diff != null) text = string.Join(Environment.NewLine, diff.Where(l => l.Kind == DiffKind.Added).Select(l => l.Text));
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
