using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Events;
using CherBot.Integrations.Community;
using CherBot.Integrations.Loyalty;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Games;

public sealed record GamePlayer(string UserId, string Login, string Name);

/// <summary>
/// Chat games paid in loyalty points: gamble, slots, coinflip, duel, heist and roulette. Each play returns the chat reply;
/// duels and heists keep state and announce their own outcomes. Results are raised as CherBot/GameResult events.
/// </summary>
public sealed class GameService : IHostedService
{
    public const string Platform = "twitch";
    public const string Group = "CherBot Games";
    private const string Key = "games.settings";
    private const string SeedKey = "seed.games.v1";
    private static readonly HashSet<int> RedNumbers = [1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36];

    private readonly CherBotDb _db;
    private readonly EventBus _bus;
    private readonly ConfigService _config;
    private readonly LoyaltyService _loyalty;
    private readonly TwitchService _twitch;
    private readonly ILogger<GameService> _log;
    private readonly ConcurrentDictionary<(string Game, string User), DateTimeOffset> _lastPlay = new();
    private readonly ConcurrentDictionary<string, Duel> _duels = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _heistLock = new();
    private Heist? _heist;
    private DateTimeOffset _heistCooldownUntil;
    private GameSettings _settings;

    public GameService(CherBotDb db, EventBus bus, ConfigService config, LoyaltyService loyalty, TwitchService twitch, IGameRandom random, ILogger<GameService> log)
    {
        _db = db;
        _bus = bus;
        _config = config;
        _loyalty = loyalty;
        _twitch = twitch;
        Random = random;
        _log = log;
        _settings = db.GetKv(Key) is { } json ? JsonSerializer.Deserialize<GameSettings>(json, CherBotJson.Options) ?? new() : new();
    }

    public IGameRandom Random { get; set; }
    public GameSettings Settings => _settings;
    private string Cur => _loyalty.Currency;

    /// <summary>Delay used before announcing heist results; shortened in tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public void Save(GameSettings settings)
    {
        _settings = settings;
        _db.SetKv(Key, JsonSerializer.Serialize(settings, CherBotJson.Options));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_db.GetKv(SeedKey) is null) Seed();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Creates the game commands once, disabled; they are switched on from the Loyalty &amp; Games page.</summary>
    internal void Seed()
    {
        void Add(string name, string text, int type, object? fields = null) =>
            Seeding.CommandAction(_config, Group, name, text, [Seeding.Step(type, fields), Seeding.Reply()], enabled: false,
                configure: c => { c.GlobalCooldown = 0; c.UserCooldown = 0; });
        Add("!gamble", "!gamble\n!roll", GameHandlers.Gamble);
        Add("!slots", "!slots", GameHandlers.Slots);
        Add("!coinflip", "!coinflip\n!flip", GameHandlers.Coinflip);
        Add("!duel", "!duel", GameHandlers.Duel, new { mode = "challenge" });
        Add("!accept", "!accept", GameHandlers.Duel, new { mode = "accept" });
        Add("!decline", "!decline", GameHandlers.Duel, new { mode = "decline" });
        Add("!heist", "!heist", GameHandlers.Heist);
        Add("!roulette", "!roulette", GameHandlers.Roulette);
        _db.SetKv(SeedKey, "true");
        _log.LogInformation("Seeded game commands (disabled): !gamble, !slots, !coinflip, !duel, !accept, !decline, !heist, !roulette");
    }

    // ── shared rules ─────────────────────────────────────────────────────────

    /// <summary>Parses "100", "1k", "2.5m", "all", "half" or "25%" against a balance. Returns null when unreadable.</summary>
    public static long? ParseBet(string? text, long balance)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToLowerInvariant().Replace(",", "");
        if (t is "all" or "allin" or "max") return balance;
        if (t == "half") return balance / 2;
        if (t.EndsWith('%') && double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            return pct is > 0 and <= 100 ? (long)Math.Floor(balance * pct / 100) : null;
        var mult = 1.0;
        if (t.EndsWith('k')) { mult = 1_000; t = t[..^1]; }
        else if (t.EndsWith('m')) { mult = 1_000_000; t = t[..^1]; }
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > 0 ? (long)Math.Floor(n * mult) : null;
    }

