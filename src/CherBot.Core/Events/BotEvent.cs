using CherBot.Core.Generated;

namespace CherBot.Core.Events;

/// <summary>
/// An engine event. <see cref="Source"/>/<see cref="Type"/> use Streamer.bot's WebSocket naming (for example Twitch/ChatMessage),
/// and <see cref="TypeId"/> is the numeric EventType id used by triggers when the event maps to one.
/// </summary>
public sealed class BotEvent
{
    public BotEvent(string source, string type, IDictionary<string, object?>? data = null, int? typeId = null)
    {
        Source = source;
        Type = type;
        TypeId = typeId ?? EventTypes.Get(source, type)?.Id;
        Data = new Dictionary<string, object?>(data ?? new Dictionary<string, object?>(), StringComparer.OrdinalIgnoreCase);
        Timestamp = DateTimeOffset.UtcNow;
    }

    public static BotEvent FromTypeName(string enumName, IDictionary<string, object?>? data = null)
    {
        var info = EventTypes.Get(enumName) ?? throw new KeyNotFoundException($"Unknown event type '{enumName}'");
        return new BotEvent(info.Source, info.Type, data, info.Id);
    }

    public string Source { get; }
    public string Type { get; }
    public int? TypeId { get; }
    public Dictionary<string, object?> Data { get; }
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Monotonic sequence number assigned by the bus when the event is published.</summary>
    public long Seq { get; internal set; }

    /// <summary>When false the event is only delivered live and is not written to the event log (used for high-volume or private events).</summary>
    public bool Persist { get; init; } = true;

    public override string ToString() => $"{Source}.{Type}#{Seq}";
}
