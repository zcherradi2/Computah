using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace Assist;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        using var mutex = new Mutex(true, "Computah_SingleInstance_v1", out bool first);
        if (!first) return;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Paths.Log("UI error: " + e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Paths.Log("Fatal: " + e.ExceptionObject);

        using var host = new AssistHost(app);
        app.Run();
    }
}

sealed class AssistHost : IDisposable
{
    const int HotkeyContinue = 1; // Ctrl+Alt+A: add to the target session
    const int HotkeyNew = 2;      // Alt+A: start a new session
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "Computah";

    readonly Application app;
    readonly HwndSource msgWindow;
    readonly WF.NotifyIcon tray;
    readonly SessionStore store = SessionStore.Load();
    DockWindow? dock;
    SessionsPanel? panel;
    PickerWindow? picker;
    bool busy, sendQueued;
    int busyHotkey;

    public AssistHost(Application app)
    {
        this.app = app;

        // Message-only window that receives WM_HOTKEY.
        msgWindow = new HwndSource(new HwndSourceParameters("ComputahHotkeys")
        { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        msgWindow.AddHook(WndProc);

        var settings = Settings.Load(); // creates settings.json on first run

        tray = new WF.NotifyIcon { Icon = MakeIcon(), Text = "Computah: Alt+A new, Ctrl+Alt+A continue", Visible = true };
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("New session  (Alt+A)", null, (_, _) => Trigger(HotkeyNew));
        menu.Items.Add("Continue target session  (Ctrl+Alt+A)", null, (_, _) => Trigger(HotkeyContinue));
        menu.Items.Add("Sessions…", null, (_, _) => TogglePanel());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Choose agent…", null, (_, _) => ShowPicker(firstRun: false));
        var control = new WF.ToolStripMenuItem("Mouse && keyboard control");
        foreach (var (label, value) in new[] { ("Off (agent must ask)", "off"), ("Ask me each time", "ask"), ("On", "on") })
        {
            var item = new WF.ToolStripMenuItem(label) { Tag = value };
            item.Click += (_, _) => { var s = Settings.Load(); s.Control = value; s.Save(); };
            control.DropDownItems.Add(item);
        }
        control.DropDownOpening += (_, _) =>
        {
            var current = Settings.Load().Control;
            foreach (WF.ToolStripMenuItem i in control.DropDownItems) i.Checked = (string)i.Tag! == current;
        };
        menu.Items.Add(control);
        var showDock = new WF.ToolStripMenuItem("Show floating button") { Checked = settings.ShowDock, CheckOnClick = true };
        showDock.CheckedChanged += (_, _) => { var s = Settings.Load(); s.ShowDock = showDock.Checked; s.Save(); SetDockVisible(showDock.Checked); };
        menu.Items.Add(showDock);
        menu.Items.Add("Open screenshots folder", null, (_, _) => Shell(Paths.Screenshots, true));
        menu.Items.Add("Command log", null, (_, _) =>
        {
            if (!File.Exists(CommandLog.File))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CommandLog.File)!);
                File.WriteAllText(CommandLog.File, "Computah command log: every command the agents ran and every mouse/keyboard action, with the session and its permissions.\n\n");
            }
            Shell(CommandLog.File, false);
        });
        menu.Items.Add("Edit settings", null, (_, _) => { Settings.Load(); Shell(Paths.SettingsFile, false); });
        var startup = new WF.ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => app.Shutdown());
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) TogglePanel(); };

        var failed = new List<string>();
        if (!Native.RegisterHotKey(msgWindow.Handle, HotkeyContinue, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_A))
            failed.Add("Ctrl+Alt+A");
        if (!Native.RegisterHotKey(msgWindow.Handle, HotkeyNew, Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_A))
            failed.Add("Alt+A");
        if (failed.Count > 0)
            tray.ShowBalloonTip(4000, "Computah", string.Join(" and ", failed) + " already used by another app.", WF.ToolTipIcon.Warning);

        app.Dispatcher.BeginInvoke(() =>
        {
            SetDockVisible(settings.ShowDock);
            // Name older sessions that don't have a title yet.
            foreach (var rec in store.Sessions.Where(r => r.Name == null).ToList())
                if (Agents.ById(rec.AgentId) is { } a) MaybeNameSession(rec, a);
            // First run (or the chosen agent was uninstalled): ask which agent to use.
            if (Agents.Resolve(settings) == null) ShowPicker(firstRun: string.IsNullOrEmpty(settings.Agent));
        });
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            handled = true;
            Trigger(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    // ---------- hotkeys ----------

    /// First press captures the screen into a compact input; pressing the same hotkey again sends it as is.
    /// Alt+A starts a new session. Ctrl+Alt+A adds to the target session (picked in the sessions panel, or
    /// the last one used), reopening its bubble with history if it was closed.
    async void Trigger(int hotkey)
    {
        // Double tap = A pressed again while the modifiers stayed held → send what's pending.
        // Pressing the whole chord again after letting go → re-capture (handled below).
        bool doubleTap = chordHeld && chordHotkey == hotkey;
        WatchChord(hotkey);

        var focusedWin = store.Focused?.Window;
        if (doubleTap)
        {
            if (busy) { if (hotkey == busyHotkey) sendQueued = true; }
            else if (focusedWin != null && focusedWin.CanSendAsIs(hotkey)) focusedWin.SendAsIs();
            return;
        }
        if (busy) return;

        var settings = Settings.Load();
        var agent = Agents.Resolve(settings);
        if (agent == null) { ShowPicker(firstRun: string.IsNullOrEmpty(settings.Agent)); return; }

        busy = true;
        busyHotkey = hotkey;
        sendQueued = false;
        try
        {
            // The session this capture goes to. Alt+A pressed again while its popup is still unsent
            // re-captures into that same popup instead of opening another one.
            SessionRecord? rec = hotkey == HotkeyContinue ? store.Focused
                               : focusedWin != null && focusedWin.CanSendAsIs(hotkey) ? store.Focused : null;

            // Hide its popup so it isn't in the new screenshot.
            var existing = rec?.Window;
            if (existing is { IsVisible: true })
            {
                existing.Hide();
                await Task.Delay(120);
            }

            var ctx = await ContextCapture.CaptureAsync(settings);
            BubbleWindow w;
            if (rec?.Window != null) w = rec.Window;
            else if (rec != null && Agents.ById(rec.AgentId) is { } recAgent) w = OpenBubble(rec, recAgent, settings);
            else
            {
                rec = store.Create(agent.Id);
                w = OpenBubble(rec, agent, settings);
            }
            SetFocused(rec);

            // The window Computah was called from becomes (or refreshes) a playground of this session.
            if (Playgrounds.From(ctx) is { } pg)
            {
                rec.TouchPlayground(pg);
                store.Save();
                RefreshUi();
                if (pg.IsBrowser) _ = FillPlaygroundUrl(rec, pg, ctx.ForegroundHwnd);
            }

            w.AttachContext(ctx, hotkey);
            if (!w.IsVisible) w.Show();
            await ContextCapture.GrabSelectionAsync(ctx, settings);
            // A double tap can land at any point while we were busy: check only now, after the new
            // context is attached, so nothing resets it. It sends as soon as the bubble is ready.
            if (sendQueued) w.SendAsIs();
            w.ContextReady(ctx);
        }
        catch (Exception ex)
        {
            Paths.Log("trigger: " + ex);
            tray.ShowBalloonTip(4000, "Computah", ex.Message, WF.ToolTipIcon.Error);
        }
        finally { busy = false; }
    }

    bool chordHeld;
    int chordHotkey, chordGen;

    /// Tracks whether the hotkey's modifiers stay held after a press (Alt, plus Ctrl for Ctrl+Alt+A).
    async void WatchChord(int hotkey)
    {
        int gen = ++chordGen;
        chordHeld = true;
        chordHotkey = hotkey;
        bool needCtrl = hotkey == HotkeyContinue;
        while (Native.IsDown(Native.VK_MENU) && (!needCtrl || Native.IsDown(Native.VK_CONTROL)))
        {
            await Task.Delay(15);
            if (gen != chordGen) return; // a newer press took over
        }
        if (gen == chordGen) chordHeld = false;
    }

    /// Browser playgrounds: read the tab's address in the background, then merge with an earlier entry for the same site.
    async Task FillPlaygroundUrl(SessionRecord rec, Playground pg, IntPtr hwnd)
    {
        var url = await Playgrounds.BrowserUrlAsync(hwnd);
        if (url == null) return;
        pg.Url = url;
        var dup = rec.Playgrounds.FirstOrDefault(x => x != pg && x.SameAs(pg));
        if (dup != null) { pg.Added = dup.Added; rec.Playgrounds.Remove(dup); }
        store.Save();
        RefreshUi();
    }

    BubbleWindow OpenBubble(SessionRecord rec, AgentInfo agent, Settings settings)
    {
        var w = new BubbleWindow(settings, agent, rec.AgentSessionId, rec.History, rec.SharedPaths);
        rec.Window = w;
        w.Record = rec;
        w.RefreshSetup();
        w.SetupChanged += () => { store.Save(); RefreshUi(); };
        w.AccessPaths = () => rec.AccessPaths;
        w.SessionContext = () => Playgrounds.Describe(rec);
        w.WorkspaceAdded += (path, edit) => { rec.AddWorkspace(path, edit); store.Save(); RefreshUi(); };
        w.LogName = () => $"\"{rec.Title}\" (session {rec.Id[..8]})";
        w.Changed += () =>
        {
            rec.History = w.History.ToList();
            rec.AgentSessionId = w.SessionId ?? rec.AgentSessionId;
            if (w.HasConversation) rec.Updated = DateTime.Now;
            store.Save();
            RefreshUi();
            MaybeNameSession(rec, agent);
        };
        w.UserFocused += () => { if (store.FocusedId != rec.Id) SetFocused(rec); };
        w.ToolUsed += (tool, detail) => dock?.ShowCommand(rec, tool, detail);
        w.TurnFinished += () => dock?.CommandsDone(rec);
        w.Closed += (_, _) =>
        {
            rec.Window = null;
            if (rec.History.Count == 0) store.Remove(rec); // opened and closed without sending: don't keep it
            store.Save();
            RefreshUi();
        };
        return w;
    }

    // ---------- sessions ----------

    /// After the first answer, ask the agent for a short title and use it in the sessions list.
    async void MaybeNameSession(SessionRecord rec, AgentInfo agent)
    {
        if (rec.Name != null || rec.TitleRequested) return;
        var user = rec.History.FirstOrDefault(t => t.User);
        var answer = rec.History.FirstOrDefault(t => !t.User);
        if (user == null || answer == null) return;
        rec.TitleRequested = true;

        var userText = user.Text.Length > 0 ? user.Text : $"(shared {user.Attach ?? "their screen"} without typing anything)";
        var title = await Titles.GenerateAsync(agent, userText, answer.Text);
        if (title == null || rec.Name != null || !store.Sessions.Contains(rec)) return; // renamed meanwhile
        rec.Name = title;
        store.Save();
        RefreshUi();
    }

    /// Files/folders dropped on the Computah button: share them with the target session (or a new one) and open it.
    /// `to`: a specific session (from the drag fan), null = the target. `newSession`: start a fresh one.
    void ShareWithTarget(string[] paths, SessionRecord? to, bool newSession)
    {
        var settings = Settings.Load();
        var rec = newSession ? null : to ?? store.Focused;
        if (rec != null && !store.Sessions.Contains(rec)) rec = null;
        var w = rec?.Window;
        if (w == null)
        {
            var agent = rec != null ? Agents.ById(rec.AgentId) : Agents.Resolve(settings);
            if (agent == null) { ShowPicker(firstRun: string.IsNullOrEmpty(settings.Agent)); return; }
            rec ??= store.Create(agent.Id);
            w = OpenBubble(rec, agent, settings);
        }
        SetFocused(rec!);
        // Folders dropped on the button become the session's workspaces; files are shared with the next message.
        var folders = paths.Where(System.IO.Directory.Exists).ToList();
        var added = folders.Where(f => rec!.AddWorkspace(f)).ToList();
        if (added.Count > 0)
            w.Notice("Workspace: " + string.Join(", ", added.Select(f => System.IO.Path.GetFileName(f.TrimEnd('\\')))) +
                     ". The agent will focus its work there and can edit it (every change shows up with Undo).");
        else if (folders.Count > 0) w.Notice("Already a workspace of this session.");
        var files = paths.Except(folders).ToArray();
        if (files.Length > 0) w.ShareFiles(files);
        store.Save();
        RefreshUi();
        Native.GetCursorPos(out var pt);
        w.ShowFocused(pt.X, pt.Y);
    }

    /// From the sessions panel: show the conversation as it is, next to the mouse, without capturing anything.
    void OpenSession(SessionRecord rec)
    {
        var w = rec.Window;
        if (w == null)
        {
            var agent = Agents.ById(rec.AgentId);
            if (agent == null)
            {
                tray.ShowBalloonTip(3000, "Computah", $"{Agents.NameOf(rec.AgentId)} isn't installed any more.", WF.ToolTipIcon.Warning);
                return;
            }
            w = OpenBubble(rec, agent, Settings.Load());
        }
        SetFocused(rec);
        Native.GetCursorPos(out var pt);
        w.ShowFocused(pt.X, pt.Y);
    }

    void SetFocused(SessionRecord rec)
    {
        if (!store.Sessions.Contains(rec)) store.Sessions.Add(rec);
        store.FocusedId = rec.Id;
        store.Save();
        RefreshUi();
    }

    void RefreshUi()
    {
        dock?.Refresh();
        panel?.Refresh();
        foreach (var s in store.Sessions) s.Window?.RefreshSetup();
    }

    void SetDockVisible(bool visible)
    {
        if (visible && dock == null)
        {
            dock = new DockWindow(store);
            dock.Clicked += TogglePanel;
            dock.FilesDropped += ShareWithTarget;
            dock.Show();
            dock.Refresh();
        }
        else if (!visible && dock != null)
        {
            panel?.SafeClose();
            dock.Close();
            dock = null;
        }
    }

    void TogglePanel()
    {
        if (panel != null) { panel.SafeClose(); return; }
        if (dock == null) SetDockVisible(true); // the panel is anchored to the button
        panel = new SessionsPanel(store, dock!);
        panel.FocusRequested += SetFocused;
        panel.OpenRequested += rec =>
        {
            panel?.SafeClose();
            OpenSession(rec);
        };
        panel.DeleteRequested += rec =>
        {
            var w = rec.Window;
            store.Remove(rec);
            store.Save();
            w?.Close();
            RefreshUi();
        };
        panel.SessionChanged += rec =>
        {
            store.Save();
            RefreshUi(); // an open bubble picks up the new access with its next message
        };
        panel.RenameRequested += (rec, name) =>
        {
            rec.Name = name;
            rec.TitleRequested = true; // the user's name wins over the automatic one
            store.Save();
            RefreshUi();
        };
        panel.NewRequested += async () =>
        {
            panel?.SafeClose();
            await Task.Delay(160);
            Trigger(HotkeyNew);
        };
        panel.Closed += (_, _) => panel = null;
        panel.Show();
        panel.Activate();
    }

    // ---------- agent picker ----------

    void ShowPicker(bool firstRun)
    {
        if (picker != null) { picker.Activate(); return; }
        picker = new PickerWindow(Settings.Load().Agent, firstRun);
        picker.Closed += (_, _) =>
        {
            var chosen = picker.Chosen;
            picker = null;
            if (chosen == null) return;
            var s = Settings.Load();
            s.Agent = chosen;
            s.Save();
            var name = Agents.NameOf(chosen);
            tray.ShowBalloonTip(2500, $"Computah will use {name}", "Alt+A: new session · Ctrl+Alt+A: continue", WF.ToolTipIcon.None);
        };
        picker.Show();
        picker.Activate();
    }

    // ---------- misc ----------

    static void Shell(string path, bool folder)
    {
        try
        {
            if (folder) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Paths.Log("open: " + ex.Message); }
    }

    static bool IsStartupEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) != null;
    }

    static void SetStartup(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(RunName, false);
    }

    /// The embedded computah.ico at the tray's size for the current DPI.
    static Icon MakeIcon()
    {
        using var s = typeof(AssistHost).Assembly.GetManifestResourceStream("computah.ico")!;
        return new Icon(s, WF.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        store.Save();
        Native.UnregisterHotKey(msgWindow.Handle, HotkeyContinue);
        Native.UnregisterHotKey(msgWindow.Handle, HotkeyNew);
        tray.Visible = false;
        tray.Dispose();
        msgWindow.Dispose();
    }
}
