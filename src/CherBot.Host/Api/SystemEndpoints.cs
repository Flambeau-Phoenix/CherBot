using System.Runtime.InteropServices;
using CherBot.Core.Engine;
using CherBot.Core.Platform;
using CherBot.Storage;

namespace CherBot.Host.Api;

internal static class SystemEndpoints
{
    public static void Map(WebApplication app, CherBotHostOptions options)
    {
        var started = DateTimeOffset.UtcNow;

        app.MapGet("/health", (CherBotDb db, QueueManager queues, ISoundPlayer sound, IEnumerable<IIntegrationStatus> integrations) => Results.Ok(new
        {
            status = "ok",
            service = "cherbot",
            version = CherBotHost.Version,
            instanceName = options.InstanceName,
            os = RuntimeInformation.OSDescription,
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - started).TotalSeconds,
            dataDir = options.DataDir,
            schemaVersion = db.SchemaVersion,
            sound = sound.Name,
            queues = queues.Status().Count,
            integrations = integrations.Select(i => i.GetStatus()).ToList(),
        }));

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
