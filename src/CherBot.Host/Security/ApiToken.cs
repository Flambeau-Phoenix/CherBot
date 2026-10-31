using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace CherBot.Host.Security;

/// <summary>The bearer token protecting the management API and web UI.</summary>
public sealed class ApiToken
{
    public const string FileName = "api-token";

    public ApiToken(string value) => Value = value;

    public string Value { get; }

    public static ApiToken Load(string dataDir, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return new ApiToken(configured.Trim());
        var path = Path.Combine(dataDir, FileName);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 16) return new ApiToken(existing);
        }
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        DataDirectory.WritePrivate(path, token);
        return new ApiToken(token);
    }

    public bool Matches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var a = Encoding.UTF8.GetBytes(candidate);
        var b = Encoding.UTF8.GetBytes(Value);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Accepts "Authorization: Bearer", the X-CherBot-Token header, or ?token= (for EventSource and WebSocket clients).</summary>
    public bool IsAuthorized(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && Matches(auth[7..].Trim())) return true;
        if (Matches(ctx.Request.Headers["X-CherBot-Token"].ToString())) return true;
        return Matches(ctx.Request.Query["token"].ToString());
    }
}
