using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LinkCloudPlayer.Models;
using LinkCloudPlayer.Services;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace LinkCloudPlayer.Views;

/// <summary>
/// The video player, shown inside MainWindow in place of the library.
/// Raises <see cref="CloseRequested"/> when the user hits Back.
/// </summary>
public partial class PlayerView : UserControl
{
    // VLC's decoder-level buffer ("pts delay") for network streams. Keep this
    // at VLC's default of 1s: measured on these files, anything above ~1.5s
    // makes the audio decoder lag the video on resume/seek, so VLC drops
    // and flushes audio (an audible cut right after un-pausing) and pausing
    // takes effect late. Read-ahead of the actual download is handled
    // separately, below the decoders, by VLC's 16 MB "prefetch" stream
    // filter - that's what keeps playback smooth and what the buffered bar
    // shows.
    private const int NetworkCachingMs = 1_000;

    private readonly FileItem _file;
    private readonly long _startMs;
    private readonly string _title;
    private readonly WatchProgressStore _progressStore;
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly Media _media;
    private readonly DispatcherTimer _progressTimer;
    private PlayerControlsWindow? _controls;
    private Window? _host;
    private string? _pendingError;
    private bool _cleanedUp;
    private bool _ended;

    private bool _isFullscreen;
    private WINDOWPLACEMENT _restorePlacement;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResizeMode;


    public event Action? CloseRequested;

    public PlayerView(FileItem file, string title, string streamUrl, long startMs, WatchProgressStore progressStore)
    {
        InitializeComponent();
        _file = file;
        _startMs = startMs;
        _title = title;
        _progressStore = progressStore;

        // --avcodec-hw=none: software decoding only. Hardware-accelerated
        // video decode/render is the most common source of a silent native
        // crash (unrecoverable - can't be caught by any .NET try/catch) in
        // LibVLC-based players; this trades some CPU usage for reliability.
        // Set both globally and per-media, belt and suspenders.
        // --aout=directsound: measured on these files, Windows' default WASAPI
        // output starts ~0.5s late on a fresh player, so VLC drops and
        // flushes the first half-second of audio right after a (resumed)
        // start - an audible/visible lurch. DirectSound starts in time.
        _libVlc = new LibVLC(enableDebugLogs: false, "--avcodec-hw=none", "--no-video-title-show", "--aout=directsound");
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
        _mediaPlayer.EndReached += OnEndReached;

        // Kept alive (not disposed right after Play) so its read statistics
        // can drive the buffered-range estimate on the seek bar.
        _media = new Media(_libVlc, streamUrl, FromType.FromLocation);
        _media.AddOption(":avcodec-hw=none");
        _media.AddOption($":network-caching={NetworkCachingMs}");
        if (startMs > 0)
            _media.AddOption(":start-time=" + (startMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
        _mediaPlayer.Play(_media);

        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _progressTimer.Tick += (_, _) => SaveProgress();

        Loaded += OnLoaded;
        Unloaded += (_, _) => Cleanup();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_controls is not null || _cleanedUp) return;
        _host = Window.GetWindow(this)!;

        _controls = new PlayerControlsWindow(
            _mediaPlayer, _title, _file.SizeBytes, ReadBytes,
            _startMs) { Owner = _host };
        _controls.FullscreenToggleRequested += ToggleFullscreen;
        _controls.ExitFullscreenRequested += () => { if (_isFullscreen) ToggleFullscreen(); };
        _controls.CloseRequested += () => CloseRequested?.Invoke();
        _controls.Show();

        _host.LocationChanged += OnHostMoved;
        _host.SizeChanged += OnHostMoved;
        _host.StateChanged += OnHostMoved;
        _host.Activated += OnHostActivated;
        _host.PreviewKeyDown += OnHostKeyDown;
        _host.Closing += OnHostClosing;
        Root.SizeChanged += OnHostMoved;
        Application.Current.Activated += OnAppActivationChanged;
        Application.Current.Deactivated += OnAppActivationChanged;

        PositionControls();
        _progressTimer.Start();
        if (_pendingError is not null) _controls.ShowError(_pendingError);
    }

    private long? ReadBytes()
    {
        try { return _media.Statistics.ReadBytes; }
        catch { return null; }
    }

    // ───────────────────────── Overlay positioning ─────────────────────────

    private void OnHostMoved(object? sender, EventArgs e) => PositionControls();

    // The controls window is a separate top-level window overlaid on this
    // view - it has to be kept in sync with this view's screen position/size.
    private void PositionControls()
    {
        if (_controls is null || _host is null || _host.WindowState == WindowState.Minimized || !Root.IsLoaded) return;
        if (PresentationSource.FromVisual(Root) is null) return;
        var topLeft = Root.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(this);
        _controls.Left = topLeft.X / dpi.DpiScaleX;
        _controls.Top = topLeft.Y / dpi.DpiScaleY;
        _controls.Width = Root.ActualWidth;
        _controls.Height = Root.ActualHeight;
    }

    // ───────────────────────── Keyboard ─────────────────────────

    private void OnHostKeyDown(object sender, KeyEventArgs e)
    {
        if (_controls is not null && _controls.HandleKey(e.Key)) e.Handled = true;
    }

    // ───────────────────────── Errors / end ─────────────────────────

    // Warnings and errors only (debug level produced ~40 MB/day); AppLog
    // redacts the login token from stream URLs and caps the file size.
    private void OnVlcLog(object? sender, LogEventArgs e)
    {
        if (e.Level < LogLevel.Warning) return;
        AppLog.Write("vlc.log", $"[{e.Level}] {e.Module}: {e.Message}");
    }

    private void OnEncounteredError(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        const string message = "Can't play this file. The codec may not be supported, or the server is unreachable.";
        if (_controls is null) _pendingError = message;
        else _controls.ShowError(message);
    });

