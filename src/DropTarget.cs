using System;
using System.Runtime.InteropServices;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Assist;

/// A plain Windows (OLE) drop target for files. Used instead of WPF's drag-and-drop on the floating button,
/// because WPF misses the first drag over a window that never takes focus.
[ComVisible(true)]
public sealed class FileDropTarget : FileDropTarget.IOleDropTarget
{
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOleDropTarget
    {
        [PreserveSig] int DragEnter([In] ComIDataObject data, int keys, POINTL pt, ref int effect);
        [PreserveSig] int DragOver(int keys, POINTL pt, ref int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([In] ComIDataObject data, int keys, POINTL pt, ref int effect);
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINTL { public int X, Y; }

    [DllImport("ole32.dll")] static extern int RegisterDragDrop(IntPtr hwnd, IOleDropTarget target);
    [DllImport("ole32.dll")] static extern int RevokeDragDrop(IntPtr hwnd);

    const int DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1;

    readonly Action<bool> hover;          // true on enter, false on leave/drop
    readonly Action<string[]> dropped;
    bool hasFiles;

    FileDropTarget(Action<bool> hover, Action<string[]> dropped) { this.hover = hover; this.dropped = dropped; }

    /// Replace whatever drop target the window had with this one.
    public static FileDropTarget Register(IntPtr hwnd, Action<bool> hover, Action<string[]> dropped)
    {
        var t = new FileDropTarget(hover, dropped);
        RevokeDragDrop(hwnd);
        int hr = RegisterDragDrop(hwnd, t);
        if (hr != 0) Paths.Log($"RegisterDragDrop failed: 0x{hr:X}");
        return t;
    }

    public static void Unregister(IntPtr hwnd) => RevokeDragDrop(hwnd);

    /// Paths from a drag: Explorer-style file lists, or text/links that point at existing files or folders
    /// (VS Code, browsers and some editors drag paths or file:/// URLs as text).
    static string[]? Files(ComIDataObject data)
    {
        try
        {
            var d = new System.Windows.DataObject(data);
            if (d.GetDataPresent(System.Windows.DataFormats.FileDrop) && d.GetData(System.Windows.DataFormats.FileDrop) is string[] { Length: > 0 } list)
                return list;

            foreach (var fmt in new[] { "UniformResourceLocatorW", "text/uri-list", System.Windows.DataFormats.UnicodeText, System.Windows.DataFormats.Text })
            {
                if (!d.GetDataPresent(fmt)) continue;
                var raw = d.GetData(fmt) switch
                {
                    string s => s,
                    System.IO.MemoryStream ms => fmt.EndsWith("W") ? System.Text.Encoding.Unicode.GetString(ms.ToArray()) : System.Text.Encoding.UTF8.GetString(ms.ToArray()),
                    _ => null,
                };
                if (raw == null) continue;
                var paths = new System.Collections.Generic.List<string>();
                foreach (var line in raw.Split(new[] { '\r', '\n', '\0' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = line.Trim().Trim('"');
                    if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(p, UriKind.Absolute, out var u)) p = u.LocalPath;
                    if (p.Length > 2 && (System.IO.File.Exists(p) || System.IO.Directory.Exists(p))) paths.Add(p);
                }
                if (paths.Count > 0) return paths.ToArray();
            }
            return null;
        }
        catch { return null; }
    }

    public int DragEnter(ComIDataObject data, int keys, POINTL pt, ref int effect)
    {
        hasFiles = Files(data) is { Length: > 0 };
        effect = hasFiles ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        if (hasFiles) hover(true);
        return 0;
    }

    public int DragOver(int keys, POINTL pt, ref int effect)
    {
        effect = hasFiles ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return 0;
    }

    public int DragLeave()
    {
        if (hasFiles) hover(false);
        hasFiles = false;
        return 0;
    }

    public int Drop(ComIDataObject data, int keys, POINTL pt, ref int effect)
    {
        var files = Files(data);
        hover(false);
        hasFiles = false;
        effect = files is { Length: > 0 } ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        // Act after the drag has fully ended, so opening the bubble doesn't happen inside the drop call.
        if (files is { Length: > 0 }) System.Windows.Application.Current.Dispatcher.BeginInvoke(() => dropped(files));
        return 0;
    }
}
