using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace InspectAzureAI.Swe.Util;

/// <summary>
/// Port of inspect_swe <c>_util/toml.py</c> <c>to_toml</c>: an ordered TOML writer for agent configuration files,
/// extended with inline tables and quoted keys.
/// </summary>
/// <remarks>
/// A document is a sequence of entries in insertion order; a later entry with the same key replaces the earlier
/// value in its original position (Python dict semantics). Values may be strings, booleans, integers, floating
/// point numbers, enumerables of values, and string-keyed maps: an <see cref="IReadOnlyDictionary{TKey, TValue}"/>
/// of <c>string</c> to <c>object?</c>, a sequence of <see cref="KeyValuePair{TKey, TValue}"/>, a
/// <c>string</c>-to-<c>string</c> map or any <see cref="IDictionary"/> with string keys. A map at the top level is
/// written as a <c>[table]</c>; a map inside a table is written as an inline table (deviation D-S2: Python silently
/// drops nested maps).
/// </remarks>
public static partial class Toml
{
    /// <summary>
    /// Writes the document: the top-level non-table entries first, as <c>key = value</c> with keys written verbatim
    /// (so dotted keys such as <c>features.goals</c> are intentional); then each top-level map as a table, preceded by
    /// a blank line when anything has been written, headed <c>[name]</c> with the name written verbatim. Lines are
    /// joined with <c>\n</c> and there is no trailing newline. A null value throws.
    /// </summary>
    public static string Write(IEnumerable<KeyValuePair<string, object?>> document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var lines = new List<string>();
        var tables = new List<KeyValuePair<string, List<KeyValuePair<string, object?>>>>();
        foreach (var (key, value) in Ordered(document))
        {
            if (value is not null && AsTable(value) is { } table)
            {
                tables.Add(KeyValuePair.Create(key, table));
            }
            else
            {
                lines.Add($"{key} = {Format(value)}");
            }
        }

        foreach (var (name, table) in tables)
        {
            if (lines.Count > 0)
            {
                lines.Add("");
            }

            lines.Add($"[{name}]");
            foreach (var (key, value) in table)
            {
                lines.Add($"{key} = {Format(value)}");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Port of <c>_format_value</c>: a basic string with <c>\\</c>, <c>\"</c>, <c>\n</c>, <c>\r</c>, <c>\t</c> and
    /// <c>\uXXXX</c> escapes for the remaining control characters; <c>true</c>/<c>false</c>; invariant-culture
    /// numbers (a whole floating point value keeps a <c>.0</c>); <c>[a, b]</c> lists; <c>{ k = v }</c> inline tables
    /// with <see cref="Key"/>-quoted keys. Null throws <see cref="ArgumentException"/> ("TOML doesn't support null
    /// values"), as does an unsupported type.
    /// </summary>
    public static string FormatValue(object value) => Format(value);

    /// <summary>A key segment: bare when it matches <c>[A-Za-z0-9_-]+</c>, otherwise a quoted basic string.</summary>
    public static string Key(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return BareKeyRegex().IsMatch(segment) ? segment : QuoteString(segment);
    }

    /// <summary>A dotted table path with each segment passed through <see cref="Key"/>: <c>TablePath("mcp_servers", name)</c> is <c>"mcp_servers." + Key(name)</c>.</summary>
    public static string TablePath(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return string.Join(".", segments.Select(Key));
    }

    private static string Format(object? value)
    {
        switch (value)
        {
            case null:
                throw new ArgumentException("TOML doesn't support null values");
            case string text:
                return QuoteString(text);
            case bool flag:
                return flag ? "true" : "false";
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case float single:
                return FormatFloat(single, single.ToString("R", CultureInfo.InvariantCulture));
            case double number:
                return FormatFloat(number, number.ToString("R", CultureInfo.InvariantCulture));
            case decimal money:
                return WithFraction(money.ToString(CultureInfo.InvariantCulture));
        }

        if (AsTable(value) is { } table)
        {
            return table.Count == 0 ? "{}" : "{ " + string.Join(", ", table.Select(e => $"{Key(e.Key)} = {Format(e.Value)}")) + " }";
        }

        if (value is IEnumerable items)
        {
            return "[" + string.Join(", ", items.Cast<object?>().Select(Format)) + "]";
        }

        throw new ArgumentException($"Unsupported type: {value.GetType()}");
    }

    private static string FormatFloat(double value, string roundTrip)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        return WithFraction(roundTrip);
    }

    private static string WithFraction(string text) =>
        text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.OrdinalIgnoreCase) ? text : text + ".0";

    private static string QuoteString(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        escaped = ControlCharRegex().Replace(escaped, match => "\\u" + ((int)match.Value[0]).ToString("X4", CultureInfo.InvariantCulture));
        return "\"" + escaped + "\"";
    }

    /// <summary>The map's entries in order with Python dict semantics, or null when the value is not a map.</summary>
    private static List<KeyValuePair<string, object?>>? AsTable(object value) => value switch
    {
        string => null,
        IEnumerable<KeyValuePair<string, object?>> pairs => Ordered(pairs),
        IEnumerable<KeyValuePair<string, string>> strings => Ordered(strings.Select(kv => KeyValuePair.Create(kv.Key, (object?)kv.Value))),
        IDictionary dictionary => Ordered(dictionary.Cast<DictionaryEntry>().Select(entry => KeyValuePair.Create(
            entry.Key as string ?? throw new ArgumentException($"TOML table keys must be strings, not {entry.Key.GetType()}"),
            entry.Value))),
        _ => null,
    };

    private static List<KeyValuePair<string, object?>> Ordered(IEnumerable<KeyValuePair<string, object?>> entries)
    {
        var ordered = new List<KeyValuePair<string, object?>>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (positions.TryGetValue(key, out var position))
            {
                ordered[position] = KeyValuePair.Create(key, value);
            }
            else
            {
                positions[key] = ordered.Count;
                ordered.Add(KeyValuePair.Create(key, value));
            }
        }

        return ordered;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\z")]
    private static partial Regex BareKeyRegex();

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex ControlCharRegex();
}
