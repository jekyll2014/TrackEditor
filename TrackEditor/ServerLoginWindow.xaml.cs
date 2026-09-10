using System.Windows;

using TrackEditor.Services;

namespace TrackEditor;

public partial class ServerLoginWindow : Window
{
    readonly ServerTrackService _svc;

    public ServerLoginWindow(ServerTrackService svc, string? currentUrl, string? currentEmail)
    {
        InitializeComponent();
        _svc = svc;
        TxtUrl.Text = currentUrl ?? "";
        TxtEmail.Text = currentEmail ?? "";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Focus the first empty field
        if (string.IsNullOrWhiteSpace(TxtUrl.Text)) TxtUrl.Focus();
        else if (string.IsNullOrWhiteSpace(TxtEmail.Text)) TxtEmail.Focus();
        else TxtPassword.Focus();
    }

    private void Register_Changed(object sender, RoutedEventArgs e)
    {
        BtnOk.Content = ChkRegister.IsChecked == true ? "Register" : "Login";
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        TxtError.Text = "";
        string url = TxtUrl.Text.Trim();
        string email = TxtEmail.Text.Trim();
        string password = TxtPassword.Password;

        if (string.IsNullOrWhiteSpace(url)) { TxtError.Text = "Enter the server URL."; return; }
        if (string.IsNullOrWhiteSpace(email)) { TxtError.Text = "Enter your email."; return; }
        if (string.IsNullOrWhiteSpace(password)) { TxtError.Text = "Enter your password."; return; }

        BtnOk.IsEnabled = false;
        try
        {
            string? err = ChkRegister.IsChecked == true
                ? await _svc.RegisterAsync(url, email, password)
                : await _svc.LoginAsync(url, email, password);

            if (err is null) DialogResult = true;
            else TxtError.Text = err;
        }
        finally { BtnOk.IsEnabled = true; }
    }
}
