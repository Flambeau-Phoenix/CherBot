using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CherBot.Core;

public static class CherBotJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Converts a JSON value into plain CLR values (string, long, double, bool, list, dictionary).</summary>
    public static object? ToClr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(ToClr).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ToClr(p.Value)),
        _ => null,
    };

    public static object? ToClr(JsonNode? node) => node is null ? null : ToClr(JsonSerializer.SerializeToElement(node));

    /// <summary>Streamer.bot "auto type": turns numeric and boolean strings into typed values.</summary>
    public static object? AutoType(object? value)
    {
        if (value is not string s) return value;
        var t = s.Trim();
        if (t.Length == 0) return s;
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (t.Contains('.') && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        if (bool.TryParse(t, out var b)) return b;
        return s;
    }

    /// <summary>Formats a value the way it is substituted into %templates%.</summary>
    public static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "True" : "False",
        DateTimeOffset dto => dto.ToString("G", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("G", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.GetRawText(),
        _ when value is System.Collections.IEnumerable and not string => JsonSerializer.Serialize(value, Options),
        _ => value.ToString() ?? "",
    };

    public static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case int i: number = i; return true;
            case long l: number = l; return true;
            case double d: number = d; return true;
            case float f: number = f; return true;
            case decimal m: number = (double)m; return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p): number = p; return true;
            case JsonElement { ValueKind: JsonValueKind.Number } je: number = je.GetDouble(); return true;
            default: number = 0; return false;
        }
    }
}
