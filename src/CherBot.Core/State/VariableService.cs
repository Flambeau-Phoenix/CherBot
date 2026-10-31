using System.Collections.Concurrent;
using System.Text.Json;
using CherBot.Core.Events;
using CherBot.Storage;

namespace CherBot.Core.State;

public sealed record GlobalVariable(string Name, object? Value, DateTimeOffset LastWrite, bool Persisted);

public sealed record UserVariable(string Platform, string UserId, string UserLogin, string UserName, string Name, object? Value, DateTimeOffset LastWrite, bool Persisted);

/// <summary>
/// Globals and per-platform user globals, each either persisted (SQLite) or temporary (memory).
/// Every change raises the Streamer.bot-compatible Misc events so actions can trigger on state changes.
/// </summary>
public sealed class VariableService
{
    private readonly CherBotDb _db;
    private readonly EventBus _bus;
    private readonly ConcurrentDictionary<string, GlobalVariable> _persisted = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GlobalVariable> _temp = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string, string, string), UserVariable> _userPersisted = new();
    private readonly ConcurrentDictionary<(string, string, string), UserVariable> _userTemp = new();

    public VariableService(CherBotDb db, EventBus bus)
    {
        _db = db;
        _bus = bus;
        foreach (var row in db.GetGlobals())
            _persisted[row.Name] = new GlobalVariable(row.Name, Decode(row.ValueJson), DateTimeOffset.Parse(row.LastWrite), true);
        foreach (var row in db.GetUserGlobals())
            _userPersisted[Key(row.Platform, row.UserId, row.Name)] =
                new UserVariable(row.Platform, row.UserId, row.UserLogin, row.UserName, row.Name, Decode(row.ValueJson), DateTimeOffset.Parse(row.LastWrite), true);
    }

    private static (string, string, string) Key(string platform, string userId, string name) =>
        (platform.ToLowerInvariant(), userId, name.ToLowerInvariant());

    private static object? Decode(string json)
    {
        try { return CherBotJson.ToClr(JsonDocument.Parse(json).RootElement); }
        catch { return json; }
    }

    // ── globals ────────────────────────────────────────────────────────────

    public object? GetGlobal(string name, bool persisted = true) =>
        (persisted ? _persisted : _temp).TryGetValue(name, out var v) ? v.Value : null;

    public bool TryGetGlobal(string name, bool persisted, out object? value)
    {
        var found = (persisted ? _persisted : _temp).TryGetValue(name, out var v);
        value = v?.Value;
        return found;
    }

    public IReadOnlyList<GlobalVariable> ListGlobals(bool persisted = true) =>
        (persisted ? _persisted : _temp).Values.OrderBy(v => v.Name).ToList();

    public void SetGlobal(string name, object? value, bool persisted = true)
    {
        var store = persisted ? _persisted : _temp;
        var existed = store.TryGetValue(name, out var old);
        var gv = new GlobalVariable(name, value, DateTimeOffset.UtcNow, persisted);
        store[name] = gv;
        if (persisted) _db.SetGlobal(name, CherBotJson.Serialize(value));

        var data = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["variableName"] = name,
            ["newValue"] = value,
            ["persisted"] = persisted,
            ["lastWrite"] = gv.LastWrite,
        };
        if (existed) data["oldValue"] = old!.Value;
        _bus.Publish(BotEvent.FromTypeName(existed ? "GlobalVariableUpdated" : "GlobalVariableCreated", data));
    }

    public bool UnsetGlobal(string name, bool persisted = true)
    {
        var store = persisted ? _persisted : _temp;
        if (!store.TryRemove(name, out var old)) return false;
        if (persisted) _db.DeleteGlobal(name);
        _bus.Publish(BotEvent.FromTypeName("GlobalVariableDeleted", new Dictionary<string, object?>
        {
            ["name"] = name,
            ["variableName"] = name,
            ["oldValue"] = old.Value,
            ["persisted"] = persisted,
        }));
        return true;
    }

    public void ClearNonPersistedGlobals()
    {
        foreach (var name in _temp.Keys.ToList()) UnsetGlobal(name, false);
    }

    // ── user globals ───────────────────────────────────────────────────────

    public object? GetUser(string platform, string userId, string name, bool persisted = true) =>
        (persisted ? _userPersisted : _userTemp).TryGetValue(Key(platform, userId, name), out var v) ? v.Value : null;

    public UserVariable? GetUserByLogin(string platform, string login, string name, bool persisted = true) =>
        (persisted ? _userPersisted : _userTemp).Values.FirstOrDefault(v =>
            string.Equals(v.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(v.UserLogin, login, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<UserVariable> ListUser(string platform, string? name = null, string? userId = null, bool persisted = true) =>
        (persisted ? _userPersisted : _userTemp).Values
            .Where(v => string.Equals(v.Platform, platform, StringComparison.OrdinalIgnoreCase))
            .Where(v => name is null || string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
            .Where(v => userId is null || v.UserId == userId)
            .OrderBy(v => v.UserLogin).ThenBy(v => v.Name)
            .ToList();

    public void SetUser(string platform, string userId, string name, object? value, bool persisted = true, string userLogin = "", string userName = "")
    {
        var store = persisted ? _userPersisted : _userTemp;
        var key = Key(platform, userId, name);
        store.TryGetValue(key, out var old);
        var uv = new UserVariable(platform.ToLowerInvariant(), userId,
            userLogin.Length > 0 ? userLogin : old?.UserLogin ?? "",
            userName.Length > 0 ? userName : old?.UserName ?? "",
            name, value, DateTimeOffset.UtcNow, persisted);
        store[key] = uv;
        if (persisted) _db.SetUserGlobal(uv.Platform, userId, uv.UserLogin, uv.UserName, name, CherBotJson.Serialize(value));

        _bus.Publish(BotEvent.FromTypeName("UserGlobalVariableUpdated", new Dictionary<string, object?>
        {
            ["userId"] = userId,
            ["userLogin"] = uv.UserLogin,
            ["userName"] = uv.UserName,
            ["platform"] = uv.Platform,
            ["name"] = name,
            ["variableName"] = name,
            ["oldValue"] = old?.Value,
            ["newValue"] = value,
            ["persisted"] = persisted,
        }));
    }

    public bool UnsetUser(string platform, string userId, string name, bool persisted = true)
    {
        var removed = (persisted ? _userPersisted : _userTemp).TryRemove(Key(platform, userId, name), out _);
        if (removed && persisted) _db.DeleteUserGlobal(platform.ToLowerInvariant(), userId, name);
        return removed;
    }

    public int UnsetAllUsers(string platform, string? name, bool persisted = true)
    {
        var store = persisted ? _userPersisted : _userTemp;
        var keys = store.Where(kv => string.Equals(kv.Value.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                                     (name is null || string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => kv.Key).ToList();
        foreach (var k in keys) store.TryRemove(k, out _);
        if (persisted) _db.DeleteUserGlobal(platform.ToLowerInvariant(), null, name);
        return keys.Count;
    }

    public void ClearNonPersistedUserGlobals() => _userTemp.Clear();

    /// <summary>Adds <paramref name="delta"/> to a numeric user variable (missing counts as 0) and returns the new value.</summary>
    public long IncrementUser(string platform, string userId, string name, long delta, bool persisted = true, string userLogin = "", string userName = "")
    {
        CherBotJson.TryNumber(GetUser(platform, userId, name, persisted), out var current);
        var next = (long)current + delta;
        SetUser(platform, userId, name, next, persisted, userLogin, userName);
        return next;
    }
}
