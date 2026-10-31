using CherBot.Core.Engine;
using CherBot.Core.Generated;

namespace CherBot.Integrations.Modules;

/// <summary>Spam Filter: Permit (50041) lets a user post links for the link filter's permit window.</summary>
public sealed class ModuleHandlers : SubActionHandlerGroup
{
    public const int Permit = 50041;

    static ModuleHandlers()
    {
        SubActionTypes.RegisterCustom(new(Permit, "CherBotSpamFilterPermit", "Spam Filter: Permit Links", "CherBot / Moderation", ["userLogin:string"]));
    }

    public ModuleHandlers()
    {
        On(Permit, (s, c) =>
        {
            var target = c.Parse(s.Str("userLogin")).Trim();
            if (target.Length == 0) target = c.ArgString("input0");
            target = target.TrimStart('@');
            if (target.Length == 0)
            {
                c.SetArg("reply", $"Usage: {c.ArgString("command", "!permit")} @user");
                return Task.CompletedTask;
            }
            var seconds = c.Service<SpamFilterModule>().Permit(target);
            c.SetArg("permitSeconds", seconds);
            c.SetArg("reply", $"@{target} may post links for the next {seconds}s.");
            return Task.CompletedTask;
        });
    }
}
