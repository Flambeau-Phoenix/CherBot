using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace CherBot.Desktop;

/// <summary>Shows the engine's web UI in WebView2, with the API token passed in the URL fragment so no login is needed.</summary>
public partial class MainWindow : Window
{
    private string _url = "";
    private string _token = "";
    private bool _webReady;

    public MainWindow() => InitializeComponent();

    /// <summary>When true, closing hides to the tray instead of exiting.</summary>
    public bool HideOnClose { get; set; } = true;

    public async Task ShowEngineAsync(string url, string token)
    {
        _url = url.TrimEnd('/');
        _token = token;
        if (!await EnsureWebViewAsync()) return;
        Web.CoreWebView2.Navigate($"{_url}/#token={Uri.EscapeDataString(_token)}");
    }

    private async Task<bool> EnsureWebViewAsync()
    {
        if (_webReady) return true;
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            var env = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CherBot", "WebView2"));
            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                // Links such as twitch.tv/activate open in the normal browser.
                e.Handled = true;
                Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
            };
            _webReady = true;
            Fallback.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowFallback("The Microsoft Edge WebView2 runtime is not installed, so the CherBot interface can't be shown in this window. You can open it in your browser instead, or install the runtime.");
            return false;
        }
        catch (Exception ex)
        {
            ShowFallback($"The CherBot interface could not be shown here ({ex.Message}). You can open it in your browser instead.");
            return false;
        }
    }

    private void ShowFallback(string message)
    {
        FallbackText.Text = message;
        Fallback.Visibility = Visibility.Visible;
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e) => OpenInBrowser(_url, _token);

    private void GetRuntime_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });

    public static void OpenInBrowser(string url, string token) =>
        Process.Start(new ProcessStartInfo($"{url.TrimEnd('/')}/#token={Uri.EscapeDataString(token)}") { UseShellExecute = true });

    protected override void OnClosing(CancelEventArgs e)
    {
        if (HideOnClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
