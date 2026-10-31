namespace CherBot.Core.Platform;

/// <summary>Declares which EventType ids a component can raise, so imports can flag triggers that will never fire.</summary>
public interface IEventCatalog
{
    IEnumerable<int> EmittedEventTypes { get; }
}

/// <summary>Event types raised by the core engine itself.</summary>
public sealed class CoreEventCatalog : IEventCatalog
{
    public IEnumerable<int> EmittedEventTypes =>
    [
        1,                      // General Custom
        401, 402,               // Command Triggered / Cooldown
        601, 602,               // Quote Added / Show
        701, 702,               // Timed Action, Test
        706, 707,               // Started, Exiting
        709, 710, 711, 712,     // global variable events
        801, 806,               // Raw Action / Action Completed
        18001, 18002,           // Custom Event / Custom Code Event
    ];
}
