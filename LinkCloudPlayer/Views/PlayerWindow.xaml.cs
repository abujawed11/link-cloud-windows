using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace LinkCloudPlayer.Views;

public partial class PlayerWindow : Window
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private PlayerControlsWindow? _controls;
    private string? _pendingError;

    private bool _isFullscreen;
    private Rect _restoreBounds;
    private WindowState _restoreState;

    private static readonly string VlcLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LinkCloudPlayer",
        "vlc.log"
    );

    public PlayerWindow(string title, string streamUrl)
    {
        InitializeComponent();
        Title = title;

        // --avcodec-hw=none: software decoding only. Hardware-accelerated
        // video decode/render is the most common source of a silent native
        // crash (unrecoverable - can't be caught by any .NET try/catch) in
        // LibVLC-based players; this trades some CPU usage for reliability.
        // Set both globally and per-media, belt and suspenders.
        _libVlc = new LibVLC(enableDebugLogs: true, "--avcodec-hw=none");
        _libVlc.Log += OnVlcLog;
        _mediaPlayer = new MediaPlayer(_libVlc)
        {
            // The overlay window handles all input; don't let the native
            // video surface swallow mouse/keyboard events.
            EnableMouseInput = false,
            EnableKeyInput = false,
        };
        VlcVideoView.MediaPlayer = _mediaPlayer;
        _mediaPlayer.EncounteredError += OnEncounteredError;

        using var media = new Media(_libVlc, streamUrl, FromType.FromLocation);
        media.AddOption(":avcodec-hw=none");
        _mediaPlayer.Play(media);

        Loaded += OnLoaded;
        LocationChanged += (_, _) => PositionControls();
        Root.SizeChanged += (_, _) => PositionControls();
        StateChanged += (_, _) => PositionControls();
        Activated += (_, _) => UpdateTopmost();
        Application.Current.Activated += OnAppActivationChanged;
        Application.Current.Deactivated += OnAppActivationChanged;
        Closed += (_, _) => Cleanup();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _controls = new PlayerControlsWindow(_mediaPlayer, Title) { Owner = this };
        _controls.FullscreenToggleRequested += ToggleFullscreen;
        _controls.ExitFullscreenRequested += () => { if (_isFullscreen) ToggleFullscreen(); };
        _controls.CloseRequested += Close;
        _controls.Show();
        PositionControls();
        if (_pendingError is not null) _controls.ShowError(_pendingError);
    }

    // The controls window is a separate top-level window overlaid on this
    // one's CLIENT area (not including title bar/borders) - it has to be
    // manually kept in sync with this window's screen position/size.
    private void PositionControls()
    {
        if (_controls is null || WindowState == WindowState.Minimized || !Root.IsLoaded) return;
        var topLeft = Root.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(this);
        _controls.Left = topLeft.X / dpi.DpiScaleX;
        _controls.Top = topLeft.Y / dpi.DpiScaleY;
        _controls.Width = Root.ActualWidth;
        _controls.Height = Root.ActualHeight;
    }

    private void OnVlcLog(object? sender, LogEventArgs e)
    {
        try
        {
            File.AppendAllText(VlcLogPath, $"[{DateTime.Now:O}] [{e.Level}] {e.Module}: {e.Message}\n");
        }
        catch { /* logging must never crash playback */ }
    }

    private void OnEncounteredError(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        const string message = "Can't play this file. The codec may not be supported, or the server is unreachable.";
        if (_controls is null) _pendingError = message;
        else _controls.ShowError(message);
    });

    // ───────────────────────── Fullscreen ─────────────────────────
    // WindowStyle=None + WindowState=Maximized is unreliable in WPF (the
    // window overhangs the screen edges and the taskbar can stay on top), so
    // instead the window is sized to exactly cover the monitor it's on.

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _restoreState = WindowState;
            _restoreBounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            var monitor = GetMonitorBoundsDip();
            WindowState = WindowState.Normal; // must leave Maximized before resizing manually
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Left = monitor.Left;
            Top = monitor.Top;
            Width = monitor.Width;
            Height = monitor.Height;
            _isFullscreen = true;
        }
        else
        {
            _isFullscreen = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
            if (_restoreState == WindowState.Maximized) WindowState = WindowState.Maximized;
        }

        UpdateTopmost();
        _controls?.SetFullscreenState(_isFullscreen);
        // Style changes shift the client area without always raising
        // LocationChanged/SizeChanged - re-sync once layout has settled.
        Dispatcher.BeginInvoke(PositionControls, System.Windows.Threading.DispatcherPriority.Loaded);
        Activate();
    }

    // Stay above the taskbar while fullscreen, but only while this app is in
    // the foreground - otherwise alt-tabbed apps would be stuck behind us.
    private void OnAppActivationChanged(object? sender, EventArgs e) => UpdateTopmost();

    private void UpdateTopmost()
    {
        var appActive = Application.Current.Windows.OfType<Window>().Any(w => w.IsActive);
        var topmost = _isFullscreen && appActive;
        Topmost = topmost;
        if (_controls is not null) _controls.Topmost = topmost;
    }

    private Rect GetMonitorBoundsDip()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

        var dpi = VisualTreeHelper.GetDpi(this);
        var r = info.rcMonitor;
        return new Rect(
            r.Left / dpi.DpiScaleX,
            r.Top / dpi.DpiScaleY,
            (r.Right - r.Left) / dpi.DpiScaleX,
            (r.Bottom - r.Top) / dpi.DpiScaleY);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_controls is not null && _controls.HandleKey(e.Key)) e.Handled = true;
    }

    private void Cleanup()
    {
        Application.Current.Activated -= OnAppActivationChanged;
        Application.Current.Deactivated -= OnAppActivationChanged;
        _controls?.Close(); // unsubscribes its own MediaPlayer event handlers first
        _mediaPlayer.EncounteredError -= OnEncounteredError;
        _mediaPlayer.Stop();
        VlcVideoView.MediaPlayer = null;
        _mediaPlayer.Dispose();
        _libVlc.Log -= OnVlcLog;
        _libVlc.Dispose();
    }

    // ───────────────────────── Win32 ─────────────────────────

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
