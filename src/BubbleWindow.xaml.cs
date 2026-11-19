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

    sealed record Attachment(string Kind, string Label, FrameworkElement Chip, string? Image = null, string? Path = null);

    Settings settings;
    readonly AgentInfo agent;
    readonly string? resumeId;
    readonly List<Attachment> attachments = new();

    // Context captured by the latest hotkey press, not sent yet.
    CapturedContext? pendingCtx;
    int pendingHotkey;
    bool contextReady, sendRequested;

    int anchorX, anchorY;
    bool anchorBelow;

    AgentSession? session;
    bool streaming, userMoved, turnDonePending, closed;
    (bool err, string? msg) doneInfo;
    TextBox? current;
    readonly StringBuilder pending = new();
    readonly object gate = new();

    // On-screen hints: tags are stripped from the stream and drawn over the screenshot's monitor.
    readonly HintFilter hintFilter = new();
    ScreenMap? lastScreen;     // the latest screenshot sent in this conversation
    HintOverlay? overlay;
    int hintTurn, overlayTurn;

    // Mouse/keyboard control: actions collected during a reply run once it is complete.
    readonly List<HintTag> turnActions = new();
    bool turnWantsControl, acting;
    IntPtr lastTarget;         // the app the user was in at the latest capture
    int autoTurns;             // look → act → look loop guard
    string? pendingReport;     // told to the agent with the next message (stopped/declined actions)
    readonly List<HintTag> turnAccess = new(); // <request-access/> tags in the current reply

    public List<Turn> History { get; } = new();
    public string? SessionId => session?.SessionId ?? resumeId;
    public bool HasConversation => History.Count > 0;
    public bool IsClosed => closed;

    public string AgentId => agent.Id;
    public bool IsStreaming => streaming;

    /// History, streaming state or session id changed.
    public event Action? Changed;
    /// The user clicked into this bubble.
    public event Action? UserFocused;
    /// The agent ran a tool: (tool name, command/path/query). UI thread.
    public event Action<string, string>? ToolUsed;
    /// How this conversation is named in the command log.
    public Func<string>? LogName;
    /// The agent finished answering (UI thread).
    public event Action? TurnFinished;

    // Files/folders shared with this session (the SessionRecord's list), and what the running agent was started with.
    readonly List<string> sharedPaths;
    string startedPaths = "";

    public BubbleWindow(Settings settings, AgentInfo agent, string? resumeId = null, IReadOnlyList<Turn>? history = null,
                        List<string>? sharedPaths = null)
    {
        this.settings = settings;
        this.agent = agent;
        this.resumeId = resumeId;
        this.sharedPaths = sharedPaths ?? new List<string>();
        InitializeComponent();
        AllowDrop = true;
        PreviewDragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
        };
        PreviewDrop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { e.Handled = true; ShareFiles(files); }
        };
        AgentLabel.Text = agent.Name;
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
        Activated += (_, _) => UserFocused?.Invoke();
        PreviewKeyDown +=(_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        CompositionTarget.Rendering += OnFrame;

        // Start claude right away so it is warm by the time the user hits Enter.
        try { StartSession(); }
        catch (Exception ex) { Expand(); AddError($"Couldn't start {agent.Name}: " + ex.Message); }
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
        // Files dropped but never sent are not shared after all.
        foreach (var a in attachments.Where(a => a.Path != null)) sharedPaths.Remove(a.Path!);
        closed = true;
        CompositionTarget.Rendering -= OnFrame;
        session?.Dispose();
    }

    void StartSession(string? resume = null, bool refreshInstructions = false)
    {
        var paths = sharedPaths.ToList();
        session = new AgentSession(settings, agent, resume ?? resumeId, refreshInstructions, paths) { LogName = () => LogName?.Invoke() ?? "session" };
        startedPaths = string.Join("|", paths);
        session.Text += t =>
        {
            var tags = new List<HintTag>();
            var shown = hintFilter.Feed(t, tags);
            lock (gate) pending.Append(shown);
            if (tags.Count > 0) Dispatcher.BeginInvoke(() =>
            {
                var hints = tags.Where(x => x.Name == "hint").ToList();
                if (hints.Count > 0) ShowHints(hints);
                turnActions.AddRange(tags.Where(x => x.Name == "action"));
                if (tags.Any(x => x.Name == "request-control")) turnWantsControl = true;
                turnAccess.AddRange(tags.Where(x => x.Name == "request-access"));
            });
        };
        session.Status += s => Dispatcher.BeginInvoke(() => { if (streaming) StatusText.Text = s; });
        session.ToolUsed += (name, detail) => Dispatcher.BeginInvoke(() => ToolUsed?.Invoke(name, detail));
        session.TurnDone += (err, msg) =>
        {
            var rest = hintFilter.Flush();
            if (rest.Length > 0) lock (gate) pending.Append(rest);
            Dispatcher.BeginInvoke(() =>
            {
                doneInfo = (err, msg);
                turnDonePending = true;
                TurnFinished?.Invoke();
            });
        };
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

    /// Open (or bring back) this conversation next to a screen point, focused and ready to type. No capture.
    public void ShowFocused(int x, int y)
    {
        if (!IsVisible || !userMoved)
        {
            anchorX = x; anchorY = y;
            var wa = WF.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
            anchorBelow = y < wa.Top + wa.Height * 0.55;
        }
        UpdatePlaceholder();
        if (!IsVisible) Show();
        else if (!userMoved) PlaceNearCursor();
        Native.ForceForeground(new WindowInteropHelper(this).Handle);
        Activate();
        Input.Focus();
        Scroller.ScrollToEnd();
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
                       string? glyph = null, string? text = null, int? at = null, string? open = null, string? image = null,
                       string? path = null)
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

        var a = new Attachment(kind, label, chip, image, path);
        x.MouseLeftButtonUp += (_, e) => { e.Handled = true; RemoveAttachment(a); };
        int i = Math.Clamp(at ?? attachments.Count, 0, attachments.Count);
        attachments.Insert(i, a);
        Attachments.Children.Insert(i, chip);
    }

    void RemoveAttachment(Attachment a)
    {
        attachments.Remove(a);
        Attachments.Children.Remove(a.Chip);
        if (a.Path != null) { sharedPaths.Remove(a.Path); Changed?.Invoke(); } // un-share before it was ever sent
        UpdatePlaceholder();
        Input.Focus();
    }

    /// Files/folders dropped on the bubble or the Computah button: give this session access and show a tag for each.
    public void ShareFiles(IEnumerable<string> paths)
    {
        foreach (var raw in paths)
        {
            string p;
            try { p = System.IO.Path.GetFullPath(raw); } catch { continue; }
            bool isDir = System.IO.Directory.Exists(p);
            if (!isDir && !System.IO.File.Exists(p)) continue;
            if (attachments.Any(x => x.Path != null && string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            if (!sharedPaths.Contains(p, StringComparer.OrdinalIgnoreCase)) sharedPaths.Add(p);

            var name = System.IO.Path.GetFileName(p.TrimEnd('\\', '/'));
            if (name.Length == 0) name = p;
            AddAttachment("file", name, null, p + (isDir ? "\n(folder)" : ""), isDir ? "" : "",
                          name.Length > 24 ? name[..22] + "…" : name, path: p);
        }
        Attachments.Visibility = attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePlaceholder();
        Changed?.Invoke();
    }

    void UpdatePlaceholder()
    {
        if (attachments.Count > 0) Placeholder.Text = "Ask, or just press Enter";
        else if (HasConversation) Placeholder.Text = "Ask a follow-up…";
        else Placeholder.Text = $"Ask {agent.Name}…";
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- sending ----------

    void Send(string text)
    {
        text = text.Trim();
        if (streaming || acting || closed) return;
        var ctx = pendingCtx;
        if (text.Length == 0 && attachments.Count == 0) return;
        autoTurns = 0;

        var blocks = new List<object>();
        string body = text.Length > 0 ? text
                    : ctx != null ? settings.Instruction
                    : attachments.Any(a => a.Path != null) ? "Take a look at what I shared and tell me briefly what it is and anything that stands out."
                    : "Look at the pasted image(s) and help with what I most likely need. Be concise.";
        string? attachLine = attachments.Count > 0 ? string.Join(" · ", attachments.Select(a => a.Label)) : null;
        var pastedImages = attachments.Where(a => a.Image != null).Select(a => a.Image!).ToList();

        if (ctx != null)
        {
            ctx.IncludeScreen = attachments.Any(a => a.Kind == "screen");
            if (ctx.IncludeScreen) lastScreen = new ScreenMap(ctx);
            lastTarget = ctx.ForegroundHwnd;
            ctx.IncludeSelection = attachments.Any(a => a.Kind == "selection");
            ctx.IncludeClipboard = attachments.Any(a => a.Kind == "clipboard");
            blocks.AddRange(ctx.ImageBlocks());
            body = ctx.ContextText() + "\n\n" + body;
            pendingCtx = null;
        }
        if (pastedImages.Count > 0)
        {
            blocks.AddRange(pastedImages.Select(Img.Block));
            body = $"[The user pasted {pastedImages.Count} image(s); they are the last image(s) attached to this message.]\n\n" + body;
        }
        var files = attachments.Where(a => a.Path != null).Select(a => a.Path!).ToList();
        if (files.Count > 0)
        {
            var list = string.Join("\n", files.Select(p => $"- {p}{(System.IO.Directory.Exists(p) ? "  (folder)" : "")}"));
            var how = agent.SupportsSharedPaths
                ? "You have read access to them: use your file tools (read, list, search) on these exact paths."
                : "Try to open them with your file tools using these exact paths.";
            body = $"[Computah] The user shared these with you. {how}\n{list}\n\n" + body;
        }
        attachments.Clear();
        Attachments.Children.Clear();
        Attachments.Visibility = Visibility.Collapsed;
        if (pendingReport != null) { body = pendingReport + "\n\n" + body; pendingReport = null; }
        if (PeekWindow.DescribeOpen(this) is { } cards) body = cards + "\n\n" + body;
        blocks.Add(new { type = "text", text = body });
        Dispatch(blocks, text, attachLine);
    }

    /// Shows the user's side of the turn and sends it to the agent.
    void Dispatch(List<object> blocks, string userText, string? attachLine)
    {
        // The control permission changed since this agent started: restart it (same conversation) so it knows.
        // The control permission or the shared files changed since this agent started: restart it
        // (same conversation) so it has the right instructions and file access.
        if (session != null && (Settings.Load().Control != settings.Control || string.Join("|", sharedPaths) != startedPaths))
            RestartSession();
        if (session == null)
        {
            try { StartSession(); }
            catch (Exception ex) { Expand(); AddError($"Couldn't start {agent.Name}: " + ex.Message); return; }
        }

        Expand();
        AddUserBubble(userText, attachLine);
        current = MakeText("", Fg);
        current.Margin = new Thickness(0, 4, 0, 8);
        Messages.Children.Add(current);
        UpdatePlaceholder();

        SetStreaming(true);
        StatusText.Text = "Thinking…";
        hintFilter.Reset();
        turnActions.Clear();
        turnWantsControl = false;
        turnAccess.Clear();
        hintTurn++;
        try { session!.Send(blocks); }
        catch (Exception ex)
        {
            SetStreaming(false);
            AddError($"Lost connection to {agent.Name}: " + ex.Message);
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
        Changed?.Invoke();
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
            // hint tags often sat on their own line: drop the blank lines they leave behind
            current.Text = System.Text.RegularExpressions.Regex.Replace(current.Text, @"[ \t]*\n[ \t]*\n(?:[ \t]*\n)+", "\n\n").Trim();
            History.Add(new Turn(false, current.Text, null));
        }
        if (IsActive) Input.Focus();
        Changed?.Invoke();

        // Actions / control requests from this reply.
        var acts = turnActions.ToList();
        bool wantsControl = turnWantsControl;
        var access = turnAccess.ToList();
        turnActions.Clear();
        turnWantsControl = false;
        turnAccess.Clear();
        if (!doneInfo.err)
        {
            if (access.Count > 0) AddAccessCard(access);
            var mode = Settings.Load().Control;
            if (acts.Count > 0)
            {
                if (mode == "on") _ = RunActions(acts);
                else if (mode == "ask") AddApprovalCard(acts);
                else AddPermissionCard();
            }
            else if (wantsControl && mode == "off") AddPermissionCard();
        }

        if (sendRequested && contextReady && !acting) SendAsIs();
    }

    // ---------- mouse & keyboard control ----------

    /// Restart the agent process in the same conversation so it picks up new instructions.
    void RestartSession()
    {
        var sid = SessionId;
        session?.Dispose();
        session = null;
        settings = Settings.Load();
        try { StartSession(sid, refreshInstructions: true); }
        catch (Exception ex) { AddError($"Couldn't restart {agent.Name}: " + ex.Message); }
    }

    void AddPermissionCard()
    {
        var card = Card(
            $"Let {agent.Name} control your mouse and keyboard?",
            "It can then click, type and press keys for you. Move the mouse or press Esc to stop it at any time.");
        var buttons = (StackPanel)((StackPanel)card.Child).Children[2];
        buttons.Children.Add(CardButton("Allow", true, () => Grant(card, "on")));
        buttons.Children.Add(CardButton("Ask me each time", false, () => Grant(card, "ask")));
        buttons.Children.Add(CardButton("Not now", false, () => { Resolve(card, "Control not allowed."); pendingReport = "[Computah] The user did not allow mouse and keyboard control. Help them do it themselves."; }));
        Messages.Children.Add(card);
        Scroller.ScrollToEnd();
        ShowBubble(activate: true);
    }

    void Grant(Border card, string mode)
    {
        var s = Settings.Load();
        s.Control = mode;
        s.Save();
        Resolve(card, mode == "on" ? "Control allowed." : "Control allowed: you'll approve each step.");
        RestartSession();
        var note = "[Computah] The user allowed mouse and keyboard control" + (mode == "ask" ? " (they approve each batch)" : "") +
                   ". Do what they asked now by writing <action/> tags in your reply (coordinates from the latest screenshot), " +
                   "ending with <action type=\"look\"/> if you need to check the result.";
        Dispatch(new List<object> { new { type = "text", text = note } }, "", "Computah: control allowed");
    }

    // ---------- file access ----------

    /// The agent asked to read files/folders it can't reach: ask the user, then restart it with access and continue.
    void AddAccessCard(List<HintTag> requests)
    {
        var paths = new List<string>();
        var missing = new List<string>();
        foreach (var r in requests)
        {
            var raw = r.Get("path");
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var p = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')).Replace('/', '\\');
            try { p = System.IO.Path.GetFullPath(p); } catch { missing.Add(raw); continue; }
            if (!System.IO.Directory.Exists(p) && !System.IO.File.Exists(p)) { missing.Add(raw); continue; }
            if (sharedPaths.Contains(p, StringComparer.OrdinalIgnoreCase)) continue; // already has it
            if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p);
        }
        if (paths.Count == 0)
        {
            if (missing.Count > 0) pendingReport = $"[Computah] These paths don't exist, so no access was asked for: {string.Join(", ", missing)}";
            return;
        }

        var reason = requests.Select(r => r.Get("reason")).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        var body = string.Join("\n", paths.Select(p => (System.IO.Directory.Exists(p) ? "📁 " : "📄 ") + p))
                 + (reason != null ? $"\n\nWhy: {reason}" : "")
                 + "\n\nIt can read and search them for this conversation only. It can't change or delete anything.";
        var card = Card($"Let {agent.Name} read {(paths.Count == 1 ? "this" : "these")}?", body);
        var buttons = (StackPanel)((StackPanel)card.Child).Children[2];
        buttons.Children.Add(CardButton("Allow", true, () =>
        {
            foreach (var p in paths) if (!sharedPaths.Contains(p, StringComparer.OrdinalIgnoreCase)) sharedPaths.Add(p);
            Changed?.Invoke(); // saved with the session
            Resolve(card, "Access allowed.");
            var note = "[Computah] The user gave you read access to:\n" + string.Join("\n", paths.Select(p => "- " + p)) +
                       "\nUse your file tools (read, list, search) on these paths now and continue with what they asked.";
            Dispatch(new List<object> { new { type = "text", text = note } }, "", "Computah: read access allowed");
        }));
        buttons.Children.Add(CardButton("Not now", false, () =>
        {
            Resolve(card, "Access not allowed.");
            pendingReport = "[Computah] The user did not give you access to: " + string.Join(", ", paths) +
                            ". Don't ask again for these unless they bring it up; help with what you can.";
        }));
        Messages.Children.Add(card);
        Scroller.ScrollToEnd();
        ShowBubble(activate: true);
    }

    void AddApprovalCard(List<HintTag> acts)
    {
        var lines = string.Join("\n", acts.Take(10).Select(a => "• " + ActionRunner.Describe(a))) + (acts.Count > 10 ? $"\n• …and {acts.Count - 10} more" : "");
        var card = Card($"{agent.Name} wants to:", lines);
        var buttons = (StackPanel)((StackPanel)card.Child).Children[2];
        buttons.Children.Add(CardButton("Run", true, () => { Resolve(card, "Approved."); _ = RunActions(acts); }));
        buttons.Children.Add(CardButton("Cancel", false, () => { Resolve(card, "Cancelled."); pendingReport = "[Computah] The user cancelled your last actions; nothing was done."; }));
        Messages.Children.Add(card);
        Scroller.ScrollToEnd();
        ShowBubble(activate: true);
    }

    async Task RunActions(List<HintTag> acts)
    {
        if (acting || closed) return;
        acting = true;
        SendBtn.IsEnabled = false;
        StatusText.Text = "Working… move the mouse or press Esc to stop";
        Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(300)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

        bool wasVisible = IsVisible;
        Hide();                     // never click our own bubble
        await Task.Delay(150);

        ActionRunner.Result res;
        try { res = await ActionRunner.RunAsync(acts, lastScreen, lastTarget, FlashPointer); }
        catch (Exception ex) { res = new ActionRunner.Result(false, "an error: " + ex.Message, new List<string>()); }
        CommandLog.Actions(LogName?.Invoke() ?? "session", Settings.Load().Control, acts.Select(ActionRunner.Describe),
            res.Stopped == null ? "completed" : $"stopped after {res.Done.Count} of {acts.Count}: {res.Stopped}");

        CapturedContext? shot = null;
        if (res.Stopped == null && res.Look && autoTurns < 10 && !closed)
        {
            await Task.Delay(450);  // let the screen settle
            try { shot = await ContextCapture.CaptureAsync(Settings.Load()); } catch (Exception ex) { Paths.Log("look: " + ex.Message); }
        }

        acting = false;
        Dot.BeginAnimation(OpacityProperty, null);
        SendBtn.IsEnabled = true;
        StatusText.Text = "";
        if (closed) return;
        if (wasVisible) ShowBubble(activate: false);

        var doneText = res.Done.Count > 0 ? string.Join("; ", res.Done) : "nothing";
        if (res.Stopped != null)
        {
            AddNote($"Stopped: {res.Stopped}.");
            pendingReport = $"[Computah] Your actions were stopped because {res.Stopped}. Completed before that: {doneText}.";
        }
        else if (shot != null)
        {
            autoTurns++;
            shot.IncludeSelection = false;
            shot.IncludeClipboard = false;
            lastScreen = new ScreenMap(shot);
            lastTarget = shot.ForegroundHwnd;
            var blocks = new List<object>(shot.ImageBlocks())
            {
                new { type = "text", text = shot.ContextText() + $"\n\n[Computah] I ran your actions: {doneText}. This is the screen now. " +
                                                              "Continue with the next step, or tell the user you're done." },
            };
            Dispatch(blocks, "", "Computah: screen after actions");
        }
        else
        {
            AddNote("Done.");
            if (res.Look) pendingReport = $"[Computah] Ran your actions ({doneText}), but stopped the automatic look-loop after {autoTurns} rounds.";
            else pendingReport = $"[Computah] Ran your actions: {doneText}.";
        }
        Changed?.Invoke();
    }

    /// The cartoon hand briefly shows where a click is about to land.
    void FlashPointer(Point screen)
    {
        var mon = WF.Screen.FromPoint(new System.Drawing.Point((int)screen.X, (int)screen.Y)).Bounds;
        var o = new HintOverlay(mon);
        var tag = new HintTag { Name = "hint" };
        tag.Attr["type"] = "point";
        tag.Attr["x"] = (screen.X - mon.X).ToString(System.Globalization.CultureInfo.InvariantCulture);
        tag.Attr["y"] = (screen.Y - mon.Y).ToString(System.Globalization.CultureInfo.InvariantCulture);
        tag.Attr["seconds"] = "2";
        o.Show();
        o.Add(tag, ScreenMap.Identity(mon));
    }

    void ShowBubble(bool activate)
    {
        if (closed) return;
        if (!IsVisible) Show();
        if (activate)
        {
            Native.ForceForeground(new WindowInteropHelper(this).Handle);
            Activate();
        }
    }

    static Border Card(string title, string body)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, Foreground = Fg, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        sp.Children.Add(new TextBlock { Text = body, Foreground = Muted, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });
        sp.Children.Add(new StackPanel { Orientation = Orientation.Horizontal });
        return new Border
        {
            Child = sp, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 6, 0, 8),
            Background = Frozen(Color.FromArgb(0x1A, 0x5C, 0xE1, 0xFF)), BorderBrush = Frozen(Color.FromArgb(0x60, 0x5C, 0xE1, 0xFF)), BorderThickness = new Thickness(1),
        };
    }

    static Button CardButton(string text, bool primary, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, 1) },
            Foreground = primary ? Frozen(Color.FromRgb(0x0B, 0x12, 0x20)) : Fg, Cursor = Cursors.Hand, Focusable = false, Margin = new Thickness(0, 0, 6, 0),
        };
        b.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
            $"<Border x:Name='Bd' CornerRadius='8' Padding='12,5' Background='{(primary ? "#5CE1FF" : "#22FFFFFF")}'><ContentPresenter VerticalAlignment='Center'/></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'>" +
            $"<Setter TargetName='Bd' Property='Background' Value='{(primary ? "#8AEBFF" : "#33FFFFFF")}'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
        b.Click += (_, _) => click();
        return b;
    }

    /// Replace a card's buttons with a short outcome line.
    static void Resolve(Border card, string outcome)
    {
        var sp = (StackPanel)card.Child;
        sp.Children.RemoveAt(2);
        ((TextBlock)sp.Children[1]).Margin = new Thickness(0, 4, 0, 4);
        sp.Children.Add(new TextBlock { Text = outcome, Foreground = Frozen(Color.FromRgb(0x5C, 0xE1, 0xFF)), FontSize = 12 });
    }

    void AddNote(string text)
    {
        Messages.Children.Add(new TextBlock
        {
            Text = text, FontSize = 11.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 6),
        });
        Scroller.ScrollToEnd();
    }

    // ---------- hints ----------

    /// Draw the agent's hints on the monitor of the latest screenshot. A new reply replaces older hints.
    void ShowHints(List<HintTag> hints)
    {
        if (closed) return;
        // files and folders pop up as their own cards: no screenshot needed
        var peeks = hints.Where(h => h.Type is "file" or "folder" or "open").ToList();
        if (peeks.Count > 0)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            var dips = new Rect(wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY, wa.Width / dpi.DpiScaleX, wa.Height / dpi.DpiScaleY);
            foreach (var p in peeks.Take(3))
                if (!PeekWindow.Show(p, dips, this)) AddNote($"Couldn't find {p.Get("path") ?? "that file"}.");
            hints = hints.Except(peeks).ToList();
            if (hints.Count == 0) return;
        }
        if (lastScreen == null) return; // no screenshot in this conversation: nothing to point at
        if (overlay != null && (overlayTurn != hintTurn || !overlay.IsVisible || overlay.MonitorRect != lastScreen.Monitor))
        {
            overlay.Close();
            overlay = null;
        }
        if (overlay == null)
        {
            overlay = new HintOverlay(lastScreen.Monitor);
            var o = overlay;
            o.Closed += (_, _) => { if (overlay == o) overlay = null; };
            overlayTurn = hintTurn;
            o.Show();
        }
        foreach (var h in hints.Take(5)) overlay.Add(h, lastScreen);
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
        else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && TryPasteImage())
        {
            e.Handled = true;
        }
    }

    /// Ctrl+V with an image on the clipboard: attach it as a tag (text pastes normally into the input).
    bool TryPasteImage()
    {
        try
        {
            if (WF.Clipboard.ContainsText() || !WF.Clipboard.ContainsImage()) return false;
            using var img = WF.Clipboard.GetImage();
            if (img == null) return false;
            var jpeg = Img.ScaledJpeg(img, 1568, 1_250_000).b64;
            var thumb = new Border
            {
                Width = 34, Height = 22, CornerRadius = new CornerRadius(4),
                Background = new ImageBrush(Thumbnail(jpeg)) { Stretch = Stretch.UniformToFill },
                BorderBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AddAttachment("pasted", "Pasted image", thumb, $"{img.Width}×{img.Height} image from the clipboard", image: jpeg);
            Attachments.Visibility = Visibility.Visible;
            UpdatePlaceholder();
            return true;
        }
        catch (Exception ex) { Paths.Log("paste image: " + ex.Message); return false; }
    }

    void SendFromInput()
    {
        if (streaming) return;
        var text = Input.Text;
        if (text.Trim().Length == 0 && attachments.Count == 0) return;
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
