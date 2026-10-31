using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;

namespace CherBot.Desktop;

/// <summary>Chooses Local or Remote mode and manages remote connection profiles.</summary>
public partial class ConnectionWindow : Window
{
    private readonly DesktopSettings _settings;

    public ConnectionWindow(DesktopSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        LocalPort.Text = settings.LocalApiPort.ToString(CultureInfo.InvariantCulture);
        LocalWsPort.Text = settings.LocalWebSocketPort.ToString(CultureInfo.InvariantCulture);
        LocalDataDir.Text = settings.LocalDataDir ?? "";
        RefreshProfiles(settings.Active);
        if (settings.Mode == DesktopSettings.RemoteMode) RemoteRadio.IsChecked = true;
        else LocalRadio.IsChecked = true;
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (LocalPanel is null || RemotePanel is null) return;
        LocalPanel.IsEnabled = LocalRadio.IsChecked == true;
        RemotePanel.IsEnabled = RemoteRadio.IsChecked == true;
    }

    private void RefreshProfiles(RemoteProfile? select)
    {
        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = _settings.Profiles;
        ProfileList.SelectedItem = select ?? _settings.Profiles.FirstOrDefault();
        if (ProfileList.SelectedItem is null) NewProfile_Click(this, new RoutedEventArgs());
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileList.SelectedItem is not RemoteProfile p) return;
        ProfileName.Text = p.Name;
        ProfileUrl.Text = p.Url;
        ProfileToken.Password = p.Token;
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        ProfileList.SelectedItem = null;
        ProfileName.Text = _settings.Profiles.Count == 0 ? "Server" : $"Engine {_settings.Profiles.Count + 1}";
        ProfileUrl.Text = "http://192.168.1.50:8091";
        ProfileToken.Password = "";
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not RemoteProfile p) return;
        _settings.Profiles.Remove(p);
        _settings.Save();
        RefreshProfiles(null);
    }

    private RemoteProfile? SaveCurrentProfile()
    {
        var name = ProfileName.Text.Trim();
        if (name.Length == 0 || !Uri.TryCreate(ProfileUrl.Text.Trim(), UriKind.Absolute, out var uri))
        {
            Error.Text = "A remote profile needs a name and a full URL such as http://192.168.1.50:8091.";
            return null;
        }
        var profile = _settings.Profiles.FirstOrDefault(p => p.Name == name);
        if (profile is null)
        {
            profile = new RemoteProfile { Name = name };
            _settings.Profiles.Add(profile);
        }
        profile.Url = uri.ToString().TrimEnd('/');
        profile.Token = ProfileToken.Password.Trim();
        _settings.Save();
        Error.Text = "";
        return profile;
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SaveCurrentProfile() is { } p) RefreshProfiles(p);
    }

    private async void TestProfile_Click(object sender, RoutedEventArgs e)
    {
        TestResult.Text = "Testing…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ProfileUrl.Text.Trim().TrimEnd('/')}/v1/integrations");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ProfileToken.Password.Trim());
            using var resp = await http.SendAsync(req);
            TestResult.Text = resp.IsSuccessStatusCode ? "Connected" : resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "Token rejected" : $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            TestResult.Text = $"Unreachable: {ex.Message}";
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (LocalRadio.IsChecked == true)
        {
            if (!int.TryParse(LocalPort.Text, out var port) || port is < 1 or > 65535 || !int.TryParse(LocalWsPort.Text, out var ws) || ws is < 1 or > 65535 || ws == port)
            {
                Error.Text = "Enter two different port numbers between 1 and 65535.";
                return;
            }
            _settings.Mode = DesktopSettings.LocalMode;
            _settings.LocalApiPort = port;
            _settings.LocalWebSocketPort = ws;
            _settings.LocalDataDir = string.IsNullOrWhiteSpace(LocalDataDir.Text) ? null : LocalDataDir.Text.Trim();
        }
        else
        {
            if (SaveCurrentProfile() is not { } p) return;
            if (string.IsNullOrEmpty(p.Token))
            {
                Error.Text = "Paste the API token for this engine.";
                return;
            }
            _settings.Mode = DesktopSettings.RemoteMode;
            _settings.ActiveProfile = p.Name;
        }
        _settings.Save();
        DialogResult = true;
    }
}
