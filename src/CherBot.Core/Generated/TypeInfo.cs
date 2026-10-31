namespace CherBot.Core.Generated;

/// <summary>A Streamer.bot-compatible event type: numeric id (as stored in trigger <c>type</c>) plus the WebSocket source/type pair.</summary>
public sealed record EventTypeInfo(int Id, string Name, string Source, string Type, string Display, string Category, string[] Fields);

/// <summary>A Streamer.bot-compatible sub-action type: numeric id (as stored in sub-action <c>type</c>) plus its persisted field list.</summary>
public sealed record SubActionTypeInfo(int Id, string Name, string Display, string Category, string[] Fields);

public static partial class EventTypes
{
    private static readonly Dictionary<int, EventTypeInfo> ById = All.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
    private static readonly Dictionary<string, EventTypeInfo> ByName = All.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string, string), EventTypeInfo> BySourceType =
        All.GroupBy(e => (e.Source.ToLowerInvariant(), e.Type.ToLowerInvariant())).ToDictionary(g => g.Key, g => g.First());

    public static EventTypeInfo? Get(int id) => ById.GetValueOrDefault(id);
    public static EventTypeInfo? Get(string name) => ByName.GetValueOrDefault(name);
    public static EventTypeInfo? Get(string source, string type) =>
        BySourceType.GetValueOrDefault((source.ToLowerInvariant(), type.ToLowerInvariant()));

    /// <summary>Returns the numeric id for an enum-style name such as <c>TwitchChatMessage</c>; throws when unknown.</summary>
    public static int Id(string name) => ByName.TryGetValue(name, out var info) ? info.Id : throw new KeyNotFoundException($"Unknown event type '{name}'");
}

public static partial class SubActionTypes
{
    /// <summary>First id used for CherBot's own sub-action types; Streamer.bot never uses this range.</summary>
    public const int CherBotRangeStart = 50000;

    private static readonly Dictionary<int, SubActionTypeInfo> ById = All.ToDictionary(s => s.Id);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SubActionTypeInfo> CustomById = new();

    public static SubActionTypeInfo? Get(int id) => ById.GetValueOrDefault(id) ?? CustomById.GetValueOrDefault(id);
    public static string NameOf(int id) => Get(id)?.Name ?? $"Unknown({id})";

    /// <summary>Registers a CherBot-specific sub-action type so it appears in catalogs and run traces.</summary>
    public static void RegisterCustom(SubActionTypeInfo info) => CustomById[info.Id] = info;

    public static IEnumerable<SubActionTypeInfo> Custom => CustomById.Values.OrderBy(s => s.Id);
}
