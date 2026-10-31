using System.Text.Json.Serialization;

namespace CherBot.Core.Model;

/// <summary>An action: a named list of sub-actions started by triggers, run on a queue. Shape follows Streamer.bot's actions.json v24.</summary>
public sealed class ActionDef : JsonRecord
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("queue")] public Guid? QueueId { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("excludeFromHistory")] public bool ExcludeFromHistory { get; set; }
    [JsonPropertyName("excludeFromPending")] public bool ExcludeFromPending { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    [JsonPropertyName("alwaysRun")] public bool AlwaysRun { get; set; }
    [JsonPropertyName("randomAction")] public bool RandomAction { get; set; }
    [JsonPropertyName("concurrent")] public bool Concurrent { get; set; }
    [JsonPropertyName("triggers")] public List<TriggerDef> Triggers { get; set; } = [];
    [JsonPropertyName("subActions")] public List<SubActionDef> SubActions { get; set; } = [];

    /// <summary>Rebuilds container nesting when sub-actions arrive as a flat list linked by <c>parentId</c>, and orders by <c>index</c>.</summary>
    public void Normalize()
    {
        SubActions = SubActionDef.Nest(SubActions);
        foreach (var t in Triggers) t.Id = t.Id == Guid.Empty ? Guid.NewGuid() : t.Id;
    }

    public IEnumerable<SubActionDef> AllSubActions() => SubActions.SelectMany(s => s.SelfAndDescendants());

    public SubActionDef? FindSubAction(Guid id) => AllSubActions().FirstOrDefault(s => s.Id == id);
}

public sealed class QueueDef : JsonRecord
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("blocking")] public bool Blocking { get; set; }
}

/// <summary>A trigger on an action. <see cref="Type"/> is the Streamer.bot EventType id; per-type filters live in <see cref="JsonRecord.Extra"/>.</summary>
public sealed class TriggerDef : JsonRecord
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("exclusions")] public List<Guid>? Exclusions { get; set; }
}

/// <summary>A sub-action. <see cref="Type"/> is the Streamer.bot sub-action type id; per-type fields live in <see cref="JsonRecord.Extra"/>.</summary>
public sealed class SubActionDef : JsonRecord
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("weight")] public double Weight { get; set; }
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("parentId")] public Guid? ParentId { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    [JsonPropertyName("subActions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SubActionDef>? SubActions { get; set; }

    public IEnumerable<SubActionDef> SelfAndDescendants()
    {
        yield return this;
        if (SubActions is null) yield break;
        foreach (var child in SubActions)
            foreach (var d in child.SelfAndDescendants())
                yield return d;
    }

    internal static List<SubActionDef> Nest(List<SubActionDef> list)
    {
        var all = list.SelectMany(s => s.SelfAndDescendants()).ToList();
        var byId = new Dictionary<Guid, SubActionDef>();
        foreach (var s in all)
        {
            if (s.Id == Guid.Empty) s.Id = Guid.NewGuid();
            byId.TryAdd(s.Id, s);
        }

        // Detach any child listed at the top level that names a parent we know.
        var roots = new List<SubActionDef>();
        foreach (var s in list)
        {
            if (s.ParentId is { } pid && byId.TryGetValue(pid, out var parent) && !ReferenceEquals(parent, s))
            {
                parent.SubActions ??= [];
                if (!parent.SubActions.Contains(s)) parent.SubActions.Add(s);
            }
            else
            {
                roots.Add(s);
            }
        }

        void Sort(List<SubActionDef> items)
        {
            items.Sort((a, b) => a.Index.CompareTo(b.Index));
            foreach (var i in items)
                if (i.SubActions is { } c) Sort(c);
        }
        Sort(roots);
        return roots;
    }
}

/// <summary>A chat command, field-compatible with Streamer.bot's commands.json.</summary>
public sealed class CommandDef : JsonRecord
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("include")] public bool Include { get; set; } = true;
    /// <summary>0 = Basic, 1 = Regex.</summary>
    [JsonPropertyName("mode")] public int Mode { get; set; }
    /// <summary>Command strings, one per line.</summary>
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("regexExplicitCapture")] public bool RegexExplicitCapture { get; set; }
    /// <summary>0 = Start, 1 = Exact, 2 = Anywhere.</summary>
    [JsonPropertyName("location")] public int Location { get; set; }
    [JsonPropertyName("ignoreBotAccount")] public bool IgnoreBotAccount { get; set; } = true;
    [JsonPropertyName("ignoreInternal")] public bool IgnoreInternal { get; set; } = true;
    /// <summary>Flags: 1 Twitch message, 2 whisper, 4 bot whisper, 8 resub, 16 shared chat, 1024 YouTube, 2097152 Kick.</summary>
    [JsonPropertyName("sources")] public int Sources { get; set; } = 1;
    [JsonPropertyName("persistCounter")] public bool PersistCounter { get; set; }
    [JsonPropertyName("persistUserCounter")] public bool PersistUserCounter { get; set; }
    [JsonPropertyName("caseSensitive")] public bool CaseSensitive { get; set; }
    [JsonPropertyName("globalCooldown")] public int GlobalCooldown { get; set; }
    [JsonPropertyName("userCooldown")] public int UserCooldown { get; set; }
    [JsonPropertyName("group")] public string Group { get; set; } = "";
    /// <summary>0 = Allow (only listed may run; empty = everyone), 1 = Deny (everyone except listed; empty = nobody).</summary>
    [JsonPropertyName("grantType")] public int GrantType { get; set; }
    [JsonPropertyName("permittedGroups")] public List<string> PermittedGroups { get; set; } = [];

    public IEnumerable<string> Aliases() =>
        Command.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>A timer that raises the Timed Actions trigger, field-compatible with Streamer.bot.</summary>
public sealed class TimerDef : JsonRecord
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("repeat")] public bool Repeat { get; set; } = true;
    /// <summary>Seconds between fires.</summary>
    [JsonPropertyName("interval")] public int Interval { get; set; } = 60;
    [JsonPropertyName("randomInterval")] public bool RandomInterval { get; set; }
    [JsonPropertyName("upperInterval")] public int UpperInterval { get; set; }
    /// <summary>Minimum chat lines since the last fire; 0 disables the check.</summary>
    [JsonPropertyName("lines")] public int Lines { get; set; }
    [JsonPropertyName("counter")] public int Counter { get; set; }
}
