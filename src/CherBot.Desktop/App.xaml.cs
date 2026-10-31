using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace CherBot.Desktop;

/// <summary>
/// Tray app. Local mode hosts the engine in-process; Remote mode points at an engine elsewhere. The window shows the
/// engine's own web UI; the tray shows status and offers quick queue control.
/// </summary>
public partial class App : Application
{
    private Mutex? _single;
    private DesktopSettings _settings = new();
    private readonly LocalEngine _engine = new();
    private readonly StatusMonitor _monitor = new();
    private readonly WpfDesktopServices _desktop = new();
    private WinForms.NotifyIcon? _tray;
    private MainWindow? _window;
    private string _url = "";
    private string _token = "";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\CherBot.Desktop", out var first);
        if (!first)
        {
            MessageBox.Show("CherBot is already running in the system tray.", "CherBot");
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "CherBot error");
            ex.Handled = true;
        };

        _settings = DesktopSettings.Load();
        if (string.IsNullOrEmpty(_settings.Mode) && new ConnectionWindow(_settings).ShowDialog() != true)
        {
            Shutdown();
            return;
        }

        _desktop.ShowBalloon = (title, text) =>
        {
            Dispatcher.Invoke(() => _tray?.ShowBalloonTip(5000, title, text, WinForms.ToolTipIcon.Info));
            return Task.CompletedTask;
        };
        CreateTray();
        _monitor.HealthChanged += h => Dispatcher.Invoke(() => UpdateTray(h));
        _monitor.IntegrationChanged += (i, connected) => Dispatcher.Invoke(() =>
            _tray?.ShowBalloonTip(4000, "CherBot", $"{i.Name} {(connected ? "connected" : $"disconnected{(i.Detail is null ? "" : $": {i.Detail}")}")}",
                connected ? WinForms.ToolTipIcon.Info : WinForms.ToolTipIcon.Warning));

        await ConnectAsync();
        _monitor.Start();
        if (!e.Args.Contains("--minimized")) ShowWindow();
    }

    /// <summary>Starts (Local) or targets (Remote) the engine for the current settings.</summary>
    private async Task ConnectAsync()
    {
        try
        {
            if (_settings.Mode == DesktopSettings.LocalMode)
            {
                await _engine.StartAsync(_settings, _desktop);
                _url = _engine.Url;
                _token = _engine.Token;
            }
            else
            {
                await _engine.StopAsync();
                var profile = _settings.Active ?? throw new InvalidOperationException("No remote profile is configured.");
                _url = profile.Url;
                _token = profile.Token;
            }
            _monitor.Target(_url, _token);
            await _monitor.PollAsync();
            RebuildMenu();
            if (_window is not null) await _window.ShowEngineAsync(_url, _token);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"CherBot could not start: {ex.Message}", "CherBot");
            UpdateTray(EngineHealth.Unreachable);
        }
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _ = _window.ShowEngineAsync(_url, _token);
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    // ── tray ────────────────────────────────────────────────────────────────

    private void CreateTray()
    {
        _tray = new WinForms.NotifyIcon { Icon = TrayIcons.For(EngineHealth.Unknown), Text = "CherBot", Visible = true };
        _tray.DoubleClick += (_, _) => ShowWindow();
        RebuildMenu();
    }

    private void UpdateTray(EngineHealth health)
    {
        if (_tray is null) return;
        _tray.Icon = TrayIcons.For(health);
        var where = _settings.Mode == DesktopSettings.LocalMode ? "local" : _settings.Active?.Name ?? "remote";
        var text = health switch
        {
            EngineHealth.Healthy => $"CherBot ({where}): running",
            EngineHealth.Degraded => $"CherBot ({where}): {string.Join(", ", _monitor.Integrations.Where(i => !i.Connected && i.State is not ("disabled" or "not configured")).Select(i => $"{i.Name} {i.State}"))}",
            EngineHealth.Unreachable => $"CherBot ({where}): engine unreachable",
            _ => "CherBot",
        };
        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void RebuildMenu()
    {
        if (_tray is null) return;
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open CherBot", null, (_, _) => ShowWindow());
        menu.Items.Add("Open in browser", null, (_, _) => CherBot.Desktop.MainWindow.OpenInBrowser(_url, _token));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Pause all queues", null, async (_, _) => await QueueControl("/v1/queues/all/pause", "All queues paused"));
        menu.Items.Add("Resume all queues", null, async (_, _) => await QueueControl("/v1/queues/all/resume", "All queues resumed"));
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var profiles = new WinForms.ToolStripMenuItem("Profiles");
        var local = new WinForms.ToolStripMenuItem("Local engine (this PC)") { Checked = _settings.Mode == DesktopSettings.LocalMode };
        local.Click += async (_, _) => { _settings.Mode = DesktopSettings.LocalMode; _settings.Save(); await ConnectAsync(); };
        profiles.DropDownItems.Add(local);
        foreach (var p in _settings.Profiles)
        {
            var item = new WinForms.ToolStripMenuItem(p.ToString()) { Checked = _settings.Mode == DesktopSettings.RemoteMode && _settings.Active == p };
            item.Click += async (_, _) => { _settings.Mode = DesktopSettings.RemoteMode; _settings.ActiveProfile = p.Name; _settings.Save(); await ConnectAsync(); };
            profiles.DropDownItems.Add(item);
        }
        profiles.DropDownItems.Add(new WinForms.ToolStripSeparator());
        profiles.DropDownItems.Add("Manage…", null, async (_, _) =>
        {
            if (new ConnectionWindow(_settings).ShowDialog() == true) await ConnectAsync();
        });
        menu.Items.Add(profiles);

        var autostart = new WinForms.ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsEnabled, CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => Autostart.Set(autostart.Checked);
        menu.Items.Add(autostart);

        var localOnly = _settings.Mode == DesktopSettings.LocalMode && _engine.IsRunning;
        menu.Items.Add(new WinForms.ToolStripMenuItem("Open data folder", null, (_, _) => OpenFolder(_engine.DataDir)) { Enabled = localOnly });
        menu.Items.Add(new WinForms.ToolStripMenuItem("Open logs", null, (_, _) => OpenFolder(Path.Combine(_engine.DataDir, "logs"))) { Enabled = localOnly });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        _tray.ContextMenuStrip = menu;
    }

    private async Task QueueControl(string path, string done)
    {
        try
        {
            await _monitor.PostAsync(path);
            _tray?.ShowBalloonTip(2000, "CherBot", done, WinForms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloonTip(4000, "CherBot", $"Failed: {ex.Message}", WinForms.ToolTipIcon.Error);
        }
    }

    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private async Task ExitAsync()
    {
        if (_tray is not null) _tray.Visible = false;
        _monitor.Dispose();
        await _engine.StopAsync();
        if (_window is not null) { _window.HideOnClose = false; _window.Close(); }
        _tray?.Dispose();
        _single?.ReleaseMutex();
        Shutdown();
    }
}
