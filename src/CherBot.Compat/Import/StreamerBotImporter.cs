using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Generated;
using CherBot.Core.Model;
using CherBot.Core.Platform;
using Microsoft.Extensions.Logging;

namespace CherBot.Compat.Import;

public sealed class ImportOptions
{
    /// <summary>Report what would happen without saving anything.</summary>
    public bool DryRun { get; set; }
    /// <summary>Replace existing items that have the same name (Streamer.bot's default import behaviour).</summary>
    public bool OverwriteByName { get; set; } = true;
    /// <summary>Write mapped paths back into imported sub-actions instead of only reporting them.</summary>
    public bool RewritePaths { get; set; }
}

public sealed record ImportItem(string Kind, string Id, string Name, string Status, string? Note = null);
public sealed record UnsupportedItem(string Kind, string Action, int Type, string TypeName);
public sealed record PathItem(string Action, string Field, string Original, string Mapped, bool Exists);

public sealed class ImportReport
{
    public bool DryRun { get; init; }
    public string Format { get; set; } = "";
    public string? SourceVersion { get; set; }
    public List<ImportItem> Items { get; } = [];
    public List<UnsupportedItem> Unsupported { get; } = [];
    public List<PathItem> Paths { get; } = [];
    public List<string> Warnings { get; } = [];

    public int Actions => Items.Count(i => i.Kind == "action" && i.Status != "skipped");
    public int Commands => Items.Count(i => i.Kind == "command" && i.Status != "skipped");
    public int Queues => Items.Count(i => i.Kind == "queue" && i.Status != "skipped");
    public int Timers => Items.Count(i => i.Kind == "timer" && i.Status != "skipped");
}

/// <summary>
/// Imports Streamer.bot data: actions.json (v12–v24), commands.json, export strings ("SBAE" + gzip JSON, base64) and
/// decoded export JSON. Unknown sub-action and trigger types are kept as raw JSON so they round-trip on export.
/// </summary>
public sealed class StreamerBotImporter(ConfigService config, IPathMapper paths, SubActionRegistry registry, IEnumerable<IEventCatalog> catalogs, ILogger<StreamerBotImporter> log)
{
    private static readonly byte[] ExportHeader = "SBAE"u8.ToArray();
    private static readonly string[] PathFields = ["soundFile", "folder", "file", "fileName", "path", "iconPath", "command", "workingDir"];
    private static readonly HashSet<int> ContainerTypes = [99900, 99901, 99902, 99903, 99904, 120, 124, 127, 128, 129, 130];