    private long Balance(GamePlayer p) => _db.GetPoints(Platform, p.UserId);

    private long Pay(GamePlayer p, long delta) => _db.AddPoints(Platform, p.UserId, p.Name, delta);

    /// <summary>Checks cooldown, bet syntax, limits and balance. Returns an error reply, or null with the bet.</summary>
    private string? Validate(string game, BetRules rules, GamePlayer p, string? betText, string usage, out long bet, bool checkCooldown = true)
    {
        bet = 0;
        if (checkCooldown && rules.CooldownSeconds > 0 && _lastPlay.TryGetValue((game, p.UserId), out var last))
        {
            var left = rules.CooldownSeconds - (DateTimeOffset.UtcNow - last).TotalSeconds;
            if (left > 0) return $"{p.Name}, you can play {game} again in {Math.Ceiling(left)}s.";
        }
        var balance = Balance(p);
        if (ParseBet(betText, balance) is not { } parsed) return $"{p.Name}, usage: {usage}";
        if (parsed < rules.MinBet) return $"{p.Name}, the minimum bet is {rules.MinBet} {Cur}.";
        if (rules.MaxBet > 0 && parsed > rules.MaxBet) return $"{p.Name}, the maximum bet is {rules.MaxBet} {Cur}.";
        if (parsed > balance) return $"{p.Name}, you only have {balance} {Cur}.";
        bet = parsed;
        return null;
    }

    private void Played(string game, GamePlayer p) => _lastPlay[(game, p.UserId)] = DateTimeOffset.UtcNow;

    private void Result(string game, GamePlayer p, long bet, long net, string detail) =>
        _bus.Publish(new BotEvent("CherBot", "GameResult", new Dictionary<string, object?>
        {
            ["game"] = game, ["user"] = p.Name, ["userId"] = p.UserId, ["userName"] = p.Login, ["bet"] = bet,
            ["won"] = net > 0, ["net"] = net, ["balance"] = Balance(p), ["detail"] = detail, ["currency"] = Cur,
        }));

    private static long Winnings(long bet, double multiplier) => (long)Math.Floor(bet * multiplier) - bet;

    // ── gamble ───────────────────────────────────────────────────────────────

    public string Gamble(GamePlayer p, string? betText)
    {
        var s = _settings.Gamble;
        if (Validate("gamble", s, p, betText, $"!gamble <amount|all|half|25%>", out var bet) is { } error) return error;
        Played("gamble", p);
        var roll = Random.Next(1, 101);
        var won = roll <= s.WinChancePercent;
        var net = won ? Winnings(bet, s.PayoutMultiplier) : -bet;
        var balance = Pay(p, net);
        Result("gamble", p, bet, net, $"rolled {roll}");
        return won
            ? $"{p.Name} rolled {roll} and won {net} {Cur}! Now {balance} {Cur}."
            : $"{p.Name} rolled {roll} and lost {bet} {Cur}. Now {balance} {Cur}.";
    }

    // ── slots ────────────────────────────────────────────────────────────────

