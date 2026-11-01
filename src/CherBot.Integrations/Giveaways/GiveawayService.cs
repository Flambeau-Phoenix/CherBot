using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Generated;
using CherBot.Core.Model;
using CherBot.Integrations.Common;
using CherBot.Integrations.Community;
using CherBot.Integrations.Loyalty;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Giveaways;

/// <summary>
/// Raffles and giveaways: stored definitions with their own entry command, timed or open-ended entry windows, ticket and
/// cost rules, weighted draws, claim timeouts and reminders. State survives restarts. Raises Custom Events "giveaway.*".
/// </summary>
public sealed class GiveawayService : BackgroundService
{
    public const string Kind = "giveaway";
    public const string Group = "CherBot Giveaways";
    public const string Platform = "twitch";
    private const string SeedKey = "seed.giveaways.v1";

    private readonly CherBotDb _db;
    private readonly ConfigService _config;
    private readonly EventBus _bus;
    private readonly TwitchService _twitch;
    private readonly LoyaltyService _loyalty;
    private readonly HelixClient _helix;
    private readonly TwitchAuth _auth;
    private readonly TemplateEngine _templates;
    private readonly ILogger<GiveawayService> _log;
    private readonly ConcurrentDictionary<Guid, GiveawayDef> _defs = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDisposable? _sub;

    public GiveawayService(CherBotDb db, ConfigService config, EventBus bus, TwitchService twitch, LoyaltyService loyalty, HelixClient helix,
        TwitchAuth auth, TemplateEngine templates, ILogger<GiveawayService> log)
    {
        _db = db;
        _config = config;
        _bus = bus;
        _twitch = twitch;
        _loyalty = loyalty;
        _helix = helix;
        _auth = auth;
        _templates = templates;
        _log = log;
        foreach (var d in config.GetDocuments<GiveawayDef>(Kind)) _defs[d.Id] = d;
    }

    /// <summary>Picks an index in [0, max); cryptographic by default, replaceable in tests.</summary>
    public Func<int, int> Pick { get; set; } = max => RandomNumberGenerator.GetInt32(max);
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    /// <summary>Chat messages the service sent (latest last), for the UI preview and tests.</summary>
    public ConcurrentQueue<string> Sent { get; } = new();

    public IReadOnlyList<GiveawayDef> All => _defs.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    public GiveawayDef? Get(Guid id) => _defs.GetValueOrDefault(id);

    /// <summary>By id, exact name, then name prefix (case-insensitive).</summary>
    public GiveawayDef? Find(string? nameOrId)
    {
        if (string.IsNullOrWhiteSpace(nameOrId)) return null;
        var key = nameOrId.Trim();
        if (Guid.TryParse(key, out var id)) return Get(id);
        return _defs.Values.FirstOrDefault(d => string.Equals(d.Name, key, StringComparison.OrdinalIgnoreCase))
               ?? _defs.Values.Where(d => d.Name.StartsWith(key, StringComparison.OrdinalIgnoreCase)).OrderBy(d => d.Name.Length).FirstOrDefault();
    }

    public GiveawayDef? OpenOne() => _defs.Values.Where(d => d.Status == GiveawayStatus.Open).OrderByDescending(d => d.OpenedAt).FirstOrDefault();

    /// <summary>The giveaway a control command means when none is named.</summary>
    public GiveawayDef? DefaultFor(string operation) => operation switch
    {
        "start" => _defs.Count == 1 ? _defs.Values.First()
            : _defs.Values.Where(d => d.Status != GiveawayStatus.Open).OrderByDescending(d => d.Status == GiveawayStatus.Draft).ThenByDescending(d => d.ClosedAt ?? d.OpenedAt).FirstOrDefault(),
        "draw" or "reroll" => OpenOne() ?? _defs.Values.Where(d => d.Status is GiveawayStatus.Closed or GiveawayStatus.Drawn).OrderByDescending(d => d.ClosedAt).FirstOrDefault(),
        _ => OpenOne(),
    };

    // ── definitions ────────────────────────────────────────────────────────

