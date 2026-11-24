using System.Windows;
using System.Windows.Controls;
using LinkCloudPlayer.Models;
using LinkCloudPlayer.Services;

namespace LinkCloudPlayer.Views;

public partial class LoginView : UserControl
{
    public event Action<ApiClient, User>? LoggedIn;

    public LoginView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var saved = CredentialStore.Load();
        if (saved is null) return;

        ServerUrlBox.Text = saved.ServerUrl;
        EmailBox.Text = saved.Email;
        PasswordBox.Password = saved.Password;
        RememberBox.IsChecked = true;

        // Try a silent auto-login with the saved session; fall back to the
        // filled-in form (user just clicks Log in) if the server is down or
        // the password changed.
        await TryLogin(auto: true);
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e) => await TryLogin(auto: false);

    private async Task TryLogin(bool auto)
    {
        var serverUrl = ServerUrlBox.Text.Trim().TrimEnd('/');
        var email = EmailBox.Text.Trim();
        var password = PasswordBox.Password;

        if (serverUrl.Length == 0 || email.Length == 0 || password.Length == 0)
        {
            if (!auto) ShowError("Server URL, email and password are all required.");
            return;
        }

        SetBusy(true);
        try
        {
            var apiClient = new ApiClient(serverUrl);
            var user = await apiClient.LoginAsync(email, password);

            if (RememberBox.IsChecked == true)
            {
                CredentialStore.Save(new SavedSession { ServerUrl = serverUrl, Email = email, Password = password });
            }
            else
            {
                CredentialStore.Clear();
            }

            LoggedIn?.Invoke(apiClient, user);
        }
        catch (ApiException ex)
        {
            if (!auto) ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            if (!auto) ShowError($"Could not reach {serverUrl}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        LoginButton.IsEnabled = !busy;
        LoginButton.Content = busy ? "Logging in..." : "Log in";
        if (busy) ErrorText.Visibility = Visibility.Collapsed;
    }
}
