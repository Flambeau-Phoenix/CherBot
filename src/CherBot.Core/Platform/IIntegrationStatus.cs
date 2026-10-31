namespace CherBot.Core.Platform;

public sealed record IntegrationStatus(string Name, string State, bool Connected, string? Detail = null, IReadOnlyDictionary<string, object?>? Extra = null);

/// <summary>Reported by every integration for /health, the web UI and the Windows tray.</summary>
public interface IIntegrationStatus
{
    IntegrationStatus GetStatus();
}
