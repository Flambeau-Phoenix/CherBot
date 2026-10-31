using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Events;
using CherBot.Core.Platform;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Twitch;

public enum TwitchRole { Broadcaster, Bot }

public sealed record TwitchToken(
    string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt,
    string Login, string UserId, string DisplayName, string[] Scopes, string? BroadcasterType = null)
{
    public bool ExpiresSoon => ExpiresAt - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(5);
}

public sealed record DeviceFlowState(
    TwitchRole Role, string UserCode, string VerificationUri, DateTimeOffset ExpiresAt, string Status, string? Error = null, string? Login = null);

/// <summary>
/// Twitch OAuth for both accounts: device code flow (default, works on headless machines), optional authorization-code flow,
/// refresh and validation. Tokens are kept in the encrypted secret store.
/// </summary>
public sealed class TwitchAuth
{
    private const string IdBase = "https://id.twitch.tv/oauth2";
    private readonly IHttpClientFactory _http;
    private readonly ISecretStore _secrets;
    private readonly TwitchOptions _options;
    private readonly EventBus _bus;
    private readonly ILogger<TwitchAuth> _log;
    private readonly ConcurrentDictionary<TwitchRole, DeviceFlowState> _flows = new();
    private readonly ConcurrentDictionary<TwitchRole, SemaphoreSlim> _refreshLocks = new();
    private readonly ConcurrentDictionary<string, TwitchRole> _codeStates = new();

    public TwitchAuth(IHttpClientFactory http, ISecretStore secrets, TwitchOptions options, EventBus bus, ILogger<TwitchAuth> log)
    {
        _http = http;
        _secrets = secrets;
        _options = options;
        _bus = bus;
        _log = log;
    }

    /// <summary>Raised after a token is stored or removed.</summary>
    public event Action<TwitchRole>? TokenChanged;

    public string? ClientId => string.IsNullOrWhiteSpace(_options.ClientId) ? null : _options.ClientId;

    private static string Key(TwitchRole role) => $"twitch.token.{role.ToString().ToLowerInvariant()}";

    public TwitchToken? Get(TwitchRole role)
    {
        var json = _secrets.Get(Key(role));
        return json is null ? null : JsonSerializer.Deserialize<TwitchToken>(json, CherBotJson.Options);
    }

    public void Save(TwitchRole role, TwitchToken token)
    {
        _secrets.Set(Key(role), JsonSerializer.Serialize(token, CherBotJson.Options));
        TokenChanged?.Invoke(role);
    }

    public void Logout(TwitchRole role)
    {
        _secrets.Delete(Key(role));
        TokenChanged?.Invoke(role);
    }

    public DeviceFlowState? Flow(TwitchRole role) => _flows.GetValueOrDefault(role);

    /// <summary>Returns a token that is valid for at least five more minutes, refreshing when needed. Null when not linked.</summary>
    public async Task<TwitchToken?> GetValidAsync(TwitchRole role, CancellationToken ct)
    {
        var token = Get(role);
        if (token is null || !token.ExpiresSoon) return token;
        return await RefreshAsync(role, ct) ?? token;
    }