    public GiveawayDef Save(GiveawayDef def)
    {
        if (def.Id == Guid.Empty) def.Id = Guid.NewGuid();
        if (_defs.TryGetValue(def.Id, out var existing)) def.CopyStateFrom(existing);
        Normalize(def);
        SyncCommand(def);
        Persist(def);
        return def;
    }

    public bool Delete(Guid id)
    {
        if (!_defs.TryRemove(id, out var def)) return false;
        if (def.CommandId is { } c) _config.DeleteCommand(c);
        if (def.ActionId is { } a) _config.DeleteAction(a);
        _db.ClearGiveawayEntries(id.ToString());
        _db.ClearGiveawayWinners(id.ToString());
        _config.DeleteDocument(Kind, id.ToString());
        return true;
    }

    private static void Normalize(GiveawayDef d)
    {
        d.Name = string.IsNullOrWhiteSpace(d.Name) ? "Giveaway" : d.Name.Trim();
        d.Commands = d.Commands.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        d.DurationSeconds = Math.Max(0, d.DurationSeconds);
        d.WinnerCount = Math.Max(1, d.WinnerCount);
        d.MaxTickets = Math.Max(1, d.MaxTickets);
        d.EntryCost = Math.Max(0, d.EntryCost);
        d.ClaimSeconds = Math.Max(0, d.ClaimSeconds);
        d.ReminderSeconds = d.ReminderSeconds <= 0 ? 0 : Math.Max(30, d.ReminderSeconds);
        d.Messages ??= new GiveawayMessages();
    }

    private void Persist(GiveawayDef def)
    {
        _defs[def.Id] = def;
        _config.SaveDocument(Kind, def.Id.ToString(), def.Name, def);
    }

    /// <summary>Keeps the giveaway's own command (enabled only while open) and its Giveaway Enter action in step with the definition.</summary>
    private void SyncCommand(GiveawayDef def)
    {
        var text = string.Join("\n", def.Commands);
        var cmd = def.CommandId is { } cid ? _config.GetCommand(cid) : null;
        if (text.Length == 0)
        {
            if (cmd is not null && cmd.Enabled) { cmd.Enabled = false; _config.SaveCommand(cmd); }
            return;
        }
        if (cmd is null)
        {
            cmd = new CommandDef { Name = $"Giveaway: {def.Name}", Group = Group, GlobalCooldown = 0, UserCooldown = 0 };
            def.CommandId = cmd.Id;
        }
        cmd.Name = $"Giveaway: {def.Name}";
        cmd.Command = text;
        cmd.Enabled = def.Status == GiveawayStatus.Open;
        _config.SaveCommand(cmd);

        if (def.ActionId is { } aid && _config.GetAction(aid) is not null) return;
        var trigger = new TriggerDef { Type = EventTypes.Id("CommandTriggered") };
        trigger.Set("commandId", cmd.Id);
        var enter = Seeding.Step(GiveawayHandlers.Enter, new { giveawayId = def.Id });
        var action = new ActionDef { Name = $"Giveaway: {def.Name}", Group = Group, Triggers = [trigger], SubActions = [enter] };
        _config.SaveAction(action);
        def.ActionId = action.Id;
    }

    private void SetCommandEnabled(GiveawayDef def, bool enabled)
    {
        if (def.CommandId is { } id && _config.GetCommand(id) is { } cmd && cmd.Enabled != enabled)
        {
            cmd.Enabled = enabled;
            _config.SaveCommand(cmd);
        }
    }

    // ── live numbers ───────────────────────────────────────────────────────

    public List<GiveawayEntryRow> Entries(Guid id) => _db.GetGiveawayEntries(id.ToString());
    public List<GiveawayWinnerRow> Winners(Guid id) => _db.GetGiveawayWinners(id.ToString());

    public double RemainingSeconds(GiveawayDef d) =>
        d.Status == GiveawayStatus.Open && d.ClosesAt is { } c ? Math.Max(0, (c - Clock()).TotalSeconds) : 0;

