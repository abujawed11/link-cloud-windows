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
    private Rect _restoreBounds;
    private WindowState _restoreState;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResizeMode;

    private static readonly string VlcLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LinkCloudPlayer",
        "vlc.log");

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
        _libVlc = new LibVLC(enableDebugLogs: true, "--avcodec-hw=none", "--no-video-title-show");
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
    // WindowStyle=None + WindowState=Maximized is unreliable in WPF (the
    // window overhangs the screen edges and the taskbar can stay on top), so
    // instead the host window is sized to exactly cover its monitor.

    private void ToggleFullscreen()
    {
        if (_host is null) return;
        if (!_isFullscreen)
        {
            _restoreState = _host.WindowState;
            _restoreStyle = _host.WindowStyle;
            _restoreResizeMode = _host.ResizeMode;
            _restoreBounds = _host.WindowState == WindowState.Normal
                ? new Rect(_host.Left, _host.Top, _host.Width, _host.Height)
                : _host.RestoreBounds;

            var monitor = GetMonitorBoundsDip(_host);
            _host.WindowState = WindowState.Normal; // must leave Maximized before resizing manually
            _host.WindowStyle = WindowStyle.None;
            _host.ResizeMode = ResizeMode.NoResize;
            _host.Left = monitor.Left;
            _host.Top = monitor.Top;
            _host.Width = monitor.Width;
            _host.Height = monitor.Height;
            _isFullscreen = true;
        }
        else
        {
            _isFullscreen = false;
            _host.WindowStyle = _restoreStyle;
            _host.ResizeMode = _restoreResizeMode;
            _host.Left = _restoreBounds.Left;
            _host.Top = _restoreBounds.Top;
            _host.Width = _restoreBounds.Width;
            _host.Height = _restoreBounds.Height;
            if (_restoreState == WindowState.Maximized) _host.WindowState = WindowState.Maximized;
        }

        UpdateTopmost();
        _controls?.SetFullscreenState(_isFullscreen);
        // Style changes shift the client area without always raising
        // LocationChanged/SizeChanged - re-sync once layout has settled.
        Dispatcher.BeginInvoke(PositionControls, DispatcherPriority.Loaded);
        _host.Activate();
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
        _host.Topmost = topmost;
        if (_controls is not null) _controls.Topmost = topmost;
    }

    private static Rect GetMonitorBoundsDip(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

        var dpi = VisualTreeHelper.GetDpi(window);
        var r = info.rcMonitor;
        return new Rect(
            r.Left / dpi.DpiScaleX,
            r.Top / dpi.DpiScaleY,
            (r.Right - r.Left) / dpi.DpiScaleX,
            (r.Bottom - r.Top) / dpi.DpiScaleY);
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
