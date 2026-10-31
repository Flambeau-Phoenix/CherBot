using CherBot.Core.Events;
using Microsoft.Extensions.Logging;

namespace CherBot.Core.Moderation;

/// <summary>Why a chat message was blocked.</summary>
public sealed record ChatVerdict(string Filter, string Reason);

/// <summary>Inspects a chat message before anything else sees it. Return a verdict to block it; punishments run on their own.</summary>
public interface IChatModerator
{
    ChatVerdict? Check(BotEvent chat);
}

/// <summary>
/// Runs registered moderators over each chat message before it is published. A blocked message is still published, with
/// <c>filtered = true</c> and <c>filterReason</c>, so commands, points and modules can skip it; a Custom Event
/// "chat.filtered" is raised as well.
/// </summary>
public sealed class ChatModerationPipeline(EventBus bus, ILogger<ChatModerationPipeline> log)
{
    private readonly List<IChatModerator> _moderators = [];
    private readonly object _lock = new();

    public void Register(IChatModerator moderator)
    {
        lock (_lock) if (!_moderators.Contains(moderator)) _moderators.Add(moderator);
    }

    public void Unregister(IChatModerator moderator)
    {
        lock (_lock) _moderators.Remove(moderator);
    }

    public ChatVerdict? Apply(BotEvent chat)
    {
        IChatModerator[] list;
        lock (_lock) list = [.. _moderators];
        foreach (var m in list)
        {
            ChatVerdict? verdict;
            try { verdict = m.Check(chat); }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Chat moderator {Moderator} failed", m.GetType().Name);
                continue;
            }
            if (verdict is null) continue;
            chat.Data["filtered"] = true;
            chat.Data["filterName"] = verdict.Filter;
            chat.Data["filterReason"] = verdict.Reason;
            var data = new Dictionary<string, object?>(chat.Data, StringComparer.OrdinalIgnoreCase) { ["eventName"] = "chat.filtered" };
            data["args"] = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
            bus.Publish(BotEvent.FromTypeName("CustomEvent", data));
            return verdict;
        }
        return null;
    }
}
