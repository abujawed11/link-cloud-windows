using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace LinkCloudPlayer.Views;

public partial class PlayerWindow : Window
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private bool _isSeeking;
    private bool _isFullscreen;
    private static readonly float[] Rates = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f];

    public PlayerWindow(string title, string streamUrl)
    {
        InitializeComponent();
        Title = title;

        _libVlc = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVlc);
        VlcVideoView.MediaPlayer = _mediaPlayer;

        _mediaPlayer.TimeChanged += OnTimeChanged;
        _mediaPlayer.LengthChanged += OnLengthChanged;
        _mediaPlayer.Playing += (_, _) => Dispatcher.Invoke(() => PlayPauseButton.Content = "⏸");
        _mediaPlayer.Paused += (_, _) => Dispatcher.Invoke(() => PlayPauseButton.Content = "▶");
        _mediaPlayer.EndReached += (_, _) => Dispatcher.Invoke(() => PlayPauseButton.Content = "▶");
        _mediaPlayer.EncounteredError += (_, _) => Dispatcher.Invoke(ShowPlaybackError);

        BuildSpeedMenu();

        using var media = new Media(_libVlc, streamUrl, FromType.FromLocation);
        _mediaPlayer.Play(media);

        Loaded += (_, _) => PopulateTrackMenusWhenReady();
        Closed += (_, _) => Cleanup();
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
        Dispatcher.Invoke(() =>
        {
            if (_isSeeking) return;
            var length = Math.Max(_mediaPlayer.Length, 1);
            SeekSlider.Value = (double)e.Time / length * 1000;
            TimeText.Text = $"{FormatTime(e.Time)} / {FormatTime(_mediaPlayer.Length)}";
        });
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        Dispatcher.Invoke(() => TimeText.Text = $"{FormatTime(_mediaPlayer.Time)} / {FormatTime(e.Length)}");
    }

    private void ShowPlaybackError()
    {
        ErrorText.Text = "Can't play this file. The codec may not be supported, or the server is unreachable.";
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string FormatTime(long ms)
    {
        if (ms < 0) ms = 0;
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer.IsPlaying) _mediaPlayer.Pause();
        else _mediaPlayer.Play();
    }

    private void OnSkipBackClick(object sender, RoutedEventArgs e) => Skip(-10000);
    private void OnSkipForwardClick(object sender, RoutedEventArgs e) => Skip(10000);

    private void Skip(long deltaMs)
    {
        var target = Math.Clamp(_mediaPlayer.Time + deltaMs, 0, Math.Max(_mediaPlayer.Length, 0));
        _mediaPlayer.Time = target;
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        _mediaPlayer.Mute = !_mediaPlayer.Mute;
        MuteButton.Content = _mediaPlayer.Mute ? "🔇" : "🔊";
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
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

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

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

    private void OnVideoClick(object sender, MouseButtonEventArgs e) => OnPlayPauseClick(sender, e);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
            case Key.K:
                OnPlayPauseClick(sender, e);
                break;
            case Key.Right:
                Skip(10000);
                break;
            case Key.Left:
                Skip(-10000);
                break;
            case Key.Up:
                VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
                break;
            case Key.Down:
                VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
                break;
            case Key.M:
                OnMuteClick(sender, e);
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
        _mediaPlayer.Stop();
        VlcVideoView.MediaPlayer = null;
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }
}