    public string Slots(GamePlayer p, string? betText)
    {
        var s = _settings.Slots;
        if (s.Symbols.Count < 2) return "Slots are not configured.";
        if (Validate("slots", s, p, betText, "!slots <amount>", out var bet) is { } error) return error;
        Played("slots", p);
        var reels = Enumerable.Range(0, 3).Select(_ => s.Symbols[Random.Next(0, s.Symbols.Count)]).ToArray();
        var counts = reels.GroupBy(x => x).Select(g => (Symbol: g.Key, Count: g.Count())).OrderByDescending(x => x.Count).First();
        var multiplier = counts.Count switch
        {
            3 when counts.Symbol == s.JackpotSymbol => s.JackpotMultiplier,
            3 => s.ThreeOfAKindMultiplier,
            2 => s.TwoOfAKindMultiplier,
            _ => 0,
        };
        var net = multiplier > 0 ? Winnings(bet, multiplier) : -bet;
        var balance = Pay(p, net);
        var line = string.Join(" | ", reels);
        Result("slots", p, bet, net, line);
        if (multiplier == 0) return $"[ {line} ] {p.Name} lost {bet} {Cur}. Now {balance} {Cur}.";
        var label = counts.Count == 3 && counts.Symbol == s.JackpotSymbol ? "JACKPOT! " : "";
        return net >= 0
            ? $"[ {line} ] {label}{p.Name} won {net} {Cur}! Now {balance} {Cur}."
            : $"[ {line} ] {p.Name} got some back but lost {-net} {Cur}. Now {balance} {Cur}.";
    }

    // ── coinflip ─────────────────────────────────────────────────────────────

    public string Coinflip(GamePlayer p, string? first, string? second)
    {
        var s = _settings.Coinflip;
        // Accept both "!coinflip heads 100" and "!coinflip 100 heads".
        static string? Side(string? x) => x?.ToLowerInvariant() switch { "heads" or "head" or "h" => "heads", "tails" or "tail" or "t" => "tails", _ => null };
        var side = Side(first) ?? Side(second);
        var betText = Side(first) is null ? first : second;
        if (side is null) return $"{p.Name}, usage: !coinflip <heads|tails> <amount>";
        if (Validate("coinflip", s, p, betText, "!coinflip <heads|tails> <amount>", out var bet) is { } error) return error;
        Played("coinflip", p);
        var landed = Random.Next(0, 2) == 0 ? "heads" : "tails";
        var won = landed == side;
        var net = won ? Winnings(bet, s.PayoutMultiplier) : -bet;
        var balance = Pay(p, net);
        Result("coinflip", p, bet, net, landed);
        return won
            ? $"The coin landed on {landed}! {p.Name} won {net} {Cur}. Now {balance} {Cur}."
            : $"The coin landed on {landed}. {p.Name} lost {bet} {Cur}. Now {balance} {Cur}.";
    }

    // ── roulette ─────────────────────────────────────────────────────────────

    public string Roulette(GamePlayer p, string? first, string? second)
    {
        var s = _settings.Roulette;
        static string? Choice(string? x)
        {
            var t = x?.Trim().ToLowerInvariant();
            if (t is "red" or "black" or "green" or "even" or "odd" or "low" or "high") return t;
            return int.TryParse(t, out var n) && n is >= 0 and <= 36 ? n.ToString(CultureInfo.InvariantCulture) : null;
        }
        // "!roulette 100 red" or "!roulette red 100" — the first token that parses as a choice is the choice.
        var (betText, choice) = Choice(second) is { } c2 ? (first, c2) : (second, Choice(first));
        if (choice is null || betText is null)
            return $"{p.Name}, usage: !roulette <amount> <red|black|green|even|odd|low|high|0-36>";
        if (Validate("roulette", s, p, betText, "!roulette <amount> <red|black|green|0-36>", out var bet) is { } error) return error;
        Played("roulette", p);

        var spin = Random.Next(0, 37);
        var color = spin == 0 ? "green" : RedNumbers.Contains(spin) ? "red" : "black";
        var multiplier = choice switch
        {
            "red" or "black" => color == choice ? s.EvenMoneyMultiplier : 0,
            "green" => spin == 0 ? s.GreenMultiplier : 0,
            "even" => spin != 0 && spin % 2 == 0 ? s.EvenMoneyMultiplier : 0,
            "odd" => spin % 2 == 1 ? s.EvenMoneyMultiplier : 0,
            "low" => spin is >= 1 and <= 18 ? s.EvenMoneyMultiplier : 0,
            "high" => spin is >= 19 and <= 36 ? s.EvenMoneyMultiplier : 0,
            _ => int.Parse(choice, CultureInfo.InvariantCulture) == spin ? s.NumberMultiplier : 0,
        };
        var net = multiplier > 0 ? Winnings(bet, multiplier) : -bet;
        var balance = Pay(p, net);
        Result("roulette", p, bet, net, $"{spin} {color}");
        return net > 0
            ? $"The ball lands on {spin} {color}! {p.Name} won {net} {Cur}. Now {balance} {Cur}."
            : $"The ball lands on {spin} {color}. {p.Name} lost {bet} {Cur}. Now {balance} {Cur}.";
    }

