using System.Text.RegularExpressions;
using CherBot.Core;

namespace CherBot.Integrations.Common;

/// <summary>Friendly <c>{token}</c> placeholders used by giveaway, module and custom-command messages.</summary>
public static partial class Tokens
{
    [GeneratedRegex(@"\{(?<name>[A-Za-z][A-Za-z0-9_]*)\}")]
    private static partial Regex TokenPattern();

    /// <summary>Replaces known <c>{name}</c> tokens (case-insensitive); unknown ones are left as typed.</summary>
    public static string Fill(string? template, IReadOnlyDictionary<string, object?> values)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('{')) return template ?? "";
        return TokenPattern().Replace(template, m =>
        {
            var name = m.Groups["name"].Value;
            foreach (var (k, v) in values)
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return CherBotJson.Format(v);
            return m.Value;
        });
    }

    /// <summary>"90" → "1m 30s"; 0 → "until stopped" when <paramref name="zero"/> is given.</summary>
    public static string Duration(double seconds, string zero = "0s")
    {
        if (seconds <= 0) return zero;
        var t = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        var parts = new List<string>();
        if (t.Days > 0) parts.Add($"{t.Days}d");
        if (t.Hours > 0) parts.Add($"{t.Hours}h");
        if (t.Minutes > 0) parts.Add($"{t.Minutes}m");
        if (t.Seconds > 0 && t.TotalHours < 1) parts.Add($"{t.Seconds}s");
        return string.Join(" ", parts);
    }
}

/// <summary>Chat role ranks shared by giveaways, modules and custom commands: viewer 0, subscriber 1, VIP 2, moderator 3, broadcaster 4.</summary>
public static class ChatRoles
{
    public static readonly string[] Names = ["everyone", "subscriber", "vip", "moderator", "broadcaster"];

    public static int Rank(IReadOnlyDictionary<string, object?> chat) =>
        Flag(chat, "isBroadcaster") ? 4 : Flag(chat, "isModerator") ? 3 : Flag(chat, "isVip") ? 2 : Flag(chat, "isSubscribed") ? 1 : 0;

    public static int Rank(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "broadcaster" => 4,
        "moderator" or "mod" or "moderators" => 3,
        "vip" or "vips" => 2,
        "subscriber" or "sub" or "subscribers" => 1,
        _ => 0,
    };

    public static bool AtLeast(IReadOnlyDictionary<string, object?> chat, string? role) => Rank(chat) >= Rank(role);

    public static bool Flag(IReadOnlyDictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is true or "true" or "True";

    public static string Str(IReadOnlyDictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is not null ? CherBotJson.Format(v) : "";
}
