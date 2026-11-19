using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WF = System.Windows.Forms;

namespace Assist;

public partial class BubbleWindow : Window
{
    static readonly Brush Fg = Frozen(Color.FromRgb(0xEC, 0xEC, 0xEE));
    static readonly Brush Muted = Frozen(Color.FromRgb(0x8C, 0x8C, 0x96));
    static readonly Brush ErrorFg = Frozen(Color.FromRgb(0xFF, 0x8A, 0x80));
    static readonly Brush ChipBg = Frozen(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
    static readonly Brush UserBg = Frozen(Color.FromArgb(0x38, 0xD9, 0x77, 0x57));
    static readonly Brush AccentBrush = Frozen(Color.FromRgb(0xD9, 0x77, 0x57));
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    readonly CapturedContext ctx;
    readonly Settings settings;
    readonly bool autoSend;
    readonly bool anchorBelow;

    ClaudeSession? session;
    bool contextSent, streaming, userMoved, turnDonePending, closed;
    (bool err, string? msg) doneInfo;
    TextBox? current;
    readonly StringBuilder pending = new();
    readonly object gate = new();

    public BubbleWindow(CapturedContext ctx, Settings settings, bool autoSend)
    {
        this.ctx = ctx;
        this.settings = settings;
        this.autoSend = autoSend;
        InitializeComponent();

        var wa = WF.Screen.FromPoint(new System.Drawing.Point(ctx.CursorX, ctx.CursorY)).WorkingArea;
        anchorBelow = ctx.CursorY < wa.Top + wa.Height * 0.55;
        Placeholder.Text = autoSend ? "Ask a follow-up…" : "Ask about your screen…";

        BuildChips();
        SourceInitialized += (_, _) => PlaceNearCursor();
        SizeChanged += (_, _) => { if (!userMoved) PlaceNearCursor(); };
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        CompositionTarget.Rendering += OnFrame;

        // Start claude right away so it is warm by the time we send.
        try { StartSession(); }
        catch (Exception ex) { AddError("Couldn't start Claude Code: " + ex.Message); }
    }

    // ---------- lifecycle ----------

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceNearCursor();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        Slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(anchorBelow ? -8 : 8, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });

        Native.ForceForeground(new WindowInteropHelper(this).Handle);
        Activate();
        Input.Focus();

