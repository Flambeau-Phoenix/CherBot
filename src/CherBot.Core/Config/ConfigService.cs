using System.Collections.Concurrent;
using CherBot.Core.Model;
using CherBot.Storage;

namespace CherBot.Core.Config;

/// <summary>
/// Holds actions, queues, commands and timers in memory and writes changes through to SQLite.
/// Readers get snapshots; writers replace whole records.
/// </summary>
public sealed class ConfigService
{
    public const string DefaultQueueName = "Default";

    private readonly CherBotDb _db;
    private readonly ConcurrentDictionary<Guid, ActionDef> _actions = new();
    private readonly ConcurrentDictionary<Guid, QueueDef> _queues = new();
    private readonly ConcurrentDictionary<Guid, CommandDef> _commands = new();
    private readonly ConcurrentDictionary<Guid, TimerDef> _timers = new();

    public event Action<string, Guid>? Changed;

    public ConfigService(CherBotDb db)
    {
        _db = db;
        Load(_actions, "action");
        Load(_queues, "queue");
        Load(_commands, "command");
        Load(_timers, "timer");
        foreach (var a in _actions.Values) a.Normalize();
    }

    private void Load<T>(ConcurrentDictionary<Guid, T> target, string kind) where T : JsonRecord
    {
        foreach (var row in _db.GetDocs(kind))
        {
            var item = CherBotJson.Deserialize<T>(row.Json);
            if (item is not null && Guid.TryParse(row.Id, out var id)) target[id] = item;
        }
    }

    // ── actions ────────────────────────────────────────────────────────────

    public IReadOnlyList<ActionDef> Actions => _actions.Values.OrderBy(a => a.Group).ThenBy(a => a.Name).ToList();

    public ActionDef? GetAction(Guid id) => _actions.GetValueOrDefault(id);

    public ActionDef? FindAction(Guid? id, string? name)
    {
        if (id is { } g && g != Guid.Empty && _actions.TryGetValue(g, out var byId)) return byId;
        if (!string.IsNullOrWhiteSpace(name))
            return _actions.Values.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    public ActionDef SaveAction(ActionDef action)
    {
        if (action.Id == Guid.Empty) action.Id = Guid.NewGuid();
        action.Normalize();
        _db.UpsertDoc("action", action.Id.ToString(), action.Name, CherBotJson.Serialize(action));
        _actions[action.Id] = action;
        Changed?.Invoke("action", action.Id);
        return action;
    }

    public bool DeleteAction(Guid id)
    {
        var removed = _actions.TryRemove(id, out _);
        _db.DeleteDoc("action", id.ToString());
        if (removed) Changed?.Invoke("action", id);
        return removed;
    }

    // ── queues ─────────────────────────────────────────────────────────────

    public IReadOnlyList<QueueDef> Queues => _queues.Values.OrderBy(q => q.Name).ToList();

    public QueueDef? GetQueue(Guid id) => _queues.GetValueOrDefault(id);

    public QueueDef? FindQueue(Guid? id, string? name)
    {
        if (id is { } g && _queues.TryGetValue(g, out var q)) return q;
        if (!string.IsNullOrWhiteSpace(name))
            return _queues.Values.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    public QueueDef SaveQueue(QueueDef queue)
    {
        if (queue.Id == Guid.Empty) queue.Id = Guid.NewGuid();
        _db.UpsertDoc("queue", queue.Id.ToString(), queue.Name, CherBotJson.Serialize(queue));
        _queues[queue.Id] = queue;
        Changed?.Invoke("queue", queue.Id);
        return queue;
    }

    public bool DeleteQueue(Guid id)
    {
        var removed = _queues.TryRemove(id, out _);
        _db.DeleteDoc("queue", id.ToString());
        if (removed) Changed?.Invoke("queue", id);
        return removed;
    }

    // ── commands ───────────────────────────────────────────────────────────

    public IReadOnlyList<CommandDef> Commands => _commands.Values.OrderBy(c => c.Group).ThenBy(c => c.Name).ToList();

    public CommandDef? GetCommand(Guid id) => _commands.GetValueOrDefault(id);

    public CommandDef? FindCommand(Guid? id, string? name)
    {
        if (id is { } g && _commands.TryGetValue(g, out var c)) return c;
        if (!string.IsNullOrWhiteSpace(name))
            return _commands.Values.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    public CommandDef SaveCommand(CommandDef command)
    {
        if (command.Id == Guid.Empty) command.Id = Guid.NewGuid();
        _db.UpsertDoc("command", command.Id.ToString(), command.Name, CherBotJson.Serialize(command));
        _commands[command.Id] = command;
        Changed?.Invoke("command", command.Id);
        return command;
    }

    public bool DeleteCommand(Guid id)
    {
        var removed = _commands.TryRemove(id, out _);
        _db.DeleteDoc("command", id.ToString());
        if (removed) Changed?.Invoke("command", id);
        return removed;
    }

    // ── timers ─────────────────────────────────────────────────────────────

    public IReadOnlyList<TimerDef> Timers => _timers.Values.OrderBy(t => t.Name).ToList();

    public TimerDef? GetTimer(Guid id) => _timers.GetValueOrDefault(id);

    public TimerDef? FindTimer(Guid? id, string? name)
    {
        if (id is { } g && _timers.TryGetValue(g, out var t)) return t;
        if (!string.IsNullOrWhiteSpace(name))
            return _timers.Values.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    public TimerDef SaveTimer(TimerDef timer)
    {
        if (timer.Id == Guid.Empty) timer.Id = Guid.NewGuid();
        _db.UpsertDoc("timer", timer.Id.ToString(), timer.Name, CherBotJson.Serialize(timer));
        _timers[timer.Id] = timer;
        Changed?.Invoke("timer", timer.Id);
        return timer;
    }

    public bool DeleteTimer(Guid id)
    {
        var removed = _timers.TryRemove(id, out _);
        _db.DeleteDoc("timer", id.ToString());
        if (removed) Changed?.Invoke("timer", id);
        return removed;
    }

    // ── generic documents (integration connections etc.) ───────────────────

    public IReadOnlyList<T> GetDocuments<T>(string kind) =>
        _db.GetDocs(kind).Select(r => CherBotJson.Deserialize<T>(r.Json)).Where(x => x is not null).Select(x => x!).ToList();

    public void SaveDocument<T>(string kind, string id, string name, T doc)
    {
        _db.UpsertDoc(kind, id, name, CherBotJson.Serialize(doc));
        Changed?.Invoke(kind, Guid.TryParse(id, out var g) ? g : Guid.Empty);
    }

    public bool DeleteDocument(string kind, string id) => _db.DeleteDoc(kind, id);
}
