using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WF = System.Windows.Forms;

namespace Assist;

/// Popup above the dock button listing all sessions.
public partial class SessionsPanel : Window
{
    static readonly Brush Fg = Frozen(Color.FromRgb(0xEC, 0xEC, 0xEE));
    static readonly Brush Muted = Frozen(Color.FromRgb(0x8C, 0x8C, 0x96));
    internal static readonly Brush Accent = Frozen(Color.FromRgb(0x5C, 0xE1, 0xFF));
    static readonly Brush RowHover = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    static readonly Brush RowTarget = Frozen(Color.FromArgb(0x1C, 0x5C, 0xE1, 0xFF));
    static readonly Brush TargetBorder = Frozen(Color.FromArgb(0x70, 0x5C, 0xE1, 0xFF));
    static readonly Brush IdleDot = Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    readonly SessionStore store;
    readonly DockWindow dock;
    bool closing;

    public event Action<SessionRecord>? FocusRequested;
    public event Action<SessionRecord>? OpenRequested;
    public event Action<SessionRecord>? DeleteRequested;
    public event Action<SessionRecord, string>? RenameRequested;
    public event Action? NewRequested;
    /// A session's workspaces or playgrounds were changed here (save it).
    public event Action<SessionRecord>? SessionChanged;
    bool dialogOpen; // a folder picker is up: don't close on losing focus

    public SessionsPanel(SessionStore store, DockWindow dock)
    {
        this.store = store;
        this.dock = dock;
        InitializeComponent();
        Refresh();

        SizeChanged += (_, _) => Place();
        Loaded += (_, _) =>
        {
            Place();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
        };
        Deactivated += (_, _) => { if (!dialogOpen) SafeClose(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) SafeClose(); };
    }

    public void SafeClose()
    {
        if (closing) return;
        closing = true;
        Close();
    }

    public void Refresh()
    {
        List.Children.Clear();
        var items = store.NewestFirst.ToList();
        foreach (var s in items) List.Children.Add(MakeRow(s));
        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Count.Text = items.Count > 0 ? items.Count.ToString() : "";
    }

