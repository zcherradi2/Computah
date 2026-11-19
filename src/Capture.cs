using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WF = System.Windows.Forms;

namespace Assist;

public sealed class CapturedContext
{
    public DateTime Time;
    public int CursorX, CursorY;                 // physical screen pixels
    public Rectangle Monitor;
    public string WindowTitle = "", ProcessName = "", ProcessPath = "";
    public int ProcessId;
    public IntPtr ForegroundHwnd;

    public string ScreenshotPath = "";
    public string FullJpeg = ""; public int FullW, FullH, FullCursorX, FullCursorY;
    public string? CropJpeg; public int CropW, CropH, CropCursorX, CropCursorY;

    public string? SelectedText, ClipboardText, ClipboardImageJpeg;
    public List<string> ClipboardFiles = new();

    // The user can remove attachments before sending.
    public bool IncludeScreen = true, IncludeSelection = true, IncludeClipboard = true;

    public bool HasClipboard => ClipboardText != null || ClipboardImageJpeg != null || ClipboardFiles.Count > 0;

    public List<object> ImageBlocks()
    {
        var list = new List<object>();
        if (IncludeScreen)
        {
            list.Add(Img.Block(FullJpeg));
            if (CropJpeg != null) list.Add(Img.Block(CropJpeg));
        }
        if (IncludeClipboard && ClipboardImageJpeg != null) list.Add(Img.Block(ClipboardImageJpeg));
        return list;
    }

    public string ContextText()
    {
        var sb = new StringBuilder();
        int n = 1;
        sb.AppendLine($"[New desktop context captured with the hotkey at {Time:yyyy-MM-dd HH:mm:ss}]");
        sb.AppendLine("Focused app at capture time:");
        sb.AppendLine($"  Id: {ProcessId}");
        sb.AppendLine($"  ProcessName: {ProcessName}");
        sb.AppendLine($"  WindowTitle: {WindowTitle}");
        sb.AppendLine($"  Path: {(ProcessPath.Length > 0 ? ProcessPath : "unknown")}");
        sb.AppendLine($"Mouse pointer: ({CursorX - Monitor.X}, {CursorY - Monitor.Y}) on a {Monitor.Width}x{Monitor.Height} monitor " +
                      $"(monitor origin {Monitor.X}, {Monitor.Y} in virtual-screen coordinates).");
        if (IncludeScreen)
        {
            sb.AppendLine($"Image {n++}: the whole monitor scaled to {FullW}x{FullH}; pointer at ({FullCursorX}, {FullCursorY}), marked with a red circle.");
            if (CropJpeg != null)
                sb.AppendLine($"Image {n++}: full-resolution {CropW}x{CropH} crop around the pointer; pointer at ({CropCursorX}, {CropCursorY}), red circle.");
        }

        var withheld = new List<string>();
        if (!IncludeScreen) withheld.Add("screenshot");
        if (!IncludeSelection && SelectedText != null) withheld.Add("selected text");
        if (!IncludeClipboard && HasClipboard) withheld.Add("clipboard");
        if (withheld.Count > 0)
            sb.AppendLine($"The user chose NOT to share: {string.Join(", ", withheld)}. Do not try to obtain it any other way.");

        bool sel = IncludeSelection && SelectedText != null;
        bool clip = IncludeClipboard;
        bool sameAsClip = sel && clip && SelectedText == ClipboardText;
        if (sel)
            sb.AppendLine("Selected text (grabbed with Ctrl+C; some editors copy the current line when nothing is selected):")
              .AppendLine("<<<").AppendLine(Trunc(SelectedText!)).AppendLine(">>>");

        if (clip)
        {
            if (sameAsClip) sb.AppendLine("Clipboard text: same as the selected text.");
            else if (ClipboardText != null)
                sb.AppendLine("Clipboard text:").AppendLine("<<<").AppendLine(Trunc(ClipboardText)).AppendLine(">>>");
            if (ClipboardImageJpeg != null) sb.AppendLine($"Clipboard image: attached as Image {n++}.");
            if (ClipboardFiles.Count > 0) sb.AppendLine("Clipboard files: " + string.Join(", ", ClipboardFiles.Take(30)));
        }
        return sb.ToString().TrimEnd();
    }

    static string Trunc(string s, int max = 15000) => s.Length <= max ? s : s[..max] + $"\n…[truncated, {s.Length - max} more chars]";
}

