using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Generated;
using CherBot.Core.Model;

namespace CherBot.Integrations.Community;

/// <summary>Creates a command plus an action triggered by it. Seeded records are ordinary and fully editable afterwards.</summary>
public static class Seeding
{
    public static SubActionDef Step(int type, object? fields = null)
    {
        var sa = new SubActionDef { Type = type };
        if (fields is not null)
            foreach (var p in JsonSerializer.SerializeToElement(fields, CherBotJson.Options).EnumerateObject()) sa.Set(p.Name, p.Value);
        return sa;
    }

    public static SubActionDef Reply() => Step(10, new { text = "%reply%", useBot = true, fallback = true });

    /// <summary>Adds a command and its action unless a command with that name exists. Returns the command (existing or new).</summary>
    public static (CommandDef Command, ActionDef? Action) CommandAction(
        ConfigService config, string group, string name, string text, IEnumerable<SubActionDef> steps,
        bool enabled = true, Action<CommandDef>? configure = null, Action<ActionDef>? configureAction = null)
    {
        if (config.Commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is { } existing)
            return (existing, null);
        var cmd = new CommandDef { Name = name, Command = text, Group = group, Enabled = enabled, GlobalCooldown = 2, UserCooldown = 5 };
        configure?.Invoke(cmd);
        config.SaveCommand(cmd);

        var trigger = new TriggerDef { Type = EventTypes.Id("CommandTriggered") };
        trigger.Set("commandId", cmd.Id);
        var list = steps.ToList();
        for (var i = 0; i < list.Count; i++) list[i].Index = i;
        var action = new ActionDef { Name = name, Group = group, Triggers = [trigger], SubActions = list };
        configureAction?.Invoke(action);
        config.SaveAction(action);
        return (cmd, action);
    }

    /// <summary>Finds the action whose Command Triggered trigger points at the command.</summary>
    public static ActionDef? ActionFor(ConfigService config, Guid commandId) =>
        config.Actions.FirstOrDefault(a => a.Triggers.Any(t => t.Type == 401 && t.GuidOf("commandId") == commandId));
}
