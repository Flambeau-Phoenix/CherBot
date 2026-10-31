using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CherBot.Core.Platform;
using CherBot.Host;
using CherBot.Host.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace CherBot.Desktop;

/// <summary>Runs the CherBot engine inside the tray app (Local mode), bound to 127.0.0.1.</summary>
public sealed class LocalEngine : IAsyncDisposable
{
    private WebApplication? _app;

    public string Url { get; private set; } = "";
    public string Token { get; private set; } = "";
    public string DataDir { get; private set; } = "";
    public bool IsRunning => _app is not null;

    public async Task StartAsync(DesktopSettings settings, IDesktopServices desktop)
    {
        if (_app is not null) return;
        var args = new List<string>
        {
            "--CherBot:Api:Address", "127.0.0.1",
            "--CherBot:Api:Port", settings.LocalApiPort.ToString(CultureInfo.InvariantCulture),
            "--CherBot:WebSocketServer:Address", "127.0.0.1",
            "--CherBot:WebSocketServer:Port", settings.LocalWebSocketPort.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(settings.LocalDataDir)) args.AddRange(new[] { "--data-dir", settings.LocalDataDir });

        // Desktop-only sub-actions (toast, input, key press) get real implementations here.
        var app = CherBotHost.Build(args.ToArray(), b => b.Services.AddSingleton(desktop));
        await app.StartAsync();
        _app = app;
        Url = $"http://127.0.0.1:{settings.LocalApiPort}";
        Token = app.Services.GetRequiredService<ApiToken>().Value;
        DataDir = app.Services.GetRequiredService<CherBotHostOptions>().DataDir ?? "";
    }

    /// <summary>Stops the engine cleanly, which also writes the pending-queue cache.</summary>
    public async Task StopAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.StopAsync(timeout.Token);
        await app.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
