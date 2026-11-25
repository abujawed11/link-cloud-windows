using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using LinkCloudPlayer.Models;
using LinkCloudPlayer.Services;

namespace LinkCloudPlayer.Views;

public partial class LibraryView : UserControl
{
    private readonly ApiClient _apiClient;
    private readonly MediaInfoService _mediaInfo;
    private readonly WatchProgressStore _progress;
    private readonly ObservableCollection<FileCard> _cards = [];
    private readonly ICollectionView _libraryView;
    private FileCard? _heroCard;
    private FileCard? _resumeCard;
    private bool _loadedOnce;
    private double _savedScrollOffset;

    /// <summary>(file, display title, start position in ms)</summary>
    public event Action<FileItem, string, long>? PlayRequested;
    public event Action? LoggedOut;

    public LibraryView(ApiClient apiClient, User user, MediaInfoService mediaInfo, WatchProgressStore progress)
    {
        InitializeComponent();
        _apiClient = apiClient;
        _mediaInfo = mediaInfo;
        _progress = progress;
        UserEmailText.Text = user.Email;
        AvatarText.Text = user.Email.Length > 0 ? user.Email[..1].ToUpperInvariant() : "?";

        _libraryView = new CollectionViewSource { Source = _cards }.View;
        _libraryView.Filter = MatchesSearch;
        LibraryList.ItemsSource = _libraryView;

        _mediaInfo.InfoReady += OnInfoReady;

        Loaded += async (_, _) =>
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                await RefreshAsync();
            }
        };
    }

    // ───────────────────────── Loading ─────────────────────────

    private async Task RefreshAsync()
    {
        HideError();
        try
        {
            var files = await _apiClient.GetFilesAsync();
            _cards.Clear();
            foreach (var file in files.OrderByDescending(f => f.CreatedAt))
            {
                var card = new FileCard(file) { Progress = _progress.Get(file.Id) };
                var info = _mediaInfo.GetCached(file);
                if (info is not null) ApplyInfo(card, info);
                if (card.IsPlayable && MediaInfoService.NeedsGeneration(info)) _mediaInfo.Enqueue(file);
                _cards.Add(card);
            }
            RebuildSections();
        }
        catch (Exception ex)
        {
            ShowError($"Could not load your library: {ex.Message}");
        }
    }

    private static void ApplyInfo(FileCard card, MediaInfo info)
    {
        card.Info = info;
        card.Thumbnail = MediaInfoService.LoadImage(info.ThumbnailPath);
    }

    private void OnInfoReady(int fileId, MediaInfo info)
    {
        var card = _cards.FirstOrDefault(c => c.File.Id == fileId);
        if (card is null) return;
        ApplyInfo(card, info);
        if (ReferenceEquals(card, _heroCard)) ShowHero(card);
    }

    /// <summary>Called by MainWindow when the user comes back from the player.</summary>
    public void OnReturnedFromPlayer()
    {
        foreach (var card in _cards) card.Progress = _progress.Get(card.File.Id);
        RebuildSections();
        // Restore the scroll position once layout has run again.
        Dispatcher.BeginInvoke(() => MainScroll.ScrollToVerticalOffset(_savedScrollOffset),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RebuildSections()
    {
        var searching = SearchBox.Text.Trim().Length > 0;

        var continueCards = _cards
            .Where(c => c.HasProgress)
            .OrderByDescending(c => c.Progress!.UpdatedAt)
            .ToList();
        ContinueList.ItemsSource = continueCards;
        ContinueSection.Visibility = !searching && continueCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _heroCard = continueCards.FirstOrDefault() ?? _cards.FirstOrDefault(c => c.File.IsVideo);
        if (!searching && _heroCard is not null) ShowHero(_heroCard);
        else Hero.Visibility = Visibility.Collapsed;

        // Leave room for the floating nav bar when there is no hero above.
        Sections.Margin = new Thickness(48, Hero.Visibility == Visibility.Visible ? 8 : 90, 30, 40);

        _libraryView.Refresh();
        var visibleCount = _libraryView.Cast<object>().Count();
        LibraryHeader.Text = searching ? $"Results for \"{SearchBox.Text.Trim()}\"" : "My Library";
        EmptyText.Text = searching ? "No titles match your search." : "Your library is empty. Files you download on the server will show up here.";
        EmptyText.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowHero(FileCard card)
    {
        Hero.Visibility = Visibility.Visible;
        HeroImage.ImageSource = card.Thumbnail;
        HeroTitle.Text = card.Title;
        HeroMeta.Text = card.MetaLine;
        HeroLanguages.Text = card.HasLanguages ? "Audio: " + card.LanguagesLabel : "";
        HeroLanguages.Visibility = card.HasLanguages ? Visibility.Visible : Visibility.Collapsed;

        var resumable = card.HasProgress;
        HeroBadge.Text = resumable ? "CONTINUE WATCHING" : "RECENTLY ADDED";
        HeroPlayText.Text = resumable ? "Resume" : "Play";
        HeroRestartButton.Visibility = resumable ? Visibility.Visible : Visibility.Collapsed;
        HeroProgress.Visibility = resumable ? Visibility.Visible : Visibility.Collapsed;
        if (resumable)
        {
            HeroProgressDone.Width = card.ProgressDone;
            HeroProgressLeft.Width = card.ProgressLeft;
            HeroRemaining.Text = card.RemainingLabel;
        }
    }

    // ───────────────────────── Search ─────────────────────────

    private bool MatchesSearch(object obj)
    {
        if (obj is not FileCard c) return false;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return true;
        return c.File.OriginalName.Contains(query, StringComparison.OrdinalIgnoreCase)
               || c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
               || c.Languages.Any(l => l.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildSections();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) SearchBox.Clear();
    }

    // ───────────────────────── Playing ─────────────────────────

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileCard card) return;
        if (!card.IsPlayable)
        {
            ShowError($"\"{card.File.OriginalName}\" doesn't look like a playable video/audio file.");
            return;
        }
        if (card.HasProgress) ShowResumePrompt(card);
        else Play(card, 0);
    }

    private void Play(FileCard card, long startMs)
    {
        HideError();
        _savedScrollOffset = MainScroll.VerticalOffset;
        PlayRequested?.Invoke(card.File, card.DisplayTitle, startMs);
    }

    private void OnHeroPlay(object sender, RoutedEventArgs e)
    {
        if (_heroCard is not null) Play(_heroCard, _heroCard.ResumePositionMs);
    }

    private void OnHeroRestart(object sender, RoutedEventArgs e)
    {
        if (_heroCard is not null) Play(_heroCard, 0);
    }

    private void OnMenuPlayFromStart(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileCard card && card.IsPlayable) Play(card, 0);
    }

    private void OnMenuRemoveProgress(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileCard card) return;
        _progress.Remove(card.File.Id);
        card.Progress = null;
        RebuildSections();
    }

    // ───────────────────────── Resume prompt ─────────────────────────

    private void ShowResumePrompt(FileCard card)
    {
        _resumeCard = card;
        ResumeImage.ImageSource = card.Thumbnail;
        ResumeTitle.Text = card.DisplayTitle;
        ResumeMeta.Text = card.RemainingLabel.Length > 0 ? $"{card.MetaLine}  ·  {card.RemainingLabel}" : card.MetaLine;
        ResumeButtonText.Text = card.ResumeLabel;
        ResumeOverlay.Visibility = Visibility.Visible;
    }

    private void HideResumePrompt()
    {
        ResumeOverlay.Visibility = Visibility.Collapsed;
        _resumeCard = null;
    }

    private void OnResumeContinue(object sender, RoutedEventArgs e)
    {
        var card = _resumeCard;
        HideResumePrompt();
        if (card is not null) Play(card, card.ResumePositionMs);
    }

    private void OnResumeRestart(object sender, RoutedEventArgs e)
    {
        var card = _resumeCard;
        HideResumePrompt();
        if (card is not null) Play(card, 0);
    }

    private void OnResumeOverlayBackgroundClick(object sender, MouseButtonEventArgs e) => HideResumePrompt();
    private void OnResumeDialogClick(object sender, MouseButtonEventArgs e) => e.Handled = true; // don't close when clicking inside

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ResumeOverlay.Visibility == Visibility.Visible)
        {
            HideResumePrompt();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    // ───────────────────────── Chrome ─────────────────────────

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        NavBackground.Opacity = Math.Clamp(MainScroll.VerticalOffset / 200.0, 0, 1);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        _mediaInfo.InfoReady -= OnInfoReady;
        LoggedOut?.Invoke();
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void HideError() => StatusText.Visibility = Visibility.Collapsed;
}
