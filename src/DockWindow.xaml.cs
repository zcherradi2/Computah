using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using WF = System.Windows.Forms;

namespace Assist;

/// The small floating Computah button. Click: sessions panel. Drag: move (position is remembered).
public partial class DockWindow : Window
{
    readonly SessionStore store;
    bool pulsing;

    public event Action? Clicked;
    /// Files dropped: (paths, session to share with — null means the target —, start a new session instead).
    public event Action<string[], SessionRecord?, bool>? FilesDropped;

    DropFan? fan;
    readonly System.Windows.Threading.DispatcherTimer fanHide = new() { Interval = TimeSpan.FromMilliseconds(380) };

    public DockWindow(SessionStore store)
    {
        this.store = store;
        InitializeComponent();
        Face.Source = LoadFace();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeNoActivate(hwnd);
            PlaceInitial(hwnd);
        };
        Root.MouseEnter += (_, _) => Zoom(1.07);
        Root.MouseLeave += (_, _) => Zoom(1.0);
        Root.MouseLeftButtonDown += OnMouseDown;

        // Drop files/folders on the button to share them with the target session.
        // Registered directly with Windows (see FileDropTarget): WPF's own drop handling misses the first drag here.
        Root.Background = Brushes.Transparent; // whole button area is part of the window
        SourceInitialized += (_, _) =>
            dropTarget = FileDropTarget.Register(new WindowInteropHelper(this).Handle, new DropHandlers
            {
                Enter = _ => { Zoom(1.18); DropGlow(true); ShowFan(); fan?.Highlight(fan.TargetZone); },
                Leave = () => { Zoom(1.0); DropGlow(false); fan?.Highlight(null); fanHide.Start(); },
                Drop = (files, _) => { HideFan(); FilesDropped?.Invoke(files, null, false); },
            });
        Closed += (_, _) => { FileDropTarget.Unregister(new WindowInteropHelper(this).Handle); HideFan(); };
        fanHide.Tick += (_, _) => HideFan();
    }

    FileDropTarget? dropTarget; // kept alive while registered

    /// Session tags above the button while files are dragged over it.
    void ShowFan()
    {
        fanHide.Stop();
        if (fan != null) return;
        fan = new DropFan(this, store);
        fan.DragEntered += () => fanHide.Stop();
        fan.DragLeft += () => fanHide.Start();
        fan.Dropped += (files, zone) =>
        {
            HideFan();
            FilesDropped?.Invoke(files, zone.IsNew ? null : zone.Record, zone.IsNew);
        };
        fan.Show();
    }

    void HideFan()
    {
        fanHide.Stop();
        var f = fan;
        fan = null;
        f?.Close();
    }

    void DropGlow(bool on)
    {
        if (pulsing) return; // the "answering" glow already owns the effect
        Glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        Glow.Color = on ? Color.FromRgb(0x5C, 0xE1, 0xFF) : Colors.Black;
        Glow.ShadowDepth = on ? 0 : 3;
        Glow.BlurRadius = on ? 26 : 14;
        Glow.Opacity = on ? 1 : 0.55;
    }

    /// Physical screen rectangle of the button (for placing the panel next to it).
    internal Native.RECT ScreenRect
    {
        get { Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var r); return r; }
    }

    public void Refresh()
    {
        int n = store.Sessions.Count;
        Badge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        BadgeText.Text = n > 9 ? "9+" : n.ToString();

        bool busy = store.Sessions.Any(s => s.Window is { IsStreaming: true });
        if (busy && !pulsing)
        {
            Glow.Color = Color.FromRgb(0x5C, 0xE1, 0xFF);
            Glow.ShadowDepth = 0;
            Glow.BlurRadius = 22;
            Glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.25, 0.95, TimeSpan.FromMilliseconds(700))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else if (!busy && pulsing)
        {
            Glow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
            Glow.Color = Colors.Black;
            Glow.ShadowDepth = 3;
            Glow.BlurRadius = 14;
            Glow.Opacity = 0.55;
        }
        pulsing = busy;
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var before = ScreenRect;
        try { DragMove(); } catch { }
        var after = ScreenRect;
        if (Math.Abs(after.Left - before.Left) + Math.Abs(after.Top - before.Top) < 4)
            Clicked?.Invoke();
        else
        {
            store.DockX = after.Left; store.DockY = after.Top;
            store.Save();
        }
    }

    void PlaceInitial(IntPtr hwnd)
    {
        Native.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int x, y;
        if (store.DockX is int sx && store.DockY is int sy &&
            WF.Screen.AllScreens.Any(s => s.WorkingArea.Contains(sx + w / 2, sy + h / 2)))
        {
            x = sx; y = sy;
        }
        else
        {
            var wa = WF.Screen.PrimaryScreen!.WorkingArea;
            x = wa.Right - w - 18;
            y = wa.Bottom - h - 18;
        }
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    void Zoom(double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase() };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    public static ImageSource? LoadFace()
    {
        try
        {
            using var s = typeof(DockWindow).Assembly.GetManifestResourceStream("computah.ico")!;
            var dec = new IconBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return dec.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch { return null; }
    }
}
