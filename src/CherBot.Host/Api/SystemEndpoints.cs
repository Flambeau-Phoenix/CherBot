using System.Runtime.InteropServices;
using CherBot.Core.Engine;
using CherBot.Core.Platform;
using CherBot.Host.Security;
using CherBot.Storage;

namespace CherBot.Host.Api;

internal static class SystemEndpoints
{
    public static void Map(WebApplication app, CherBotHostOptions options)
    {
        var started = DateTimeOffset.UtcNow;

        // Public, so load balancers and the tray can probe it. Host details (machine name, OS, data folder) are
        // only included when the caller presents the API token.
        app.MapGet("/health", (HttpContext http, ApiToken token, CherBotDb db, QueueManager queues, ISoundPlayer sound, IEnumerable<IIntegrationStatus> integrations) =>
        {
            var detailed = token.IsAuthorized(http);
            return Results.Ok(new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["service"] = "cherbot",
                ["version"] = CherBotHost.Version,
                ["instanceName"] = detailed ? options.InstanceName : null,
                ["os"] = detailed ? RuntimeInformation.OSDescription : null,
                ["uptimeSeconds"] = (long)(DateTimeOffset.UtcNow - started).TotalSeconds,
                ["dataDir"] = detailed ? options.DataDir : null,
                ["schemaVersion"] = db.SchemaVersion,
                ["sound"] = sound.Name,
                ["queues"] = queues.Status().Count,
                // Without the token only name, state and connected are shown: the rest holds account ids, scopes,
                // error text and pending device-login codes.
                ["integrations"] = integrations.Select(i => i.GetStatus())
                    .Select(s => detailed ? (object)s : new { s.Name, s.State, s.Connected }).ToList(),
            }.Where(kv => kv.Value is not null).ToDictionary());
        });

        app.MapGet("/version", () => Results.Ok(new
        {
            service = "cherbot",
            version = CherBotHost.Version,
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            arch = RuntimeInformation.ProcessArchitecture.ToString(),
        }));
    }
}