    FrameworkElement MakeRow(SessionRecord s)
    {
        bool target = s.Id == store.FocusedId;
        bool open = s.Window != null;
        bool busy = s.Window?.IsStreaming == true;

        var dot = new Ellipse
        {
            Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 6, 10, 0),
            Fill = open ? Accent : IdleDot, ToolTip = busy ? "Answering…" : open ? "Bubble open" : "Closed",
        };
        if (busy)
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(550))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

        var title = new TextBlock { Text = s.Title, Foreground = Fg, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var titleLine = new DockPanel { LastChildFill = true };
        if (target)
        {
            var pill = new Border
            {
                Child = new TextBlock { Text = "TARGET", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Accent },
                BorderBrush = TargetBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(pill, Dock.Right);
            titleLine.Children.Add(pill);
        }
        titleLine.Children.Add(title);

        int msgs = s.History.Count;
        var meta = new TextBlock
        {
            Text = $"{Agents.NameOf(s.AgentId)} · {msgs} message{(msgs == 1 ? "" : "s")} · {Ago(s.Updated)}",
            Foreground = Muted, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var text = new StackPanel();
        text.Children.Add(titleLine);
        text.Children.Add(meta);
        text.Children.Add(MakeChips(s));

        var openBtn = IconButton("", "Continue (opens the bubble)", () => OpenRequested?.Invoke(s));
        var renameBtn = IconButton("", "Rename", () => BeginRename(s, titleLine, title));
        var delBtn = IconButton("", "Delete session", () => DeleteRequested?.Invoke(s));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        actions.Children.Add(openBtn);
        actions.Children.Add(renameBtn);
        actions.Children.Add(delBtn);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1); Grid.SetColumn(actions, 2);
        grid.Children.Add(dot); grid.Children.Add(text); grid.Children.Add(actions);

        var row = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 8, 6, 8), Margin = new Thickness(0, 0, 0, 3),
            Background = target ? RowTarget : Brushes.Transparent, Cursor = Cursors.Hand,
        };
        actions.Opacity = target ? 1 : 0.55;
        row.MouseEnter += (_, _) => { if (!target) row.Background = RowHover; actions.Opacity = 1; };
        row.MouseLeave += (_, _) => { if (!target) row.Background = Brushes.Transparent; actions.Opacity = target ? 1 : 0.55; };
        row.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) OpenRequested?.Invoke(s);
            else FocusRequested?.Invoke(s);
        };
        return row;
    }

    static readonly Brush ChipBg = Frozen(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
    static readonly Brush ChipBorder = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    internal static readonly Brush Folder = Frozen(Color.FromRgb(0xF2, 0xC2, 0x4B));

    /// Workspaces (folders/files it works on) and playgrounds (windows it works in) as small chips; right-click to manage.
    FrameworkElement MakeChips(SessionRecord s)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        foreach (var w in s.Workspaces)
        {
            bool exists = System.IO.Directory.Exists(w.Path) || System.IO.File.Exists(w.Path);
            var chip = Chip(w.IsFolder || !exists ? "\uE8B7" : "\uE8A5", w.Name + (w.Edit ? "  \u270E" : ""), Folder,
                $"Workspace: {w.Path}\n{(w.Edit ? "The agent can read and edit it." : "Read-only.")}{(exists ? "" : "\n(not found)")}\nRight-click for options");
            if (!exists) chip.Opacity = 0.5;
            chip.ContextMenu = Menu(
                (w.Edit ? "Make read-only" : "Allow edits", () => { w.Edit = !w.Edit; SessionChanged?.Invoke(s); }),
                ("Show in File Explorer", () => { try { System.Diagnostics.Process.Start("explorer.exe", $"\"{w.Path}\""); } catch { } }),
                ("Remove workspace", () => { s.Workspaces.Remove(w); SessionChanged?.Invoke(s); }));
            wrap.Children.Add(chip);
        }
        foreach (var p in s.Playgrounds)
        {
            var chip = Chip(p.IsBrowser ? "\uE774" : "\uE737", p.Label, Accent,
                $"Playground: {p.Process}{(p.Url != null ? " \u00B7 " + p.Url : "")}\n{p.Title}\nRight-click to remove");
            chip.ContextMenu = Menu(("Remove playground", () => { s.Playgrounds.Remove(p); SessionChanged?.Invoke(s); }));
            wrap.Children.Add(chip);
        }
        var add = Chip("\uE710", "folder", Muted, "Add a workspace folder");
        add.Opacity = 0.7;
        add.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            dialogOpen = true;
            try
            {
                using var dlg = new WF.FolderBrowserDialog { Description = $"Workspace for \u201C{s.Title}\u201D", UseDescriptionForTitle = true };
                if (dlg.ShowDialog() == WF.DialogResult.OK && s.AddWorkspace(dlg.SelectedPath)) SessionChanged?.Invoke(s);
            }
            finally { dialogOpen = false; }
        };
        add.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true; // not a row click
        wrap.Children.Add(add);
        return wrap;
    }

    internal static Border Chip(string glyph, string label, Brush iconBrush, string tip)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 10.5, Foreground = iconBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) });
        sp.Children.Add(new TextBlock { Text = label.Length > 28 ? label[..26] + "\u2026" : label, Foreground = Fg, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center });
        var b = new Border
        {
            Child = sp, Background = ChipBg, BorderBrush = ChipBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 1, 7, 2), Margin = new Thickness(0, 0, 4, 4), ToolTip = tip, Cursor = Cursors.Arrow,
        };
        b.MouseEnter += (_, _) => b.BorderBrush = Accent;
        b.MouseLeave += (_, _) => b.BorderBrush = ChipBorder;
        b.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) e.Handled = true; }; // not "open the session"
        return b;
    }

    internal static ContextMenu Menu(params (string Text, Action Do)[] items)
    {
        var m = new ContextMenu
        {
            Background = Frozen(Color.FromRgb(0x26, 0x26, 0x2C)), Foreground = Fg, BorderBrush = ChipBorder,
        };
        foreach (var (text, act) in items)
        {
            var mi = new MenuItem { Header = text, Foreground = Fg, Background = Brushes.Transparent };
            mi.Click += (_, _) => act();
            m.Items.Add(mi);
        }
        return m;
    }

    /// Swap the title for an inline text box. Enter or clicking away saves, Esc cancels.
    void BeginRename(SessionRecord s, DockPanel titleLine, TextBlock title)
    {
        if (!titleLine.Children.Contains(title)) return; // already editing
        var box = new TextBox
        {
            Text = s.Title, Foreground = Fg, CaretBrush = Fg, SelectionBrush = Accent, FontWeight = FontWeights.SemiBold,
            Background = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), BorderBrush = Accent, BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 1, 4, 2), MaxLength = 80,
        };
        bool done = false;
        void Finish(bool save)
        {
            if (done) return;
            done = true;
            var name = box.Text.Trim();
            if (save && name.Length > 0 && name != s.Title) RenameRequested?.Invoke(s, name);
            else Refresh();
        }
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        box.MouseDoubleClick += (_, e) => e.Handled = true;
        box.PreviewMouseLeftButtonDown += (_, e) => e.Handled = false;

        int i = titleLine.Children.IndexOf(title);
        titleLine.Children.RemoveAt(i);
        titleLine.Children.Insert(i, box);
        box.Focus();
        box.SelectAll();
    }

    Button IconButton(string glyph, string tip, Action click)
    {
        var tb = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12 };
        var b = new Button { Content = tb, ToolTip = tip, Width = 28, Height = 28, Focusable = false, Cursor = Cursors.Hand, Foreground = Muted };
        b.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<Border x:Name='Bd' CornerRadius='7' Background='Transparent' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
            "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'>" +
            "<Setter TargetName='Bd' Property='Background' Value='#22FFFFFF'/><Setter Property='Foreground' Value='#FFFFFF'/>" +
            "</Trigger></ControlTemplate.Triggers></ControlTemplate>");
        b.Click += (_, e) => { e.Handled = true; click(); };
        b.PreviewMouseLeftButtonDown += (_, e) => e.Handled = false;
        b.MouseDoubleClick += (_, e) => e.Handled = true;
        return b;
    }

    /// Above the dock button (or below if there is no room), aligned to its right edge.
    void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var d = dock.ScreenRect;
        var wa = WF.Screen.FromPoint(new System.Drawing.Point((d.Left + d.Right) / 2, (d.Top + d.Bottom) / 2)).WorkingArea;

        int x = d.Right - w + 6;
        if (x < wa.Left) x = d.Left - 6;
        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - w));
        int y = d.Top - h + 14;
        if (y < wa.Top) y = d.Bottom - 14;
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    void New_Click(object sender, RoutedEventArgs e) => NewRequested?.Invoke();

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} d ago";
        return t.ToString("MMM d");
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