    private void OnEndReached(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        _ended = true;
        _progressStore.Remove(_file.Id);
    });

    // ───────────────────────── Watch progress ─────────────────────────

    private void SaveProgress()
    {
        if (_ended) return;
        var length = _mediaPlayer.Length;
        var time = _mediaPlayer.Time;
        if (length <= 0 || time <= 0) return;
        // Under 30s isn't worth a Continue Watching entry - and must not
        // overwrite an older resume point after "Play from beginning".
        if (time < 30_000) return;
        _progressStore.Update(_file.Id, time, length);
    }

    // ───────────────────────── Fullscreen ─────────────────────────
    // Fullscreen = borderless window sized to exactly cover its monitor
    // (WindowStyle=None + Maximized is unreliable in WPF: the window
    // overhangs the screen edges and the taskbar can stay on top).
    //
    // Smoothness: changing WPF's WindowState/WindowStyle/Left/Top/Width/
    // Height one by one makes the window visibly step through every
    // intermediate state (plus the Windows minimize/maximize animation and
    // the overlay lagging behind), which looks like stuttering. Instead:
    //  - the Windows window animation is switched off for the transition,
    //  - the overlay is hidden until it has been re-positioned,
    //  - the geometry change is ONE Win32 call (SetWindowPos for the monitor
    //    rect, SetWindowPlacement to go back - straight to maximized if the
    //    window was maximized before).

    private void ToggleFullscreen()
    {
        if (_host is null) return;
        var hwnd = new WindowInteropHelper(_host).Handle;
        if (hwnd == IntPtr.Zero) return;

        SetTransitionsDisabled(hwnd, true);
        if (_controls is not null) _controls.Opacity = 0;

        if (!_isFullscreen) EnterFullscreen(hwnd);
        else ExitFullscreen(hwnd);

        UpdateTopmost();
        _controls?.SetFullscreenState(_isFullscreen);
        _host.Activate();

        // Once layout has caught up with the new size: put the overlay back
        // exactly over the video, show it, and give the window its normal
        // animations back.
        Dispatcher.BeginInvoke(() =>
        {
            PositionControls();
            if (_controls is not null) _controls.Opacity = 1;
            SetTransitionsDisabled(hwnd, false);
        }, DispatcherPriority.Loaded);
    }

    private void EnterFullscreen(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        var m = info.rcMonitor;

        _restoreState = _host!.WindowState;
        _restoreStyle = _host.WindowStyle;
        _restoreResizeMode = _host.ResizeMode;
        _restorePlacement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(hwnd, ref _restorePlacement); // normal bounds, even while maximized

        _host.ResizeMode = ResizeMode.NoResize;
        _host.WindowStyle = WindowStyle.None;

        if (_restoreState == WindowState.Maximized)
        {
            // Un-maximize straight onto the monitor rect (instead of first
            // shrinking back to the old normal size). Placement uses
            // work-area-relative coordinates.
            var dx = info.rcWork.Left - m.Left;
            var dy = info.rcWork.Top - m.Top;
            var p = _restorePlacement;
            p.flags = 0;
            p.showCmd = SW_SHOWNORMAL;
            p.rcNormalPosition = new RECT { Left = m.Left - dx, Top = m.Top - dy, Right = m.Right - dx, Bottom = m.Bottom - dy };
            SetWindowPlacement(hwnd, ref p);
        }

        // Exact screen coordinates, one call.
        SetWindowPos(hwnd, IntPtr.Zero, m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        _isFullscreen = true;
    }

    private void ExitFullscreen(IntPtr hwnd)
    {
        _isFullscreen = false;
        _host!.WindowStyle = _restoreStyle;
        _host.ResizeMode = _restoreResizeMode;

        // One step back to exactly where the window was - maximized or not.
        var p = _restorePlacement;
        p.flags = 0;
        if (_restoreState == WindowState.Maximized)
        {
            // Maximize directly from the fullscreen rect (SetWindowPlacement
            // alone would first jump to the old normal size, then maximize),
            // then - already maximized, so nothing moves - put back the
            // remembered normal bounds for a later un-maximize.
            ShowWindow(hwnd, SW_MAXIMIZE);
            p.showCmd = SW_SHOWMAXIMIZED;
        }
        else
        {
            p.showCmd = SW_SHOWNORMAL;
        }
        SetWindowPlacement(hwnd, ref p);
    }

    private static void SetTransitionsDisabled(IntPtr hwnd, bool disabled)
    {
        try
        {
            var value = disabled ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref value, sizeof(int));
        }
        catch { /* cosmetic only */ }
    }

    // Stay above the taskbar while fullscreen, but only while this app is in
    // the foreground - otherwise alt-tabbed apps would be stuck behind us.
    private void OnAppActivationChanged(object? sender, EventArgs e) => UpdateTopmost();
    private void OnHostActivated(object? sender, EventArgs e) => UpdateTopmost();

    private void UpdateTopmost()
    {
        if (_host is null) return;
        var appActive = Application.Current.Windows.OfType<Window>().Any(w => w.IsActive);
        var topmost = _isFullscreen && appActive;
        if (_host.Topmost != topmost) _host.Topmost = topmost;
        if (_controls is not null && _controls.Topmost != topmost) _controls.Topmost = topmost;
    }

    // ───────────────────────── Teardown ─────────────────────────

    private void OnHostClosing(object? sender, System.ComponentModel.CancelEventArgs e) => Cleanup();

    /// <summary>Stops playback, saves progress and releases the player. Safe to call twice.</summary>
    public void Shutdown() => Cleanup();

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        SaveProgress();
        _progressTimer.Stop();
        if (_isFullscreen) ToggleFullscreen();

        if (_host is not null)
        {
            _host.LocationChanged -= OnHostMoved;
            _host.SizeChanged -= OnHostMoved;
            _host.StateChanged -= OnHostMoved;
            _host.Activated -= OnHostActivated;
            _host.PreviewKeyDown -= OnHostKeyDown;
            _host.Closing -= OnHostClosing;
            _host.Topmost = false;
        }
        Root.SizeChanged -= OnHostMoved;
        Application.Current.Activated -= OnAppActivationChanged;
        Application.Current.Deactivated -= OnAppActivationChanged;

        _controls?.Close(); // unsubscribes its own MediaPlayer event handlers first
        _controls = null;
        _mediaPlayer.EncounteredError -= OnEncounteredError;
        _mediaPlayer.EndReached -= OnEndReached;
        _mediaPlayer.Stop();
        VlcVideoView.MediaPlayer = null;
        _mediaPlayer.Dispose();
        _media.Dispose();
        _libVlc.Log -= OnVlcLog;
        _libVlc.Dispose();
    }

    // ───────────────────────── Win32 ─────────────────────────

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMAXIMIZED = 3;
    private const int SW_MAXIMIZE = 3;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
