using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Generated;
using CherBot.Core.Model;
using CherBot.Integrations.Twitch;
using CherBot.Storage;

namespace CherBot.Integrations.Community;

public sealed class FirstSettings
{
    public bool Enabled { get; set; }
    public int Bonus { get; set; } = 100;
}
public sealed record SetupCommand(string Name, string Text, int Step, int GlobalCooldown, int UserCooldown);

/// <summary>Managed command creation is explicit; later edits in the builder are protected by a saved fingerprint.</summary>
public sealed class QuickSetupService(ConfigService config, CherBotDb db)
{
    public FirstSettings First => db.GetKv("first.settings") is { } json ? JsonSerializer.Deserialize<FirstSettings>(json, CherBotJson.Options) ?? new() : new();
    public static readonly SetupCommand[] ClipCommands = [new("!clip", "!clip", 18, 60, 120)];
    public static readonly SetupCommand[] FirstCommands = [new("!first", "!first\n!1st", LiveCommunityHandlers.FirstClaim, 0, 0), new("!pinned", "!pinned", LiveCommunityHandlers.Pinned, 5, 10)];

    public object Preview(string workflow)
    {
        var specs = Specs(workflow);
        return new
        {
            workflow,
            commands = specs.Select(s =>
            {
                var existing = config.Commands.FirstOrDefault(c => c.Command.Split('\n').Any(t => t.Trim().Equals(s.Name, StringComparison.OrdinalIgnoreCase)));
                var action = existing is null ? null : Seeding.ActionFor(config, existing.Id);
                return new { s.Name, commandId = existing?.Id, actionId = action?.Id, customized = action is not null && db.GetKv("setup.action." + action.Id) != CherBotJson.Serialize(action) };
            })
        };
    }

    private static SetupCommand[] Specs(string workflow) => workflow switch
    {
        "first" => FirstCommands,
        "clip" => ClipCommands,
        _ => throw new ArgumentException("Unknown managed setup workflow.")
    };

    public void Apply(string workflow, bool enabled, int bonus, bool replace)
    {
        if (bonus < 0 || bonus > 1_000_000) throw new ArgumentException("First bonus must be 0–1000000.");
        var specs = Specs(workflow);
        // Validate every conflict before changing any record.
        foreach (var spec in specs)
        {
            var command = config.Commands.FirstOrDefault(c => c.Command.Split('\n').Any(t => t.Trim().Equals(spec.Name, StringComparison.OrdinalIgnoreCase)));
            var action = command is null ? null : Seeding.ActionFor(config, command.Id);
            if (command is not null && (action is null || db.GetKv("setup.action." + action.Id) != CherBotJson.Serialize(action)) && !replace)
                throw new InvalidOperationException($"{spec.Name} already has a custom or legacy action. Preview and select Replace existing workflow to convert it.");
        }
        if (workflow == "first")
        {
            // Preserve the legacy ranking action under its own command before converting !first.
            var legacy = config.Commands.FirstOrDefault(c => c.Name == "!first" && c.Group == "CherBot Community");
            if (legacy is not null && Seeding.ActionFor(config, legacy.Id) is { } old && old.AllSubActions().Any(s => s.Type == CommunityHandlers.FirstWords))
                Seeding.CommandAction(config, "CherBot Community", "!firstwords", "!firstwords", [Seeding.Step(CommunityHandlers.FirstWords), Seeding.Reply()]);
            db.SetKv("first.settings", CherBotJson.Serialize(new FirstSettings { Enabled = enabled, Bonus = bonus }));
        }
        foreach (var spec in specs)
        {
            var existing = config.Commands.FirstOrDefault(c => c.Command.Split('\n').Any(t => t.Trim().Equals(spec.Name, StringComparison.OrdinalIgnoreCase)));
            var command = existing ?? new CommandDef();
            var oldAction = existing is null ? null : Seeding.ActionFor(config, existing.Id);
            command.Name = spec.Name; command.Command = spec.Text; command.Group = "Quick Setup"; command.Enabled = enabled;
            command.GlobalCooldown = spec.GlobalCooldown; command.UserCooldown = spec.UserCooldown;
            config.SaveCommand(command);
            var trigger = new TriggerDef { Type = 401 }; trigger.Set("commandId", command.Id);
            var action = new ActionDef
            {
                Id = oldAction?.Id ?? Guid.NewGuid(),
                Name = spec.Name,
                Group = "Quick Setup",
                Triggers = [trigger],
                SubActions = [Seeding.Step(spec.Step), spec.Step == 18 ? Seeding.Step(10, new { text = "%createClipCreatedUrl%", useBot = true, fallback = true }) : Seeding.Reply()]
            };
            action.SubActions[1].Index = 1;
            config.SaveAction(action);
            db.SetKv("setup.action." + action.Id, CherBotJson.Serialize(action));
        }
    }
}

public sealed class LiveCommunityHandlers : SubActionHandlerGroup
{
    public const int FirstClaim = 50006, Pinned = 50007;
    static LiveCommunityHandlers()
    {
        SubActionTypes.RegisterCustom(new(FirstClaim, "CherBotFirstClaim", "Claim first this stream", "CherBot / Community", []));
        SubActionTypes.RegisterCustom(new(Pinned, "CherBotPinned", "Current stream spotlight", "CherBot / Community", []));
    }
    public LiveCommunityHandlers()
    {
        On(FirstClaim, (s, c) =>
        {
            var setup = c.Service<QuickSetupService>();
            var sessions = c.Service<StreamSessionService>();
            var current = sessions.Current;
            var id = c.ArgString("userId"); var name = c.ArgString("user", c.ArgString("userName"));
            var settings = setup.First;
            if (!settings.Enabled || !sessions.Live || current is not { Live: true }) { c.SetArg("reply", "!first is available during a confirmed live stream."); return; }
            if (id.Length == 0 || c.GetArg("filtered") is true || c.GetArg("isBot") is true || c.GetArg("isBroadcaster") is true || id == current.BroadcasterId)
            { c.SetArg("reply", "Only eligible viewers can claim !first."); return; }
            var won = c.Service<CherBotDb>().ClaimFirst(current.BroadcasterId, current.StreamId, id, name, settings.Bonus);
            var winner = sessions.Current;
            c.SetArg("reply", won ? $"{name} is first and pinned for this stream! Bonus: {settings.Bonus} {CommunityHandlers.Currency(c)}." : $"{winner?.WinnerName ?? "Someone"} already claimed first this stream.");
        });
        On(Pinned, (s, c) =>
        {
            var session = c.Service<StreamSessionService>().Current;
            c.SetArg("reply", session is { Live: true, WinnerName: not null } ? $"Pinned this stream: {session.WinnerName}!" : "No viewer is pinned for the current stream yet.");
        });
    }
}
