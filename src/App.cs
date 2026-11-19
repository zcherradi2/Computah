using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Threading;
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
    const int HotkeyContinue = 1; // Ctrl+Alt+A: add to the latest session
    const int HotkeyNew = 2;      // Alt+A: start a new session
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "Computah";

    readonly Application app;
    readonly HwndSource msgWindow;
    readonly WF.NotifyIcon tray;
    bool busy, sendQueued;
    int busyHotkey;

    readonly List<BubbleWindow> open = new();   // open bubbles, newest last
    string? lastSessionId, lastAgentId;         // latest conversation, kept after its bubble closes
    List<Turn>? lastHistory;

    public AssistHost(Application app)
    {
        this.app = app;

        // Message-only window that receives WM_HOTKEY.
        msgWindow = new HwndSource(new HwndSourceParameters("ComputahHotkeys")
        { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        msgWindow.AddHook(WndProc);

        tray = new WF.NotifyIcon { Icon = MakeIcon(), Text = "Computah — Alt+A new, Ctrl+Alt+A continue", Visible = true };
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("New session  (Alt+A)", null, (_, _) => Trigger(HotkeyNew));
        menu.Items.Add("Continue latest session  (Ctrl+Alt+A)", null, (_, _) => Trigger(HotkeyContinue));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Choose agent…", null, (_, _) => ShowPicker(firstRun: false));
        menu.Items.Add("Open screenshots folder", null, (_, _) => Shell(Paths.Screenshots, true));
        menu.Items.Add("Edit settings", null, (_, _) => { Settings.Load(); Shell(Paths.SettingsFile, false); });
        var startup = new WF.ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => app.Shutdown());
        tray.ContextMenuStrip = menu;

        var failed = new List<string>();
        if (!Native.RegisterHotKey(msgWindow.Handle, HotkeyContinue, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_A))
            failed.Add("Ctrl+Alt+A");
        if (!Native.RegisterHotKey(msgWindow.Handle, HotkeyNew, Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_A))
            failed.Add("Alt+A");
        if (failed.Count > 0)
            tray.ShowBalloonTip(4000, "Computah", string.Join(" and ", failed) + " already used by another app.", WF.ToolTipIcon.Warning);
        else
            tray.ShowBalloonTip(2500, "Computah is running", "Alt+A: new session · Ctrl+Alt+A: continue", WF.ToolTipIcon.None);

        // First run (or the chosen agent was uninstalled): ask which agent to use.
        var s = Settings.Load(); // creates settings.json on first run
        if (Agents.Resolve(s) == null)
            app.Dispatcher.BeginInvoke(() => ShowPicker(firstRun: string.IsNullOrEmpty(s.Agent)));
    }

    PickerWindow? picker;

    void ShowPicker(bool firstRun)
    {
        if (picker != null) { picker.Activate(); return; }
        var current = Settings.Load().Agent;
        picker = new PickerWindow(current, firstRun);
        picker.Closed += (_, _) =>
        {
            var chosen = picker.Chosen;
            picker = null;
            if (chosen == null) return;
            var s = Settings.Load();
            s.Agent = chosen;
            s.Save();
            var name = Agents.All().First(a => a.Id == chosen).Name;
            tray.Text = $"Computah ({name}) — Alt+A new, Ctrl+Alt+A continue";
            tray.ShowBalloonTip(2500, $"Computah will use {name}", "Alt+A: new session · Ctrl+Alt+A: continue", WF.ToolTipIcon.None);
        };
        picker.Show();
        picker.Activate();
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

    /// First press captures the screen into a compact input; pressing the same hotkey again sends it as is.
    /// Alt+A always starts a new session. Ctrl+Alt+A adds to the newest open bubble, or reopens the last
    /// closed conversation (claude --resume), or starts fresh if there is none.
    async void Trigger(int hotkey)
    {
        var newest = open.Count > 0 ? open[^1] : null;
        if (newest != null && newest.CanSendAsIs(hotkey)) { newest.SendAsIs(); return; }
        if (busy) { if (hotkey == busyHotkey) sendQueued = true; return; } // fast double press during capture

        var settings = Settings.Load();
        var agent = Agents.Resolve(settings);
        if (agent == null) { ShowPicker(firstRun: string.IsNullOrEmpty(settings.Agent)); return; }

        busy = true;
        busyHotkey = hotkey;
        sendQueued = false;
        try
        {
            var ctx = await ContextCapture.CaptureAsync(settings);

            BubbleWindow w;
            if (hotkey == HotkeyContinue && newest != null) w = newest;
            else if (hotkey == HotkeyContinue && lastSessionId != null && lastAgentId == agent.Id)
                w = Track(new BubbleWindow(settings, agent, lastSessionId, lastHistory));
            else w = Track(new BubbleWindow(settings, agent));

            w.AttachContext(ctx, hotkey);
            if (!w.IsVisible) w.Show();
            if (sendQueued) w.SendAsIs(); // runs once the selection grab finishes
            await ContextCapture.GrabSelectionAsync(ctx, settings);
            w.ContextReady(ctx);
        }
        catch (Exception ex)
        {
            Paths.Log("trigger: " + ex);
            tray.ShowBalloonTip(4000, "Computah", ex.Message, WF.ToolTipIcon.Error);
        }
        finally { busy = false; }
    }

    BubbleWindow Track(BubbleWindow w)
    {
        open.Add(w);
        w.Closed += (_, _) =>
        {
            open.Remove(w);
            if (w.HasConversation && w.SessionId != null)
            {
                lastSessionId = w.SessionId;
                lastAgentId = w.AgentId;
                lastHistory = w.History.ToList();
            }
        };
        return w;
    }

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
        Native.UnregisterHotKey(msgWindow.Handle, HotkeyContinue);
        Native.UnregisterHotKey(msgWindow.Handle, HotkeyNew);
        tray.Visible = false;
        tray.Dispose();
        msgWindow.Dispose();
    }
}
