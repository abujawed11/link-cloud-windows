using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Microsoft.Win32;

namespace LinkCloudPlayer.Views;

// A separate, borderless window overlaid on top of PlayerWindow's video
// surface, spanning its ENTIRE area (not just a bottom bar). Necessary
// because LibVLCSharp's VideoView hosts a native child window (HwndHost) for
// rendering, and a child HWND both paints over AND captures mouse input
// ahead of ordinary WPF siblings in the same window ("airspace") - any WPF
// control placed directly in PlayerWindow would be invisible and
// unclickable. PlayerWindow keeps this window positioned over its client
// area.
//
// Because this is a layered window (AllowsTransparency), Windows lets clicks
// on fully transparent pixels fall through to the window underneath. Every
// region that needs mouse input is painted with an almost-invisible
// #01000000 brush in XAML for that reason - do not change those to
// Transparent.
public partial class PlayerControlsWindow : Window
{
    private static readonly float[] Rates = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f];
    private static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(3);
    private const double VolumeTrackWidth = 78; // VolumeArea expanded width (90) minus its 6px side margins
    private const double VolumeAreaWidth = 90;

    private readonly MediaPlayer _mediaPlayer;
    private readonly DispatcherTimer _hideTimer;

    // Seek state
    private bool _scrubbing;
    private double _scrubFraction;
    private long? _pendingSeekMs;      // target of the last seek, until playback catches up
    private DateTime _pendingSeekAt;
    private double _displayFraction;   // what the seek bar currently shows
    private bool _showRemaining;

    // Volume state
    private bool _volumeDragging;
    private int _volume = 100;

    // UI state
    private bool _chromeVisible = true;
    private bool _isFullscreen;
    private Point _lastMousePos;
    private float _rate = 1f;

    public event Action? FullscreenToggleRequested;
    public event Action? ExitFullscreenRequested;
    public event Action? CloseRequested;

    public PlayerControlsWindow(MediaPlayer mediaPlayer, string title)
    {
        InitializeComponent();
        _mediaPlayer = mediaPlayer;
        TitleText.Text = title;

        _hideTimer = new DispatcherTimer { Interval = HideDelay };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (CanAutoHide()) HideChrome();
        };

        _mediaPlayer.TimeChanged += OnTimeChanged;
        _mediaPlayer.LengthChanged += OnLengthChanged;
        _mediaPlayer.Playing += OnPlaying;
        _mediaPlayer.Paused += OnPaused;
        _mediaPlayer.Stopped += OnStopped;
        _mediaPlayer.EndReached += OnEndReached;
        _mediaPlayer.Buffering += OnBuffering;

        SeekArea.SizeChanged += (_, _) => RenderSeekBar(_displayFraction);
        PreviewKeyDown += (_, e) => { if (HandleKey(e.Key)) e.Handled = true; };

        Loaded += (_, _) =>
        {
            SetVolume(_volume, showFlash: false);
            SetBuffering(true, "Loading...");
            _hideTimer.Start();
        };
        Closed += (_, _) => UnsubscribeAll();
    }

    private void UnsubscribeAll()
    {
        // Unsubscribed before PlayerWindow.Cleanup() calls Stop()/Dispose() on
        // the same _mediaPlayer - a native callback landing mid-teardown and
        // trying to marshal onto a UI thread that is itself blocked inside
        // that teardown call is exactly how this used to hang on close.
        _hideTimer.Stop();
        _mediaPlayer.TimeChanged -= OnTimeChanged;
        _mediaPlayer.LengthChanged -= OnLengthChanged;
        _mediaPlayer.Playing -= OnPlaying;
        _mediaPlayer.Paused -= OnPaused;
        _mediaPlayer.Stopped -= OnStopped;
        _mediaPlayer.EndReached -= OnEndReached;
        _mediaPlayer.Buffering -= OnBuffering;
    }

    // ───────────────────────── Public API (used by PlayerWindow) ─────────────────────────

    public void SetFullscreenState(bool isFullscreen)
    {
        _isFullscreen = isFullscreen;
        FullscreenIcon.Data = (Geometry)FindResource(isFullscreen ? "IconFullscreenExit" : "IconFullscreen");
        BarTitleText.Text = isFullscreen ? TitleText.Text : "";
    }

    public void ShowError(string message)
    {
        SetBuffering(false);
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        ShowChrome();
    }

    /// <summary>Handles a player keyboard shortcut. Returns true if the key was used.</summary>
    public bool HandleKey(Key key)
    {
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (key)
        {
            case Key.Space:
            case Key.K:
            case Key.MediaPlayPause:
                TogglePlayPause(flash: true);
                return true;
            case Key.Left:
            case Key.J:
                Skip(-10_000);
                return true;
            case Key.Right:
            case Key.L:
                Skip(10_000);
                return true;
            case Key.Up:
                SetVolume(_volume + 5, showFlash: true);
                return true;
            case Key.Down:
                SetVolume(_volume - 5, showFlash: true);
                return true;
            case Key.M:
                ToggleMute();
                return true;
            case Key.F:
                FullscreenToggleRequested?.Invoke();
                return true;
            case Key.Home:
                SeekTo(0);
                return true;
            case Key.OemPeriod when shift:
                StepRate(+1);
                return true;
            case Key.OemComma when shift:
                StepRate(-1);
                return true;
            case >= Key.D0 and <= Key.D9:
                SeekToFraction((key - Key.D0) / 10.0);
                return true;
            case >= Key.NumPad0 and <= Key.NumPad9:
                SeekToFraction((key - Key.NumPad0) / 10.0);
                return true;
            case Key.Escape:
                if (ClosePanels()) return true;
                if (_isFullscreen) { ExitFullscreenRequested?.Invoke(); return true; }
                return false;
        }
        return false;
    }

    // ───────────────────────── Playback ─────────────────────────

    private void TogglePlayPause(bool flash)
    {
        var state = _mediaPlayer.State;
        if (state == VLCState.Ended || state == VLCState.Stopped)
        {
            // libvlc won't resume from Ended; restart from the beginning.
            _mediaPlayer.Stop();
            _mediaPlayer.Play();
            if (flash) Flash("IconPlay");
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.SetPause(true);
            PlayPauseIcon.Data = (Geometry)FindResource("IconPlay");
            if (flash) Flash("IconPause");
            ShowChrome();
        }
        else
        {
            _mediaPlayer.SetPause(false);
            PlayPauseIcon.Data = (Geometry)FindResource("IconPause");
            if (flash) Flash("IconPlay");
            RestartHideTimer();
        }
    }

    private long CurrentTimeMs => _pendingSeekMs ?? _mediaPlayer.Time;

    private void Skip(long deltaMs)
    {
        if (_mediaPlayer.Length <= 0) return;
        SeekTo(CurrentTimeMs + deltaMs);
        Flash(null, deltaMs > 0 ? "+10s" : "−10s");
        ShowChrome();
    }

    private void SeekToFraction(double fraction)
    {
        if (_mediaPlayer.Length <= 0) return;
        SeekTo((long)(fraction * _mediaPlayer.Length));
        ShowChrome();
    }

    private void SeekTo(long ms)
    {
        var length = _mediaPlayer.Length;
        if (length <= 0) return;
        ms = Math.Clamp(ms, 0, Math.Max(length - 500, 0));
        _pendingSeekMs = ms;
        _pendingSeekAt = DateTime.UtcNow;
        _mediaPlayer.Time = ms;
        UpdateProgress(ms, length);
    }

    private void StepRate(int direction)
    {
        var index = Array.IndexOf(Rates, _rate);
        if (index < 0) index = Array.IndexOf(Rates, 1f);
        index = Math.Clamp(index + direction, 0, Rates.Length - 1);
        SetRate(Rates[index]);
        Flash(null, $"{_rate}x");
    }

    private void SetRate(float rate)
    {
        _rate = rate;
        _mediaPlayer.SetRate(rate);
    }

    // ───────────────────────── libvlc events (raised on libvlc threads) ─────────────────────────

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_scrubbing) return;
        if (_pendingSeekMs is long target)
        {
            // Hold the bar at the seek target until playback actually gets
            // there, so it doesn't jump back to the old position briefly.
            var caughtUp = Math.Abs(e.Time - target) < 2000;
            var timedOut = DateTime.UtcNow - _pendingSeekAt > TimeSpan.FromSeconds(4);
            if (!caughtUp && !timedOut) return;
            _pendingSeekMs = null;
        }
        UpdateProgress(e.Time, _mediaPlayer.Length);
    });

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => UpdateProgress(CurrentTimeMs, e.Length));

    private void OnPlaying(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        PlayPauseIcon.Data = (Geometry)FindResource("IconPause");
        SetBuffering(false);
        RestartHideTimer();
    });

    private void OnPaused(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        PlayPauseIcon.Data = (Geometry)FindResource("IconPlay");
        ShowChrome();
    });

    private void OnStopped(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
        PlayPauseIcon.Data = (Geometry)FindResource("IconPlay"));

    private void OnEndReached(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        PlayPauseIcon.Data = (Geometry)FindResource("IconPlay");
        SetBuffering(false);
        UpdateProgress(_mediaPlayer.Length, _mediaPlayer.Length);
        ShowChrome();
    });

    // libvlc reports how full its input cache is (0-100) during the initial
    // load and on every re-buffer (e.g. right after a seek).
    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (e.Cache >= 100) SetBuffering(false);
        else SetBuffering(true, $"Buffering {e.Cache:0}%");
    });

    // ───────────────────────── Seek bar ─────────────────────────

    private void UpdateProgress(long timeMs, long lengthMs)
    {
        var fraction = lengthMs > 0 ? Math.Clamp((double)timeMs / lengthMs, 0, 1) : 0;
        RenderSeekBar(fraction);
        UpdateTimeText(timeMs, lengthMs);
    }

    private void RenderSeekBar(double fraction)
    {
        _displayFraction = fraction;
        var width = SeekArea.ActualWidth;
        SeekFill.Width = fraction * width;
        Canvas.SetLeft(SeekThumb, fraction * width - SeekThumb.Width / 2);
    }

    private void UpdateTimeText(long timeMs, long lengthMs)
    {
        if (lengthMs <= 0) { TimeText.Text = FormatTime(timeMs); return; }
        TimeText.Text = _showRemaining
            ? "-" + FormatTime(lengthMs - timeMs)
            : $"{FormatTime(timeMs)} / {FormatTime(lengthMs)}";
    }

    private double SeekFractionAt(MouseEventArgs e) =>
        SeekArea.ActualWidth <= 0 ? 0 : Math.Clamp(e.GetPosition(SeekArea).X / SeekArea.ActualWidth, 0, 1);

    private void OnSeekMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_mediaPlayer.Length <= 0) return;
        _scrubbing = true;
        SeekArea.CaptureMouse();
        Scrub(SeekFractionAt(e));
    }

    private void OnSeekMouseMove(object sender, MouseEventArgs e)
    {
        var fraction = SeekFractionAt(e);
        ShowSeekHover(fraction);
        if (_scrubbing) Scrub(fraction);
    }

    private void OnSeekMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_scrubbing) return;
        _scrubbing = false;          // before releasing: ReleaseMouseCapture raises LostMouseCapture synchronously
        SeekArea.ReleaseMouseCapture();
        CommitScrub();
    }

    private void OnSeekLostCapture(object sender, MouseEventArgs e)
    {
        if (!_scrubbing) return;     // capture stolen mid-drag (e.g. alt-tab) - commit what we have
        _scrubbing = false;
        CommitScrub();
    }

    private void Scrub(double fraction)
    {
        _scrubFraction = fraction;
        var length = _mediaPlayer.Length;
        RenderSeekBar(fraction);
        UpdateTimeText((long)(fraction * length), length);
        ShowSeekHover(fraction);
    }

    private void CommitScrub()
    {
        SeekTo((long)(_scrubFraction * _mediaPlayer.Length));
        if (!SeekArea.IsMouseOver) OnSeekMouseLeave(SeekArea, null);
    }

    private void ShowSeekHover(double fraction)
    {
        var length = _mediaPlayer.Length;
        if (length <= 0) return;
        var width = SeekArea.ActualWidth;
        var x = fraction * width;

        SeekHoverFill.Visibility = Visibility.Visible;
        SeekHoverFill.Width = x;

        SeekTooltipText.Text = FormatTime((long)(fraction * length));
        SeekTooltip.Visibility = Visibility.Visible;
        SeekTooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tipWidth = SeekTooltip.DesiredSize.Width;
        Canvas.SetLeft(SeekTooltip, Math.Clamp(x - tipWidth / 2, 0, Math.Max(width - tipWidth, 0)));
    }

    private void OnSeekMouseEnter(object sender, MouseEventArgs e)
    {
        Animate(SeekTrack, HeightProperty, 7, 120);
        Animate(SeekThumbScale, ScaleTransform.ScaleXProperty, 1.15, 120);
        Animate(SeekThumbScale, ScaleTransform.ScaleYProperty, 1.15, 120);
    }

    private void OnSeekMouseLeave(object sender, MouseEventArgs? e)
    {
        if (_scrubbing) return;
        Animate(SeekTrack, HeightProperty, 4, 120);
        Animate(SeekThumbScale, ScaleTransform.ScaleXProperty, 0.8, 120);
        Animate(SeekThumbScale, ScaleTransform.ScaleYProperty, 0.8, 120);
        SeekHoverFill.Visibility = Visibility.Collapsed;
        SeekTooltip.Visibility = Visibility.Collapsed;
    }

    private void OnTimeTextClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _showRemaining = !_showRemaining;
        UpdateTimeText(CurrentTimeMs, _mediaPlayer.Length);
    }

    // ───────────────────────── Volume ─────────────────────────

    private void SetVolume(int volume, bool showFlash)
    {
        _volume = Math.Clamp(volume, 0, 100);
        _mediaPlayer.Volume = _volume;
        if (_mediaPlayer.Mute && _volume > 0) _mediaPlayer.Mute = false;
        RenderVolume();
        if (showFlash) Flash(VolumeIconKey(), $"{_volume}%");
    }

    private void ToggleMute()
    {
        _mediaPlayer.Mute = !_mediaPlayer.Mute;
        RenderVolume();
        Flash(VolumeIconKey());
    }

    private string VolumeIconKey() =>
        _mediaPlayer.Mute || _volume == 0 ? "IconVolumeOff" : _volume < 50 ? "IconVolumeLow" : "IconVolume";

    private void RenderVolume()
    {
        var shown = _mediaPlayer.Mute ? 0 : _volume;
        VolumeFill.Width = shown / 100.0 * VolumeTrackWidth;
        Canvas.SetLeft(VolumeThumb, shown / 100.0 * VolumeTrackWidth - VolumeThumb.Width / 2);
        VolumeIcon.Data = (Geometry)FindResource(VolumeIconKey());
    }

    private int VolumeAt(MouseEventArgs e) =>
        (int)Math.Round(Math.Clamp((e.GetPosition(VolumeArea).X - 6) / VolumeTrackWidth, 0, 1) * 100);

    private void OnVolumeMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _volumeDragging = true;
        VolumeArea.CaptureMouse();
        SetVolume(VolumeAt(e), showFlash: false);
    }

    private void OnVolumeMouseMove(object sender, MouseEventArgs e)
    {
        if (_volumeDragging) SetVolume(VolumeAt(e), showFlash: false);
    }

    private void OnVolumeMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _volumeDragging = false;
        VolumeArea.ReleaseMouseCapture();
        if (!VolumeGroup.IsMouseOver) OnVolumeGroupLeave(VolumeGroup, e);
    }

    private void OnVolumeGroupEnter(object sender, MouseEventArgs e) => Animate(VolumeArea, WidthProperty, VolumeAreaWidth, 150);

    private void OnVolumeGroupLeave(object sender, MouseEventArgs e)
    {
        if (_volumeDragging) return;
        Animate(VolumeArea, WidthProperty, 0, 150);
    }

    // ───────────────────────── Buttons ─────────────────────────

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayPause(flash: false);
    private void OnSkipBackClick(object sender, RoutedEventArgs e) => Skip(-10_000);
    private void OnSkipForwardClick(object sender, RoutedEventArgs e) => Skip(10_000);
    private void OnMuteClick(object sender, RoutedEventArgs e) => ToggleMute();
    private void OnFullscreenClick(object sender, RoutedEventArgs e) => FullscreenToggleRequested?.Invoke();
    private void OnBackClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        var open = SpeedPanel.Visibility != Visibility.Visible;
        ClosePanels();
        if (open) { BuildSpeedPanel(); SpeedPanel.Visibility = Visibility.Visible; }
    }

    private void OnTracksClick(object sender, RoutedEventArgs e)
    {
        var open = TracksPanel.Visibility != Visibility.Visible;
        ClosePanels();
        if (open) { BuildTracksPanel(); TracksPanel.Visibility = Visibility.Visible; }
    }

    private bool ClosePanels()
    {
        var anyOpen = SpeedPanel.Visibility == Visibility.Visible || TracksPanel.Visibility == Visibility.Visible;
        SpeedPanel.Visibility = Visibility.Collapsed;
        TracksPanel.Visibility = Visibility.Collapsed;
        if (anyOpen) RestartHideTimer();
        return anyOpen;
    }

    private bool AnyPanelOpen =>
        SpeedPanel.Visibility == Visibility.Visible || TracksPanel.Visibility == Visibility.Visible;

    private void BuildSpeedPanel()
    {
        SpeedOptions.Children.Clear();
        foreach (var rate in Rates)
        {
            var selected = Math.Abs(rate - _rate) < 0.001f;
            var button = new Button
            {
                Style = (Style)FindResource("OptionButton"),
                Content = new TextBlock
                {
                    Text = rate == 1f ? "1x (Normal)" : $"{rate}x",
                    FontWeight = selected ? FontWeights.Bold : FontWeights.Normal,
                },
                Margin = new Thickness(2, 0, 2, 0),
            };
            if (selected) button.Foreground = Brushes.White;
            button.Click += (_, _) =>
            {
                SetRate(rate);
                ClosePanels();
            };
            SpeedOptions.Children.Add(button);
        }
    }

    private void BuildTracksPanel()
    {
        AudioOptions.Children.Clear();
        foreach (var track in _mediaPlayer.AudioTrackDescription)
        {
            if (track.Id == -1) continue; // "Disable" entry - not useful for audio
            var id = track.Id;
            AudioOptions.Children.Add(MakeOption(track.Name ?? $"Track {id}", _mediaPlayer.AudioTrack == id, () =>
            {
                _mediaPlayer.SetAudioTrack(id);
                BuildTracksPanel();
            }));
        }
        if (AudioOptions.Children.Count == 0)
            AudioOptions.Children.Add(MakeOption("Default", true, () => { }));

        SubtitleOptions.Children.Clear();
        SubtitleOptions.Children.Add(MakeOption("Off", _mediaPlayer.Spu == -1, () =>
        {
            _mediaPlayer.SetSpu(-1);
            BuildTracksPanel();
        }));
        foreach (var track in _mediaPlayer.SpuDescription)
        {
            if (track.Id == -1) continue;
            var id = track.Id;
            SubtitleOptions.Children.Add(MakeOption(track.Name ?? $"Track {id}", _mediaPlayer.Spu == id, () =>
            {
                _mediaPlayer.SetSpu(id);
                BuildTracksPanel();
            }));
        }
        SubtitleOptions.Children.Add(MakeOption("Load subtitle file…", false, LoadExternalSubtitle, isAction: true));
    }

    private Button MakeOption(string text, bool selected, Action onClick, bool isAction = false)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var check = new TextBlock { Text = selected ? "✓" : "", FontWeight = FontWeights.Bold };
        var label = new TextBlock
        {
            Text = text,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            FontStyle = isAction ? FontStyles.Italic : FontStyles.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 280,
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(check);
        row.Children.Add(label);

        var button = new Button { Style = (Style)FindResource("OptionButton"), Content = row };
        if (selected) button.Foreground = Brushes.White;
        button.Click += (_, _) => onClick();
        return button;
    }

    private void LoadExternalSubtitle()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Subtitle files|*.srt;*.ass;*.ssa;*.vtt|All files|*.*",
            Title = "Load subtitle file",
        };
        if (dialog.ShowDialog(this) != true) return;

        var uri = new Uri(dialog.FileName).AbsoluteUri;
        _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, uri, true);
        // The added slave takes a moment to register as a selectable track.
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(600);
            if (TracksPanel.Visibility == Visibility.Visible) BuildTracksPanel();
        });
    }

    // ───────────────────────── Video area mouse ─────────────────────────

    private void OnVideoAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only clicks on the bare video area - not on panels, bars or labels.
        if (!ReferenceEquals(e.OriginalSource, Root)) return;

        if (ClosePanels()) return;

        if (e.ClickCount == 2)
        {
            // The first click of the double-click already toggled playback;
            // undo that so double-click only changes fullscreen.
            TogglePlayPause(flash: false);
            FullscreenToggleRequested?.Invoke();
        }
        else if (e.ClickCount == 1)
        {
            TogglePlayPause(flash: true);
        }
    }

    private void OnVideoAreaMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetVolume(_volume + (e.Delta > 0 ? 5 : -5), showFlash: true);
    }

    private void OnRootMouseMove(object sender, MouseEventArgs e)
    {
        // WPF raises MouseMove on layout/cursor changes too - only react to real movement.
        var pos = e.GetPosition(Root);
        if (Math.Abs(pos.X - _lastMousePos.X) < 1 && Math.Abs(pos.Y - _lastMousePos.Y) < 1) return;
        _lastMousePos = pos;
        ShowChrome();
    }

    private void OnRootMouseLeave(object sender, MouseEventArgs e)
    {
        if (CanAutoHide()) HideChrome();
    }

    // ───────────────────────── Auto-hide ─────────────────────────

    private bool CanAutoHide() =>
        _mediaPlayer.IsPlaying
        && !_scrubbing
        && !_volumeDragging
        && !AnyPanelOpen
        && !BottomBar.IsMouseOver
        && ErrorText.Visibility != Visibility.Visible;

    private void ShowChrome()
    {
        if (!_chromeVisible)
        {
            _chromeVisible = true;
            Chrome.IsHitTestVisible = true;
            Root.Cursor = null;
            Animate(Chrome, OpacityProperty, 1, 150);
        }
        RestartHideTimer();
    }

    private void HideChrome()
    {
        if (!_chromeVisible) return;
        _chromeVisible = false;
        Chrome.IsHitTestVisible = false;
        Root.Cursor = Cursors.None;
        Animate(Chrome, OpacityProperty, 0, 300);
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    // ───────────────────────── Visual feedback ─────────────────────────

    private void SetBuffering(bool visible, string? text = null)
    {
        if (text is not null) BufferingText.Text = text;
        var wasVisible = BufferingPanel.Visibility == Visibility.Visible;
        if (visible == wasVisible) return;

        BufferingPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    private void Flash(string? iconKey, string? text = null)
    {
        FlashIcon.Data = iconKey is null ? null : (Geometry)FindResource(iconKey);
        FlashIcon.Visibility = iconKey is null ? Visibility.Collapsed : Visibility.Visible;
        FlashText.Text = text ?? "";
        FlashText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (iconKey is not null && text is not null)
        {
            // Icon + text: shrink icon and put text beneath it.
            FlashIcon.Width = FlashIcon.Height = 30;
            FlashIcon.VerticalAlignment = VerticalAlignment.Top;
            FlashIcon.Margin = new Thickness(0, 18, 0, 0);
            FlashText.VerticalAlignment = VerticalAlignment.Bottom;
            FlashText.Margin = new Thickness(0, 0, 0, 16);
            FlashText.FontSize = 18;
        }
        else
        {
            FlashIcon.Width = FlashIcon.Height = 44;
            FlashIcon.VerticalAlignment = VerticalAlignment.Center;
            FlashIcon.Margin = new Thickness(0);
            FlashText.VerticalAlignment = VerticalAlignment.Center;
            FlashText.Margin = new Thickness(0);
            FlashText.FontSize = 22;
        }

        var duration = TimeSpan.FromMilliseconds(650);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        FlashBadge.BeginAnimation(OpacityProperty,
            new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))),
                    new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(duration)),
                },
            });
        var scale = new DoubleAnimation(0.85, 1.15, duration) { EasingFunction = ease };
        FlashScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        FlashScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private static void Animate(IAnimatable target, DependencyProperty property, double to, int ms) =>
        target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });

    private static string FormatTime(long ms)
    {
        if (ms < 0) ms = 0;
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }
}