        if (autoSend) Send(settings.Instruction, showBubble: false);
    }

    void OnClosed(object? sender, EventArgs e)
    {
        closed = true;
        CompositionTarget.Rendering -= OnFrame;
        session?.Dispose();
    }

    void StartSession()
    {
        session = new ClaudeSession(settings);
        session.Text += t => { lock (gate) pending.Append(t); };
        session.Status += s => Dispatcher.BeginInvoke(() => { if (streaming) StatusText.Text = s; });
        session.TurnDone += (err, msg) => Dispatcher.BeginInvoke(() =>
        {
            doneInfo = (err, msg);
            turnDonePending = true;
        });
    }

    // ---------- sending ----------

    void Send(string text, bool showBubble)
    {
        if (streaming || closed || string.IsNullOrWhiteSpace(text)) return;
        if (session == null)
        {
            try { StartSession(); }
            catch (Exception ex) { AddError("Couldn't start Claude Code: " + ex.Message); return; }
        }

        var blocks = new List<object>();
        string body = text.Trim();
        if (!contextSent)
        {
            blocks.AddRange(ctx.ImageBlocks());
            body = ctx.ContextText() + "\n\n" + body;
            contextSent = true;
        }
        blocks.Add(new { type = "text", text = body });

        if (showBubble) AddUserBubble(text.Trim());
        current = MakeText("", Fg);
        current.Margin = new Thickness(0, 4, 0, 8);
        Messages.Children.Add(current);

        SetStreaming(true);
        StatusText.Text = "Thinking…";
        try { session!.Send(blocks); }
        catch (Exception ex)
        {
            SetStreaming(false);
            AddError("Lost connection to Claude: " + ex.Message);
            session?.Dispose(); session = null; contextSent = false;
        }
        Scroller.ScrollToEnd();
    }

    void SetStreaming(bool on)
    {
        streaming = on;
        SendBtn.IsEnabled = !on;
        if (on)
            Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(550))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        else
            Dot.BeginAnimation(OpacityProperty, null);
    }

    // Smooth typing: drain the received text a little each frame, faster when there's a backlog.
    void OnFrame(object? sender, EventArgs e)
    {
        string? chunk = null;
        bool empty;
        lock (gate)
        {
            int len = pending.Length;
            if (len > 0)
            {
                int n = len <= 3 ? len : Math.Max(3, len / 7);
                if (n < len && char.IsHighSurrogate(pending[n - 1])) n++;
                chunk = pending.ToString(0, n);
                pending.Remove(0, n);
            }
            empty = pending.Length == 0;
        }

        if (chunk != null && current != null)
        {
            bool atBottom = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 4;
            current.AppendText(chunk);
            if (atBottom) Scroller.ScrollToEnd();
        }
        else if (empty && turnDonePending)
        {
            turnDonePending = false;
            FinishTurn();
        }
    }

    void FinishTurn()
    {
        SetStreaming(false);
        StatusText.Text = "";
        if (doneInfo.err)
        {
            if (current != null && current.Text.Length == 0) Messages.Children.Remove(current);
            AddError(doneInfo.msg ?? "Something went wrong.");
        }
        else if (current != null) current.Text = current.Text.TrimEnd();
        if (IsActive) Input.Focus();
    }

    // ---------- UI building ----------

    void BuildChips()
    {
        AddChip("", "Screenshot", ctx.ScreenshotPath, () => Open(ctx.ScreenshotPath));
        AddChip("", $"{ctx.CursorX - ctx.Monitor.X}, {ctx.CursorY - ctx.Monitor.Y}", "Mouse position", null);
        if (!string.IsNullOrEmpty(ctx.ProcessName))
            AddChip("", ctx.ProcessName, ctx.WindowTitle, null);
        if (ctx.SelectedText != null)
            AddChip("", $"Selection · {Short(ctx.SelectedText.Length)}", Preview(ctx.SelectedText), null);
        if (ctx.ClipboardText != null)
            AddChip("", $"Clipboard · {Short(ctx.ClipboardText.Length)}", Preview(ctx.ClipboardText), null);
        if (ctx.ClipboardImageJpeg != null) AddChip("", "Clipboard image", null, null);
        if (ctx.ClipboardFiles.Count > 0)
            AddChip("", $"{ctx.ClipboardFiles.Count} file(s)", string.Join("\n", ctx.ClipboardFiles), null);
    }

    void AddChip(string glyph, string text, string? tip, Action? click)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = Muted, Margin = new Thickness(5, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis });
        var b = new Border { Child = sp, CornerRadius = new CornerRadius(7), Background = ChipBg, Padding = new Thickness(7, 3, 8, 3), Margin = new Thickness(0, 0, 5, 5) };
        if (!string.IsNullOrEmpty(tip)) b.ToolTip = tip;
        if (click != null) { b.Cursor = Cursors.Hand; b.MouseLeftButtonUp += (_, _) => click(); }
        Chips.Children.Add(b);
    }

    void AddUserBubble(string text)
    {
        var tb = MakeText(text, Fg);
        Messages.Children.Add(new Border
        {
            Child = tb,
            Background = UserBg,
            CornerRadius = new CornerRadius(12, 12, 4, 12),
            Padding = new Thickness(10, 6, 10, 7),
            Margin = new Thickness(48, 8, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
        });
    }

    void AddError(string text)
    {
        var tb = MakeText(text, ErrorFg);
        tb.FontSize = 13;
        tb.Margin = new Thickness(0, 4, 0, 8);
        Messages.Children.Add(tb);
        Scroller.ScrollToEnd();
    }

    static TextBox MakeText(string text, Brush fg) => new()
    {
        Text = text,
        IsReadOnly = true,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Foreground = fg,
        TextWrapping = TextWrapping.Wrap,
        Padding = new Thickness(0),
        FontSize = 14,
        SelectionBrush = AccentBrush,
        FocusVisualStyle = null,
        Cursor = Cursors.IBeam,
    };

    // ---------- placement ----------

    void PlaceNearCursor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        var wa = WF.Screen.FromPoint(new System.Drawing.Point(ctx.CursorX, ctx.CursorY)).WorkingArea;

        int x = ctx.CursorX - 8;
        if (x + w > wa.Right) x = ctx.CursorX - w + 8;
        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - w));

        int y = anchorBelow ? ctx.CursorY + 6 : ctx.CursorY - h - 6;
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - h));

        if (x != r.Left || y != r.Top)
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    // ---------- events ----------

    void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        userMoved = true;
        try { DragMove(); } catch { }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Send_Click(object sender, RoutedEventArgs e) => SendInput();

    void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendInput();
        }
    }

    void SendInput()
    {
        if (streaming || string.IsNullOrWhiteSpace(Input.Text)) return;
        var text = Input.Text;
        Input.Clear();
        Send(text, showBubble: true);
    }

    void Input_TextChanged(object sender, TextChangedEventArgs e) =>
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta * 0.5);
        e.Handled = true;
    }

    async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var text = current?.Text;
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { return; }
        CopyGlyph.Text = "";
        await Task.Delay(1100);
        CopyGlyph.Text = "";
    }

    // ---------- helpers ----------

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    static string Short(int chars) => chars < 1000 ? $"{chars} chars" : $"{chars / 1000.0:0.#}k chars";

    static string Preview(string s) => s.Length <= 600 ? s : s[..600] + "…";

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
