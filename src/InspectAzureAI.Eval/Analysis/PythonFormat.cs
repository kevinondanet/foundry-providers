using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log.Json;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>
/// Python's textual forms of JSON-decoded values, as the column extractors need them: <c>str()</c> and
/// <c>repr()</c> of scalars, lists and dicts, <c>pprint.pformat()</c> (dict keys sorted) and
/// <c>_util/format.py</c> <c>format_function_call</c>.
/// </summary>
internal static class PythonFormat
{
    /// <summary>Python <c>str(value)</c> of a JSON-decoded value: strings verbatim, everything else its <c>repr</c>.</summary>
    public static string Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && !IsNonFiniteSentinel(value) ? text : Repr(node);

    /// <summary>Python <c>repr(value)</c> of a JSON-decoded value (dict keys in insertion order).</summary>
    public static string Repr(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteRepr(sb, node, sortKeys: false);
        return sb.ToString();
    }

    /// <summary>
    /// Port of <c>pprint.pformat(value, width=width)</c>: <see cref="Repr"/> with dict keys sorted. pprint's
    /// line-wrapping of values longer than <paramref name="width"/> is not reproduced (the callers use width 1000).
    /// </summary>
    public static string PFormat(JsonNode? node, int width)
    {
        _ = width;
        var sb = new StringBuilder();
        WriteRepr(sb, node, sortKeys: true);
        return sb.ToString();
    }

    /// <summary>Port of <c>format_function_call</c>: <c>name(k=v, ...)</c> on one line when it fits in <paramref name="width"/>, else one argument per indented line.</summary>
    public static string FormatFunctionCall(string funcName, JsonObject args, int indentSpaces = 4, int width = 80)
    {
        ArgumentNullException.ThrowIfNull(funcName);
        ArgumentNullException.ThrowIfNull(args);
        var formatted = args.Select(pair => $"{pair.Key}={FormatValue(pair.Value, width)}").ToList();
        var argsText = string.Join(", ", formatted);
        if (argsText.Length <= width - 1 - funcName.Length - 2)
        {
            return $"{funcName}({argsText})";
        }

        var pad = new string(' ', indentSpaces);
        var indented = string.Join(",\n", formatted).Split('\n').Select(line => line.Length > 0 ? pad + line : line);
        return $"{funcName}(\n{string.Join('\n', indented)}\n)";
    }

    /// <summary>Port of <c>format_value</c>: strings single-quoted verbatim, lists and dicts pretty-printed, other scalars as <c>str()</c>.</summary>
    public static string FormatValue(JsonNode? value, int width)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && !IsNonFiniteSentinel(scalar))
        {
            return $"'{text}'";
        }

        return value is JsonArray or JsonObject ? PFormat(value, width) : Str(value);
    }

    /// <summary>Python <c>repr(float)</c>: shortest round-trip digits, <c>nan</c> / <c>inf</c> / <c>-inf</c> for the non-finite values.</summary>
    public static string FloatRepr(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        return PythonJsonFormat.FormatDouble(value);
    }

    /// <summary>Python <c>time.isoformat()</c>: <c>HH:MM:SS</c> plus <c>.ffffff</c> when there are microseconds.</summary>
    public static string TimeIso(TimeOnly time) =>
        time.Ticks % TimeSpan.TicksPerSecond == 0
            ? time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : time.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

    /// <summary>
    /// The CLR scalar of a JSON value: <see cref="bool"/>, <see cref="long"/> (integers), <see cref="double"/>
    /// (fractions, exponents and the non-finite sentinels) or <see cref="string"/>.
    /// </summary>
    public static object Scalar(JsonValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.TryGetValue<JsonElement>(out var element))
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    var raw = element.GetRawText();
                    if (!raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E') && element.TryGetInt64(out var integer))
                    {
                        return integer;
                    }

                    return element.GetDouble();
                case JsonValueKind.String:
                    var text = element.GetString()!;
                    return PythonJsonFormat.TryNonFinite(text, out var nonFinite) ? nonFinite : text;
                default:
                    throw new ArgumentException($"Unsupported JSON scalar {element.ValueKind}.", nameof(value));
            }
        }

        if (value.TryGetValue<bool>(out var b))
        {
            return b;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return (long)i;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (value.TryGetValue<float>(out var f))
        {
            return (double)f;
        }

        if (value.TryGetValue<decimal>(out var m))
        {
            return (double)m;
        }

        if (value.TryGetValue<string>(out var s))
        {
            return PythonJsonFormat.TryNonFinite(s, out var nonFinite) ? nonFinite : s;
        }

        if (value.TryGetValue<DateTimeOffset>(out var dto))
        {
            return PythonJsonFormat.FormatIso(dto);
        }

        if (value.TryGetValue<DateTime>(out var dt))
        {
            return PythonJsonFormat.FormatIso(new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt));
        }

        throw new ArgumentException($"Unsupported JSON scalar {value.ToJsonString()}.", nameof(value));
    }

    /// <summary>Port of <c>json.dumps(value)</c> for a cell: Python's separators and escaping, with non-finite sentinels restored to the <c>NaN</c> / <c>Infinity</c> constants.</summary>
    public static string Dumps(JsonNode? node) =>
        Provider.Util.PythonJson.Dumps(node)
            .Replace("\"\\u0001NaN\"", "NaN", StringComparison.Ordinal)
            .Replace("\"\\u0001Infinity\"", "Infinity", StringComparison.Ordinal)
            .Replace("\"\\u0001-Infinity\"", "-Infinity", StringComparison.Ordinal);

    private static bool IsNonFiniteSentinel(JsonValue value) =>
        value.TryGetValue<string>(out var text) && PythonJsonFormat.IsSentinel(text);

    private static void WriteRepr(StringBuilder sb, JsonNode? node, bool sortKeys)
    {
        switch (node)
        {
            case null:
                sb.Append("None");
                break;
            case JsonArray array:
                sb.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    WriteRepr(sb, array[i], sortKeys);
                }

                sb.Append(']');
                break;
            case JsonObject obj:
                sb.Append('{');
                var pairs = sortKeys ? obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList() : obj.ToList();
                for (var i = 0; i < pairs.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    WriteString(sb, pairs[i].Key);
                    sb.Append(": ");
                    WriteRepr(sb, pairs[i].Value, sortKeys);
                }

                sb.Append('}');
                break;
            case JsonValue value:
                switch (Scalar(value))
                {
                    case bool b:
                        sb.Append(b ? "True" : "False");
                        break;
                    case long l:
                        sb.Append(l.ToString(CultureInfo.InvariantCulture));
                        break;
                    case double d:
                        sb.Append(FloatRepr(d));
                        break;
                    case string s:
                        WriteString(sb, s);
                        break;
                }

                break;
            default:
                throw new ArgumentException($"Unsupported JSON node {node.GetType().Name}.", nameof(node));
        }
    }

    /// <summary>Python <c>repr(str)</c>: single quotes unless the text has a single quote and no double quote; backslash escapes for control characters.</summary>
    private static void WriteString(StringBuilder sb, string text)
    {
        var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
        sb.Append(quote);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c == quote)
                    {
                        sb.Append('\\').Append(c);
                    }
                    else if (c < 0x20 || c == 0x7f)
                    {
                        sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append(quote);
    }
}