    // ── duel ─────────────────────────────────────────────────────────────────

    private sealed record Duel(GamePlayer Challenger, string TargetLogin, long Bet, DateTimeOffset Expires);

    public IReadOnlyList<object> PendingDuels() =>
        _duels.Values.Where(d => d.Expires > DateTimeOffset.UtcNow)
            .Select(d => (object)new { challenger = d.Challenger.Name, target = d.TargetLogin, bet = d.Bet, expires = d.Expires }).ToList();

    public string DuelChallenge(GamePlayer p, string? target, string? betText)
    {
        var s = _settings.Duel;
        var login = target?.Trim().TrimStart('@').ToLowerInvariant();
        if (string.IsNullOrEmpty(login)) return $"{p.Name}, usage: !duel @user <amount>";
        if (login == p.Login.ToLowerInvariant()) return $"{p.Name}, you can't duel yourself.";
        if (Validate("duel", s, p, betText, "!duel @user <amount>", out var bet) is { } error) return error;
        if (_duels.TryGetValue(login, out var existing) && existing.Expires > DateTimeOffset.UtcNow)
            return $"{p.Name}, {login} already has a duel waiting.";
        Played("duel", p);
        var duel = new Duel(p, login, bet, DateTimeOffset.UtcNow.AddSeconds(Math.Max(10, s.TimeoutSeconds)));
        _duels[login] = duel;
        _ = ExpireDuelAsync(duel);
        return $"{p.Name} challenges @{login} to a duel for {bet} {Cur}! Type !accept or !decline within {Math.Max(10, s.TimeoutSeconds)}s.";
    }

    private async Task ExpireDuelAsync(Duel duel)
    {
        await Delay(duel.Expires - DateTimeOffset.UtcNow is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero, CancellationToken.None);
        if (_duels.TryGetValue(duel.TargetLogin, out var current) && ReferenceEquals(current, duel) && _duels.TryRemove(duel.TargetLogin, out _))
            await Announce($"@{duel.TargetLogin} didn't answer {duel.Challenger.Name}'s duel in time.");
    }

    public string DuelAccept(GamePlayer p)
    {
        if (!_duels.TryRemove(p.Login.ToLowerInvariant(), out var duel) || duel.Expires < DateTimeOffset.UtcNow)
            return $"{p.Name}, nobody has challenged you to a duel.";
        if (Balance(p) < duel.Bet) return $"{p.Name}, you need {duel.Bet} {Cur} to accept.";
        if (Balance(duel.Challenger) < duel.Bet) return $"{duel.Challenger.Name} no longer has {duel.Bet} {Cur}; the duel is off.";
        var challengerWins = Random.Next(0, 2) == 0;
        var (winner, loser) = challengerWins ? (duel.Challenger, p) : (p, duel.Challenger);
        Pay(winner, duel.Bet);
        Pay(loser, -duel.Bet);
        Result("duel", winner, duel.Bet, duel.Bet, $"beat {loser.Name}");
        Result("duel", loser, duel.Bet, -duel.Bet, $"lost to {winner.Name}");
        return $"⚔️ {winner.Name} defeats {loser.Name} and takes {duel.Bet} {Cur}!";
    }

    public string DuelDecline(GamePlayer p) =>
        _duels.TryRemove(p.Login.ToLowerInvariant(), out var duel)
            ? $"{p.Name} declined {duel.Challenger.Name}'s duel."
            : $"{p.Name}, there is no duel to decline.";

    // ── heist ────────────────────────────────────────────────────────────────

