using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WF = System.Windows.Forms;

namespace Assist;

/// One message in a conversation, kept so a closed bubble can be reopened with its history.
public sealed record Turn(bool User, string Text, string? Attach);

public partial class BubbleWindow : Window
{
    static readonly Brush Fg = Frozen(Color.FromRgb(0xEC, 0xEC, 0xEE));
    static readonly Brush Muted = Frozen(Color.FromRgb(0x8C, 0x8C, 0x96));
    static readonly Brush ErrorFg = Frozen(Color.FromRgb(0xFF, 0x8A, 0x80));
    static readonly Brush ChipBg = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    static readonly Brush UserBg = Frozen(Color.FromArgb(0x38, 0xD9, 0x77, 0x57));
    static readonly Brush AccentBrush = Frozen(Color.FromRgb(0xD9, 0x77, 0x57));
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    sealed record Attachment(string Kind, string Label, FrameworkElement Chip);

    readonly Settings settings;
    readonly string? resumeId;
    readonly List<Attachment> attachments = new();

    // Context captured by the latest hotkey press, not sent yet.
    CapturedContext? pendingCtx;
    int pendingHotkey;
    bool contextReady, sendRequested;

    int anchorX, anchorY;
    bool anchorBelow;

    ClaudeSession? session;
    bool streaming, userMoved, turnDonePending, closed;
    (bool err, string? msg) doneInfo;
    TextBox? current;
    readonly StringBuilder pending = new();
    readonly object gate = new();

    public List<Turn> History { get; } = new();
    public string? SessionId => session?.SessionId ?? resumeId;
    public bool HasConversation => History.Count > 0;
    public bool IsClosed => closed;

    public BubbleWindow(Settings settings, string? resumeId = null, IReadOnlyList<Turn>? history = null)
    {
        this.settings = settings;
        this.resumeId = resumeId;
        InitializeComponent();
        ShowActivated = false; // keep focus on the user's app until the selection has been grabbed

        if (history is { Count: > 0 })
        {
            Expand();
            foreach (var t in history)
            {
                if (t.User) AddUserBubble(t.Text, t.Attach);
                else AddAssistantText(t.Text);
            }
            Messages.Children.Add(new TextBlock
            {
                Text = "Resumed", FontSize = 11, Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 6),
            });
        }

        SourceInitialized += (_, _) => PlaceNearCursor();
        SizeChanged += (_, _) => { if (!userMoved) PlaceNearCursor(); };
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        CompositionTarget.Rendering += OnFrame;

