using CherBot.Core.Platform;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Core;

/// <summary>Prunes run history and the event log on a schedule.</summary>
public sealed class MaintenanceService(CherBotDb db, EngineOptions options, ILogger<MaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var runs = db.PruneRuns(TimeSpan.FromDays(Math.Max(1, options.RunHistoryDays)));
                var events = db.PruneEvents(TimeSpan.FromDays(Math.Max(1, options.EventLogDays)));
                if (runs + events > 0) log.LogInformation("Pruned {Runs} runs and {Events} events", runs, events);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Maintenance failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