    /// <param name="force">Refresh even if the token looks valid (for example after a 401).</param>
    public async Task<TwitchToken?> RefreshAsync(TwitchRole role, CancellationToken ct, bool force = false)
    {
        var gate = _refreshLocks.GetOrAdd(role, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var token = Get(role);
            if (token?.RefreshToken is null || ClientId is null) return null;
            if (!force && !token.ExpiresSoon) return token;

            var form = new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = token.RefreshToken,
            };
            if (!string.IsNullOrWhiteSpace(_options.ClientSecret)) form["client_secret"] = _options.ClientSecret;
            using var resp = await _http.CreateClient("twitch").PostAsync($"{IdBase}/token", new FormUrlEncodedContent(form), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Twitch {Role} token refresh failed ({Status}): {Body}", role, (int)resp.StatusCode, body);
                return null;
            }
            var refreshed = await CompleteAsync(role, JsonDocument.Parse(body).RootElement, ct);
            _log.LogInformation("Twitch {Role} token refreshed for {Login}", role, refreshed.Login);
            return refreshed;
        }
        finally
        {
            gate.Release();
        }
    }

    // ── device code flow ───────────────────────────────────────────────────

    public async Task<DeviceFlowState> StartDeviceFlowAsync(TwitchRole role, CancellationToken ct)
    {
        if (ClientId is null) throw new InvalidOperationException("Set CherBot:Twitch:ClientId to your Twitch application's client id first.");
        var scopes = string.Join(' ', role == TwitchRole.Broadcaster ? _options.BroadcasterScopes : _options.BotScopes);
        using var resp = await _http.CreateClient("twitch").PostAsync($"{IdBase}/device",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = ClientId, ["scopes"] = scopes }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Twitch device authorization failed ({(int)resp.StatusCode}): {body}");

        var root = JsonDocument.Parse(body).RootElement;
        var deviceCode = root.GetProperty("device_code").GetString()!;
        var interval = root.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5;
        var state = new DeviceFlowState(role, root.GetProperty("user_code").GetString()!, root.GetProperty("verification_uri").GetString()!,
            DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()), "pending");
        _flows[role] = state;
        _ = Task.Run(() => PollDeviceAsync(role, deviceCode, scopes, interval, state.ExpiresAt), CancellationToken.None);
        return state;
    }

    private async Task PollDeviceAsync(TwitchRole role, string deviceCode, string scopes, int interval, DateTimeOffset expiresAt)
    {
        var client = _http.CreateClient("twitch");
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, interval)));
            try
            {
                using var resp = await client.PostAsync($"{IdBase}/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId!,
                    ["scopes"] = scopes,
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }));
                var body = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                {
                    var token = await CompleteAsync(role, JsonDocument.Parse(body).RootElement, CancellationToken.None);
                    _flows[role] = _flows[role] with { Status = "complete", Login = token.Login };
                    _log.LogInformation("Twitch {Role} linked as {Login}", role, token.Login);
                    return;
                }
                if (body.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase)) continue;
                if (body.Contains("slow_down", StringComparison.OrdinalIgnoreCase)) { interval += 5; continue; }
                _flows[role] = _flows[role] with { Status = "failed", Error = body };
                _log.LogWarning("Twitch {Role} device flow failed: {Body}", role, body);
                return;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Device flow poll error");
            }
        }
        _flows[role] = _flows[role] with { Status = "expired" };
    }

    // ── authorization code flow (optional) ─────────────────────────────────

    public string BuildAuthorizeUrl(TwitchRole role, string redirectUri)
    {
        if (ClientId is null) throw new InvalidOperationException("Set CherBot:Twitch:ClientId first.");
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _codeStates[state] = role;
        var scopes = string.Join(' ', role == TwitchRole.Broadcaster ? _options.BroadcasterScopes : _options.BotScopes);
        return $"{IdBase}/authorize?response_type=code&client_id={Uri.EscapeDataString(ClientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&scope={Uri.EscapeDataString(scopes)}&state={state}&force_verify=true";
    }

    public async Task<TwitchToken> ExchangeCodeAsync(string state, string code, string redirectUri, CancellationToken ct)
    {
        if (!_codeStates.TryRemove(state, out var role)) throw new InvalidOperationException("Unknown or expired OAuth state.");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret)) throw new InvalidOperationException("The authorization-code flow needs CherBot:Twitch:ClientSecret.");
        using var resp = await _http.CreateClient("twitch").PostAsync($"{IdBase}/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId!,
            ["client_secret"] = _options.ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Code exchange failed ({(int)resp.StatusCode}): {body}");
        return await CompleteAsync(role, JsonDocument.Parse(body).RootElement, ct);
    }

    /// <summary>Validates a fresh token response, looks up the user, stores it and announces it.</summary>
    private async Task<TwitchToken> CompleteAsync(TwitchRole role, JsonElement tokenResponse, CancellationToken ct)
    {
        var access = tokenResponse.GetProperty("access_token").GetString()!;
        var refresh = tokenResponse.TryGetProperty("refresh_token", out var r) ? r.GetString() : Get(role)?.RefreshToken;
        var expiresIn = tokenResponse.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

        var client = _http.CreateClient("twitch");
        using var validateReq = new HttpRequestMessage(HttpMethod.Get, $"{IdBase}/validate");
        validateReq.Headers.Authorization = new AuthenticationHeaderValue("OAuth", access);
        using var validate = await client.SendAsync(validateReq, ct);
        var v = JsonDocument.Parse(await validate.Content.ReadAsStringAsync(ct)).RootElement;
        var login = v.GetProperty("login").GetString()!;
        var userId = v.GetProperty("user_id").GetString()!;
        var scopes = v.TryGetProperty("scopes", out var s) && s.ValueKind == JsonValueKind.Array ? s.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];

        using var userReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/users?id={userId}");
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        userReq.Headers.Add("Client-Id", ClientId);
        using var userResp = await client.SendAsync(userReq, ct);
        var display = login;
        string? broadcasterType = null;
        if (userResp.IsSuccessStatusCode)
        {
            var u = JsonDocument.Parse(await userResp.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("data")[0];
            display = u.GetProperty("display_name").GetString() ?? login;
            broadcasterType = u.TryGetProperty("broadcaster_type", out var bt) ? bt.GetString() : null;
        }

        var token = new TwitchToken(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn), login, userId, display, scopes, broadcasterType);
        Save(role, token);
        if (role == TwitchRole.Broadcaster)
            _bus.Publish(BotEvent.FromTypeName("TwitchBroadcasterAuthenticated", new Dictionary<string, object?>
            {
                ["broadcastUser"] = display, ["broadcastUserName"] = login, ["broadcastUserId"] = userId,
            }));
        return token;
    }
}
