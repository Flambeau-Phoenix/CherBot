namespace CherBot.Integrations.Giveaways;

/// <summary>Chat messages a giveaway sends. Leave one empty to stay silent.</summary>
public sealed class GiveawayMessages
{
    public string Started { get; set; } = "🎉 Giveaway \"{name}\" is open! Prize: {prize}. Type {command} to enter ({duration}).";
    public string Entered { get; set; } = "{user}, you're in with {tickets} ticket(s)!";
    public string AlreadyEntered { get; set; } = "{user}, you're already entered with {tickets} ticket(s).";
    public string NotEligible { get; set; } = "{user}, you're not eligible for this giveaway.";
    public string NotEnoughPoints { get; set; } = "{user}, entering costs {cost} {currency} per ticket.";
    public string Reminder { get; set; } = "The \"{name}\" giveaway is still open! Type {command} to enter. {entrants} entered so far, {remaining} left.";
    public string Closed { get; set; } = "Entries for \"{name}\" are closed! {entrants} people entered with {entries} tickets.";
    public string Winner { get; set; } = "🏆 Congratulations {winner}, you won {prize}!";
    public string WinnerClaim { get; set; } = "🏆 {winner} won {prize}! {winner}, say something in chat within {claimSeconds}s to claim it.";
    public string NoEntries { get; set; } = "Nobody (else) entered the \"{name}\" giveaway, so there is no winner.";
    public string Claimed { get; set; } = "✅ {winner} claimed {prize}!";
    public string Unclaimed { get; set; } = "{winner} didn't claim {prize} in time. Drawing again...";
    public string Cancelled { get; set; } = "The \"{name}\" giveaway was cancelled.";
}

public static class GiveawayStatus
{
    public const string Draft = "draft", Open = "open", Closed = "closed", Drawn = "drawn", Cancelled = "cancelled";
}

/// <summary>A stored giveaway (doc kind "giveaway"). Settings are edited in the UI; the state fields are owned by the service.</summary>
public sealed class GiveawayDef
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Giveaway";
    public string Prize { get; set; } = "";
    public List<string> Commands { get; set; } = ["!join"];
    /// <summary>0 = open until stopped.</summary>
    public int DurationSeconds { get; set; }
    public bool AutoDraw { get; set; } = true;
    public int WinnerCount { get; set; } = 1;
    public bool ExcludePreviousWinners { get; set; } = true;

    public long EntryCost { get; set; }
    public int MaxTickets { get; set; } = 1;
    public int SubscriberBonusTickets { get; set; }
    public int VipBonusTickets { get; set; }
    /// <summary>everyone, subscriber, vip or moderator.</summary>
    public string MinRole { get; set; } = "everyone";
    public bool FollowersOnly { get; set; }
    /// <summary>When set, any chat message containing it counts as an entry.</summary>
    public string Keyword { get; set; } = "";

    /// <summary>0 = off; otherwise the winner must chat within this many seconds or a new winner is drawn.</summary>
    public int ClaimSeconds { get; set; }
    /// <summary>0 = off; otherwise the reminder repeats on this interval while open.</summary>
    public int ReminderSeconds { get; set; }
    public GiveawayMessages Messages { get; set; } = new();

    // ── state ──
    public string Status { get; set; } = GiveawayStatus.Draft;
    public int Run { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }
    public DateTimeOffset? ClosesAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? LastReminder { get; set; }
    public Guid? CommandId { get; set; }
    public Guid? ActionId { get; set; }

    internal void CopyStateFrom(GiveawayDef other)
    {
        Status = other.Status;
        Run = other.Run;
        OpenedAt = other.OpenedAt;
        ClosesAt = other.ClosesAt;
        ClosedAt = other.ClosedAt;
        LastReminder = other.LastReminder;
        CommandId = other.CommandId;
        ActionId = other.ActionId;
    }
}

public sealed record GiveawayResult(bool Ok, string Message);
