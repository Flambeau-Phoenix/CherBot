namespace CherBot.Integrations.Twitch;

/// <summary>Configuration section "CherBot:Twitch".</summary>
public sealed class TwitchOptions
{
    public const string Section = "CherBot:Twitch";

    /// <summary>Your own Twitch application's client id (dev.twitch.tv). Register it as a "Public" client for the device code flow.</summary>
    public string? ClientId { get; set; }

    /// <summary>Only needed for the optional authorization-code flow with a confidential client.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Redirect URI for the authorization-code flow, e.g. http://localhost:8091/twitch/callback (must match the app registration).</summary>
    public string? RedirectUri { get; set; }

    public bool AutoConnect { get; set; } = true;

    public string[] BroadcasterScopes { get; set; } =
    [
        "user:read:chat", "user:write:chat", "channel:bot",
        "channel:read:redemptions", "channel:manage:redemptions",
        "moderator:read:followers", "moderator:manage:shoutouts", "moderator:manage:announcements",
        "moderator:manage:banned_users", "moderator:manage:chat_settings", "moderator:read:chatters",
        "moderator:manage:chat_messages", "moderator:manage:warnings",
        "bits:read", "channel:read:subscriptions", "channel:manage:broadcast", "channel:edit:commercial",
        "channel:manage:raids", "channel:manage:vips", "channel:manage:moderators", "clips:edit",
        "channel:read:ads", "user:read:email",
    ];

    public string[] BotScopes { get; set; } =
    [
        "user:read:chat", "user:write:chat", "user:bot",
        "moderator:manage:announcements", "moderator:manage:chat_messages", "moderator:manage:banned_users",
        "moderator:manage:warnings",
    ];

    /// <summary>EventSub subscription types to create. Unknown or unauthorised types are reported and skipped.</summary>
    public string[] EventSub { get; set; } =
    [
        "channel.chat.message", "channel.chat.notification", "channel.chat.message_delete",
        "channel.follow", "channel.cheer", "channel.raid",
        "channel.channel_points_custom_reward_redemption.add",
        "stream.online", "stream.offline", "channel.update",
        "channel.shoutout.create", "channel.shoutout.receive", "channel.ad_break.begin",
    ];
}

/// <summary>Configuration section "CherBot:Community": built-in points and first-words behaviour carried over from the Python prototype.</summary>
public sealed class CommunityOptions
{
    public const string Section = "CherBot:Community";

    public bool Enabled { get; set; } = true;
    public int FirstWordPoints { get; set; } = 10;
    public int ChatPoints { get; set; } = 1;
    /// <summary>Seed the !first, !points, !leaderboard and !disc commands and actions on first start.</summary>
    public bool SeedDefaults { get; set; } = true;
    public string DefaultDiscordUrl { get; set; } = "https://discord.gg/cherbot";
}
