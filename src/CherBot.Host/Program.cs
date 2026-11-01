using System.Text.Json;
using CherBot.Compat.Import;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Host;
using CherBot.Host.Security;
using CherBot.Integrations.Community;
using CherBot.Integrations.Twitch;

// Subcommands for headless use; anything else starts the engine.
//   cherbot import <file|-> [--dry-run] [--no-overwrite] [--rewrite-paths]
//   cherbot export [--name <name>]          (all actions, commands and timers)
//   cherbot token                           (prints the management API token)
//   cherbot twitch-login <broadcaster|bot>  (device code login from a terminal)
//   cherbot migrate-python <cherbot.db>     (imports the Python prototype's data)
switch (args.FirstOrDefault())
{
    case "api":
    case "pack":
        return await OnlineCli.RunAsync(args);
    case "import":
        {
            var rest = args.Skip(1).ToArray();
            var file = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            if (file is null)
            {
                Console.Error.WriteLine("usage: cherbot import <file|-> [--dry-run] [--no-overwrite] [--rewrite-paths] [--data-dir <dir>]");
                return 2;
            }
            var app = CherBotHost.Build(HostArgs(rest));
            var text = file == "-" ? await Console.In.ReadToEndAsync() : await File.ReadAllTextAsync(file);
            var report = app.Services.GetRequiredService<StreamerBotImporter>().Import(text, new ImportOptions
            {
                DryRun = rest.Contains("--dry-run"),
                OverwriteByName = !rest.Contains("--no-overwrite"),
                RewritePaths = rest.Contains("--rewrite-paths"),
            });
            Console.WriteLine(JsonSerializer.Serialize(report, CherBotJson.Indented));
            return 0;
        }
    case "export":
        {
            var rest = args.Skip(1).ToArray();
            var app = CherBotHost.Build(HostArgs(rest));
            var cfg = app.Services.GetRequiredService<ConfigService>();
            var nameIdx = Array.IndexOf(rest, "--name");
            var name = nameIdx >= 0 && nameIdx + 1 < rest.Length ? rest[nameIdx + 1] : "CherBot export";
            Console.WriteLine(app.Services.GetRequiredService<StreamerBotImporter>()
                .Export(cfg.Actions.Select(a => a.Id), cfg.Commands.Select(c => c.Id), cfg.Timers.Select(t => t.Id), name));
            return 0;
        }
    case "token":
        {
            var app = CherBotHost.Build(HostArgs(args.Skip(1).ToArray()));
            Console.WriteLine(app.Services.GetRequiredService<ApiToken>().Value);
            return 0;
        }
    case "twitch-login":
        {
            var rest = args.Skip(1).ToArray();
            if (rest.Length == 0 || !Enum.TryParse<TwitchRole>(rest[0], true, out var role))
            {
                Console.Error.WriteLine("usage: cherbot twitch-login <broadcaster|bot> [--data-dir <dir>] [--CherBot:Twitch:ClientId <id>]");
                return 2;
            }
            var app = CherBotHost.Build(HostArgs(rest));
            var auth = app.Services.GetRequiredService<TwitchAuth>();
            var flow = await auth.StartDeviceFlowAsync(role, CancellationToken.None);
            Console.WriteLine($"Open {flow.VerificationUri} and enter the code: {flow.UserCode}");
            Console.WriteLine($"(sign in as the {role.ToString().ToLowerInvariant()} account; the code expires at {flow.ExpiresAt:t})");
            while (auth.Flow(role) is { Status: "pending" }) await Task.Delay(1000);
            var final = auth.Flow(role)!;
            Console.WriteLine(final.Status == "complete" ? $"Linked {role.ToString().ToLowerInvariant()} account: {final.Login}" : $"Login {final.Status}: {final.Error}");
            return final.Status == "complete" ? 0 : 1;
        }
    case "migrate-python":
        {
            var rest = args.Skip(1).ToArray();
            var path = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            if (path is null)
            {
                Console.Error.WriteLine("usage: cherbot migrate-python <path/to/python/cherbot.db> [--data-dir <dir>]");
                return 2;
            }
            var app = CherBotHost.Build(HostArgs(rest));
            var report = app.Services.GetRequiredService<PythonMigration>().Run(path);
            Console.WriteLine(JsonSerializer.Serialize(report, CherBotJson.Indented));
            return 0;
        }
    default:
        await CherBotHost.Build(args).RunAsync();
        return 0;
}

// Keeps --data-dir and configuration overrides, drops subcommand flags.
static string[] HostArgs(string[] rest)
{
    var keep = new List<string>();
    for (var i = 0; i < rest.Length; i++)
    {
        if (rest[i].StartsWith("--CherBot:", StringComparison.OrdinalIgnoreCase) || rest[i] == "--data-dir")
        {
            keep.Add(rest[i]);
            if (i + 1 < rest.Length) keep.Add(rest[++i]);
        }
        else if (rest[i].StartsWith("--data-dir=", StringComparison.Ordinal)) keep.Add(rest[i]);
    }
    return [.. keep];
}
