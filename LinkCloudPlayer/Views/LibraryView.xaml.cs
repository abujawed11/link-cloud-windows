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
    private readonly ObservableCollection<FileItem> _files = [];
    private readonly ICollectionView _view;

    public event Action? LoggedOut;

    public LibraryView(ApiClient apiClient, User user)
    {
        InitializeComponent();
        _apiClient = apiClient;
        UserEmailText.Text = user.Email;

        _view = CollectionViewSource.GetDefaultView(_files);
        _view.Filter = MatchesSearch;
        FileListView.ItemsSource = _view;

        Loaded += async (_, _) => await RefreshAsync();
    }

    private bool MatchesSearch(object obj)
    {
        if (obj is not FileItem f) return false;
        var query = SearchBox.Text.Trim();
        return query.Length == 0 || f.OriginalName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private async Task RefreshAsync()
    {
        HideError();
        try
        {
            var files = await _apiClient.GetFilesAsync();
            _files.Clear();
            foreach (var f in files.OrderByDescending(f => f.CreatedAt)) _files.Add(f);
        }
        catch (Exception ex)
        {
            ShowError($"Could not load your library: {ex.Message}");
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    private void OnLogoutClick(object sender, RoutedEventArgs e) => LoggedOut?.Invoke();

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileListView.SelectedItem is not FileItem file) return;
        if (!file.IsVideo && !file.IsAudio)
        {
            ShowError($"\"{file.OriginalName}\" doesn't look like a playable video/audio file.");
            return;
        }

        var streamUrl = _apiClient.BuildStreamUrl(file.Id);
        var player = new PlayerWindow(file.OriginalName, streamUrl) { Owner = Window.GetWindow(this) };
        player.Show();
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void HideError() => StatusText.Visibility = Visibility.Collapsed;
}
