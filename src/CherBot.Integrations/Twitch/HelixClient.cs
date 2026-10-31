using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CherBot.Core;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Twitch;

public sealed class HelixException(HttpStatusCode status, string message) : Exception($"Helix {(int)status}: {message}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Minimal Twitch Helix client: user tokens per role, automatic refresh on 401, JSON in and out.</summary>
public sealed class HelixClient(IHttpClientFactory http, TwitchAuth auth, ILogger<HelixClient> log)
{
    public const string Base = "https://api.twitch.tv/helix/";

    public async Task<JsonElement> SendAsync(HttpMethod method, string path, IDictionary<string, string?>? query = null, object? body = null,
        TwitchRole role = TwitchRole.Broadcaster, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = attempt == 0 ? await auth.GetValidAsync(role, ct) : await auth.RefreshAsync(role, ct, force: true);
            if (token is null) throw new HelixException(HttpStatusCode.Unauthorized, $"Twitch {role} account is not linked");

            using var req = new HttpRequestMessage(method, BuildUrl(path, query));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            req.Headers.Add("Client-Id", auth.ClientId);
            if (body is not null)
                req.Content = new StringContent(JsonSerializer.Serialize(body, CherBotJson.Options), Encoding.UTF8, "application/json");

            using var resp = await http.CreateClient("twitch").SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                log.LogInformation("Helix 401 on {Path}; refreshing {Role} token", path, role);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                var message = text;
                try { message = JsonDocument.Parse(text).RootElement.GetProperty("message").GetString() ?? text; } catch { /* plain text */ }
                throw new HelixException(resp.StatusCode, message);
            }
            if (string.IsNullOrWhiteSpace(text)) return default;
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        throw new HelixException(HttpStatusCode.Unauthorized, "token refresh failed");
    }

    public static string BuildUrl(string path, IDictionary<string, string?>? query)
    {
        var url = Base + path.TrimStart('/');
        if (query is null || query.Count == 0) return url;
        var qs = string.Join('&', query.Where(kv => kv.Value is not null).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        return qs.Length == 0 ? url : $"{url}?{qs}";
    }

    public Task<JsonElement> GetAsync(string path, IDictionary<string, string?>? query = null, TwitchRole role = TwitchRole.Broadcaster, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, path, query, null, role, ct);

    /// <summary>Returns the first element of <c>data</c>, or null.</summary>
    public static JsonElement? First(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0 ? d[0] : null;

    public async Task<JsonElement?> GetUserByLoginAsync(string login, CancellationToken ct = default) =>
        First(await GetAsync("users", new Dictionary<string, string?> { ["login"] = login.TrimStart('@').Trim().ToLowerInvariant() }, ct: ct));

    public async Task<JsonElement?> GetUserByIdAsync(string id, CancellationToken ct = default) =>
        First(await GetAsync("users", new Dictionary<string, string?> { ["id"] = id }, ct: ct));
}
