using System;
using System.Diagnostics;
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
        using var mutex = new Mutex(true, "ClaudeAssist_SingleInstance_v1", out bool first);
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
    const int HotkeyAuto = 1, HotkeyAsk = 2;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "ClaudeAssist";

    readonly Application app;
    readonly HwndSource msgWindow;
    readonly WF.NotifyIcon tray;
    bool busy;

    public AssistHost(Application app)
    {
        this.app = app;

        // Message-only window that receives WM_HOTKEY.
        msgWindow = new HwndSource(new HwndSourceParameters("ClaudeAssistHotkeys")
        { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 0, Height = 0 });
        msgWindow.AddHook(WndProc);

        tray = new WF.NotifyIcon { Icon = MakeIcon(), Text = "Claude Assist — Ctrl+Alt+A", Visible = true };
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Ask Claude  (Ctrl+Alt+A)", null, (_, _) => Trigger(false));
        menu.Items.Add("Send screen instantly  (Ctrl+Alt+Shift+A)", null, (_, _) => Trigger(true));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Open screenshots folder", null, (_, _) => Shell(Paths.Screenshots, true));
        menu.Items.Add("Edit settings", null, (_, _) => { Settings.Load(); Shell(Paths.SettingsFile, false); });
        var startup = new WF.ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => app.Shutdown());
        tray.ContextMenuStrip = menu;

        bool ok1 = Native.RegisterHotKey(msgWindow.Handle, HotkeyAuto, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_A);
        bool ok2 = Native.RegisterHotKey(msgWindow.Handle, HotkeyAsk, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, Native.VK_A);
        if (!ok1)
            tray.ShowBalloonTip(4000, "Claude Assist", "Ctrl+Alt+A is already used by another app.", WF.ToolTipIcon.Warning);
        else
            tray.ShowBalloonTip(2500, "Claude Assist is running", "Press Ctrl+Alt+A anywhere.", WF.ToolTipIcon.None);
        if (!ok2) Paths.Log("Ctrl+Alt+Shift+A unavailable");

        Settings.Load(); // creates settings.json on first run
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            handled = true;
            Trigger(shift: wParam.ToInt32() == HotkeyAsk);
        }
        return IntPtr.Zero;
    }

    async void Trigger(bool shift)
    {
        if (busy) return;
        busy = true;
        try
        {
            var settings = Settings.Load();
            var ctx = await ContextCapture.CaptureAsync(settings);
            new BubbleWindow(ctx, settings, sendNow: settings.SendInstantly ^ shift).Show();
        }
        catch (Exception ex)
        {
            Paths.Log("trigger: " + ex);
            tray.ShowBalloonTip(4000, "Claude Assist", ex.Message, WF.ToolTipIcon.Error);
        }
        finally { busy = false; }
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

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var bg = new SolidBrush(Color.FromArgb(217, 119, 87));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var font = new Font("Segoe UI Semibold", 15f, GraphicsUnit.Pixel);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("A", font, Brushes.White, new RectangleF(0, 1, 32, 32), fmt);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        Native.UnregisterHotKey(msgWindow.Handle, HotkeyAuto);
        Native.UnregisterHotKey(msgWindow.Handle, HotkeyAsk);
        tray.Visible = false;
        tray.Dispose();
        msgWindow.Dispose();
    }
}
