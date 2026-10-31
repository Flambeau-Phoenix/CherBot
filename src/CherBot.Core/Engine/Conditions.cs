using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CherBot.Core.Engine;

/// <summary>If/Else and While comparison operators, numbered as Streamer.bot stores them.</summary>
public enum ConditionOperator
{
    Equals = 0,
    NotEquals = 1,
    Contains = 2,
    RegexMatch = 3,
    LessThan = 4,
    GreaterThan = 5,
    DoesNotExist = 6,
    EqualsIgnoreCase = 7,
    NotEqualsIgnoreCase = 8,
    IsNullOrEmpty = 9,
}

public static class Conditions
{
    /// <summary>
    /// Evaluates a condition. <paramref name="input"/> and <paramref name="value"/> are the raw (unparsed) fields;
    /// Regex Match writes <c>match.count</c>, <c>match[n]</c> and named groups into the run arguments.
    /// </summary>
    public static bool Evaluate(RunContext ctx, string input, int op, string value, bool autoType, bool ignoreCase = false)
    {
        var oper = (ConditionOperator)op;

        if (oper == ConditionOperator.DoesNotExist)
        {
            var name = input.Trim().Trim('%');
            return !ctx.Args.ContainsKey(name);
        }

        var parsedInput = ctx.Parse(input);
        if (oper == ConditionOperator.IsNullOrEmpty)
            return string.IsNullOrEmpty(parsedInput) || TemplateEngine.IsUnresolvedPlaceholder(parsedInput);

        var parsedValue = ctx.Parse(value);
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        switch (oper)
        {
            case ConditionOperator.Equals:
                return ValuesEqual(parsedInput, parsedValue, autoType, cmp);
            case ConditionOperator.NotEquals:
                return !ValuesEqual(parsedInput, parsedValue, autoType, cmp);
            case ConditionOperator.EqualsIgnoreCase:
                return ValuesEqual(parsedInput, parsedValue, autoType, StringComparison.OrdinalIgnoreCase);
            case ConditionOperator.NotEqualsIgnoreCase:
                return !ValuesEqual(parsedInput, parsedValue, autoType, StringComparison.OrdinalIgnoreCase);
            case ConditionOperator.Contains:
                return parsedInput.Contains(parsedValue, cmp);
            case ConditionOperator.LessThan:
            case ConditionOperator.GreaterThan:
            {
                int order;
                if (CherBotJson.TryNumber(parsedInput, out var a) && CherBotJson.TryNumber(parsedValue, out var b))
                    order = a.CompareTo(b);
                else
                    order = string.Compare(parsedInput, parsedValue, cmp);
                return oper == ConditionOperator.LessThan ? order < 0 : order > 0;
            }
            case ConditionOperator.RegexMatch:
            {
                Match m;
                try
                {
                    m = Regex.Match(parsedInput, parsedValue, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                }
                catch (ArgumentException ex)
                {
                    ctx.Log.LogWarning(ex, "Invalid regex '{Pattern}' in condition", parsedValue);
                    return false;
                }
                if (!m.Success) return false;
                ApplyMatch(ctx, m);
                return true;
            }
            default:
                return false;
        }
    }

    public static void ApplyMatch(RunContext ctx, Match m)
    {
        ctx.SetArg("match.count", m.Groups.Count);
        for (var i = 0; i < m.Groups.Count; i++)
        {
            ctx.SetArg($"match[{i}]", m.Groups[i].Value);
            var name = m.Groups[i].Name;
            if (!int.TryParse(name, out _)) ctx.SetArg(name, m.Groups[i].Value);
        }
    }

    private static bool ValuesEqual(string a, string b, bool autoType, StringComparison cmp)
    {
        if (autoType)
        {
            var ta = CherBotJson.AutoType(a);
            var tb = CherBotJson.AutoType(b);
            if (CherBotJson.TryNumber(ta, out var na) && CherBotJson.TryNumber(tb, out var nb) && ta is not string && tb is not string)
                return na.Equals(nb);
            if (ta is bool ba && tb is bool bb) return ba == bb;
        }
        return string.Equals(a, b, cmp);
    }
}