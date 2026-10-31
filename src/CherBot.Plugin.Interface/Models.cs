using System;
using System.Collections.Generic;

namespace Streamer.bot.Plugin.Interface.Enums
{
    public enum Platform
    {
        Twitch,
        YouTube,
        Trovo,
        Kick,
    }
}

namespace Streamer.bot.Plugin.Interface.Model
{
    public class ActionData
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
        public string Group { get; set; } = "";
        public string Queue { get; set; } = "";
        public Guid QueueId { get; set; }
    }

    public class GlobalVariableValue
    {
        public string VariableName { get; set; } = "";
        public object? Value { get; set; }
        public DateTime LastWrite { get; set; }
    }

    public class QuoteData
    {
        public DateTime Timestamp { get; set; }
        public int Id { get; set; }
        public string UserId { get; set; } = "";
        public string User { get; set; } = "";
        public string Platform { get; set; } = "";
        public string GameName { get; set; } = "";
        public string Quote { get; set; } = "";
    }

    public class BaseUserInfo
    {
        public string UserName { get; set; } = "";
        public string UserLogin { get; set; } = "";
        public string UserId { get; set; } = "";
        public DateTime LastActive { get; set; }
        public DateTime PreviousActive { get; set; }
    }

    public class TwitchUserInfo : BaseUserInfo
    {
        public bool IsSubscribed { get; set; }
        public string SubscriptionTier { get; set; } = "";
        public bool IsModerator { get; set; }
        public bool IsVip { get; set; }
    }

    public class TwitchUserInfoEx : TwitchUserInfo
    {
        public string Description { get; set; } = "";
        public string ProfileImageUrl { get; set; } = "";
        public string UserType { get; set; } = "";
        public bool IsPartner => string.Equals(UserType, "partner", StringComparison.OrdinalIgnoreCase);
        public bool IsAffiliate => string.Equals(UserType, "affiliate", StringComparison.OrdinalIgnoreCase);
        public bool IsFollowing { get; set; }
        public DateTime CreatedAt { get; set; }
        public double AccountAge { get; set; }
        public string Game { get; set; } = "";
        public string GameId { get; set; } = "";
        public string ChannelTitle { get; set; } = "";
        public List<string> Tags { get; set; } = new();
    }

    public class StreamMarker
    {
        public string Id { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string Description { get; set; } = "";
        public int PositionSeconds { get; set; }
    }
}

namespace Streamer.bot.Plugin.Interface
{
    public class GameInfo
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string BoxArtUrl { get; set; } = "";
    }
}
