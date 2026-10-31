using System.Globalization;
using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Engine;
using CherBot.Core.Model;
using CherBot.Core.Platform;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Obs;

/// <summary>OBS Studio sub-actions (Streamer.bot type ids 30–49 and 321–329) over obs-websocket v5.</summary>
public sealed class ObsHandlers : SubActionHandlerGroup
{
    public ObsHandlers()
    {
        On(30, async (s, c) =>
        {
            var (obs, scene, id) = await Item(s, c);
            var enabled = await Desired(s.Int("state"), () => Enabled(obs, scene, id, c));
            await obs.RequestAsync("SetSceneItemEnabled", new { sceneName = scene, sceneItemId = id, sceneItemEnabled = enabled }, c.Cancellation);
        });
        On(31, async (s, c) => await RandomVisible(c, Obs(s, c), c.Parse(s.Str("groupName")), "GetGroupSceneItemList"));
        On(32, async (s, c) => await HideAll(c, Obs(s, c), c.Parse(s.Str("groupName")), "GetGroupSceneItemList"));
        On(325, async (s, c) => await HideAll(c, Obs(s, c), c.Parse(s.Str("sceneName")), "GetSceneItemList"));
        On(326, async (s, c) => await RandomVisible(c, Obs(s, c), c.Parse(s.Str("sceneName")), "GetSceneItemList"));
        On(33, async (s, c) => await Output(Obs(s, c), c, s.Int("state"), "StartStream", "StopStream", "ToggleStream"));
        On(45, async (s, c) => await Output(Obs(s, c), c, s.Int("state"), "StartRecord", "StopRecord", "ToggleRecord", "PauseRecord", "ResumeRecord"));
        On(321, async (s, c) => await Output(Obs(s, c), c, s.Int("state"), "StartReplayBuffer", "StopReplayBuffer", "ToggleReplayBuffer", "SaveReplayBuffer"));
        On(34, async (s, c) => await Obs(s, c).RequestAsync("SetCurrentProgramScene", new { sceneName = c.Parse(s.Str("sceneName")) }, c.Cancellation));
        On(35, async (s, c) => await FilterState(c, Obs(s, c), c.Parse(s.Str("sourceName")), c.Parse(s.Str("filterName")), s.Int("state")));
        On(47, async (s, c) => await FilterState(c, Obs(s, c), c.Parse(s.Str("sceneName")), c.Parse(s.Str("filterName")), s.Int("state")));
        On(40, async (s, c) =>
        {
            var obs = Obs(s, c);
            var source = c.Parse(s.Str("sourceName"));
            var filters = await Filters(obs, source, c);
            if (filters.Count == 0) return;
            var pick = filters[Random.Shared.Next(filters.Count)];
            await FilterState(c, obs, source, pick, s.Int("state"));
            c.SetArg("filterName", pick);
        });
        On(41, async (s, c) =>
        {
            var obs = Obs(s, c);
            var source = c.Parse(s.Str("sourceName"));
            foreach (var f in await Filters(obs, source, c))
                await obs.RequestAsync("SetSourceFilterEnabled", new { sourceName = source, filterName = f, filterEnabled = false }, c.Cancellation);
        });
        On(36, async (s, c) =>
        {
            var (obs, scene, id) = await Item(s, c);
            var t = (await obs.RequestAsync("GetSceneItemTransform", new { sceneName = scene, sceneItemId = id }, c.Cancellation)).GetProperty("sceneItemTransform");
            var horizontal = s.Int("flipMode") == 0;
            var transform = horizontal
                ? new Dictionary<string, object> { ["scaleX"] = -t.GetProperty("scaleX").GetDouble() }
                : new Dictionary<string, object> { ["scaleY"] = -t.GetProperty("scaleY").GetDouble() };
            await obs.RequestAsync("SetSceneItemTransform", new { sceneName = scene, sceneItemId = id, sceneItemTransform = transform }, c.Cancellation);
        });
        On(37, async (s, c) =>
        {
            var (obs, scene, id) = await Item(s, c);
            var degrees = s.Dbl("degrees");
            if (s.Bool("additive"))
                degrees += (await obs.RequestAsync("GetSceneItemTransform", new { sceneName = scene, sceneItemId = id }, c.Cancellation))
                    .GetProperty("sceneItemTransform").GetProperty("rotation").GetDouble();
            await obs.RequestAsync("SetSceneItemTransform", new { sceneName = scene, sceneItemId = id, sceneItemTransform = new { rotation = degrees % 360 } }, c.Cancellation);
        });
        On(38, async (s, c) => await InputSettings(s, c, new { url = c.Parse(s.Str("url")) }));
        On(39, async (s, c) => await InputSettings(s, c, new { text = c.Parse(s.Str("text")) }));
        On(322, async (s, c) => await InputSettings(s, c, new { local_file = MapPath(c, s.Str("fileName")) }));
        On(323, async (s, c) => await InputSettings(s, c, new { file = MapPath(c, s.Str("fileName")) }));
        On(327, async (s, c) =>
        {
            var color = s.Bool("random") ? 0xFF000000u | (uint)Random.Shared.Next(0x1000000) : ParseColor(c.Parse(s.Str("color")));
            await InputSettings(s, c, new { color });
        });
        On(42, async (s, c) =>
        {
            var obs = Obs(s, c);
            var input = c.Parse(s.Str("sourceName"));
            if (s.Int("state") == 2) await obs.RequestAsync("ToggleInputMute", new { inputName = input }, c.Cancellation);
            else await obs.RequestAsync("SetInputMute", new { inputName = input, inputMuted = s.Int("state") == 0 }, c.Cancellation);
        });
        On(44, async (s, c) =>
        {
            var obs = Obs(s, c);
            var input = c.Parse(s.Str("sourceName"));
            var track = Math.Clamp(s.Int("track", 1), 1, 6).ToString(CultureInfo.InvariantCulture);
            var on = await Desired(s.Int("state"), async () =>
                (await obs.RequestAsync("GetInputAudioTracks", new { inputName = input }, c.Cancellation)).GetProperty("inputAudioTracks").GetProperty(track).GetBoolean());
            await obs.RequestAsync("SetInputAudioTracks", new { inputName = input, inputAudioTracks = new Dictionary<string, bool> { [track] = on } }, c.Cancellation);
        });
        On(43, async (s, c) =>
        {
            var scene = await Obs(s, c).RequestAsync("GetCurrentProgramScene", null, c.Cancellation);
            var name = scene.TryGetProperty("currentProgramSceneName", out var n) ? n.GetString() : scene.GetProperty("sceneName").GetString();
            c.SetArg("currentScene", name);
            c.SetArg("obs.sceneName", name);
        });
        On(46, async (s, c) =>
        {
            var obs = Obs(s, c);
            var raw = c.Parse(s.Str("raw"));
            var requestType = c.Parse(s.Str("name"));
            object? data = null;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                using var doc = JsonDocument.Parse(raw);
                // Streamer.bot's raw field may be a full {requestType, requestData} object or just the data.
                var root = doc.RootElement;
                if (string.IsNullOrWhiteSpace(requestType) && root.TryGetProperty("requestType", out var rt)) requestType = rt.GetString()!;
                data = root.TryGetProperty("requestData", out var rd) ? rd.Clone() : root.TryGetProperty("requestType", out _) ? null : root.Clone();
            }
            var result = await obs.RequestAsync(requestType, data, c.Cancellation);
            c.SetArg("obsRawResponse", result.GetRawText());
            if (s.Bool("resultsToArgs")) Flatten(c, s.Str("prefix") is { Length: > 0 } p ? p : "obs", result);
        });
        On(48, async (s, c) =>
        {
            var obs = Obs(s, c);
            var path = MapPath(c, s.Str("path"));
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var source = c.Parse(s.Str("sourceName")) is { Length: > 0 } src ? src : c.Parse(s.Str("sceneName"));
            await obs.RequestAsync("SaveSourceScreenshot", new
            {
                sourceName = source, imageFormat = ext is "jpg" or "jpeg" ? "jpg" : ext.Length > 0 ? ext : "png",
                imageFilePath = path, imageCompressionQuality = Math.Clamp(s.Int("quality", -1), -1, 100),
            }, c.Cancellation);
            c.SetArg("screenshotPath", path);
        });
        On(49, async (s, c) =>
        {
            var action = s.Int("state") switch
            {
                0 => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PLAY",
                1 => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PAUSE",
                2 => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_STOP",
                3 => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_RESTART",
                4 => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_NEXT",
                _ => "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_PREVIOUS",
            };
            await Obs(s, c).RequestAsync("TriggerMediaInputAction", new { inputName = c.Parse(s.Str("sourceName")), mediaAction = action }, c.Cancellation);
        });
        On(324, async (s, c) =>
        {
            var (obs, scene, id) = await Item(s, c);
            var t = (await obs.RequestAsync("GetSceneItemTransform", new { sceneName = scene, sceneItemId = id }, c.Cancellation)).GetProperty("sceneItemTransform");
            Flatten(c, "obs.sceneItem", t);
            c.SetArg("obs.sceneItemId", id);
        });
        On(328, async (s, c) =>
        {
            var obs = Obs(s, c);
            var stream = await obs.RequestAsync("GetStreamStatus", null, c.Cancellation);
            var record = await obs.RequestAsync("GetRecordStatus", null, c.Cancellation);
            c.SetArg("obsStreaming", stream.GetProperty("outputActive").GetBoolean());
            c.SetArg("obsRecording", record.GetProperty("outputActive").GetBoolean());
            c.SetArg("obsConnected", true);
        });
        On(329, async (s, c) => await Obs(s, c).RequestAsync("CreateRecordChapter", new { chapterName = c.Parse(s.Str("chapterName")) }, c.Cancellation));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ObsClient Obs(SubActionDef s, RunContext c) =>
        c.Service<ObsService>().Client(s.GuidOf("connectionId")) ?? throw new InvalidOperationException("No OBS connection is available");

