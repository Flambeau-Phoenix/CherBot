using System.Globalization;
using System.Text.RegularExpressions;
using CherBot.Core.State;

namespace CherBot.Core.Engine;

/// <summary>
/// Expands Streamer.bot-style templates: <c>%argument%</c> (optionally <c>%argument:format%</c>) from the run's
/// arguments, and <c>~global~</c> from persisted globals. Unknown names are left untouched.
/// </summary>
public sealed partial class TemplateEngine(VariableService variables)
{
    [GeneratedRegex(@"%(?<name>[A-Za-z0-9_\.\[\]\-]+)(?::(?<fmt>[^%]+))?%")]
    private static partial Regex ArgPattern();

    [GeneratedRegex(@"~(?<name>[A-Za-z0-9_\.\-]+)~")]
    private static partial Regex GlobalPattern();

    public string Parse(string? text, IReadOnlyDictionary<string, object?>? args)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = text;
        if (args is not null && result.Contains('%'))
        {
            result = ArgPattern().Replace(result, m =>
            {
                var name = m.Groups["name"].Value;
                if (!TryLookup(args, name, out var value)) return m.Value;
                var fmt = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
                return fmt is not null && value is IFormattable f ? f.ToString(fmt, CultureInfo.InvariantCulture) : CherBotJson.Format(value);
            });
        }
        if (result.Contains('~'))
        {
            result = GlobalPattern().Replace(result, m =>
                variables.TryGetGlobal(m.Groups["name"].Value, true, out var value) ? CherBotJson.Format(value) : m.Value);
        }
        return result;
    }

    public string Parse(string? text, System.Collections.Concurrent.ConcurrentDictionary<string, object?> args) =>
        Parse(text, (IReadOnlyDictionary<string, object?>)args);

    private static bool TryLookup(IReadOnlyDictionary<string, object?> args, string name, out object? value)
    {
        if (args.TryGetValue(name, out value)) return true;
        // Case-insensitive fallback for dictionaries created with an ordinal comparer.
        foreach (var (k, v) in args)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }
        value = null;
        return false;
    }

    /// <summary>True when <paramref name="text"/> is exactly one unresolved <c>%name%</c> placeholder.</summary>
    public static bool IsUnresolvedPlaceholder(string text) => ArgPattern().Match(text) is { Success: true } m && m.Length == text.Length;
}
