using Microsoft.AspNetCore.Http;

namespace CherBot.Compat.Protocol;

/// <summary>
/// Blocks cross-site requests from web pages to the unauthenticated Streamer.bot-compatible servers.
/// Requests without an Origin header (native clients, scripts, the official Streamer.bot client libraries on Node)
/// are always allowed. Browsers always send Origin on WebSocket upgrades and cross-site POSTs, so a hostile web page
/// is rejected unless its origin is loopback or listed in <c>CherBot:AllowedOrigins</c> ("*" allows everything).
/// </summary>
public static class OriginPolicy
{
    public static bool IsAllowed(HttpRequest request, IReadOnlyCollection<string> allowed)
    {
        var origin = request.Headers.Origin.ToString();
        return IsAllowed(origin, allowed);
    }

    public static bool IsAllowed(string? origin, IReadOnlyCollection<string> allowed)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        foreach (var a in allowed)
        {
            var entry = a.Trim().TrimEnd('/');
            if (entry == "*" || string.Equals(entry, origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return true;
        }
        // "null" (file:// pages and sandboxed frames) is never implicitly trusted.
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }
}
