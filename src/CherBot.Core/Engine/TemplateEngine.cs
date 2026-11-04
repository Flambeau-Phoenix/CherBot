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

    [GeneratedRegex(@"%(?<name>[A-Za-z0-9_\.\[\]\-]+)(?::(?<fmt>[^%]+))?%|~(?<global>[A-Za-z0-9_\.\-]+)~")]
    private static partial Regex PlaceholderPattern();

    /// <summary>
    /// Expands placeholders in a single pass: substituted values are inserted as-is and never scanned again, so an argument
    /// holding viewer text such as <c>~someGlobal~</c> cannot pull in a global.
    /// </summary>
    public string Parse(string? text, IReadOnlyDictionary<string, object?>? args)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (text.IndexOf('%') < 0 && text.IndexOf('~') < 0) return text;
        return PlaceholderPattern().Replace(text, m =>
        {
            if (m.Groups["global"].Success)
                return variables.TryGetGlobal(m.Groups["global"].Value, true, out var g) ? CherBotJson.Format(g) : m.Value;
            if (args is null || !TryLookup(args, m.Groups["name"].Value, out var value)) return m.Value;
            var fmt = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
            return fmt is not null && value is IFormattable f ? f.ToString(fmt, CultureInfo.InvariantCulture) : CherBotJson.Format(value);
        });
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
