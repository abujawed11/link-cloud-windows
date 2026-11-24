using System.IO;
using System.Windows;
using System.Windows.Input;
using LibVLCSharp.Shared;

namespace LinkCloudPlayer.Views;

public partial class PlayerWindow : Window
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private PlayerControlsWindow? _controls;
    private bool _isFullscreen;
    private static readonly string VlcLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LinkCloudPlayer",
        "vlc.log"
    );

    private const double ControlBarHeight = 86;

    public PlayerWindow(string title, string streamUrl)
    {
        InitializeComponent();
        Title = title;

        // --avcodec-hw=none: software decoding only. Hardware-accelerated
        // video decode/render is the most common source of a silent native
        // crash (unrecoverable - can't be caught by any .NET try/catch) in
        // LibVLC-based players; this trades some CPU usage for reliability.
        // If playback is smooth without crashing, this can be relaxed later.
        _libVlc = new LibVLC(enableDebugLogs: true, "--avcodec-hw=none");
        _libVlc.Log += OnVlcLog;
        _mediaPlayer = new MediaPlayer(_libVlc);
        VlcVideoView.MediaPlayer = _mediaPlayer;
        _mediaPlayer.EncounteredError += (_, _) => Dispatcher.Invoke(ShowPlaybackError);

        using var media = new Media(_libVlc, streamUrl, FromType.FromLocation);
        _mediaPlayer.Play(media);

        Loaded += OnLoaded;
        LocationChanged += (_, _) => PositionControls();
        SizeChanged += (_, _) => PositionControls();
        StateChanged += (_, _) => PositionControls();
        Closed += (_, _) => Cleanup();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _controls = new PlayerControlsWindow(_mediaPlayer) { Owner = this };
        _controls.FullscreenToggleRequested += ToggleFullscreen;
        _controls.Show();
        PositionControls();
    }

    // The controls window is a separate top-level window overlaid on this
    // one (see PlayerControlsWindow's class comment for why) - it has to be
    // manually kept in sync with this window's screen position/size.
    private void PositionControls()
    {
        if (_controls is null || WindowState == WindowState.Minimized) return;
        var topLeft = PointToScreen(new Point(0, 0));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        _controls.Left = topLeft.X / dpi.DpiScaleX;
        _controls.Width = ActualWidth;
        _controls.Height = ControlBarHeight;
        _controls.Top = (topLeft.Y / dpi.DpiScaleY) + ActualHeight - ControlBarHeight;
    }

    private void OnVlcLog(object? sender, LogEventArgs e)
    {
        try
        {
            File.AppendAllText(VlcLogPath, $"[{DateTime.Now:O}] [{e.Level}] {e.Module}: {e.Message}\n");
        }
        catch { /* logging must never crash playback */ }
    }

    private void ShowPlaybackError()
    {
        ErrorText.Text = "Can't play this file. The codec may not be supported, or the server is unreachable.";
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnVideoClick(object sender, MouseButtonEventArgs e) => _controls?.TogglePlayPause();

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_controls is null) return;
        switch (e.Key)
        {
            case Key.Space:
            case Key.K:
                _controls.TogglePlayPause();
                break;
            case Key.Right:
                _controls.Skip(10000);
                break;
            case Key.Left:
                _controls.Skip(-10000);
                break;
            case Key.Up:
                _controls.AdjustVolume(5);
                break;
            case Key.Down:
                _controls.AdjustVolume(-5);
                break;
            case Key.M:
                _controls.ToggleMute();
                break;
            case Key.F:
                ToggleFullscreen();
                break;
            case Key.Escape when _isFullscreen:
                ToggleFullscreen();
                break;
        }
    }

    private void Cleanup()
    {
        _controls?.Close();
        _mediaPlayer.Stop();
        VlcVideoView.MediaPlayer = null;
        _mediaPlayer.Dispose();
        _libVlc.Log -= OnVlcLog;
        _libVlc.Dispose();
    }
}
