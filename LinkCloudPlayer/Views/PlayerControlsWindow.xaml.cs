using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace LinkCloudPlayer.Views;

// A separate, borderless window overlaid on top of PlayerWindow's video
// surface, spanning its ENTIRE area (not just a bottom bar). Necessary
// because LibVLCSharp's VideoView hosts a native child window (HwndHost) for
// rendering, and a child HWND both paints over AND captures mouse input
// ahead of ordinary WPF siblings in the same window ("airspace") - a click
// handler on VideoView itself fires unreliably, and any WPF control bar
// placed directly in PlayerWindow would be invisible and unclickable.
// Covering the whole area (transparent except the bottom bar) means every
// click - whether toggling play/pause over the video or pressing a button -
// goes through this window instead. PlayerWindow keeps this positioned over
// its own bounds.
public partial class PlayerControlsWindow : Window
{
    private readonly MediaPlayer _mediaPlayer;
    private bool _isSeeking;
    private static readonly float[] Rates = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f];

    public event Action? FullscreenToggleRequested;

    public PlayerControlsWindow(MediaPlayer mediaPlayer)
    {
        InitializeComponent();
        _mediaPlayer = mediaPlayer;
        VolumeSlider.Value = 100; // after _mediaPlayer is assigned - OnVolumeChanged fires immediately

        _mediaPlayer.TimeChanged += OnTimeChanged;
        _mediaPlayer.LengthChanged += OnLengthChanged;
        _mediaPlayer.Playing += OnPlaying;
        _mediaPlayer.Paused += OnPaused;
        _mediaPlayer.EndReached += OnEndReached;
        _mediaPlayer.Buffering += OnBuffering;

        BuildSpeedMenu();
        Loaded += (_, _) => PopulateTrackMenusWhenReady();
        Closed += (_, _) => UnsubscribeAll();
    }

    private void UnsubscribeAll()
    {
        // Unsubscribed before PlayerWindow.Cleanup() calls Stop()/Dispose() on
        // the same _mediaPlayer - a native callback landing mid-teardown and
        // trying to marshal onto a UI thread that is itself blocked inside
        // that teardown call is exactly how this used to hang on close.
        _mediaPlayer.TimeChanged -= OnTimeChanged;
        _mediaPlayer.LengthChanged -= OnLengthChanged;
        _mediaPlayer.Playing -= OnPlaying;
        _mediaPlayer.Paused -= OnPaused;
        _mediaPlayer.EndReached -= OnEndReached;
        _mediaPlayer.Buffering -= OnBuffering;
    }

    // Audio/subtitle track lists aren't populated until libvlc has parsed the
    // stream, which happens shortly after playback starts - poll briefly
    // rather than guessing a fixed delay.
    private async void PopulateTrackMenusWhenReady()
    {
        for (int i = 0; i < 20 && _mediaPlayer.Media is not null; i++)
        {
            if (_mediaPlayer.AudioTrackDescription.Length > 1 || _mediaPlayer.SpuDescription.Length > 1) break;
            await Task.Delay(250);
        }
        BuildAudioMenu();
        BuildSubtitleMenu();
    }

    private void BuildSpeedMenu()
    {
        SpeedMenu.Items.Clear();
        foreach (var rate in Rates)
        {
            var item = new MenuItem { Header = rate == 1f ? "Normal" : $"{rate}x" };
            item.Click += (_, _) =>
            {
                _mediaPlayer.SetRate(rate);
                SpeedButton.Content = rate == 1f ? "1x" : $"{rate}x";
            };
            SpeedMenu.Items.Add(item);
        }
    }

    private void BuildAudioMenu()
    {
        AudioMenu.Items.Clear();
        foreach (var track in _mediaPlayer.AudioTrackDescription)
        {
            if (track.Id == -1) continue; // "Disable" entry - not useful for audio
            var item = new MenuItem { Header = track.Name, IsCheckable = true, IsChecked = _mediaPlayer.AudioTrack == track.Id };
            item.Click += (_, _) => _mediaPlayer.SetAudioTrack(track.Id);
            AudioMenu.Items.Add(item);
        }
        AudioButton.IsEnabled = AudioMenu.Items.Count > 1;
    }

    private void BuildSubtitleMenu()
    {
        SubtitleMenu.Items.Clear();

        var offItem = new MenuItem { Header = "Off", IsCheckable = true, IsChecked = _mediaPlayer.Spu == -1 };
        offItem.Click += (_, _) => _mediaPlayer.SetSpu(-1);
        SubtitleMenu.Items.Add(offItem);

        foreach (var track in _mediaPlayer.SpuDescription)
        {
            if (track.Id == -1) continue;
            var item = new MenuItem { Header = track.Name, IsCheckable = true, IsChecked = _mediaPlayer.Spu == track.Id };
            item.Click += (_, _) => _mediaPlayer.SetSpu(track.Id);
            SubtitleMenu.Items.Add(item);
        }

        SubtitleMenu.Items.Add(new Separator());
        var loadItem = new MenuItem { Header = "Load subtitle file..." };
        loadItem.Click += OnLoadExternalSubtitle;
        SubtitleMenu.Items.Add(loadItem);
    }

    private void OnLoadExternalSubtitle(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Subtitle files|*.srt;*.ass;*.ssa;*.vtt|All files|*.*",
            Title = "Load subtitle file",
        };
        if (dialog.ShowDialog() != true) return;

        var uri = new Uri(dialog.FileName).AbsoluteUri;
        _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, uri, true);
        // Re-check shortly after - the added slave takes a moment to register as a selectable track.
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(500);
            BuildSubtitleMenu();
        });
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isSeeking) return;
            var length = Math.Max(_mediaPlayer.Length, 1);
            SeekSlider.Value = (double)e.Time / length * 1000;
            TimeText.Text = $"{FormatTime(e.Time)} / {FormatTime(_mediaPlayer.Length)}";
        });
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() => TimeText.Text = $"{FormatTime(_mediaPlayer.Time)} / {FormatTime(e.Length)}");
    }

    private void OnPlaying(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        PlayPauseButton.Content = "⏸";
        BufferingText.Visibility = Visibility.Collapsed;
    });

    private void OnPaused(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => PlayPauseButton.Content = "▶");
    private void OnEndReached(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => PlayPauseButton.Content = "▶");

    // Fires during the initial load and again on a re-buffer (e.g. right
    // after a seek); libvlc does not expose a continuous "buffered ahead"
    // range the way HTML5 video does for a plain HTTP source, so this is the
    // closest available preloading signal.
    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        BufferedBar.Value = e.Cache;
        BufferingText.Visibility = e.Cache < 100 ? Visibility.Visible : Visibility.Collapsed;
    });

    private static string FormatTime(long ms)
    {
        if (ms < 0) ms = 0;
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    public void TogglePlayPause()
    {
        if (_mediaPlayer.IsPlaying) _mediaPlayer.Pause();
        else _mediaPlayer.Play();
    }

    private void OnVideoAreaClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2) FullscreenToggleRequested?.Invoke();
        else TogglePlayPause();
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayPause();
    private void OnSkipBackClick(object sender, RoutedEventArgs e) => Skip(-10000);
    private void OnSkipForwardClick(object sender, RoutedEventArgs e) => Skip(10000);

    public void Skip(long deltaMs)
    {
        var target = Math.Clamp(_mediaPlayer.Time + deltaMs, 0, Math.Max(_mediaPlayer.Length, 0));
        _mediaPlayer.Time = target;
    }

    public void ToggleMute()
    {
        _mediaPlayer.Mute = !_mediaPlayer.Mute;
        MuteButton.Content = _mediaPlayer.Mute ? "🔇" : "🔊";
    }

    private void OnMuteClick(object sender, RoutedEventArgs e) => ToggleMute();

    public void AdjustVolume(double delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mediaPlayer is null) return;
        _mediaPlayer.Volume = (int)e.NewValue;
    }

    private void OnSeekDragStart(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void OnSeekDragEnd(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = false;
        _mediaPlayer.Position = (float)(SeekSlider.Value / 1000.0);
    }

    private void OnSeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking)
        {
            TimeText.Text = $"{FormatTime((long)(e.NewValue / 1000.0 * _mediaPlayer.Length))} / {FormatTime(_mediaPlayer.Length)}";
        }
    }

    private void OnSpeedClick(object sender, RoutedEventArgs e) => SpeedButton.ContextMenu!.IsOpen = true;
    private void OnAudioClick(object sender, RoutedEventArgs e) => AudioButton.ContextMenu!.IsOpen = true;
    private void OnSubtitleClick(object sender, RoutedEventArgs e) => SubtitleButton.ContextMenu!.IsOpen = true;
    private void OnFullscreenClick(object sender, RoutedEventArgs e) => FullscreenToggleRequested?.Invoke();
}
