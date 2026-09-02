using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.Tools;

/// <summary>
/// Approximation of <c>yaml.safe_load</c> for the non-JSON branch of <see cref="ToolCallParsing.ParseToolCall"/>.
/// Handles the YAML 1.1 scalars PyYAML resolves (bools including yes/no/on/off, null, ints with
/// underscores / hex / octal / binary, floats, quoted strings) plus flow collections and a
/// single-line <c>key: value</c> mapping via JSON parsing. Anything else is returned as the raw
/// string, which is also what PyYAML's <c>YAMLError</c> fallback yields in Python.
/// </summary>
public static partial class YamlScalar
{
    [GeneratedRegex(@"^[-+]?(0|[1-9][0-9_]*)$")]
    private static partial Regex DecimalInt();

    [GeneratedRegex(@"^[-+]?0x[0-9a-fA-F_]+$")]
    private static partial Regex HexInt();

    [GeneratedRegex(@"^[-+]?0o[0-7_]+$")]
    private static partial Regex OctalInt();

    [GeneratedRegex(@"^[-+]?0b[01_]+$")]
    private static partial Regex BinaryInt();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9][0-9_]*(\.[0-9_]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex Float();

    [GeneratedRegex(@"^([^\s:#'""\[\]{},]+):\s+(.+)$", RegexOptions.Singleline)]
    private static partial Regex SingleMapping();

    private static readonly HashSet<string> TrueWords = ["true", "True", "TRUE", "yes", "Yes", "YES", "on", "On", "ON"];
    private static readonly HashSet<string> FalseWords = ["false", "False", "FALSE", "no", "No", "NO", "off", "Off", "OFF"];
    private static readonly HashSet<string> NullWords = ["null", "Null", "NULL", "~"];

    /// <summary>
    /// Parses <paramref name="text"/> into a JSON node. Throws <see cref="JsonException"/> only when a
    /// flow collection exceeds the reader's depth limit (mirroring Python's <c>RecursionError</c>).
    /// </summary>
    public static JsonNode? SafeLoad(string text)
    {
        var s = text.Trim();
        if (s.Length == 0 || NullWords.Contains(s))
        {
            return null;
        }

        if (TrueWords.Contains(s))
        {
            return JsonValue.Create(true);
        }

        if (FalseWords.Contains(s))
        {
            return JsonValue.Create(false);
        }

        if (s[0] is '[' or '{')
        {
            try
            {
                return PythonJson.Loads(s, ToolCallParsing.ParserMaxDepth);
            }
            catch (JsonException ex) when (ToolCallParsing.IsDepthExceeded(ex))
            {
                throw;
            }
            catch (JsonException)
            {
                return JsonValue.Create(text);
            }
        }

        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            try
            {
                return JsonValue.Create(JsonSerializer.Deserialize<string>(s));
            }
            catch (JsonException)
            {
                return JsonValue.Create(text);
            }
        }

        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
        {
            return JsonValue.Create(s[1..^1].Replace("''", "'"));
        }

        if (DecimalInt().IsMatch(s) && BigInteger.TryParse(s.Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var big))
        {
            return big >= long.MinValue && big <= long.MaxValue ? JsonValue.Create((long)big) : JsonValue.Create(text);
        }

        if (HexInt().IsMatch(s))
        {
            return JsonValue.Create(ParseRadix(s, "0x", 16));
        }

        if (OctalInt().IsMatch(s))
        {
            return JsonValue.Create(ParseRadix(s, "0o", 8));
        }

        if (BinaryInt().IsMatch(s))
        {
            return JsonValue.Create(ParseRadix(s, "0b", 2));
        }

        if (s is ".inf" or "+.inf" or ".Inf" or ".INF")
        {
            return JsonValue.Create(double.PositiveInfinity);
        }

        if (s is "-.inf" or "-.Inf" or "-.INF")
        {
            return JsonValue.Create(double.NegativeInfinity);
        }

        if (s is ".nan" or ".NaN" or ".NAN")
        {
            return JsonValue.Create(double.NaN);
        }

        if (Float().IsMatch(s) && double.TryParse(s.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return JsonValue.Create(d);
        }

        var mapping = SingleMapping().Match(s);
        if (mapping.Success && !s.Contains('\n'))
        {
            return new JsonObject { [mapping.Groups[1].Value] = SafeLoad(mapping.Groups[2].Value) };
        }

        return JsonValue.Create(text);
    }

    private static long ParseRadix(string s, string prefix, int radix)
    {
        var negative = s.StartsWith('-');
        var digits = s.TrimStart('+', '-')[prefix.Length..].Replace("_", "");
        var value = Convert.ToInt64(digits, radix);
        return negative ? -value : value;
    }
}
