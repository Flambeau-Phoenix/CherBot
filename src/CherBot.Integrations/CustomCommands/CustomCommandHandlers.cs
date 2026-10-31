using CherBot.Core.Engine;
using CherBot.Core.Generated;

namespace CherBot.Integrations.CustomCommands;

/// <summary>Custom Command Response (50040): sends a custom command's response with its tokens expanded.</summary>
public sealed class CustomCommandHandlers : SubActionHandlerGroup
{
    public const int Response = 50040;

    static CustomCommandHandlers()
    {
        SubActionTypes.RegisterCustom(new(Response, "CherBotCustomCommandResponse", "Custom Command Response", "CherBot / Commands", ["customCommandId:Guid"]));
    }

    public CustomCommandHandlers()
    {
        On(Response, async (s, c) =>
        {
            var svc = c.Service<CustomCommandService>();
            if (s.GuidOf("customCommandId") is not { } id || svc.Get(id) is not { } def) return;
            var args = c.Args.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            var lines = await svc.RespondAsync(def, args, c.Cancellation);
            c.SetArg("customCommandResponse", string.Join("\n", lines));
        });
    }
}
