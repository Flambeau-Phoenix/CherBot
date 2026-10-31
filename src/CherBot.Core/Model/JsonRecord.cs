using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CherBot.Core.Model;

/// <summary>
/// Base for persisted records whose per-type fields are open-ended (sub-actions, triggers, commands).
/// Unknown fields round-trip through <see cref="Extra"/>, so imported Streamer.bot data is never lost.
/// </summary>
public abstract class JsonRecord
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public bool Has(string name) => Extra is not null && Extra.TryGetValue(name, out var v) && v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    public JsonElement? Raw(string name) => Extra is not null && Extra.TryGetValue(name, out var v) ? v : null;

    public string Str(string name, string fallback = "")
    {
        if (Raw(name) is not { } v) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? fallback,
            JsonValueKind.Null or JsonValueKind.Undefined => fallback,
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => v.GetRawText(),
        };
    }

    public int Int(string name, int fallback = 0)
    {
        if (Raw(name) is not { } v) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.Number) return (int)v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        if (v.ValueKind == JsonValueKind.True) return 1;
        return fallback;
    }

    public double Dbl(string name, double fallback = 0)
    {
        if (Raw(name) is not { } v) return fallback;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return fallback;
    }

    public bool Bool(string name, bool fallback = false)
    {
        if (Raw(name) is not { } v) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetDouble() != 0,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : fallback,
            _ => fallback,
        };
    }

    public Guid? GuidOf(string name)
    {
        if (Raw(name) is not { } v || v.ValueKind != JsonValueKind.String) return null;
        return Guid.TryParse(v.GetString(), out var g) && g != Guid.Empty ? g : null;
    }

    public List<string> StrList(string name)
    {
        if (Raw(name) is not { ValueKind: JsonValueKind.Array } v) return [];
        return v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()).ToList();
    }

    public Dictionary<string, string> StrDict(string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Raw(name) is not { } v) return result;
        if (v.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in v.EnumerateObject())
                result[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        }
        else if (v.ValueKind == JsonValueKind.Array)
        {
            // Streamer.bot sometimes stores tables as [{key, value}] pairs.
            foreach (var e in v.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var key = e.TryGetProperty("key", out var k) ? k.GetString() : e.TryGetProperty("Key", out var k2) ? k2.GetString() : null;
                var val = e.TryGetProperty("value", out var vv) ? vv : e.TryGetProperty("Value", out var vv2) ? vv2 : default;
                if (key is not null) result[key] = val.ValueKind == JsonValueKind.String ? val.GetString() ?? "" : val.ValueKind == JsonValueKind.Undefined ? "" : val.GetRawText();
            }
        }
        return result;
    }

    public void Set(string name, object? value)
    {
        Extra ??= new Dictionary<string, JsonElement>();
        Extra[name] = JsonSerializer.SerializeToElement(value, CherBotJson.Options);
    }
}
