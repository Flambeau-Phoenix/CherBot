using System.Net;
using CherBot.Integrations.Twitch;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Moderation;

public sealed record ModResult(bool Ok, string? Error = null);

/// <summary>
/// Twitch moderation calls used by modules: delete a message, warn, time out, announce. Acts as the bot account when it
/// is linked (it must be a moderator), falling back to the broadcaster when the bot is refused.
/// </summary>
public class ChatModeration(HelixClient helix, TwitchAuth auth, ILogger<ChatModeration> log)
{
    private async Task<ModResult> Act(string what, Func<string, string, TwitchRole, Task> call)
    {
        if (auth.Get(TwitchRole.Broadcaster) is not { } broadcaster) return new ModResult(false, "broadcaster is not linked");
        var roles = auth.Get(TwitchRole.Bot) is not null ? new[] { TwitchRole.Bot, TwitchRole.Broadcaster } : [TwitchRole.Broadcaster];
        string? error = null;
        foreach (var role in roles)
        {
            var moderator = auth.Get(role)!.UserId;
            try
            {
                await call(broadcaster.UserId, moderator, role);
                return new ModResult(true);
            }
            catch (HelixException ex) when (role == TwitchRole.Bot && ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            {
                error = ex.Message;
                log.LogDebug("{What} as bot refused ({Error}); trying the broadcaster", what, ex.Message);
            }
            catch (HelixException ex)
            {
                log.LogWarning("{What} failed: {Error}", what, ex.Message);
                return new ModResult(false, ex.Message);
            }
        }
        return new ModResult(false, error);
    }

    public virtual Task<ModResult> DeleteMessageAsync(string messageId) =>
        string.IsNullOrEmpty(messageId)
            ? Task.FromResult(new ModResult(false, "no message id"))
            : Act("Delete message", (b, m, role) => helix.SendAsync(HttpMethod.Delete, "moderation/chat",
                new Dictionary<string, string?> { ["broadcaster_id"] = b, ["moderator_id"] = m, ["message_id"] = messageId }, role: role));

    public virtual Task<ModResult> TimeoutAsync(string userId, int seconds, string reason) =>
        Act("Timeout", (b, m, role) => helix.SendAsync(HttpMethod.Post, "moderation/bans",
            new Dictionary<string, string?> { ["broadcaster_id"] = b, ["moderator_id"] = m },
            new { data = new { user_id = userId, duration = Math.Clamp(seconds, 1, 1_209_600), reason } }, role));

    public virtual Task<ModResult> WarnAsync(string userId, string reason) =>
        Act("Warn", (b, m, role) => helix.SendAsync(HttpMethod.Post, "moderation/warnings",
            new Dictionary<string, string?> { ["broadcaster_id"] = b, ["moderator_id"] = m },
            new { data = new { user_id = userId, reason = string.IsNullOrWhiteSpace(reason) ? "Please follow the chat rules." : reason } }, role));

    public virtual Task<ModResult> AnnounceAsync(string message, string color, bool useBot) =>
        Act("Announcement", (b, m, role) =>
        {
            if (!useBot && role == TwitchRole.Bot) throw new HelixException(HttpStatusCode.Forbidden, "broadcaster requested");
            return helix.SendAsync(HttpMethod.Post, "chat/announcements",
                new Dictionary<string, string?> { ["broadcaster_id"] = b, ["moderator_id"] = m },
                new { message, color = string.IsNullOrWhiteSpace(color) ? "primary" : color.ToLowerInvariant() }, role);
        });
}