    /// <summary>The definition plus live counts, as the API and UI show it.</summary>
    public object View(GiveawayDef d)
    {
        var entries = Entries(d.Id);
        return new
        {
            giveaway = d,
            entrants = entries.Count,
            entries = entries.Sum(e => e.TotalTickets),
            remainingSeconds = d.ClosesAt is null ? (double?)null : RemainingSeconds(d),
            winners = Winners(d.Id).Where(w => w.Run == d.Run && w.Status != "rerolled").Select(w => new { w.Id, w.UserName, w.Status }),
        };
    }

    private Dictionary<string, object?> BaseTokens(GiveawayDef d)
    {
        var entries = Entries(d.Id);
        var remaining = RemainingSeconds(d);
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = d.Name,
            ["prize"] = d.Prize.Length > 0 ? d.Prize : d.Name,
            ["command"] = d.Commands.FirstOrDefault() ?? (d.Keyword.Length > 0 ? d.Keyword : "!join"),
            ["entries"] = entries.Sum(e => e.TotalTickets),
            ["entrants"] = entries.Count,
            ["cost"] = d.EntryCost,
            ["currency"] = _loyalty.Currency,
            ["duration"] = d.ClosesAt is { } c && d.OpenedAt is { } o ? Tokens.Duration((c - o).TotalSeconds) : "until stopped",
            ["remaining"] = d.ClosesAt is null ? "until stopped" : Tokens.Duration(remaining),
            ["claimSeconds"] = d.ClaimSeconds,
            ["maxTickets"] = d.MaxTickets,
        };
    }

    private async Task Say(GiveawayDef d, string? template, Dictionary<string, object?>? extra = null, IReadOnlyDictionary<string, object?>? args = null, string? replyTo = null)
    {
        if (string.IsNullOrWhiteSpace(template)) return;
        var tokens = BaseTokens(d);
        if (extra is not null) foreach (var (k, v) in extra) tokens[k] = v;
        var text = _templates.Parse(Tokens.Fill(template, tokens), args ?? tokens);
        if (string.IsNullOrWhiteSpace(text)) return;
        Sent.Enqueue(text);
        while (Sent.Count > 50) Sent.TryDequeue(out _);
        try
        {
            var res = await _twitch.SendChatAsync(text, useBot: true, fallback: true, replyTo, CancellationToken.None);
            if (!res.Sent) _log.LogDebug("Giveaway message not sent: {Error}", res.Error);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Giveaway message failed"); }
    }

    private void Raise(GiveawayDef d, string eventName, Dictionary<string, object?>? extra = null)
    {
        var entries = Entries(d.Id);
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["eventName"] = eventName,
            ["giveawayId"] = d.Id.ToString(),
            ["giveawayName"] = d.Name,
            ["prize"] = d.Prize,
            ["status"] = d.Status,
            ["entrants"] = entries.Count,
            ["entries"] = entries.Sum(e => e.TotalTickets),
        };
        if (extra is not null) foreach (var (k, v) in extra) data[k] = v;
        data["args"] = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
        _bus.Publish(BotEvent.FromTypeName("CustomEvent", data));
    }

    private async Task<T> Locked<T>(Func<Task<T>> work)
    {
        await _gate.WaitAsync();
        try { return await work(); }
        finally { _gate.Release(); }
    }

    // ── lifecycle ──────────────────────────────────────────────────────────

    public Task<GiveawayResult> StartAsync(Guid id, int? durationSeconds = null) => Locked(async () =>
    {
        if (Get(id) is not { } d) return new GiveawayResult(false, "Giveaway not found.");
        if (d.Status == GiveawayStatus.Open) return new GiveawayResult(false, $"\"{d.Name}\" is already open.");
        var now = Clock();
        var duration = Math.Max(0, durationSeconds ?? d.DurationSeconds);
        _db.ClearGiveawayEntries(d.Id.ToString());
        d.Run++;
        d.Status = GiveawayStatus.Open;
        d.OpenedAt = now;
        d.ClosesAt = duration > 0 ? now.AddSeconds(duration) : null;
        d.ClosedAt = null;
        d.LastReminder = now;
        SyncCommand(d);
        Persist(d);
        await Say(d, d.Messages.Started);
        Raise(d, "giveaway.started", new() { ["durationSeconds"] = duration });
        return new GiveawayResult(true, $"\"{d.Name}\" is open{(duration > 0 ? $" for {Tokens.Duration(duration)}" : " until stopped")}.");
    });

    public Task<GiveawayResult> StopAsync(Guid id) => Locked(() => CloseLocked(id));

    private async Task<GiveawayResult> CloseLocked(Guid id)
    {
        if (Get(id) is not { } d) return new GiveawayResult(false, "Giveaway not found.");
        if (d.Status != GiveawayStatus.Open) return new GiveawayResult(false, $"\"{d.Name}\" isn't open.");
        d.Status = GiveawayStatus.Closed;
        d.ClosedAt = Clock();
        SetCommandEnabled(d, false);
        Persist(d);
        var entrants = Entries(d.Id).Count;
        if (entrants > 0 || !d.AutoDraw) await Say(d, d.Messages.Closed);
        Raise(d, "giveaway.closed");
        if (d.AutoDraw) return await DrawLocked(d, d.WinnerCount);
        return new GiveawayResult(true, $"\"{d.Name}\" is closed with {entrants} entrants.");
    }

    public Task<GiveawayResult> DrawAsync(Guid id, int? count = null) => Locked(async () =>
    {
        if (Get(id) is not { } d) return new GiveawayResult(false, "Giveaway not found.");
        if (d.Status == GiveawayStatus.Open)
        {
            var closed = await CloseLocked(id);
            if (d.AutoDraw) return closed;
        }
        if (d.Status is GiveawayStatus.Draft or GiveawayStatus.Cancelled) return new GiveawayResult(false, $"\"{d.Name}\" has no entries to draw from.");
        return await DrawLocked(d, count ?? d.WinnerCount);
    });

    /// <summary>Draws up to <paramref name="count"/> winners, weighted by tickets, skipping anyone already drawn this run.</summary>
    private async Task<GiveawayResult> DrawLocked(GiveawayDef d, int count)
    {
        var gid = d.Id.ToString();
        var winners = _db.GetGiveawayWinners(gid);
        var excluded = new HashSet<string>(winners.Where(w => w.Run == d.Run).Select(w => w.UserId));
        if (d.ExcludePreviousWinners)
            foreach (var w in winners.Where(w => w.Run != d.Run && w.Status is "won" or "claimed" or "pending")) excluded.Add(w.UserId);
        var pool = _db.GetGiveawayEntries(gid).Where(e => !excluded.Contains(e.UserId) && e.TotalTickets > 0).ToList();

        var drawn = new List<(long Id, GiveawayEntryRow Entry)>();
        for (var i = 0; i < Math.Max(1, count) && pool.Count > 0; i++)
        {
            var total = pool.Sum(e => e.TotalTickets);
            var ticket = Pick(total);
            var pick = pool[^1];
            foreach (var e in pool)
            {
                if (ticket < e.TotalTickets) { pick = e; break; }
                ticket -= e.TotalTickets;
            }
            pool.Remove(pick);
            var status = d.ClaimSeconds > 0 ? "pending" : "won";
            drawn.Add((_db.AddGiveawayWinner(gid, d.Run, pick.Platform, pick.UserId, pick.UserLogin, pick.UserName, status), pick));
        }

        d.Status = GiveawayStatus.Drawn;
        Persist(d);
        if (drawn.Count == 0)
        {
            await Say(d, d.Messages.NoEntries);
            return new GiveawayResult(false, "No eligible entrants left to draw.");
        }
        var names = string.Join(", ", drawn.Select(x => x.Entry.UserName));
        foreach (var (winnerId, e) in drawn)
        {
            var template = d.ClaimSeconds > 0 ? d.Messages.WinnerClaim : d.Messages.Winner;
            await Say(d, template, new() { ["winner"] = e.UserName, ["user"] = e.UserName, ["winners"] = names, ["tickets"] = e.TotalTickets });
            Raise(d, "giveaway.winner", new()
            {
                ["winnerId"] = winnerId,
                ["user"] = e.UserName,
                ["userName"] = e.UserLogin,
                ["userId"] = e.UserId,
                ["tickets"] = e.TotalTickets,
                ["claimSeconds"] = d.ClaimSeconds,
            });
        }
        return new GiveawayResult(true, $"Winner{(drawn.Count > 1 ? "s" : "")}: {names}");
    }

    public Task<GiveawayResult> RerollAsync(Guid id) => Locked(async () =>
    {
        if (Get(id) is not { } d) return new GiveawayResult(false, "Giveaway not found.");
        var last = _db.GetGiveawayWinners(d.Id.ToString()).Where(w => w.Run == d.Run && w.Status is "won" or "pending").LastOrDefault();
        if (last is null) return new GiveawayResult(false, $"\"{d.Name}\" has no winner to reroll.");
        _db.SetGiveawayWinnerStatus(last.Id, "rerolled");
        return await DrawLocked(d, 1);
    });

    public Task<GiveawayResult> CancelAsync(Guid id) => Locked(async () =>
    {
        if (Get(id) is not { } d) return new GiveawayResult(false, "Giveaway not found.");
        if (d.Status is GiveawayStatus.Cancelled or GiveawayStatus.Draft) return new GiveawayResult(false, $"\"{d.Name}\" isn't running.");
        var refunded = 0L;
        foreach (var e in Entries(d.Id).Where(e => e.Paid > 0))
        {
            _loyalty.Award(e.UserId, e.UserName, e.Paid, "giveaway");
            refunded += e.Paid;
        }
        _db.ClearGiveawayEntries(d.Id.ToString());
        d.Status = GiveawayStatus.Cancelled;
        d.ClosedAt = Clock();
        SetCommandEnabled(d, false);
        Persist(d);
        await Say(d, d.Messages.Cancelled);
        Raise(d, "giveaway.cancelled", new() { ["refunded"] = refunded });
        return new GiveawayResult(true, $"\"{d.Name}\" was cancelled{(refunded > 0 ? $"; refunded {refunded} {_loyalty.Currency}" : "")}.");
    });

    /// <summary>Clears entries and the winner history and returns the giveaway to draft.</summary>
    public Task<GiveawayResult> ResetAsync(Guid id) => Locked(() =>
    {
        if (Get(id) is not { } d) return Task.FromResult(new GiveawayResult(false, "Giveaway not found."));
        _db.ClearGiveawayEntries(d.Id.ToString());
        _db.ClearGiveawayWinners(d.Id.ToString());
        d.Status = GiveawayStatus.Draft;
        d.OpenedAt = d.ClosesAt = d.ClosedAt = d.LastReminder = null;
        SetCommandEnabled(d, false);
        Persist(d);
        return Task.FromResult(new GiveawayResult(true, $"\"{d.Name}\" was reset."));
    });

    public Task<GiveawayResult> ClaimAsync(Guid id, long winnerId) => Locked(async () =>
    {
        if (Get(id) is not { } d) return new GiveawayResult(false, "Giveaway not found.");
        if (_db.GetGiveawayWinner(winnerId) is not { } w || w.Giveaway != d.Id.ToString()) return new GiveawayResult(false, "Winner not found.");
        await MarkClaimed(d, w);
        return new GiveawayResult(true, $"{w.UserName} claimed the prize.");
    });

    private async Task MarkClaimed(GiveawayDef d, GiveawayWinnerRow w)
    {
        _db.SetGiveawayWinnerStatus(w.Id, "claimed");
        await Say(d, d.Messages.Claimed, new() { ["winner"] = w.UserName, ["user"] = w.UserName });
        Raise(d, "giveaway.claimed", new() { ["winnerId"] = w.Id, ["user"] = w.UserName, ["userName"] = w.UserLogin, ["userId"] = w.UserId });
    }

    public bool RemoveEntry(Guid id, string platform, string userId) => _db.DeleteGiveawayEntry(id.ToString(), platform, userId);

    // ── entries ────────────────────────────────────────────────────────────

    /// <summary>Enters the chatter in <paramref name="d"/>. <paramref name="ticketsText"/> is the requested ticket count (default 1).</summary>
    public Task<GiveawayResult> EnterAsync(Guid id, IReadOnlyDictionary<string, object?> chat, string? ticketsText, bool quietDuplicate = false) => Locked(async () =>
    {
        if (Get(id) is not { } d) return new GiveawayResult(false, "Giveaway not found.");
        if (d.Status != GiveawayStatus.Open) return new GiveawayResult(false, $"\"{d.Name}\" isn't open.");
        var userId = ChatRoles.Str(chat, "userId");
        if (userId.Length == 0) return new GiveawayResult(false, "No user.");
        var login = ChatRoles.Str(chat, "userName");
        var name = ChatRoles.Str(chat, "user") is { Length: > 0 } n ? n : login;
        var replyTo = ChatRoles.Str(chat, "msgId") is { Length: > 0 } m ? m : null;
        var platform = ChatRoles.Str(chat, "platform") is { Length: > 0 } p ? p : Platform;
        var who = new Dictionary<string, object?> { ["user"] = name };

        if (!ChatRoles.AtLeast(chat, d.MinRole) || (d.FollowersOnly && !ChatRoles.Flag(chat, "isBroadcaster") && await IsFollowerAsync(userId) == false))
        {
            await Say(d, d.Messages.NotEligible, who, chat, replyTo);
            return new GiveawayResult(false, "Not eligible.");
        }

        var existing = _db.GetGiveawayEntry(d.Id.ToString(), platform, userId);
        var requested = int.TryParse(ticketsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 1;
        requested = Math.Clamp(requested, 1, d.MaxTickets);
        var add = existing is null ? requested : Math.Min(requested, d.MaxTickets - existing.Tickets);
        if (existing is not null && (string.IsNullOrWhiteSpace(ticketsText) || add <= 0))
        {
            if (!quietDuplicate) await Say(d, d.Messages.AlreadyEntered, new(who) { ["tickets"] = existing.TotalTickets }, chat, replyTo);
            return new GiveawayResult(false, "Already entered.");
        }

        var cost = d.EntryCost * add;
        if (cost > 0)
        {
            if (_db.GetPoints(Platform, userId) < cost)
            {
                await Say(d, d.Messages.NotEnoughPoints, new(who) { ["tickets"] = add, ["cost"] = d.EntryCost }, chat, replyTo);
                return new GiveawayResult(false, "Not enough points.");
            }
            _loyalty.Award(userId, name, -cost, "giveaway");
        }

        var bonus = existing?.Bonus ?? (ChatRoles.Flag(chat, "isSubscribed") ? d.SubscriberBonusTickets : 0) + (ChatRoles.Flag(chat, "isVip") ? d.VipBonusTickets : 0);
        var entry = new GiveawayEntryRow(d.Id.ToString(), platform, userId, login, name, (existing?.Tickets ?? 0) + add, bonus, (existing?.Paid ?? 0) + cost,
            existing?.Timestamp ?? "");
        _db.UpsertGiveawayEntry(entry);
        await Say(d, d.Messages.Entered, new(who) { ["tickets"] = entry.TotalTickets }, chat, replyTo);
        Raise(d, "giveaway.entry", new() { ["user"] = name, ["userName"] = login, ["userId"] = userId, ["tickets"] = entry.TotalTickets, ["paid"] = cost });
        return new GiveawayResult(true, $"{name} entered with {entry.TotalTickets} ticket(s).");
    });

    /// <summary>True/false from Helix, or null when it can't be checked (scope missing, not linked).</summary>
    private async Task<bool?> IsFollowerAsync(string userId)
    {
        if (_auth.Get(TwitchRole.Broadcaster) is not { } b) return null;
        try
        {
            var res = await _helix.GetAsync("channels/followers", new Dictionary<string, string?> { ["broadcaster_id"] = b.UserId, ["user_id"] = userId });
            return HelixClient.First(res) is not null;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Follower check unavailable");
            return null;
        }
    }

    // ── chat: keyword entries and claims ───────────────────────────────────

    internal async Task OnChatAsync(BotEvent chat)
    {
        if (chat.Data.GetValueOrDefault("filtered") is true || chat.Data.GetValueOrDefault("isBot") is true) return;
        var message = ChatRoles.Str(chat.Data, "message");
        var userId = ChatRoles.Str(chat.Data, "userId");
        if (userId.Length == 0) return;

        foreach (var d in _defs.Values.Where(d => d.Status == GiveawayStatus.Open && d.Keyword.Length > 0).ToList())
            if (message.Contains(d.Keyword, StringComparison.OrdinalIgnoreCase))
                await EnterAsync(d.Id, chat.Data, null, quietDuplicate: true);

        foreach (var d in _defs.Values.Where(d => d.Status == GiveawayStatus.Drawn && d.ClaimSeconds > 0).ToList())
        {
            var pending = _db.GetGiveawayWinners(d.Id.ToString()).FirstOrDefault(w => w.Run == d.Run && w.Status == "pending" && w.UserId == userId);
            if (pending is not null) await Locked(async () => { await MarkClaimed(d, pending); return true; });
        }
    }

    // ── timers ─────────────────────────────────────────────────────────────

    /// <summary>Closes expired giveaways, sends reminders and redraws unclaimed winners. Runs every second.</summary>
    public async Task TickAsync()
    {
        var now = Clock();
        foreach (var d in _defs.Values.ToList())
        {
            if (d.Status == GiveawayStatus.Open)
            {
                if (d.ClosesAt is { } closes && closes <= now)
                {
                    await StopAsync(d.Id);
                    continue;
                }
                if (d.ReminderSeconds > 0 && (now - (d.LastReminder ?? d.OpenedAt ?? now)).TotalSeconds >= d.ReminderSeconds)
                {
                    d.LastReminder = now;
                    Persist(d);
                    await Say(d, d.Messages.Reminder);
                }
            }
            else if (d.Status == GiveawayStatus.Drawn && d.ClaimSeconds > 0)
            {
                var expired = _db.GetGiveawayWinners(d.Id.ToString()).Where(w => w.Run == d.Run && w.Status == "pending" &&
                    DateTimeOffset.TryParse(w.DrawnAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var drawn) &&
                    (now - drawn).TotalSeconds >= d.ClaimSeconds).ToList();
                foreach (var w in expired)
                {
                    await Locked(async () =>
                    {
                        _db.SetGiveawayWinnerStatus(w.Id, "expired");
                        await Say(d, d.Messages.Unclaimed, new() { ["winner"] = w.UserName, ["user"] = w.UserName });
                        return await DrawLocked(d, 1);
                    });
                }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_db.GetKv(SeedKey) is null)
        {
            Seeding.CommandAction(_config, Group, "!giveaway", "!giveaway",
                [Seeding.Step(GiveawayHandlers.Control, new { operation = "" }), Seeding.Reply()], enabled: false,
                configure: c => { c.Set("minRole", "moderator"); c.GlobalCooldown = 0; c.UserCooldown = 0; });
            _db.SetKv(SeedKey, "true");
        }
        // Commands of giveaways that were open at shutdown stay enabled; anything else is switched off.
        foreach (var d in _defs.Values) SetCommandEnabled(d, d.Status == GiveawayStatus.Open);

        _sub = _bus.Subscribe("giveaways", OnChatAsync, e => e.Source == "Twitch" && e.Type == "ChatMessage");
        using var tick = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await tick.WaitForNextTickAsync(stoppingToken))
            {
                try { await TickAsync(); }
                catch (Exception ex) { _log.LogWarning(ex, "Giveaway tick failed"); }
            }
        }
        catch (OperationCanceledException) { }
        finally { _sub?.Dispose(); }
    }
}