    private sealed class Heist(DateTimeOffset closes)
    {
        public DateTimeOffset Closes { get; } = closes;
        public List<(GamePlayer Player, long Bet)> Crew { get; } = [];
    }

    public object? HeistState()
    {
        lock (_heistLock)
            return _heist is null
                ? new { active = false, cooldownUntil = _heistCooldownUntil }
                : new { active = true, closes = _heist.Closes, crew = _heist.Crew.Select(c => new { user = c.Player.Name, bet = c.Bet }).ToList() };
    }

    public string HeistJoin(GamePlayer p, string? betText)
    {
        var s = _settings.Heist;
        bool starting;
        lock (_heistLock)
        {
            if (_heist is null && DateTimeOffset.UtcNow < _heistCooldownUntil)
                return $"{p.Name}, the crew is lying low. Next heist in {Math.Ceiling((_heistCooldownUntil - DateTimeOffset.UtcNow).TotalSeconds)}s.";
            if (_heist?.Crew.Any(c => c.Player.UserId == p.UserId) == true) return $"{p.Name}, you're already in the crew.";
            if (Validate("heist", s, p, betText, "!heist <amount>", out var bet, checkCooldown: false) is { } error) return error;
            starting = _heist is null;
            _heist ??= new Heist(DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, s.JoinSeconds)));
            _heist.Crew.Add((p, bet));
            Pay(p, -bet);
            if (starting) _ = RunHeistAsync(_heist);
            return starting
                ? $"{p.Name} is planning a heist with {bet} {Cur}! Join with !heist <amount> in the next {Math.Max(5, s.JoinSeconds)}s."
                : $"{p.Name} joined the heist with {bet} {Cur}. Crew size: {_heist.Crew.Count}.";
        }
    }

    private async Task RunHeistAsync(Heist heist)
    {
        await Delay(heist.Closes - DateTimeOffset.UtcNow is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero, CancellationToken.None);
        var s = _settings.Heist;
        List<(GamePlayer Player, long Bet)> crew;
        lock (_heistLock)
        {
            crew = [.. heist.Crew];
            _heist = null;
            _heistCooldownUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, s.CooldownSeconds));
        }
        await Announce(ResolveHeist(crew, s));
    }

    internal string ResolveHeist(List<(GamePlayer Player, long Bet)> crew, HeistSettings s)
    {
        if (crew.Count < Math.Max(1, s.MinPlayers))
        {
            foreach (var (player, bet) in crew) Pay(player, bet);
            return $"The heist was called off: it needs {s.MinPlayers} players. Everyone got their {Cur} back.";
        }
        var chance = Math.Min(s.MaxSuccessPercent, s.BaseSuccessPercent + s.SuccessPerPlayerPercent * (crew.Count - 1));
        var survivors = new List<string>();
        var caught = new List<string>();
        foreach (var (player, bet) in crew)
        {
            if (Random.Next(1, 101) <= chance)
            {
                var payout = (long)Math.Floor(bet * s.PayoutMultiplier);
                Pay(player, payout);
                survivors.Add($"{player.Name} (+{payout - bet})");
                Result("heist", player, bet, payout - bet, "escaped");
            }
            else
            {
                caught.Add(player.Name);
                Result("heist", player, bet, -bet, "caught");
            }
        }
        if (survivors.Count == 0) return $"🚨 The heist failed! The whole crew was caught: {string.Join(", ", caught)}.";
        var tail = caught.Count > 0 ? $" Caught: {string.Join(", ", caught)}." : " Nobody was caught!";
        return $"💰 Heist complete ({chance}% odds)! Escaped: {string.Join(", ", survivors)}.{tail}";
    }

    private async Task Announce(string text)
    {
        try { await _twitch.SendChatAsync(text, useBot: true, fallback: true, null, CancellationToken.None); }
        catch (Exception ex) { _log.LogWarning(ex, "Game announcement failed"); }
        _bus.Publish(new BotEvent("CherBot", "GameAnnouncement", new Dictionary<string, object?> { ["message"] = text }));
    }
}
