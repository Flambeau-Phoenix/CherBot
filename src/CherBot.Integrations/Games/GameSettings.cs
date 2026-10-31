namespace CherBot.Integrations.Games;

public class BetRules
{
    public long MinBet { get; set; } = 10;
    public long MaxBet { get; set; } = 10_000;
    /// <summary>Seconds a user must wait between plays of this game.</summary>
    public int CooldownSeconds { get; set; } = 30;
}

public sealed class GambleSettings : BetRules
{
    public int WinChancePercent { get; set; } = 45;
    /// <summary>Total returned on a win, as a multiple of the bet (2 = the bet back plus the same again).</summary>
    public double PayoutMultiplier { get; set; } = 2.0;
}

public sealed class SlotsSettings : BetRules
{
    public List<string> Symbols { get; set; } = ["🍒", "🍋", "🔔", "⭐", "💎", "7️⃣"];
    public double TwoOfAKindMultiplier { get; set; } = 1.5;
    public double ThreeOfAKindMultiplier { get; set; } = 10;
    public string JackpotSymbol { get; set; } = "7️⃣";
    public double JackpotMultiplier { get; set; } = 50;
}

public sealed class CoinflipSettings : BetRules
{
    public double PayoutMultiplier { get; set; } = 2.0;
}

public sealed class DuelSettings : BetRules
{
    public int TimeoutSeconds { get; set; } = 60;
}

public sealed class HeistSettings : BetRules
{
    public HeistSettings() { CooldownSeconds = 300; }

    public int JoinSeconds { get; set; } = 60;
    public int MinPlayers { get; set; } = 1;
    public int BaseSuccessPercent { get; set; } = 35;
    public int SuccessPerPlayerPercent { get; set; } = 5;
    public int MaxSuccessPercent { get; set; } = 75;
    public double PayoutMultiplier { get; set; } = 2.0;
}

public sealed class RouletteSettings : BetRules
{
    /// <summary>Red/black/even/odd/low/high.</summary>
    public double EvenMoneyMultiplier { get; set; } = 2.0;
    public double GreenMultiplier { get; set; } = 14;
    public double NumberMultiplier { get; set; } = 36;
}

/// <summary>All game settings, stored in kv "games.settings" and editable at runtime.</summary>
public sealed class GameSettings
{
    public GambleSettings Gamble { get; set; } = new();
    public SlotsSettings Slots { get; set; } = new();
    public CoinflipSettings Coinflip { get; set; } = new();
    public DuelSettings Duel { get; set; } = new();
    public HeistSettings Heist { get; set; } = new();
    public RouletteSettings Roulette { get; set; } = new();
}

/// <summary>Random source for games; replaceable in tests.</summary>
public interface IGameRandom
{
    /// <summary>Returns an integer in [min, maxExclusive).</summary>
    int Next(int min, int maxExclusive);
}

public sealed class SharedGameRandom : IGameRandom
{
    public int Next(int min, int maxExclusive) => Random.Shared.Next(min, maxExclusive);
}
