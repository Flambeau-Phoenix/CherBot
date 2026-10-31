using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CherBot.Desktop;

public sealed record IntegrationState(string Name, string State, bool Connected, string? Detail);

/// <summary>Polls the engine's /health and reports overall health plus integration connect/disconnect changes.</summary>
public sealed class StatusMonitor : IDisposable
{
    private static readonly HashSet<string> Quiet = new(StringComparer.OrdinalIgnoreCase) { "disabled", "not configured" };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly CancellationTokenSource _cts = new();
    private Dictionary<string, IntegrationState> _last = new();

    public string Url { get; private set; } = "";
    public string Token { get; private set; } = "";
    public EngineHealth Health { get; private set; } = EngineHealth.Unknown;
    public IReadOnlyList<IntegrationState> Integrations { get; private set; } = Array.Empty<IntegrationState>();

    public event Action<EngineHealth>? HealthChanged;
    public event Action<IntegrationState, bool>? IntegrationChanged;

    public void Target(string url, string token)
    {
        Url = url.TrimEnd('/');
        Token = token;
        _last = new Dictionary<string, IntegrationState>();
    }

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await PollAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task PollAsync()
    {
        EngineHealth health;
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{Url}/health", _cts.Token));
            var list = doc.RootElement.GetProperty("integrations").EnumerateArray().Select(i => new IntegrationState(
                i.GetProperty("name").GetString() ?? "",
                i.GetProperty("state").GetString() ?? "",
                i.GetProperty("connected").GetBoolean(),
                i.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null)).ToList();
            Integrations = list;
            health = list.Any(i => !i.Connected && !Quiet.Contains(i.State)) ? EngineHealth.Degraded : EngineHealth.Healthy;

            foreach (var i in list)
            {
                if (_last.TryGetValue(i.Name, out var before) && before.Connected != i.Connected) IntegrationChanged?.Invoke(i, i.Connected);
                _last[i.Name] = i;
            }
        }
        catch (Exception) when (!_cts.IsCancellationRequested)
        {
            health = EngineHealth.Unreachable;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (health != Health)
        {
            Health = health;
            HealthChanged?.Invoke(health);
        }
    }

    /// <summary>Authenticated POST to the management API (queue control from the tray menu).</summary>
    public async Task PostAsync(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Url}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }
}
