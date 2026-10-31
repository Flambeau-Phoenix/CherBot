using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Model;
using CherBot.Core.Platform;
using CherBot.Core.State;
using CherBot.Storage;
using Microsoft.Extensions.Logging;

namespace CherBot.Core.Handlers;

/// <summary>Core sub-actions: variables, arguments, actions, queues, commands, timers, files, network, quotes, signals, logging.</summary>
public sealed class CoreHandlers : SubActionHandlerGroup
{
    private static readonly string[] AudioExtensions = [".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".opus"];

    public CoreHandlers()
    {
        // ── sounds, network, files, programs ─────────────────────────────────
        On(1, PlaySoundAsync);
        On(7, PlaySoundFromFolderAsync);
        On(2, UdpBroadcastAsync);
        On(3, WriteToFileAsync);
        On(5, ReadRandomLines);
        On(1006, ReadLines);
        On(1021, ReadSpecificLine);
        On(1039, (s, c) => c.SetArg(Or(s.Str("variableName"), "fileExists"), File.Exists(MapPath(c, s.Str("file")))));
        On(1040, (s, c) => c.SetArg(Or(s.Str("variableName"), "folderExists"), Directory.Exists(MapPath(c, s.Str("folder")))));
        On(6, RunProgramAsync);
        On(1007, FetchUrlAsync);

        // ── actions, queues ──────────────────────────────────────────────────
        On(4, RunActionAsync);
        On(1004, (s, c) => SetActionState(c, s.GuidOf("actionId"), s.Int("state")));
        On(1016, (s, c) => c.SetArg("actionState", s.GuidOf("actionId") is { } id && c.Service<ConfigService>().GetAction(id) is { Enabled: true }));
        On(1011, SetActionGroupState);
        On(1017, GetActionGroupState);
        On(1028, SetSubActionState);
        On(1038, SetSubActionWeight);
        On(1033, SetTriggerState);
        On(1001, (s, c) =>
        {
            var queues = c.Service<QueueManager>();
            var qid = s.GuidOf("actionQueueId");
            if (s.Int("state") == 0) queues.Pause(qid);
            else queues.Resume(qid, s.Bool("clear"));
        });
        On(1005, (s, c) => c.Service<QueueManager>().Clear(s.GuidOf("actionQueueId")));
        On(1022, (s, c) =>
        {
            if (s.GuidOf("actionQueueId") is not { } qid || c.Service<ConfigService>().GetQueue(qid) is not { } q) return;
            c.Service<QueueManager>().SetBlocking(qid, ApplyState(q.Blocking, s.Int("state")));
        });

        // ── commands, timers ─────────────────────────────────────────────────
        On(9, (s, c) =>
        {
            var cfg = c.Service<ConfigService>();
            if (s.GuidOf("commandId") is { } id && cfg.GetCommand(id) is { } cmd)
            {
                cmd.Enabled = ApplyState(cmd.Enabled, s.Int("state"));
                cfg.SaveCommand(cmd);
            }
        });
        On(1010, (s, c) =>
        {
            var cfg = c.Service<ConfigService>();
            foreach (var cmd in cfg.Commands.Where(x => string.Equals(x.Group, s.Str("groupName"), StringComparison.OrdinalIgnoreCase)))
            {
                cmd.Enabled = ApplyState(cmd.Enabled, s.Int("state"));
                cfg.SaveCommand(cmd);
            }
        });
        On(1014, (s, c) => c.SetArg("commandState", s.GuidOf("commandId") is { } id && c.Service<ConfigService>().GetCommand(id) is { Enabled: true }));
        On(1015, (s, c) =>
        {
            var group = c.Service<ConfigService>().Commands.Where(x => string.Equals(x.Group, s.Str("groupName"), StringComparison.OrdinalIgnoreCase)).ToList();
            c.SetArg("commandGroupState", group.Count == 0 ? 0 : group.All(x => x.Enabled) ? 1 : group.Any(x => x.Enabled) ? 2 : 0);
        });
        On(1013, GetCommands);
        On(8, (s, c) =>
        {
            var cfg = c.Service<ConfigService>();
            if (s.GuidOf("timerId") is { } id && cfg.GetTimer(id) is { } t)
            {
                t.Enabled = ApplyState(t.Enabled, s.Int("state"));
                cfg.SaveTimer(t);
            }
        });

        // ── variables and arguments ──────────────────────────────────────────
        On(121, GlobalGet);
        On(122, GlobalSet);
        On(123, (s, c) =>
        {
            var value = c.Parse(s.Str("value"));
            c.SetArg(c.Parse(s.Str("variableName")), s.Bool("autoType") ? CherBotJson.AutoType(value) : value);
        });
        On(125, (_, c) => c.Service<VariableService>().ClearNonPersistedGlobals());
        On(126, (_, c) => c.Service<VariableService>().ClearNonPersistedUserGlobals());

        // ── logic helpers ────────────────────────────────────────────────────
        On(1002, DelayAsync);
        On(1003, (s, c) =>
        {
            if (s.Int("randomType") == 1)
            {
                var f = Random.Shared.NextDouble();
                c.SetArg("randomFloat", f);
                c.SetArg("randomPercent", (int)Math.Round(f * 100));
            }
            else
            {
                var min = s.Int("min");
                var max = Math.Max(min, s.Int("max"));
                c.SetArg("randomNumber", Random.Shared.Next(min, max + 1));
            }
        });
        On(1009, (_, _) => { });
        On(1020, (s, c) =>
        {
            var msg = c.Parse(s.Str("message"));
            var level = s.Int("logLevel", 2) switch { 0 => LogLevel.Trace, 1 => LogLevel.Debug, 3 => LogLevel.Warning, 4 => LogLevel.Error, 5 => LogLevel.Critical, _ => LogLevel.Information };
            c.Log.Log(level, "[{Action}] {Message}", c.Action.Name, msg);
            c.Service<EventBus>().Publish(new BotEvent("CherBot", "Log", new Dictionary<string, object?>
            {
                ["actionId"] = c.Action.Id.ToString(), ["actionName"] = c.Action.Name, ["level"] = level.ToString(), ["message"] = msg,
            }));
        });

        // ── signals and custom events ────────────────────────────────────────
        On(1031, (s, c) =>
        {
            var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (s.Bool("useArge") || s.Bool("useArgs"))
                foreach (var (k, v) in c.Args) payload[k] = v;
            foreach (var (k, v) in s.StrDict("customArgs")) payload[k] = c.Parse(v);
            payload["signalSender"] = c.Action.Name;
            payload["signalSenderId"] = c.Action.Id.ToString();
            c.Service<SignalHub>().Send(c.Parse(s.Str("signalName")), payload, s.Bool("queueSignal"));
        });
        On(1032, WaitForSignalAsync);
        On(1034, (s, c) => RaiseCustomEvent(c, c.Parse(s.Str("eventName")), s.Bool("useArgs")));

        // ── quotes, first words, credits ─────────────────────────────────────
        On(1000, GetQuote);
        On(1035, (_, c) => c.SetArg("quoteCount", c.Service<CherBotDb>().QuoteCount()));
        On(1036, (s, c) =>
        {
            var ok = long.TryParse(c.Parse(s.Str("quoteId")), out var id) && c.Service<CherBotDb>().DeleteQuote(id);
            c.SetArg("success", ok);
        });
        On(1037, AddQuote);
        On(1026, (_, c) => c.Service<CherBotDb>().ResetFirstWords());
        On(1027, (_, c) => c.Log.LogInformation("Reset Credits: credits are not tracked by CherBot; nothing to reset"));

        // ── desktop-only (real on the Windows GUI in Local mode) ─────────────
        On(1024, async (s, c) =>
        {
            var desktop = c.Service<IDesktopServices>();
            if (!desktop.IsAvailable) throw new UnsupportedOnHostException("toast notifications need a desktop session");
            await desktop.ShowToastAsync(c.Parse(s.Str("title")), c.Parse(s.Str("text")), MapPath(c, s.Str("iconPath")), c.Cancellation);
        });
        On(1023, async (s, c) =>
        {
            var desktop = c.Service<IDesktopServices>();
            if (!desktop.IsAvailable) throw new UnsupportedOnHostException("Get Input needs a desktop session");
            var text = await desktop.GetInputAsync(c.Parse(s.Str("title")), c.Parse(s.Str("prompt")), c.Parse(s.Str("originalText")), c.Cancellation);
            var name = Or(s.Str("outputVariable"), "input");
            c.SetArg(name, s.Bool("autoTypeOutput") ? CherBotJson.AutoType(text) : text);
        });
        On(1012, async (s, c) =>
        {
            var desktop = c.Service<IDesktopServices>();
            if (!desktop.IsAvailable) throw new UnsupportedOnHostException("Keyboard Press needs a desktop session");
            await desktop.KeyPressAsync(s.Raw("key"), s.Raw("modifiers"), c.Cancellation);
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>
    /// Raises Custom/Event. Data carries the arguments both flattened (for actions triggered by it) and under <c>args</c>
    /// (the shape Streamer.bot clients read for DoAction custom-event responses).
    /// </summary>
    public static void RaiseCustomEvent(RunContext c, string eventName, bool includeArgs)
    {
        var args = includeArgs ? c.Args.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase) : [];
        var data = new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase)
        {
            ["eventName"] = eventName,
            ["args"] = args,
        };
        if (eventName.Length > 0 && string.Equals(eventName, c.ArgString("sbClientResponse"), StringComparison.Ordinal))
            c.SetArg("__sbClientResponded", true);
        c.Service<EventBus>().Publish(BotEvent.FromTypeName("CustomEvent", data));
    }

    private static string MapPath(RunContext c, string raw) => c.Service<IPathMapper>().Map(c.Parse(raw));

    /// <summary>Streamer.bot state selector: 0 = enable, 1 = disable, 2 = toggle.</summary>
    internal static bool ApplyState(bool current, int state) => state switch { 0 => true, 1 => false, _ => !current };

    private static void SetActionState(RunContext c, Guid? id, int state)
    {
        var cfg = c.Service<ConfigService>();
        if (id is null || cfg.GetAction(id.Value) is not { } a) return;
        a.Enabled = ApplyState(a.Enabled, state);
        cfg.SaveAction(a);
    }

    private static void SetActionGroupState(SubActionDef s, RunContext c)
    {
        var cfg = c.Service<ConfigService>();
        foreach (var a in cfg.Actions.Where(x => string.Equals(x.Group, s.Str("groupName"), StringComparison.OrdinalIgnoreCase)))
        {
            a.Enabled = ApplyState(a.Enabled, s.Int("state"));
            cfg.SaveAction(a);
        }
    }

    private static void GetActionGroupState(SubActionDef s, RunContext c)
    {
        var group = c.Service<ConfigService>().Actions.Where(x => string.Equals(x.Group, s.Str("groupName"), StringComparison.OrdinalIgnoreCase)).ToList();
        var enabled = group.Where(a => a.Enabled).Select(a => a.Id.ToString()).ToList();
        var disabled = group.Where(a => !a.Enabled).Select(a => a.Id.ToString()).ToList();
        c.SetArg("actionGroupState", group.Count == 0 || enabled.Count == 0 ? 0 : disabled.Count == 0 ? 1 : 2);
        c.SetArg("actionsEnabled", enabled);
        c.SetArg("actionsDisabled", disabled);
    }

    private static void SetSubActionState(SubActionDef s, RunContext c)
    {
        var cfg = c.Service<ConfigService>();
        if (s.GuidOf("actionId") is not { } aid || cfg.GetAction(aid) is not { } a) return;
        if (s.GuidOf("subActionId") is not { } sid || a.FindSubAction(sid) is not { } target) return;
        target.Enabled = ApplyState(target.Enabled, s.Int("state"));
        cfg.SaveAction(a);
    }

    private static void SetSubActionWeight(SubActionDef s, RunContext c)
    {
        var cfg = c.Service<ConfigService>();
        if (s.GuidOf("actionId") is not { } aid || cfg.GetAction(aid) is not { } a) return;
        if (s.GuidOf("subActionId") is not { } sid || a.FindSubAction(sid) is not { } target) return;
        if (!double.TryParse(c.Parse(s.Str("weight")), NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) return;
        target.Weight = w;
        cfg.SaveAction(a);
    }

    private static void SetTriggerState(SubActionDef s, RunContext c)
    {
        var cfg = c.Service<ConfigService>();
        if (s.GuidOf("actionId") is not { } aid || cfg.GetAction(aid) is not { } a) return;
        if (s.GuidOf("triggerId") is not { } tid || a.Triggers.FirstOrDefault(t => t.Id == tid) is not { } trigger) return;
        trigger.Enabled = ApplyState(trigger.Enabled, s.Int("state"));
        cfg.SaveAction(a);
    }

    private static async Task RunActionAsync(SubActionDef s, RunContext c)
    {
        var cfg = c.Service<ConfigService>();
        if (s.GuidOf("actionId") is not { } id || cfg.GetAction(id) is not { } target)
        {
            c.Log.LogWarning("Run Action: action {Id} not found", s.Str("actionId"));
            return;
        }
        var args = new Dictionary<string, object?>(c.Args, StringComparer.OrdinalIgnoreCase);
        var dispatcher = c.Service<ActionDispatcher>();
        var source = new DispatchSource($"Run Action ({c.Action.Name})");
        // "runImmedately" is Streamer.bot's spelling; default true per the docs.
        if (s.Bool("runImmedately", s.Bool("runImmediately", true)))
        {
            if (!target.Enabled) return;
            var result = await dispatcher.RunNowAsync(target, args, source, c.Cancellation, c.Depth + 1);
            // Variables populated by the called action become available to the caller.
            foreach (var (k, v) in result.Args)
                if (k is not ("actionId" or "actionName" or "runningActionId")) c.Args[k] = v;
        }
        else
        {
            dispatcher.Dispatch(target, args, source);
        }
    }

    private static async Task DelayAsync(SubActionDef s, RunContext c)
    {
        var min = ParseInt(c.Parse(s.Str("value")));
        var ms = min;
        if (s.Bool("random"))
        {
            var max = ParseInt(c.Parse(s.Str("maxValue")));
            if (max > min) ms = Random.Shared.Next(min, max + 1);
        }
        if (ms > 0) await Task.Delay(ms, c.Cancellation);
    }

    private static int ParseInt(string s) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v :
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : 0;

    private static async Task WaitForSignalAsync(SubActionDef s, RunContext c)
    {
        var name = c.Parse(s.Str("signalName"));
        var timeoutMs = ParseInt(c.Parse(s.Str("timeout")));
        if (timeoutMs <= 0 || timeoutMs > 30_000) timeoutMs = 30_000;
        var payload = await c.Service<SignalHub>().WaitAsync(name, TimeSpan.FromMilliseconds(timeoutMs), c.Cancellation);
        if (payload is null)
        {
            c.SetArg("signalTimeout", true);
            return;
        }
        c.SetArg("signalTimeout", false);
        c.SetArg("signalName", name);
        c.SetArg("hasResult", payload.Count > 0);
        var overwrite = s.Bool("overwrite");
        foreach (var (k, v) in payload)
            if (overwrite || !c.Args.ContainsKey(k)) c.Args[k] = v;
    }

    private static (string Platform, string UserId, string Login, string Name)? ResolveUser(RunContext c, int selector)
    {
        var platform = c.ArgString("userType", c.ArgString("platform", "twitch")).ToLowerInvariant();
        if (selector == 2)
        {
            var tid = c.ArgString("targetUserId");
            return tid.Length == 0 ? null : (platform, tid, c.ArgString("targetUserName", c.ArgString("targetUser")), c.ArgString("targetUser"));
        }
        var uid = c.ArgString("userId");
        return uid.Length == 0 ? null : (platform, uid, c.ArgString("userName"), c.ArgString("user"));
    }

    private static void GlobalGet(SubActionDef s, RunContext c)
    {
        var vars = c.Service<VariableService>();
        var name = c.Parse(s.Str("variableName"));
        var destination = Or(c.Parse(s.Str("destinationVariable")), name);
        var persisted = s.Bool("persisted", true);
        object? value;
        var source = s.Int("source");
        if (source == 0)
        {
            value = vars.TryGetGlobal(name, persisted, out var v) ? v : null;
        }
        else
        {
            var user = ResolveUser(c, source);
            value = user is { } u ? vars.GetUser(u.Platform, u.UserId, name, persisted) : null;
        }
        if (value is null && s.Raw("defaultValue") is { ValueKind: not JsonValueKind.Null } dv) value = CherBotJson.ToClr(dv);
        c.SetArg(destination, value);
    }

    private static void GlobalSet(SubActionDef s, RunContext c)
    {
        var vars = c.Service<VariableService>();
        var name = c.Parse(s.Str("variableName"));
        var persisted = s.Bool("persisted", true);
        var dest = s.Int("destination");
        var mode = s.Int("source");
        var raw = c.Parse(s.Str("value"));
        var user = dest == 0 ? null : ResolveUser(c, dest);
        if (dest != 0 && user is null)
        {
            c.Log.LogWarning("Global (Set): no user in arguments for {Name}", name);
            return;
        }

        object? newValue;
        if (mode is 1 or 2)
        {
            var delta = string.IsNullOrWhiteSpace(raw) ? 1 : ParseInt(raw);
            if (mode == 2) delta = -delta;
            var current = dest == 0 ? vars.GetGlobal(name, persisted) : vars.GetUser(user!.Value.Platform, user.Value.UserId, name, persisted);
            CherBotJson.TryNumber(current, out var cur);
            newValue = (long)cur + delta;
        }
        else
        {
            newValue = s.Bool("autoType") ? CherBotJson.AutoType(raw) : raw;
        }

        if (dest == 0) vars.SetGlobal(name, newValue, persisted);
        else vars.SetUser(user!.Value.Platform, user.Value.UserId, name, newValue, persisted, user.Value.Login, user.Value.Name);
    }

    private static void GetCommands(SubActionDef s, RunContext c)
    {
        var group = s.Str("groupName");
        var cmds = c.Service<ConfigService>().Commands
            .Where(x => x.Enabled && x.Mode == 0 && x.Location != 2)
            .Where(x => s.Bool("includeAll") || x.Include)
            .Where(x => string.IsNullOrWhiteSpace(group) || group.Equals("All", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.Group, group, StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => s.Bool("ignoreAliases") ? x.Aliases().Take(1) : x.Aliases())
            .ToList();
        var name = Or(s.Str("variableName"), "commands");
        c.SetArg(name, string.Join(",", cmds));
        c.SetArg(name + "List", cmds);
    }

    private static void SetQuoteArgs(RunContext c, QuoteRow q)
    {
        var ts = DateTimeOffset.Parse(q.Timestamp, CultureInfo.InvariantCulture);
        c.SetArg("quoteTimestamp", ts);
        c.SetArg("quoteTime", ts.ToString("d", CultureInfo.InvariantCulture));
        c.SetArg("quoteId", q.Id.ToString(CultureInfo.InvariantCulture));
        c.SetArg("quoteUserId", q.UserId);
        c.SetArg("quoteUser", q.User);
        c.SetArg("quotePlatform", q.Platform);
        c.SetArg("quoteGame", q.Game);
        c.SetArg("quote", q.Quote);
    }

    private static void GetQuote(SubActionDef s, RunContext c)
    {
        var db = c.Service<CherBotDb>();
        var idText = c.Parse(s.Str("quoteId"));
        var specific = s.Int("quoteType") == 1 || (s.Str("quoteType").Contains("Specific", StringComparison.OrdinalIgnoreCase));
        var quote = specific && long.TryParse(idText, out var id) ? db.GetQuote(id) : db.GetRandomQuote();
        c.SetArg("success", quote is not null);
        if (quote is not null)
        {
            SetQuoteArgs(c, quote);
            c.Service<EventBus>().Publish(BotEvent.FromTypeName("QuoteShow", new Dictionary<string, object?>
            {
                ["id"] = quote.Id, ["quote"] = quote.Quote, ["user"] = quote.User, ["platform"] = quote.Platform,
            }));
        }
    }

    private static void AddQuote(SubActionDef s, RunContext c)
    {
        var raw = s.Str("quoteVariable");
        var text = c.Args.TryGetValue(raw.Trim('%'), out var v) && !raw.Contains(' ') ? CherBotJson.Format(v) : c.Parse(raw);
        if (string.IsNullOrWhiteSpace(text))
        {
            c.SetArg("success", false);
            return;
        }
        var db = c.Service<CherBotDb>();
        var platform = c.ArgString("userType", "twitch").ToLowerInvariant();
        var id = db.AddQuote(c.ArgString("userId"), c.ArgString("user", c.ArgString("userName")), platform, c.ArgString("game", c.ArgString("gameName")), text);
        var row = db.GetQuote(id)!;
        c.SetArg("success", true);
        SetQuoteArgs(c, row);
        c.Service<EventBus>().Publish(BotEvent.FromTypeName("QuoteAdded", new Dictionary<string, object?>
        {
            ["id"] = row.Id, ["quote"] = row.Quote, ["user"] = row.User, ["userId"] = row.UserId, ["platform"] = row.Platform,
        }));
    }

    // ── sounds ───────────────────────────────────────────────────────────────

    private static float Volume(SubActionDef s)
    {
        var v = (float)s.Dbl("volume", 1);
        return v > 1 ? v / 100f : v;
    }

    private static async Task PlaySoundAsync(SubActionDef s, RunContext c)
    {
        var file = MapPath(c, s.Str("soundFile"));
        if (!File.Exists(file))
        {
            c.Log.LogWarning("Play Sound: file not found {File}", file);
            return;
        }
        c.SetArg("soundFile", file);
        await c.Service<ISoundPlayer>().PlayAsync(file, Volume(s), s.Bool("finishBeforeContinuing"), c.Cancellation);
    }

    private static async Task PlaySoundFromFolderAsync(SubActionDef s, RunContext c)
    {
        var folder = MapPath(c, s.Str("folder"));
        if (!Directory.Exists(folder))
        {
            c.Log.LogWarning("Play Sound From Folder: folder not found {Folder}", folder);
            return;
        }
        var files = Directory.EnumerateFiles(folder, "*", s.Bool("recursive") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        if (files.Count == 0) return;
        var file = files[Random.Shared.Next(files.Count)];
        c.SetArg("soundFile", file);
        await c.Service<ISoundPlayer>().PlayAsync(file, Volume(s), s.Bool("finishBeforeContinuing"), c.Cancellation);
    }

    // ── network, files, programs ─────────────────────────────────────────────

    private static async Task UdpBroadcastAsync(SubActionDef s, RunContext c)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        var bytes = Encoding.UTF8.GetBytes(c.Parse(s.Str("payload")));
        await udp.SendAsync(bytes, bytes.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Broadcast, s.Int("port")));
    }

    private static async Task WriteToFileAsync(SubActionDef s, RunContext c)
    {
        var file = MapPath(c, s.Str("file"));
        var text = c.Parse(s.Str("text"));
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (s.Bool("append")) await File.AppendAllTextAsync(file, text + Environment.NewLine, c.Cancellation);
        else await File.WriteAllTextAsync(file, text, c.Cancellation);
    }

    private static List<string>? ReadFileLines(SubActionDef s, RunContext c)
    {
        var file = MapPath(c, s.Str("file"));
        var found = File.Exists(file);
        c.SetArg("fileFound", found);
        if (!found) return null;
        var lines = File.ReadAllLines(file).ToList();
        if (s.Bool("parseVariables")) lines = lines.Select(l => c.Parse(l)).ToList();
        return lines;
    }

    private static object? Typed(SubActionDef s, string line) => s.Bool("autoType") ? CherBotJson.AutoType(line) : line;

    private static void ReadLines(SubActionDef s, RunContext c)
    {
        var lines = ReadFileLines(s, c);
        if (lines is null) return;
        var name = Or(s.Str("variableName"), "line");
        c.SetArg("lineCount", lines.Count);
        for (var i = 0; i < lines.Count; i++) c.SetArg($"{name}{i}", Typed(s, lines[i]));
    }

    private static void ReadSpecificLine(SubActionDef s, RunContext c)
    {
        var lines = ReadFileLines(s, c);
        if (lines is null) return;
        var n = ParseInt(c.Parse(s.Str("count")));
        if (n <= 0) n = 1;
        var name = Or(s.Str("variableName"), "line");
        c.SetArg(name, n <= lines.Count ? Typed(s, lines[n - 1]) : "");
    }

    private static void ReadRandomLines(SubActionDef s, RunContext c)
    {
        var lines = ReadFileLines(s, c);
        if (lines is null || lines.Count == 0) return;
        var name = Or(s.Str("variableName"), "randomLine");
        var count = Math.Max(1, ParseInt(c.Parse(Or(s.Str("count"), "1"))));
        for (var i = 0; i < count; i++)
        {
            var idx = Random.Shared.Next(lines.Count);
            var suffix = i == 0 ? "" : i.ToString(CultureInfo.InvariantCulture);
            c.SetArg(name + suffix, Typed(s, lines[idx]));
            c.SetArg(name + "Number" + suffix, idx);
        }
    }

    private static async Task RunProgramAsync(SubActionDef s, RunContext c)
    {
        var env = s.StrDict("envVars").ToDictionary(kv => kv.Key, kv => c.Parse(kv.Value));
        var exit = await c.Service<IProcessRunner>().RunAsync(
            MapPath(c, s.Str("command")), c.Parse(s.Str("arguments")), MapPath(c, s.Str("workingDir")), env, s.Int("waitForExit"), c.Cancellation);
        if (exit is { } code) c.SetArg("exitCode", code);
    }

    private static async Task FetchUrlAsync(SubActionDef s, RunContext c)
    {
        var url = c.Parse(s.Str("url"));
        var name = Or(s.Str("variableName"), "fetchResult");
        var http = c.Service<IHttpClientFactory>().CreateClient("fetch");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (k, v) in s.StrDict("headers")) req.Headers.TryAddWithoutValidation(k, c.Parse(v));
        using var resp = await http.SendAsync(req, c.Cancellation);
        var body = await resp.Content.ReadAsStringAsync(c.Cancellation);
        if (s.Bool("parseAsJson"))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                Flatten(c, name, doc.RootElement, s.Bool("autoType"));
                return;
            }
            catch (JsonException)
            {
                c.Log.LogDebug("Fetch URL: response was not JSON; storing as text");
            }
        }
        c.SetArg(name, body);
    }

    private static void Flatten(RunContext c, string prefix, JsonElement e, bool autoType)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Flatten(c, $"{prefix}.{p.Name}", p.Value, autoType);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in e.EnumerateArray()) Flatten(c, $"{prefix}[{i++}]", item, autoType);
                c.SetArg(prefix + ".count", i);
                break;
            default:
                c.SetArg(prefix, CherBotJson.ToClr(e));
                break;
        }
    }
}
