using System.Windows;
using LinkCloudPlayer.Models;
using LinkCloudPlayer.Services;
using LinkCloudPlayer.Views;

namespace LinkCloudPlayer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
        var library = new LibraryView(apiClient, user);
        library.LoggedOut += () =>
        {
            CredentialStore.Clear();
            ShowLogin();
        };
        RootContent.Content = library;
    }
}