static class ContextCapture
{
    static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "wezterm-gui", "alacritty",
        "mintty", "kitty", "Hyper", "Tabby", "wsl", "bash", "ubuntu", "putty", "kitty", "warp", "ConEmu64", "ConEmu",
    };

    public static async Task<CapturedContext> CaptureAsync(Settings s)
    {
        var c = new CapturedContext { Time = DateTime.Now };
        Native.GetCursorPos(out var pt);
        c.CursorX = pt.X; c.CursorY = pt.Y;

        var fg = Native.GetForegroundWindow();
        c.WindowTitle = Native.WindowTitle(fg);
        Native.GetWindowThreadProcessId(fg, out uint pid);
        c.ProcessId = (int)pid;
        try { c.ProcessName = Process.GetProcessById((int)pid).ProcessName; } catch { c.ProcessName = "unknown"; }
        try { c.ProcessPath = Native.ProcessPath(pid) ?? ""; } catch { }

        // 1) Screenshot of the monitor under the pointer (grabbed first, before anything changes).
        var screen = WF.Screen.FromPoint(new Point(pt.X, pt.Y));
        c.Monitor = screen.Bounds;
        var bmp = new Bitmap(c.Monitor.Width, c.Monitor.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(c.Monitor.X, c.Monitor.Y, 0, 0, c.Monitor.Size, CopyPixelOperation.SourceCopy);

        Directory.CreateDirectory(Paths.Screenshots);
        c.ScreenshotPath = Path.Combine(Paths.Screenshots, $"{c.Time:yyyy-MM-dd_HH-mm-ss-fff}.png");
        int mx = pt.X - c.Monitor.X, my = pt.Y - c.Monitor.Y;

        var encoded = new TaskCompletionSource();
        _ = Task.Run(() =>
        {
            try
            {
                (c.FullJpeg, c.FullW, c.FullH, double sc) = Img.ScaledJpeg(bmp, 1568, 1_250_000,
                    (g, scale) => Img.Marker(g, (float)(mx * scale), (float)(my * scale), 13));
                c.FullCursorX = (int)(mx * sc); c.FullCursorY = (int)(my * sc);

                if (s.SendCursorCrop && (bmp.Width > 1100 || bmp.Height > 700))
                {
                    int cw = Math.Min(1000, bmp.Width), ch = Math.Min(640, bmp.Height);
                    int cx = Math.Clamp(mx - cw / 2, 0, bmp.Width - cw), cy = Math.Clamp(my - ch / 2, 0, bmp.Height - ch);
                    c.CropJpeg = Img.CropJpeg(bmp, new Rectangle(cx, cy, cw, ch), g => Img.Marker(g, mx - cx, my - cy, 16));
                    c.CropW = cw; c.CropH = ch; c.CropCursorX = mx - cx; c.CropCursorY = my - cy;
                }
                encoded.TrySetResult();
                bmp.Save(c.ScreenshotPath, ImageFormat.Png);
            }
            catch (Exception ex) { encoded.TrySetException(ex); }
            finally { bmp.Dispose(); }
        });

        // 2) Clipboard as it was before we touch anything.
        c.ForegroundHwnd = fg;
        ReadClipboard(c);

        await encoded.Task;
        return c;
    }

    /// Selected text via a synthetic Ctrl+C. Runs after the bubble is shown (without focus) and only once the
    /// user has released the hotkey keys, so no modifier juggling is needed. Skipped in terminals, where
    /// Ctrl+C means "interrupt", and if focus moved away from the original window.
    public static async Task GrabSelectionAsync(CapturedContext c, Settings s)
    {
        if (!s.CaptureSelection || Terminals.Contains(c.ProcessName) || c.ProcessId == Environment.ProcessId) return;

        var sw = Stopwatch.StartNew();
        int[] held = { Native.VK_MENU, Native.VK_CONTROL, Native.VK_SHIFT, Native.VK_A, Native.VK_LWIN, Native.VK_RWIN };
        while (held.Any(Native.IsDown))
        {
            if (sw.ElapsedMilliseconds > 2500) return; // keys still held: give up rather than send a wrong combo
            await Task.Delay(10);
        }
        if (Native.GetForegroundWindow() != c.ForegroundHwnd) return;

        try { c.SelectedText = await GetSelectionAsync(); }
        catch (Exception ex) { Paths.Log("selection: " + ex.Message); }
    }

    static void ReadClipboard(CapturedContext c)
    {
        Retry(() => { if (WF.Clipboard.ContainsText()) c.ClipboardText = WF.Clipboard.GetText(); });
        Retry(() => { if (WF.Clipboard.ContainsFileDropList()) c.ClipboardFiles = WF.Clipboard.GetFileDropList().Cast<string>().ToList(); });
        Retry(() =>
        {
            if (!WF.Clipboard.ContainsImage()) return;
            using var img = WF.Clipboard.GetImage();
            if (img != null) c.ClipboardImageJpeg = Img.ScaledJpeg(img, 1568, 1_250_000).b64;
        });
        if (string.IsNullOrWhiteSpace(c.ClipboardText)) c.ClipboardText = null;
    }

    static async Task<string?> GetSelectionAsync()
    {
        var snapshot = Snapshot();
        uint seq = Native.GetClipboardSequenceNumber();
        Native.SendKeys((Native.VK_CONTROL, false), (Native.VK_C, false), (Native.VK_C, true), (Native.VK_CONTROL, true));

        bool changed = false;
        for (int i = 0; i < 25 && !changed; i++)
        {
            await Task.Delay(12);
            changed = Native.GetClipboardSequenceNumber() != seq;
        }
        if (!changed) return null;

        await Task.Delay(20);
        string? text = null;
        Retry(() => { if (WF.Clipboard.ContainsText()) text = WF.Clipboard.GetText(); });
        Restore(snapshot);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    static readonly string[] Formats =
    {
        WF.DataFormats.UnicodeText, WF.DataFormats.Text, WF.DataFormats.Rtf, WF.DataFormats.Html,
        WF.DataFormats.CommaSeparatedValue, WF.DataFormats.FileDrop, WF.DataFormats.Bitmap, "PNG",
    };

    static WF.DataObject? Snapshot()
    {
        WF.DataObject? copy = null;
        Retry(() =>
        {
            var src = WF.Clipboard.GetDataObject();
            if (src == null) return;
            var d = new WF.DataObject();
            int count = 0;
            foreach (var f in Formats)
            {
                try
                {
                    if (!src.GetDataPresent(f, false)) continue;
                    var v = src.GetData(f, false);
                    if (v != null) { d.SetData(f, false, v); count++; }
                }
                catch { }
            }
            copy = count > 0 ? d : null;
        });
        return copy;
    }

    static void Restore(WF.DataObject? snapshot)
    {
        try
        {
            if (snapshot == null) WF.Clipboard.Clear();
            else WF.Clipboard.SetDataObject(snapshot, true, 5, 40);
        }
        catch (Exception ex) { Paths.Log("clipboard restore: " + ex.Message); }
    }

    static void Retry(Action a)
    {
        for (int i = 0; i < 4; i++)
        {
            try { a(); return; }
            catch (System.Runtime.InteropServices.ExternalException) { System.Threading.Thread.Sleep(25); }
            catch (Exception ex) { Paths.Log("clipboard: " + ex.Message); return; }
        }
    }
}

static class Img
{
    public static object Block(string jpegB64) =>
        new { type = "image", source = new { type = "base64", media_type = "image/jpeg", data = jpegB64 } };

    public static (string b64, int w, int h, double scale) ScaledJpeg(Image src, int maxLong, double maxPixels,
        Action<Graphics, double>? overlay = null)
    {
        double scale = Math.Min(1.0, Math.Min((double)maxLong / Math.Max(src.Width, src.Height),
                                              Math.Sqrt(maxPixels / ((double)src.Width * src.Height))));
        int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, w, h));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            overlay?.Invoke(g, scale);
        }
        return (Jpeg(bmp), w, h, scale);
    }

    public static string CropJpeg(Bitmap src, Rectangle r, Action<Graphics> overlay)
    {
        using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            overlay(g);
        }
        return Jpeg(bmp);
    }

    public static void Marker(Graphics g, float x, float y, float r)
    {
        using var pen = new Pen(Color.FromArgb(235, 255, 45, 45), 3f);
        g.DrawEllipse(pen, x - r, y - r, 2 * r, 2 * r);
        using var dot = new SolidBrush(Color.FromArgb(235, 255, 45, 45));
        g.FillEllipse(dot, x - 2.5f, y - 2.5f, 5, 5);
    }

    static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);

    static string Jpeg(Bitmap b)
    {
        using var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        using var ms = new MemoryStream();
        b.Save(ms, JpegCodec, ps);
        return Convert.ToBase64String(ms.ToArray());
    }
}
