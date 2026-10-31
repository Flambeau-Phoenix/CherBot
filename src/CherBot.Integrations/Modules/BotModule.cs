using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Integrations.Giveaways;
using CherBot.Integrations.Loyalty;
using CherBot.Storage;

namespace CherBot.Integrations.Modules;

/// <summary>An on/off feature listed on the Modules page.</summary>
public interface IBotModule
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    bool Enabled { get; }
    /// <summary>One line of live status for the module card.</summary>
    string Summary { get; }
    /// <summary>UI page that edits the module, when it has its own page.</summary>
    string? Page => null;
    object? Settings { get; }
    void SaveSettings(JsonElement json);
    void SetEnabled(bool enabled);
}

public interface IModuleSettings
{
    bool Enabled { get; set; }
}

/// <summary>Module whose settings live in kv <c>modules.&lt;id&gt;</c>.</summary>
public abstract class BotModule<T> : IBotModule where T : class, IModuleSettings, new()
{
    protected readonly CherBotDb Db;
    private T _settings;

    protected BotModule(CherBotDb db)
    {
        Db = db;
        _settings = db.GetKv(Key) is { } json ? JsonSerializer.Deserialize<T>(json, CherBotJson.Options) ?? new T() : new T();
    }

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual string Summary => Enabled ? "On" : "Off";
    private string Key => $"modules.{Id}";

    public T Settings => _settings;
    object? IBotModule.Settings => _settings;
    public bool Enabled => _settings.Enabled;

    public void Save(T settings)
    {
        _settings = settings;
        Db.SetKv(Key, JsonSerializer.Serialize(settings, CherBotJson.Options));
        OnSaved();
    }

    public void SaveSettings(JsonElement json) => Save(json.Deserialize<T>(CherBotJson.Options) ?? new T());

    public void SetEnabled(bool enabled)
    {
        _settings.Enabled = enabled;
        Save(_settings);
    }

    protected virtual void OnSaved() { }
}

/// <summary>Features with their own page (loyalty, games, giveaways, scene commands), listed so the Modules page is the single on/off overview.</summary>
public sealed class LoyaltyModuleCard(LoyaltyService loyalty) : IBotModule
{
    public string Id => "loyalty";
    public string Name => "Loyalty points";
    public string Description => "Points for chatting, watching and supporting, spent on games and giveaways.";
    public bool Enabled => loyalty.Settings.Enabled;
    public string Summary => $"Currency: {loyalty.Currency}";
    public string? Page => "loyalty";
    public object? Settings => loyalty.Settings;
    public void SaveSettings(JsonElement json) => loyalty.Save(json.Deserialize<LoyaltySettings>(CherBotJson.Options) ?? new());
    public void SetEnabled(bool enabled)
    {
        loyalty.Settings.Enabled = enabled;
        loyalty.Save(loyalty.Settings);
    }
}

/// <summary>A module that is on when any command in its group is enabled; switching it toggles every command in the group.</summary>
public class CommandGroupModuleCard(ConfigService config, string id, string name, string description, string group, string? page) : IBotModule
{
    public string Id => id;
    public string Name => name;
    public string Description => description;
    public string? Page => page;
    private IEnumerable<CherBot.Core.Model.CommandDef> Commands => config.Commands.Where(c => c.Group == group);
    public bool Enabled => Commands.Any(c => c.Enabled);
    public virtual string Summary => $"{Commands.Count(c => c.Enabled)} of {Commands.Count()} commands enabled";
    public object? Settings => null;
    public void SaveSettings(JsonElement json) { }

    public virtual void SetEnabled(bool enabled)
    {
        foreach (var c in Commands.Where(c => c.Enabled != enabled).ToList())
        {
            c.Enabled = enabled;
            config.SaveCommand(c);
        }
    }
}

public sealed class GiveawaysModuleCard(ConfigService config, GiveawayService giveaways) : IBotModule
{
    public string Id => "giveaways";
    public string Name => "Raffles & giveaways";
    public string Description => "Timed or open-ended giveaways with entry commands, tickets and claim timers. The switch controls the !giveaway mod command.";
    public string? Page => "giveaways";
    private CherBot.Core.Model.CommandDef? Control => config.Commands.FirstOrDefault(c => c.Group == GiveawayService.Group && c.Name == "!giveaway");
    public bool Enabled => Control?.Enabled == true || giveaways.OpenOne() is not null;
    public string Summary => giveaways.OpenOne() is { } open ? $"\"{open.Name}\" is open" : $"{giveaways.All.Count} giveaway(s), none open";
    public object? Settings => null;
    public void SaveSettings(JsonElement json) { }
    public void SetEnabled(bool enabled)
    {
        if (Control is { } c && c.Enabled != enabled)
        {
            c.Enabled = enabled;
            config.SaveCommand(c);
        }
    }
}

/// <summary>Every module, in display order.</summary>
public sealed class ModuleRegistry(IEnumerable<IBotModule> modules)
{
    private static readonly string[] Order = ["chat-alerts", "emote-pyramid", "spam-filter", "loyalty", "games", "giveaways", "scene-commands"];

    public IReadOnlyList<IBotModule> All { get; } = modules
        .OrderBy(m => Array.IndexOf(Order, m.Id) is var i && i < 0 ? int.MaxValue : i).ThenBy(m => m.Name).ToList();

    public IBotModule? Get(string id) => All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}