    public ImportReport Import(string input, ImportOptions? options = null)
    {
        options ??= new ImportOptions();
        var report = new ImportReport { DryRun = options.DryRun };
        var root = Parse(input, report);

        JsonNode? data = root;
        if (root["data"] is JsonObject d)
        {
            data = d;
            report.Format = "export";
            report.SourceVersion = root["exportedFrom"]?.ToString() ?? root["meta"]?["version"]?.ToString();
        }
        else
        {
            report.Format = root["actions"] is not null ? "actions.json" : root["commands"] is not null ? "commands.json" : "unknown";
            report.SourceVersion = root["version"]?.ToString();
        }

        var queueIds = new HashSet<Guid>(config.Queues.Select(q => q.Id));
        foreach (var q in Items(data, "queues").Select(n => n.Deserialize<QueueDef>(CherBotJson.Options)).OfType<QueueDef>())
        {
            var existing = config.GetQueue(q.Id) ?? config.FindQueue(null, q.Name);
            report.Items.Add(new ImportItem("queue", q.Id.ToString(), q.Name, existing is null ? "added" : "updated"));
            queueIds.Add(q.Id);
            if (options.DryRun) continue;
            if (existing is not null && existing.Id != q.Id) config.DeleteQueue(existing.Id);
            config.SaveQueue(q);
        }

        var commandIds = new HashSet<Guid>(config.Commands.Select(c => c.Id));
        foreach (var c in Items(data, "commands").Select(n => n.Deserialize<CommandDef>(CherBotJson.Options)).OfType<CommandDef>())
        {
            var status = Upsert("command", c.Id, c.Name, config.GetCommand(c.Id), config.FindCommand(null, c.Name), x => x.Id, options, report);
            commandIds.Add(c.Id);
            if (options.DryRun || status.Skip) continue;
            if (status.Replace is { } old) config.DeleteCommand(old);
            config.SaveCommand(c);
        }

        foreach (var t in Items(data, "timers").Select(n => n.Deserialize<TimerDef>(CherBotJson.Options)).OfType<TimerDef>())
        {
            var status = Upsert("timer", t.Id, t.Name, config.GetTimer(t.Id), config.FindTimer(null, t.Name), x => x.Id, options, report);
            if (options.DryRun || status.Skip) continue;
            if (status.Replace is { } old) config.DeleteTimer(old);
            config.SaveTimer(t);
        }

        var emitted = new HashSet<int>(catalogs.SelectMany(c => c.EmittedEventTypes));
        foreach (var node in Items(data, "actions"))
        {
            NormalizeAction(node);
            var a = node.Deserialize<ActionDef>(CherBotJson.Options);
            if (a is null) continue;
            a.Normalize();

            if (a.QueueId is { } qid && !queueIds.Contains(qid))
            {
                report.Warnings.Add($"Action '{a.Name}' uses queue {qid}, which is not in this import or on this instance; it will run on Default.");
                a.QueueId = null;
            }
            foreach (var t in a.Triggers)
            {
                var info = EventTypes.Get(t.Type);
                if (info is null || !emitted.Contains(t.Type))
                    report.Unsupported.Add(new UnsupportedItem("trigger", a.Name, t.Type, info?.Name ?? $"Unknown({t.Type})"));
                if (t.Type == EventTypes.Id("CommandTriggered") && t.GuidOf("commandId") is { } cid && !commandIds.Contains(cid))
                    report.Warnings.Add($"Action '{a.Name}' has a Command Triggered trigger for command {cid}, which was not imported.");
            }
            foreach (var sa in a.AllSubActions())
            {
                if (!ContainerTypes.Contains(sa.Type) && registry.Get(sa.Type) is null)
                    report.Unsupported.Add(new UnsupportedItem("subAction", a.Name, sa.Type, SubActionTypes.NameOf(sa.Type)));
                foreach (var field in PathFields)
                {
                    if (!sa.Has(field)) continue;
                    var original = sa.Str(field);
                    if (string.IsNullOrWhiteSpace(original) || original.Contains('%')) continue;
                    if (!LooksLikePath(original)) continue;
                    var mapped = paths.Map(original);
                    report.Paths.Add(new PathItem(a.Name, field, original, mapped, File.Exists(mapped) || Directory.Exists(mapped)));
                    if (options.RewritePaths && mapped != original) sa.Set(field, mapped);
                }
            }

            var status = Upsert("action", a.Id, a.Name, config.GetAction(a.Id), config.FindAction(null, a.Name), x => x.Id, options, report);
            if (options.DryRun || status.Skip) continue;
            if (status.Replace is { } old) config.DeleteAction(old);
            config.SaveAction(a);
        }

        if (report.Items.Count == 0) report.Warnings.Add("Nothing to import: no actions, queues, commands or timers were found.");
        log.LogInformation("Import ({Format}, dry run {DryRun}): {Actions} actions, {Commands} commands, {Queues} queues, {Timers} timers, {Unsupported} unsupported items",
            report.Format, report.DryRun, report.Actions, report.Commands, report.Queues, report.Timers, report.Unsupported.Count);
        return report;
    }

    private static bool LooksLikePath(string s) =>
        s.Length > 2 && (s[1] == ':' || s.StartsWith('/') || s.StartsWith(@"\\") || s.Contains('\\'));

    private readonly record struct UpsertResult(bool Skip, Guid? Replace);