    private static string MapPath(RunContext c, string raw) => c.Service<IPathMapper>().Map(c.Parse(raw));

    private static async Task<(ObsClient Obs, string Scene, int Id)> Item(SubActionDef s, RunContext c)
    {
        var obs = Obs(s, c);
        var scene = c.Parse(s.Str("sceneName"));
        var source = c.Parse(s.Str("sourceName"));
        var res = await obs.RequestAsync("GetSceneItemId", new { sceneName = scene, sourceName = source }, c.Cancellation);
        return (obs, scene, res.GetProperty("sceneItemId").GetInt32());
    }

    private static async Task<bool> Enabled(ObsClient obs, string scene, int id, RunContext c) =>
        (await obs.RequestAsync("GetSceneItemEnabled", new { sceneName = scene, sceneItemId = id }, c.Cancellation)).GetProperty("sceneItemEnabled").GetBoolean();

    /// <summary>Streamer.bot state selector: 0 = on/visible, 1 = off/hidden, 2 = toggle (reads the current value).</summary>
    private static async Task<bool> Desired(int state, Func<Task<bool>> current) => state switch
    {
        0 => true,
        1 => false,
        _ => !await current(),
    };

    private static async Task Output(ObsClient obs, RunContext c, int state, params string[] requests)
    {
        var request = state >= 0 && state < requests.Length ? requests[state] : requests[^1];
        await obs.RequestAsync(request, null, c.Cancellation);
    }

