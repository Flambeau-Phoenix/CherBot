using System.Text.Json;
using System.Text.Json.Serialization;
using CherBot.Core.Model;
using CherBot.Integrations.CustomCommands;

namespace CherBot.Host.Sharing;

public sealed class FeaturePack
{
    public int FormatVersion { get; set; } = 1;
    [JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
    public string Version { get; set; } = "1.0.0";
    public string Name { get; set; } = "My stream features";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string MinimumCherBotVersion { get; set; } = CherBotHost.Version;
    public List<ActionDef> Actions { get; set; } = [];
    public List<CommandDef> Commands { get; set; } = [];
    public List<TimerDef> Timers { get; set; } = [];
    public List<QueueDef> Queues { get; set; } = [];
    public List<CustomCommandDef> CustomCommands { get; set; } = [];
    public Dictionary<string, JsonElement> Settings { get; set; } = [];
    public Dictionary<string, JsonElement> Globals { get; set; } = [];
}

public sealed record PackExport(List<Guid>? Actions, List<string>? Settings, List<string>? Globals, string? Name, string? Description, string? Author, Guid? Id, string? Version);
public sealed record PackRequest(FeaturePack Pack, bool Replace = false, Dictionary<string, string>? Mappings = null);
public sealed record PackPreview(bool CanApply, bool AlreadyInstalled, List<string> Changes, List<string> Conflicts, List<string> Missing, List<string> Review, List<string> Errors);
