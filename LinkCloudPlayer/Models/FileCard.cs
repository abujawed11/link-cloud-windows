using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using LinkCloudPlayer.Services;

namespace LinkCloudPlayer.Models;

/// <summary>View model for one tile in the library.</summary>
public class FileCard : INotifyPropertyChanged
{
    private MediaInfo? _info;
    private ImageSource? _thumbnail;
    private WatchProgress? _progress;

    public FileCard(FileItem file)
    {
        File = file;
        var parsed = TitleParser.Parse(file.OriginalName);
        Title = parsed.Title;
        Year = parsed.Year;
        Quality = parsed.Quality;
        FilenameLanguages = parsed.Languages;
    }

    public FileItem File { get; }
    public string Title { get; }
    public int? Year { get; }
    public string? Quality { get; }
    public IReadOnlyList<string> FilenameLanguages { get; }

    public string DisplayTitle => Year is int y ? $"{Title} ({y})" : Title;
    public bool IsPlayable => File.IsVideo || File.IsAudio;

    public MediaInfo? Info
    {
        get => _info;
        set { _info = value; RaiseAll(); }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; RaiseAll(); }
    }

    public WatchProgress? Progress
    {
        get => _progress;
        set { _progress = value; RaiseAll(); }
    }

    public bool HasThumbnail => _thumbnail is not null;
    public bool HasProgress => _progress?.IsResumable == true;

    public long DurationMs => _info?.DurationMs > 0 ? _info.DurationMs : _progress?.DurationMs ?? 0;
    public string? DurationLabel => DurationMs > 0 ? FormatDuration(DurationMs) : null;

    public IReadOnlyList<string> Languages =>
        _info?.AudioLanguages is { Count: > 0 } langs ? langs : FilenameLanguages;
    public string LanguagesLabel => string.Join("  ·  ", Languages);
    public bool HasLanguages => Languages.Count > 0;

    public string MetaLine => string.Join("  ·  ", new[]
    {
        Year?.ToString(), Quality, DurationLabel, File.SizeLabel,
    }.Where(s => !string.IsNullOrEmpty(s)));

    public GridLength ProgressDone => new(HasProgress ? _progress!.Fraction : 0, GridUnitType.Star);
    public GridLength ProgressLeft => new(HasProgress ? 1 - _progress!.Fraction : 1, GridUnitType.Star);

    public long ResumePositionMs => HasProgress ? _progress!.PositionMs : 0;
    public string ResumeLabel => HasProgress ? $"Resume from {FormatClock(_progress!.PositionMs)}" : "";
    public string RemainingLabel => HasProgress && _progress!.DurationMs > 0
        ? $"{FormatDuration(_progress.DurationMs - _progress.PositionMs)} left"
        : "";

    public static string FormatDuration(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(ms, 0));
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m";
        return $"{ts.Seconds}s";
    }

    public static string FormatClock(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(ms, 0));
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