    private static UpsertResult Upsert<T>(string kind, Guid id, string name, T? byId, T? byName, Func<T, Guid> idOf, ImportOptions options, ImportReport report)
        where T : class
    {
        if (byId is not null)
        {
            report.Items.Add(new ImportItem(kind, id.ToString(), name, "updated"));
            return new UpsertResult(false, null);
        }
        if (byName is not null)
        {
            if (!options.OverwriteByName)
            {
                report.Items.Add(new ImportItem(kind, id.ToString(), name, "skipped", "an item with this name already exists"));
                return new UpsertResult(true, null);
            }
            report.Items.Add(new ImportItem(kind, id.ToString(), name, "replaced", $"replaced existing {idOf(byName)}"));
            return new UpsertResult(false, idOf(byName));
        }
        report.Items.Add(new ImportItem(kind, id.ToString(), name, "added"));
        return new UpsertResult(false, null);
    }

    private static IEnumerable<JsonNode> Items(JsonNode? data, string key) =>
        data?[key] is JsonArray arr ? arr.Where(n => n is not null).Select(n => n!) : [];

    /// <summary>Streamer.bot 0.2.x exports call the sub-action list "actions"; 1.x uses "subActions".</summary>
    private static void NormalizeAction(JsonNode node)
    {
        if (node is not JsonObject obj) return;
        if (obj["subActions"] is null && obj["actions"] is JsonArray legacy)
        {
            obj.Remove("actions");
            obj["subActions"] = legacy;
        }
        if (obj["subActions"] is JsonArray subs)
            foreach (var s in subs)
                if (s is JsonObject so && so["subActions"] is null && so["actions"] is JsonArray nested)
                {
                    so.Remove("actions");
                    so["subActions"] = nested;
                    NormalizeAction(so);
                }
    }

    /// <summary>Accepts raw JSON, a Streamer.bot export string, or base64 of either.</summary>
    internal static JsonNode Parse(string input, ImportReport? report = null)
    {
        var text = input.Trim();
        if (text.StartsWith('{') || text.StartsWith('['))
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
                   ?? throw new InvalidDataException("Empty JSON");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(text); }
        catch (FormatException) { throw new InvalidDataException("Input is neither JSON nor a Streamer.bot export string"); }

        var offset = bytes.AsSpan().StartsWith(ExportHeader) ? ExportHeader.Length : 0;
        if (bytes.Length > offset + 1 && bytes[offset] == 0x1f && bytes[offset + 1] == 0x8b)
        {
            using var gz = new GZipStream(new MemoryStream(bytes, offset, bytes.Length - offset), CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            return Parse(reader.ReadToEnd(), report);
        }
        return Parse(Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset), report);
    }

    /// <summary>Builds a Streamer.bot-style export string ("SBAE" + gzip JSON, base64) for the given actions and commands.</summary>
    public string Export(IEnumerable<Guid> actionIds, IEnumerable<Guid>? commandIds = null, IEnumerable<Guid>? timerIds = null, string name = "CherBot export", string author = "")
    {
        var actions = actionIds.Select(config.GetAction).OfType<ActionDef>().ToList();
        var queues = actions.Where(a => a.QueueId is not null).Select(a => config.GetQueue(a.QueueId!.Value)).OfType<QueueDef>().DistinctBy(q => q.Id).ToList();
        var commands = (commandIds ?? []).Select(config.GetCommand).OfType<CommandDef>().ToList();
        var timers = (timerIds ?? []).Select(config.GetTimer).OfType<TimerDef>().ToList();
        var bundle = new
        {
            meta = new { name, author, version = "1.0.0", description = "", autoRunAction = (string?)null, minimumVersion = (string?)null },
            data = new { actions, queues, commands, websocketServers = Array.Empty<object>(), websocketClients = Array.Empty<object>(), timers },
            version = 24,
            exportedFrom = "cherbot",
            minimumVersion = "1.0.0",
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(bundle, CherBotJson.Options);
        using var ms = new MemoryStream();
        ms.Write(ExportHeader);
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(json);
        return Convert.ToBase64String(ms.ToArray());
    }
}