    private static async Task FilterState(RunContext c, ObsClient obs, string source, string filter, int state)
    {
        var enabled = await Desired(state, async () =>
            (await obs.RequestAsync("GetSourceFilter", new { sourceName = source, filterName = filter }, c.Cancellation)).GetProperty("filterEnabled").GetBoolean());
        await obs.RequestAsync("SetSourceFilterEnabled", new { sourceName = source, filterName = filter, filterEnabled = enabled }, c.Cancellation);
    }

    private static async Task<List<string>> Filters(ObsClient obs, string source, RunContext c) =>
        (await obs.RequestAsync("GetSourceFilterList", new { sourceName = source }, c.Cancellation)).GetProperty("filters")
            .EnumerateArray().Select(f => f.GetProperty("filterName").GetString()!).ToList();

    private static async Task<List<int>> Items(ObsClient obs, string scene, string request, RunContext c) =>
        (await obs.RequestAsync(request, new { sceneName = scene }, c.Cancellation)).GetProperty("sceneItems")
            .EnumerateArray().Select(i => i.GetProperty("sceneItemId").GetInt32()).ToList();

    private static async Task HideAll(RunContext c, ObsClient obs, string scene, string request)
    {
        foreach (var id in await Items(obs, scene, request, c))
            await obs.RequestAsync("SetSceneItemEnabled", new { sceneName = scene, sceneItemId = id, sceneItemEnabled = false }, c.Cancellation);
    }

    private static async Task RandomVisible(RunContext c, ObsClient obs, string scene, string request)
    {
        var items = await Items(obs, scene, request, c);
        if (items.Count == 0) return;
        var pick = items[Random.Shared.Next(items.Count)];
        foreach (var id in items)
            await obs.RequestAsync("SetSceneItemEnabled", new { sceneName = scene, sceneItemId = id, sceneItemEnabled = id == pick }, c.Cancellation);
    }

    private static async Task InputSettings(SubActionDef s, RunContext c, object settings) =>
        await Obs(s, c).RequestAsync("SetInputSettings", new { inputName = c.Parse(s.Str("sourceName")), inputSettings = settings }, c.Cancellation);

    /// <summary>Accepts #RRGGBB or #AARRGGBB and returns OBS's ABGR integer.</summary>
    internal static uint ParseColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb)) return 0xFFFFFFFF;
        var a = (argb >> 24) & 0xFF;
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    private static void Flatten(RunContext c, string prefix, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Flatten(c, $"{prefix}.{p.Name}", p.Value);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in e.EnumerateArray()) Flatten(c, $"{prefix}[{i++}]", item);
                break;
            default:
                c.SetArg(prefix, CherBotJson.ToClr(e));
                break;
        }
    }
}
