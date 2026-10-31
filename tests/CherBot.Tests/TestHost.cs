using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using CherBot.Host;
using CherBot.Host.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace CherBot.Tests;

/// <summary>Runs the full CherBot host on free local ports with a throwaway data directory.</summary>
public sealed class TestHost : IAsyncDisposable
{
    public WebApplication App { get; }
    public string Dir { get; }
    public int ApiPort { get; } = FreeTcpPort();
    public int WsPort { get; } = FreeTcpPort();
    public int HttpPort { get; } = FreeTcpPort();
    public int UdpPort { get; } = FreeUdpPort();
    public HttpClient Api { get; }
    public string Token => App.Services.GetRequiredService<ApiToken>().Value;

    public TestHost(string? wsPassword = null, Action<WebApplicationBuilder>? configure = null, params string[] extraArgs)
    {
        Dir = Path.Combine(Path.GetTempPath(), "cherbot-tests", "host-" + Guid.NewGuid().ToString("N"));
        var args = new List<string>
        {
            "--data-dir", Dir,
            "--CherBot:Api:Port", ApiPort.ToString(),
            "--CherBot:WebSocketServer:Port", WsPort.ToString(),
            "--CherBot:HttpServer:Enabled", "true",
            "--CherBot:HttpServer:Port", HttpPort.ToString(),
            "--CherBot:UdpServer:Enabled", "true",
            "--CherBot:UdpServer:Port", UdpPort.ToString(),
            "--CherBot:Engine:SoundMode", "none",
            "--CherBot:InstanceName", "test-instance",
        };
        if (wsPassword is not null) args.AddRange(["--CherBot:WebSocketServer:Password", wsPassword]);
        args.AddRange(extraArgs);
        App = CherBotHost.Build([.. args], configure);
        App.StartAsync().GetAwaiter().GetResult();
        Api = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ApiPort}") };
        Api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public T Get<T>() where T : notnull => App.Services.GetRequiredService<T>();

    public static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static int FreeUdpPort()
    {
        using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    public async ValueTask DisposeAsync()
    {
        Api.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch { /* temp */ }
    }
}
