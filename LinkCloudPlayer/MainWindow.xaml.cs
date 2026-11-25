using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LinkCloudPlayer.Models;
using LinkCloudPlayer.Services;
using LinkCloudPlayer.Views;

namespace LinkCloudPlayer;

/// <summary>
/// The app shell: swaps between the login screen, the library, and the
/// player - all inside this one window.
/// </summary>
public partial class MainWindow : Window
{
    private ApiClient? _apiClient;
    private LibraryView? _library;
    private MediaInfoService? _mediaInfo;
    private readonly WatchProgressStore _progress = new();

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Closed += (_, _) => _mediaInfo?.Dispose();
        ShowLogin();
    }

    private void ShowLogin()
    {
        var login = new LoginView();
        login.LoggedIn += (apiClient, user) => ShowLibrary(apiClient, user);
        RootContent.Content = login;
    }

    private void ShowLibrary(ApiClient apiClient, User user)
    {
        _apiClient = apiClient;
        _mediaInfo?.Dispose();
        _mediaInfo = new MediaInfoService(apiClient.BuildStreamUrl);

        _library = new LibraryView(apiClient, user, _mediaInfo, _progress);
        _library.PlayRequested += OpenPlayer;
        _library.LoggedOut += () =>
        {
            CredentialStore.Clear();
            _mediaInfo?.Dispose();
            _mediaInfo = null;
            _library = null;
            ShowLogin();
        };
        RootContent.Content = _library;
    }

    private void OpenPlayer(FileItem file, string title, long startMs)
    {
        if (_apiClient is null) return;
        if (_mediaInfo is not null) _mediaInfo.Paused = true; // keep bandwidth for playback

        var player = new PlayerView(file, title, _apiClient.BuildStreamUrl(file.Id), startMs, _progress);
        player.CloseRequested += () =>
        {
            player.Shutdown(); // saves progress before the library re-reads it
            RootContent.Content = _library;
            if (_mediaInfo is not null) _mediaInfo.Paused = false;
            _library?.OnReturnedFromPlayer();
        };
        RootContent.Content = player;
        Title = $"{title} - Link Cloud Player";
        player.Unloaded += (_, _) => Title = "Link Cloud Player";
    }

    // Windows 10 (20H1+) / 11: dark caption bar so it matches the app.
    private void UseDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var enabled = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        }
        catch { /* purely cosmetic */ }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
