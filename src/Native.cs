using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Assist;

static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_A = 0x41, VK_C = 0x43;
    public const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int cb);

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref uint size);

    /// Full exe path; works for elevated processes too (limited-information access).
    public static string? ProcessPath(uint pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);

    /// Tool window (not in Alt+Tab) that never takes focus when clicked.
    public static void MakeNoActivate(IntPtr hwnd)
    {
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    /// Layered overlay that mouse clicks pass straight through, never focused, not in Alt+Tab.
    public static void MakeClickThrough(IntPtr hwnd)
    {
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static void SendKeys(params (int vk, bool up)[] keys)
    {
        var inputs = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[i].type = 1; // INPUT_KEYBOARD
            inputs[i].u.ki.wVk = (ushort)keys[i].vk;
            inputs[i].u.ki.dwFlags = keys[i].up ? 0x0002u : 0u; // KEYEVENTF_KEYUP
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

    static void Send(INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    static INPUT Mouse(uint flags, int data = 0)
    {
        var i = new INPUT { type = 0 };
        i.u.mi.dwFlags = flags;
        i.u.mi.mouseData = unchecked((uint)data);
        return i;
    }

    static INPUT Key(ushort vk, bool up, bool extended = false)
    {
        var i = new INPUT { type = 1 };
        i.u.ki.wVk = vk;
        i.u.ki.dwFlags = (up ? 0x0002u : 0u) | (extended ? 0x0001u : 0u);
        return i;
    }

    /// Click at the current cursor position. button: "left" | "right" | "middle".
    public static void Click(string button, int count)
    {
        (uint down, uint up) = button switch { "right" => (0x0008u, 0x0010u), "middle" => (0x0020u, 0x0040u), _ => (0x0002u, 0x0004u) };
        for (int i = 0; i < Math.Clamp(count, 1, 3); i++) Send(new[] { Mouse(down), Mouse(up) });
    }

    public static void MouseDown() => Send(new[] { Mouse(0x0002) });
    public static void MouseUp() => Send(new[] { Mouse(0x0004) });

    /// Wheel notches at the current cursor position; positive scrolls down.
    public static void Wheel(int notches) => Send(new[] { Mouse(0x0800, -120 * notches) });

    /// Types any text (Unicode), newlines become Enter.
    public static void TypeText(string text)
    {
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n') { Send(new[] { Key(0x0D, false), Key(0x0D, true) }); continue; }
            var down = new INPUT { type = 1 }; down.u.ki.wScan = ch; down.u.ki.dwFlags = 0x0004;            // KEYEVENTF_UNICODE
            var up = new INPUT { type = 1 }; up.u.ki.wScan = ch; up.u.ki.dwFlags = 0x0004 | 0x0002;
            Send(new[] { down, up });
        }
    }

    static readonly HashSet<int> ExtendedKeys = new() { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C };

    /// Presses a shortcut like "ctrl+shift+s", "enter", "alt+f4". Returns false if a key name is unknown.
    public static bool PressKeys(string combo)
    {
        var parts = combo.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var vks = new List<int>();
        foreach (var p in parts)
        {
            int vk = KeyCode(p);
            if (vk == 0) return false;
            vks.Add(vk);
        }
        if (vks.Count == 0) return false;
        var seq = new List<INPUT>();
        foreach (var vk in vks) seq.Add(Key((ushort)vk, false, ExtendedKeys.Contains(vk)));
        for (int i = vks.Count - 1; i >= 0; i--) seq.Add(Key((ushort)vks[i], true, ExtendedKeys.Contains(vks[i])));
        Send(seq.ToArray());
        return true;
    }

    static int KeyCode(string k) => k switch
    {
        "ctrl" or "control" => 0x11, "alt" => 0x12, "shift" => 0x10, "win" or "windows" or "cmd" or "meta" => 0x5B,
        "enter" or "return" => 0x0D, "tab" => 0x09, "esc" or "escape" => 0x1B, "backspace" => 0x08,
        "delete" or "del" => 0x2E, "insert" or "ins" => 0x2D, "space" => 0x20,
        "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27,
        "home" => 0x24, "end" => 0x23, "pageup" or "pgup" => 0x21, "pagedown" or "pgdn" => 0x22,
        _ when k.Length == 1 && char.IsLetterOrDigit(k[0]) => char.ToUpperInvariant(k[0]),
        _ when k.Length >= 2 && k[0] == 'f' && int.TryParse(k[1..], out var n) && n is >= 1 and <= 24 => 0x6F + n,
        _ => 0,
    };

    public static string WindowTitle(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// Bring our window to the front even though another app had focus.
    public static void ForceForeground(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        if (fg == hwnd) return;
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != me)
        {
            AttachThreadInput(me, fgThread, true);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            AttachThreadInput(me, fgThread, false);
        }
        else SetForegroundWindow(hwnd);
    }
}
