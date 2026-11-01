using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Generated;
using CherBot.Core.Model;
using CherBot.Core.State;
using CherBot.Integrations.Community;
using CherBot.Integrations.CustomCommands;
using CherBot.Integrations.Loyalty;
using CherBot.Integrations.Modules;
using CherBot.Integrations.Games;
using CherBot.Integrations.Obs;
using CherBot.Integrations.Songs;
using CherBot.Storage;

namespace CherBot.Host.Sharing;

/// <summary>Feature packs have an allow-listed configuration surface, separate from private backups and legacy imports.</summary>
public sealed class FeaturePackService(CherBotDb db, ConfigService config, VariableService variables, ModuleRegistry modules,
    SubActionRegistry registry, CustomCommandService custom, GameService games, ObsService obs, SongRequestService songs)
{
    private readonly object _gate = new();
    public static readonly string[] SettingKeys = ["loyalty.settings", "first.settings", "games.settings", "songs.settings", "modules.chat-alerts", "modules.emote-pyramid", "modules.spam-filter"];
    private static readonly HashSet<string> ExternalFields = new(StringComparer.OrdinalIgnoreCase)
        { "connectionId", "obsConnection", "sceneName", "sourceName", "filterName", "rewardId", "soundFile", "fileName", "filePath", "folderPath" };
    private static readonly Dictionary<string, string> ReferenceFields = new(StringComparer.OrdinalIgnoreCase)
    { ["actionId"] = "action", ["commandId"] = "command", ["timerId"] = "timer", ["queue"] = "queue", ["queueId"] = "queue", ["customCommandId"] = "customCommand" };

    private static JsonNode Node(object value) => JsonSerializer.SerializeToNode(value, CherBotJson.Options)!;
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(CherBotJson.Serialize(value), CherBotJson.Options)!;
    private static string Fingerprint(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CherBotJson.Serialize(value))));

    private static IEnumerable<(JsonObject Object, string Field, string Value)> Fields(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj.ToArray())
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text)) yield return (obj, pair.Key, text);
                foreach (var child in Fields(pair.Value)) yield return child;
            }
        else if (node is JsonArray array) foreach (var item in array) foreach (var child in Fields(item)) yield return child;
    }

    public FeaturePack Export(PackExport selection)
    {
        var pack = new FeaturePack { Id = selection.Id ?? Guid.NewGuid(), Version = selection.Version ?? "1.0.0", Name = selection.Name ?? "My stream features", Description = selection.Description ?? "", Author = selection.Author ?? "" };
        var pending = new Queue<(string Kind, Guid Id)>((selection.Actions ?? []).Select(id => ("action", id)));
        var seen = new HashSet<(string, Guid)>();
        while (pending.TryDequeue(out var item))
        {
            if (!seen.Add(item)) continue;
            object? record = item.Kind switch { "action" => config.GetAction(item.Id), "command" => config.GetCommand(item.Id), "timer" => config.GetTimer(item.Id), "queue" => config.GetQueue(item.Id), "customCommand" => custom.Get(item.Id), _ => null };
            if (record is null) throw new ArgumentException($"Missing {item.Kind} dependency {item.Id}.");
            switch (record)
            {
                case ActionDef a: pack.Actions.Add(Clone(a)); break;
                case CommandDef c: pack.Commands.Add(Clone(c)); break;
                case TimerDef t: pack.Timers.Add(Clone(t)); break;
                case QueueDef q: pack.Queues.Add(Clone(q)); break;
                case CustomCommandDef d: if (!pack.CustomCommands.Any(c => c.Id == d.Id)) pack.CustomCommands.Add(Clone(d)); break;
            }
            foreach (var (_, field, value) in Fields(Node(record)))
                if (ReferenceFields.TryGetValue(field, out var kind) && Guid.TryParse(value, out var id)) pending.Enqueue((kind, id));
            if (record is ActionDef action)
                foreach (var definition in custom.All.Where(d => d.ActionId == action.Id))
                {
                    if (!pack.CustomCommands.Any(d => d.Id == definition.Id)) pack.CustomCommands.Add(Clone(definition));
                    if (definition.CommandId is { } command) pending.Enqueue(("command", command));
                }
        }
        foreach (var key in selection.Settings ?? [])
        {
            if (!SettingKeys.Contains(key)) throw new ArgumentException($"Setting {key} is not shareable.");
            if (db.GetKv(key) is { } value) pack.Settings[key] = JsonDocument.Parse(value).RootElement.Clone();
            else
            {
                object? settings = key switch
                {
                    "loyalty.settings" => modules.Get("loyalty")?.Settings,
                    "first.settings" => new FirstSettings(),
                    "games.settings" => games.Settings,
                    "songs.settings" => songs.Settings,
                    _ => modules.Get(key[8..])?.Settings
                };
                if (settings is not null) pack.Settings[key] = JsonSerializer.SerializeToElement(settings, CherBotJson.Options);
            }
        }
        foreach (var key in selection.Globals ?? [])
        {
            if (!variables.TryGetGlobal(key, true, out var value)) throw new ArgumentException($"Global {key} does not exist.");
            pack.Globals[key] = JsonSerializer.SerializeToElement(value, CherBotJson.Options);
        }
        return pack;
    }

    private FeaturePack Prepare(PackRequest request)
    {
        if (request.Pack is null || request.Pack.Actions is null || request.Pack.Commands is null || request.Pack.Timers is null || request.Pack.Queues is null || request.Pack.CustomCommands is null || request.Pack.Settings is null || request.Pack.Globals is null)
            throw new ArgumentException("Pack collections cannot be null.");
        var node = Node(request.Pack);
        // Map by field:value rather than replacing arbitrary user strings or script source.
        foreach (var (obj, field, value) in Fields(node).ToList())
            if (ExternalFields.Contains(field) && request.Mappings?.TryGetValue(field + ":" + value, out var replacement) == true) obj[field] = replacement;
        if (request.Replace)
        {
            // Reuse destination IDs so actions already referring to a replaced command or queue keep working.
            var remap = new Dictionary<Guid, Guid>();
            foreach (var (kind, id, name, record) in Records(request.Pack))
            {
                if (db.GetDoc(kind, id.ToString()) is not null) continue;
                var candidates = db.GetDocs(kind).Where(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || record is CommandDef command &&
                    (CherBotJson.Deserialize<CommandDef>(d.Json)?.Aliases().Intersect(command.Aliases(), StringComparer.OrdinalIgnoreCase).Any() ?? false)).ToList();
                if (candidates.Count == 1) remap[id] = Guid.Parse(candidates[0].Id);
            }
            foreach (var (obj, field, value) in Fields(node).ToList())
                if ((field == "id" || ReferenceFields.ContainsKey(field)) && Guid.TryParse(value, out var id) && remap.TryGetValue(id, out var destination)) obj[field] = destination.ToString();
        }
        return node.Deserialize<FeaturePack>(CherBotJson.Options)!;
    }

    private static IEnumerable<(string Kind, Guid Id, string Name, object Record)> Records(FeaturePack pack)
    {
        foreach (var a in pack.Actions) yield return ("action", a.Id, a.Name, a);
        foreach (var c in pack.Commands) yield return ("command", c.Id, c.Name, c);
        foreach (var t in pack.Timers) yield return ("timer", t.Id, t.Name, t);
        foreach (var q in pack.Queues) yield return ("queue", q.Id, q.Name, q);
        foreach (var d in pack.CustomCommands) yield return (CustomCommandService.Kind, d.Id, d.Name, d);
    }

    public PackPreview Preview(PackRequest request)
    {
        var pack = Prepare(request);
        var changes = new List<string>(); var conflicts = new List<string>(); var missing = new List<string>(); var review = new List<string>(); var errors = new List<string>();
        var receipt = db.GetKv("pack." + pack.Id);
        var identity = Fingerprint(request.Pack);
        var installed = receipt == identity;
        if (installed) return new(true, true, [], [], [], ["This exact pack is already installed; no configuration will change."], []);
        if (pack.FormatVersion != 1 || pack.Id == Guid.Empty) errors.Add("Unsupported pack format or empty pack ID.");
        if (!Version.TryParse(pack.Version, out _)) errors.Add("Pack version must be a semantic numeric version.");
        if (!Version.TryParse(pack.MinimumCherBotVersion, out var minimum) || minimum > Version.Parse(CherBotHost.Version)) errors.Add("This pack requires a newer or invalid CherBot version.");
        var records = Records(pack).ToList();
        if (records.Count > 1000 || Encoding.UTF8.GetByteCount(CherBotJson.Serialize(pack)) > 4_000_000) errors.Add("Pack exceeds 1000 records or 4 MB.");
        if (records.Any(r => r.Id == Guid.Empty || string.IsNullOrWhiteSpace(r.Name)) || records.GroupBy(r => (r.Kind, r.Id)).Any(g => g.Count() > 1)) errors.Add("Records need unique nonempty IDs and names.");
        foreach (var record in records)
        {
            var old = db.GetDoc(record.Kind, record.Id.ToString());
            var sameName = db.GetDocs(record.Kind).FirstOrDefault(d => d.Id != record.Id.ToString() && d.Name.Equals(record.Name, StringComparison.OrdinalIgnoreCase));
            changes.Add($"{(old is null ? "Add" : "Update")} {record.Kind}: {record.Name}");
            if (!installed && (old is not null || sameName is not null)) conflicts.Add($"{record.Kind}: {record.Name} ({old?.Id ?? sameName!.Id})");
            foreach (var (_, field, value) in Fields(Node(record.Record)))
            {
                if (ReferenceFields.TryGetValue(field, out var kind) && Guid.TryParse(value, out var id) && !records.Any(r => r.Kind == kind && r.Id == id) && db.GetDoc(kind, id.ToString()) is null)
                    missing.Add($"Missing {kind} {id} referenced by {record.Name}.");
                if (ExternalFields.Contains(field) && value.Length > 0 && !value.Contains('%'))
                {
                    var original = Fields(Node(request.Pack)).FirstOrDefault(f => f.Field == field && request.Mappings?.GetValueOrDefault(field + ":" + f.Value) == value);
                    if (original == default) missing.Add($"Map {field}:{value}");
                    else if (field.Equals("connectionId", StringComparison.OrdinalIgnoreCase) && (!Guid.TryParse(value, out var connection) || !obs.Definitions().Any(d => d.Id == connection)))
                        missing.Add($"OBS connection mapping {value} does not exist on this engine.");
                }
                if (field.Contains("code", StringComparison.OrdinalIgnoreCase) || field.Contains("script", StringComparison.OrdinalIgnoreCase)) review.Add($"Review executable code in {record.Name} ({field}).");
            }
        }
        foreach (var cmd in pack.Commands)
        {
            if (cmd.Mode == 0)
            {
                foreach (var other in config.Commands.Where(c => c.Id != cmd.Id && c.Aliases().Intersect(cmd.Aliases(), StringComparer.OrdinalIgnoreCase).Any()))
                    if (!installed) conflicts.Add($"Command text collision: {cmd.Name} with {other.Name} ({other.Id}).");
                if (pack.Commands.Any(c => c.Id != cmd.Id && c.Aliases().Intersect(cmd.Aliases(), StringComparer.OrdinalIgnoreCase).Any())) errors.Add($"Duplicate command text inside pack: {cmd.Name}.");
            }
            else try { _ = new System.Text.RegularExpressions.Regex(cmd.Command, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)); } catch (ArgumentException) { errors.Add($"Invalid regex: {cmd.Name}."); }
        }
        foreach (var action in pack.Actions)
        {
            foreach (var step in action.AllSubActions()) if (registry.Get(step.Type) is null && step.Type is not (99901 or 99902 or 99903)) errors.Add($"Unsupported step {step.Type} in {action.Name}.");
            foreach (var trigger in action.Triggers) if (EventTypes.Get(trigger.Type) is null) errors.Add($"Unknown trigger {trigger.Type} in {action.Name}.");
        }
        foreach (var setting in pack.Settings)
        {
            try { ValidateSetting(setting.Key, setting.Value); }
            catch (Exception ex) when (ex is JsonException or ArgumentException) { errors.Add(ex.Message); }
            if (!installed && db.GetKv(setting.Key) is not null) conflicts.Add($"Setting: {setting.Key}");
        }
        foreach (var global in pack.Globals) if (!installed && db.GetGlobals().Any(g => g.Name.Equals(global.Key, StringComparison.OrdinalIgnoreCase))) conflicts.Add($"Global: {global.Key}");
        if (pack.Globals.Count > 0) review.Add("Review selected globals for private values.");
        review.Add("Review scripts and user-entered text for embedded credentials. Accounts and viewer data are not exported.");
        return new(errors.Count == 0 && missing.Count == 0 && (request.Replace || conflicts.Count == 0 || installed), installed, changes, conflicts.Distinct().ToList(), missing.Distinct().ToList(), review.Distinct().ToList(), errors);
    }

    private void ValidateSetting(string key, JsonElement value)
    {
        if (!SettingKeys.Contains(key)) throw new ArgumentException($"Setting {key} is not shareable.");
        var type = key switch { "first.settings" => typeof(FirstSettings), "loyalty.settings" => typeof(LoyaltySettings), "games.settings" => typeof(GameSettings), "songs.settings" => typeof(SongSettings), _ => modules.Get(key[8..])?.Settings?.GetType() };
        if (type is null || JsonSerializer.Deserialize(value.GetRawText(), type, CherBotJson.Options) is null) throw new ArgumentException($"Invalid setting {key}.");
    }

    public PackPreview Apply(PackRequest request)
    {
        lock (_gate)
        {
            var preview = Preview(request);
            if (!preview.CanApply) throw new ArgumentException("Resolve pack errors, mappings and conflicts before applying.");
            if (preview.AlreadyInstalled) return preview;
            var pack = Prepare(request);
            foreach (var action in pack.Actions) action.Enabled = false;
            foreach (var cmd in pack.Commands) cmd.Enabled = false;
            foreach (var timer in pack.Timers) timer.Enabled = false;
            foreach (var definition in pack.CustomCommands) { definition.Enabled = false; definition.Advanced = true; }
            var records = Records(pack).ToList();
            db.InTransaction((conn, tx) =>
            {
                foreach (var (kind, id, name, record) in records)
                {
                    // Retain IDs on updates. Conflicting names/aliases are removed only after explicit replacement.
                    if (request.Replace)
                    {
                        var aliases = record is CommandDef c ? c.Aliases().ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
                        foreach (var old in db.GetDocs(kind).Where(d => d.Id != id.ToString() && (d.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || kind == "command" && (CherBotJson.Deserialize<CommandDef>(d.Json)?.Aliases().Any(aliases.Contains) ?? false))))
                        { using var remove = CherBotDb.Command(conn, "DELETE FROM docs WHERE kind=$k AND id=$id", ("$k", kind), ("$id", old.Id)); remove.Transaction = tx; remove.ExecuteNonQuery(); }
                    }
                    using var cmd = CherBotDb.Command(conn, "INSERT INTO docs(kind,id,name,json,updated) VALUES($k,$id,$n,$j,$t) ON CONFLICT(kind,id) DO UPDATE SET name=$n,json=$j,updated=$t",
                        ("$k", kind), ("$id", id.ToString()), ("$n", name), ("$j", CherBotJson.Serialize(record)), ("$t", DateTimeOffset.UtcNow.ToString("O")));
                    cmd.Transaction = tx; cmd.ExecuteNonQuery();
                }
                foreach (var setting in pack.Settings)
                {
                    var node = JsonNode.Parse(setting.Value.GetRawText())!.AsObject();
                    if (setting.Key != "games.settings")
                    {
                        foreach (var key in node.Select(p => p.Key).Where(k => k.Equals("enabled", StringComparison.OrdinalIgnoreCase)).ToArray()) node.Remove(key);
                        node["enabled"] = false;
                    }
                    using var cmd = CherBotDb.Command(conn, "INSERT INTO kv(key,json,updated) VALUES($k,$j,$t) ON CONFLICT(key) DO UPDATE SET json=$j,updated=$t", ("$k", setting.Key), ("$j", node.ToJsonString()), ("$t", DateTimeOffset.UtcNow.ToString("O")));
                    cmd.Transaction = tx; cmd.ExecuteNonQuery();
                }
                foreach (var global in pack.Globals)
                { using var cmd = CherBotDb.Command(conn, "INSERT INTO globals(name,value_json,last_write) VALUES($n,$v,$t) ON CONFLICT(name) DO UPDATE SET value_json=$v,last_write=$t", ("$n", global.Key), ("$v", global.Value.GetRawText()), ("$t", DateTimeOffset.UtcNow.ToString("O"))); cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                using var receipt = CherBotDb.Command(conn, "INSERT INTO kv(key,json,updated) VALUES($k,$j,$t) ON CONFLICT(key) DO UPDATE SET json=$j,updated=$t", ("$k", "pack." + pack.Id), ("$j", Fingerprint(request.Pack)), ("$t", DateTimeOffset.UtcNow.ToString("O")));
                receipt.Transaction = tx; receipt.ExecuteNonQuery();
            });
            config.Reload(records.Select(r => (r.Kind, r.Id)));
            variables.ReloadGlobals();
            custom.ReloadDefinitions();
            foreach (var key in pack.Settings.Keys)
                (key == "loyalty.settings" ? modules.Get("loyalty") : key.StartsWith("modules.") ? modules.Get(key[8..]) : null)?.ReloadSettings();
            if (pack.Settings.ContainsKey("games.settings")) games.ReloadSettings();
            if (pack.Settings.ContainsKey("songs.settings")) songs.ReloadSettings();
            return preview;
        }
    }
}