        // Start claude right away so it is warm by the time the user hits Enter.
        try { StartSession(); }
        catch (Exception ex) { Expand(); AddError("Couldn't start Claude Code: " + ex.Message); }
    }

    // ---------- lifecycle ----------

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceNearCursor();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        Slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(anchorBelow ? -6 : 6, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    void OnClosed(object? sender, EventArgs e)
    {
        closed = true;
        CompositionTarget.Rendering -= OnFrame;
        session?.Dispose();
    }

    void StartSession()
    {
        session = new ClaudeSession(settings, resumeId);
        session.Text += t => { lock (gate) pending.Append(t); };
        session.Status += s => Dispatcher.BeginInvoke(() => { if (streaming) StatusText.Text = s; });
        session.TurnDone += (err, msg) => Dispatcher.BeginInvoke(() =>
        {
            doneInfo = (err, msg);
            turnDonePending = true;
        });
    }

    // ---------- hotkey entry points ----------

    /// A new capture for this conversation. Replaces any attachments that weren't sent yet.
    public void AttachContext(CapturedContext ctx, int hotkey)
    {
        pendingCtx = ctx;
        pendingHotkey = hotkey;
        contextReady = false;
        sendRequested = false;

        attachments.Clear();
        Attachments.Children.Clear();
        Attachments.Visibility = Visibility.Visible;
        BuildAttachments(ctx);
        UpdatePlaceholder();

        anchorX = ctx.CursorX; anchorY = ctx.CursorY;
        var wa = WF.Screen.FromPoint(new System.Drawing.Point(anchorX, anchorY)).WorkingArea;
        anchorBelow = anchorY < wa.Top + wa.Height * 0.55;
        if (IsLoaded && !userMoved) PlaceNearCursor();
    }

    /// Pressing the same hotkey again while its capture is still unsent means "send it".
    public bool CanSendAsIs(int hotkey) => !closed && pendingCtx != null && pendingHotkey == hotkey;

    public void SendAsIs()
    {
        if (!contextReady || streaming) { sendRequested = true; return; }
        sendRequested = false;
        var text = Input.Text;
        Input.Clear();
        Send(text);
    }

    /// Called once the selected text has been grabbed (or skipped) for this capture.
    public void ContextReady(CapturedContext ctx)
    {
        if (closed || ctx != pendingCtx) return;
        contextReady = true;
        if (ctx.SelectedText != null)
            AddAttachment("selection", "Selection", null, Preview(ctx.SelectedText), "", "Selection",
                          at: attachments.Count > 0 && attachments[0].Kind == "screen" ? 1 : 0);
        UpdatePlaceholder();

        Native.ForceForeground(new WindowInteropHelper(this).Handle);
        Activate();
        Input.Focus();

        if (sendRequested) SendAsIs();
    }

    // ---------- attachments ----------

    void BuildAttachments(CapturedContext ctx)
    {
        AddAttachment("screen", "Screen", ScreenChip(ctx), $"Screenshot + mouse marker\n{ctx.ScreenshotPath}", open: ctx.ScreenshotPath);
        if (ctx.HasClipboard)
        {
            string label = ctx.ClipboardText != null ? "Clipboard"
                         : ctx.ClipboardImageJpeg != null ? "Clipboard image" : $"{ctx.ClipboardFiles.Count} file(s)";
            string tip = ctx.ClipboardText != null ? Preview(ctx.ClipboardText) : string.Join("\n", ctx.ClipboardFiles);
            AddAttachment("clipboard", label, null, tip, "", label);
        }
    }

    static FrameworkElement ScreenChip(CapturedContext ctx) => new Border
    {
        Width = 34, Height = 22, CornerRadius = new CornerRadius(4),
        Background = new ImageBrush(Thumbnail(ctx.FullJpeg)) { Stretch = Stretch.UniformToFill },
        BorderBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
    };

    void AddAttachment(string kind, string label, FrameworkElement? visual, string? tip,
                       string? glyph = null, string? text = null, int? at = null, string? open = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (visual != null) sp.Children.Add(visual);
        else sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) });
        sp.Children.Add(new TextBlock { Text = text ?? label, FontSize = 12, Foreground = Fg, Margin = new Thickness(6, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });

        var x = new TextBlock
        {
            Text = "", FontFamily = Icons, FontSize = 9, Foreground = Muted, Margin = new Thickness(7, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Remove (Backspace)",
        };
        sp.Children.Add(x);

        var chip = new Border
        {
            Child = sp, CornerRadius = new CornerRadius(8), Background = ChipBg,
            Padding = new Thickness(3, 3, 6, 3), Margin = new Thickness(0, 0, 4, 0), Height = 30,
        };
        if (!string.IsNullOrEmpty(tip)) chip.ToolTip = tip;
        if (open != null) chip.MouseLeftButtonUp += (_, e) => { if (!e.Handled) Open(open); };

        var a = new Attachment(kind, label, chip);
        x.MouseLeftButtonUp += (_, e) => { e.Handled = true; RemoveAttachment(a); };
        int i = Math.Clamp(at ?? attachments.Count, 0, attachments.Count);
        attachments.Insert(i, a);
        Attachments.Children.Insert(i, chip);
    }

    void RemoveAttachment(Attachment a)
    {
        attachments.Remove(a);
        Attachments.Children.Remove(a.Chip);
        UpdatePlaceholder();
        Input.Focus();
    }

    void UpdatePlaceholder()
    {
        if (pendingCtx != null && attachments.Count > 0) Placeholder.Text = "Ask, or just press Enter";
        else if (HasConversation) Placeholder.Text = "Ask a follow-up…";
        else Placeholder.Text = "Ask Claude…";
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- sending ----------

    void Send(string text)
    {
        text = text.Trim();
        if (streaming || closed) return;
        var ctx = pendingCtx;
        if (text.Length == 0 && !(ctx != null && attachments.Count > 0)) return;

        if (session == null)
        {
            try { StartSession(); }
            catch (Exception ex) { Expand(); AddError("Couldn't start Claude Code: " + ex.Message); return; }
        }

        var blocks = new List<object>();
        string body = text.Length > 0 ? text : settings.Instruction;
        string? attachLine = null;
        if (ctx != null)
        {
            ctx.IncludeScreen = attachments.Any(a => a.Kind == "screen");
            ctx.IncludeSelection = attachments.Any(a => a.Kind == "selection");
            ctx.IncludeClipboard = attachments.Any(a => a.Kind == "clipboard");
            blocks.AddRange(ctx.ImageBlocks());
            body = ctx.ContextText() + "\n\n" + body;
            attachLine = attachments.Count > 0 ? string.Join(" · ", attachments.Select(a => a.Label)) : null;

            pendingCtx = null;
            attachments.Clear();
            Attachments.Children.Clear();
            Attachments.Visibility = Visibility.Collapsed;
        }
        blocks.Add(new { type = "text", text = body });

        Expand();
        AddUserBubble(text, attachLine);
        current = MakeText("", Fg);
        current.Margin = new Thickness(0, 4, 0, 8);
        Messages.Children.Add(current);
        UpdatePlaceholder();

        SetStreaming(true);
        StatusText.Text = "Thinking…";
        try { session!.Send(blocks); }
        catch (Exception ex)
        {
            SetStreaming(false);
            AddError("Lost connection to Claude: " + ex.Message);
            session?.Dispose(); session = null;
        }
        Scroller.ScrollToEnd();
    }

    void Expand()
    {
        Header.Visibility = Visibility.Visible;
        Scroller.Visibility = Visibility.Visible;
        Composer.Margin = new Thickness(10, 6, 10, 10);
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
        else if (current != null)
        {
            current.Text = current.Text.TrimEnd();
            History.Add(new Turn(false, current.Text, null));
        }
        if (IsActive) Input.Focus();
        if (sendRequested && contextReady) SendAsIs();
    }

    // ---------- conversation UI ----------

    void AddUserBubble(string text, string? attachLine)
    {
        History.Add(new Turn(true, text, attachLine));
        var sp = new StackPanel();
        if (attachLine != null)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
            line.Children.Add(new TextBlock { Text = attachLine, FontSize = 12, Foreground = Muted, Margin = new Thickness(5, 0, 0, 1) });
            sp.Children.Add(line);
        }
        if (text.Length > 0)
        {
            var tb = MakeText(text, Fg);
            if (attachLine != null) tb.Margin = new Thickness(0, 3, 0, 0);
            sp.Children.Add(tb);
        }
        Messages.Children.Add(new Border
        {
            Child = sp,
            Background = UserBg,
            CornerRadius = new CornerRadius(12, 12, 4, 12),
            Padding = new Thickness(10, 6, 10, 7),
            Margin = new Thickness(48, 8, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
        });
    }

    void AddAssistantText(string text)
    {
        History.Add(new Turn(false, text, null));
        var tb = MakeText(text, Fg);
        tb.Margin = new Thickness(0, 4, 0, 8);
        Messages.Children.Add(tb);
        current = tb;
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
        var wa = WF.Screen.FromPoint(new System.Drawing.Point(anchorX, anchorY)).WorkingArea;

        int x = anchorX - 8;
        if (x + w > wa.Right) x = anchorX - w + 8;
        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - w));

        int y = anchorBelow ? anchorY + 6 : anchorY - h - 6;
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

    void Send_Click(object sender, RoutedEventArgs e) => SendFromInput();

    void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendFromInput();
        }
        else if (e.Key == Key.Back && Input.CaretIndex == 0 && Input.SelectionLength == 0 && attachments.Count > 0)
        {
            e.Handled = true;
            RemoveAttachment(attachments[^1]);
        }
    }

    void SendFromInput()
    {
        if (streaming) return;
        var text = Input.Text;
        if (text.Trim().Length == 0 && (pendingCtx == null || attachments.Count == 0)) return;
        Input.Clear();
        Send(text);
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

    static ImageSource Thumbnail(string jpegB64)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.StreamSource = new MemoryStream(Convert.FromBase64String(jpegB64));
        bi.DecodePixelHeight = 66;
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    static string Preview(string s) => s.Length <= 600 ? s : s[..600] + "…";

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
