namespace CherBot.Core.Platform;

public sealed record BroadcasterAccount(
    string Platform, bool Connected,
    string? BroadcasterLogin, string? BroadcasterName, string? BroadcasterId,
    string? BotLogin, string? BotName, string? BotId,
    bool IsAffiliate = false, bool IsPartner = false);

public sealed record ActiveViewer(string Platform, string Id, string Login, string Display, string Role, bool Subscribed, DateTimeOffset LastActive);

/// <summary>Reports the linked broadcaster/bot accounts for a platform (GetBroadcaster).</summary>
public interface IBroadcasterInfo
{
    BroadcasterAccount GetAccount();
}

/// <summary>Sends raw text to every connected Streamer.bot-compatible WebSocket client (CPH.WebsocketBroadcast*).</summary>
public interface IWebSocketBroadcaster
{
    Task BroadcastAsync(string text);
}

/// <summary>Viewers currently present in chat (GetActiveViewers).</summary>
public interface IActiveViewers
{
    IReadOnlyList<ActiveViewer> GetActiveViewers();
}
