using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace InspectAzureAI.Cli.Args;

/// <summary>
/// The subset of <c>yaml.safe_load</c> that <c>parse_cli_args</c> (<c>_util/config.py</c>) and the config-file readers
/// rely on: YAML 1.1 scalar resolution (null, booleans including <c>yes/no/on/off</c>, decimal/octal/hex/binary/sexagesimal
/// integers, floats, <c>.inf</c>/<c>.nan</c>), single- and double-quoted strings, flow sequences and mappings
/// (<c>[a, 1]</c>, <c>{model: x, temperature: 0.5}</c>) and indentation-based block mappings and sequences.
/// Values come back as <c>null</c>, <c>bool</c>, <c>long</c> (<see cref="BigInteger"/> beyond 64 bits), <c>double</c>,
/// <c>string</c>, <c>List&lt;object?&gt;</c> or <c>Dictionary&lt;string, object?&gt;</c>. Timestamps stay strings
/// (PyYAML makes them dates) and block scalars (<c>|</c>, <c>&gt;</c>), anchors and tags are not supported.
/// </summary>
public static partial class YamlValue
{
    /// <summary>Parses one YAML document. Text that PyYAML rejects (for example a bare <c>,</c>) is a <see cref="FormatException"/>.</summary>
    public static object? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = SplitLines(text);
        if (lines.Count == 0)
        {
            return null;
        }

        if (lines.Count == 1)
        {
            return ParseLine(lines[0].Text);
        }

        return ParseBlock(lines, 0, lines.Count, lines[0].Indent);
    }

    /// <summary>Resolves a plain (unquoted) scalar with YAML 1.1's implicit types.</summary>
    public static object? ResolveScalar(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var value = text.Trim();
        if (value.Length == 0 || value is "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        if (BoolTrue().IsMatch(value))
        {
            return true;
        }

        if (BoolFalse().IsMatch(value))
        {
            return false;
        }

        if (IntPattern().IsMatch(value))
        {
            return ParseInt(value);
        }

        if (FloatPattern().IsMatch(value))
        {
            return ParseFloat(value);
        }

        return value;
    }

    private static object? ParseLine(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return null;
        }

        if (trimmed[0] is '[' or '{' or '"' or '\'')
        {
            var parser = new FlowParser(trimmed);
            var value = parser.ParseValue(inFlow: false);
            parser.SkipWhitespace();
            if (!parser.AtEnd)
            {
                throw new FormatException($"Unexpected text after the YAML value: '{trimmed}'.");
            }

            return value;
        }

        if (trimmed[0] is ',' or ']' or '}')
        {
            throw new FormatException($"'{trimmed}' is not a valid YAML value (a plain scalar cannot start with '{trimmed[0]}').");
        }

        if (trimmed == "-" || trimmed.StartsWith("- ", StringComparison.Ordinal))
        {
            return new List<object?> { ParseLine(trimmed[1..]) };
        }

        var separator = MappingSeparator(trimmed);
        if (separator >= 0)
        {
            var key = trimmed[..separator].Trim();
            var rest = trimmed[(separator + 1)..];
            return new Dictionary<string, object?>(StringComparer.Ordinal) { [KeyText(key)] = ParseLine(rest) };
        }

        return ResolveScalar(trimmed);
    }

    private static object? ParseBlock(IReadOnlyList<Line> lines, int start, int end, int indent)
    {
        if (IsSequenceItem(lines[start].Text))
        {
            var list = new List<object?>();
            var index = start;
            while (index < end)
            {
                var line = lines[index];
                if (line.Indent != indent || !IsSequenceItem(line.Text))
                {
                    throw new FormatException($"Invalid YAML block sequence at line {line.Number}: '{line.Text}'.");
                }

                var next = NextSibling(lines, index, end, indent);
                var item = line.Text.TrimStart()[1..].Trim();
                if (item.Length == 0)
                {
                    list.Add(next > index + 1 ? ParseBlock(lines, index + 1, next, lines[index + 1].Indent) : null);
                }
                else if (next > index + 1)
                {
                    throw new FormatException($"Unsupported YAML at line {line.Number}: a sequence item with nested lines must start on its own line.");
                }
                else
                {
                    list.Add(ParseLine(item));
                }

                index = next;
            }

            return list;
        }

        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        var current = start;
        while (current < end)
        {
            var line = lines[current];
            var text = line.Text.Trim();
            var separator = MappingSeparator(text);
            if (line.Indent != indent || separator < 0)
            {
                throw new FormatException($"Invalid YAML block mapping at line {line.Number}: '{line.Text}'.");
            }

            var key = KeyText(text[..separator].Trim());
            var rest = text[(separator + 1)..].Trim();
            var next = NextSibling(lines, current, end, indent);
            if (rest.Length == 0)
            {
                map[key] = next > current + 1 ? ParseBlock(lines, current + 1, next, lines[current + 1].Indent) : null;
            }
            else if (next > current + 1)
            {
                throw new FormatException($"Unsupported YAML at line {line.Number}: a value with nested lines must start on its own line.");
            }
            else
            {
                map[key] = ParseLine(rest);
            }

            current = next;
        }

        return map;
    }

    private static bool IsSequenceItem(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed == "-" || trimmed.StartsWith("- ", StringComparison.Ordinal);
    }

    private static int NextSibling(IReadOnlyList<Line> lines, int index, int end, int indent)
    {
        var next = index + 1;
        while (next < end && lines[next].Indent > indent)
        {
            next++;
        }

        if (next < end && lines[next].Indent < indent)
        {
            throw new FormatException($"Invalid YAML indentation at line {lines[next].Number}: '{lines[next].Text}'.");
        }

        return next;
    }

    /// <summary>The index of the <c>:</c> that separates a block-mapping key from its value: a colon followed by whitespace or the end of the line (so <c>1:30</c> and <c>http://x</c> are scalars).</summary>
    private static int MappingSeparator(string text)
    {
        if (text.Length == 0 || text[0] is '"' or '\'' or '[' or '{')
        {
            return -1;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ':' && (i == text.Length - 1 || char.IsWhiteSpace(text[i + 1])))
            {
                return i;
            }
        }

        return -1;
    }

    private static string KeyText(string key)
    {
        if (key.Length >= 2 && (key[0] is '"' or '\'') && key[^1] == key[0])
        {
            var parser = new FlowParser(key);
            return parser.ParseValue(inFlow: false)?.ToString() ?? "";
        }

        return FlowParser.KeyOf(ResolveScalar(key));
    }

    private static object ParseInt(string text)
    {
        var negative = text.StartsWith('-');
        var body = text.TrimStart('+', '-').Replace("_", "", StringComparison.Ordinal);
        BigInteger value;
        if (body.StartsWith("0b", StringComparison.Ordinal))
        {
            value = body[2..].Aggregate(BigInteger.Zero, (acc, c) => acc * 2 + (c - '0'));
        }
        else if (body.StartsWith("0x", StringComparison.Ordinal))
        {
            value = BigInteger.Parse("0" + body[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else if (body.Contains(':', StringComparison.Ordinal))
        {
            value = body.Split(':').Aggregate(BigInteger.Zero, (acc, part) => acc * 60 + BigInteger.Parse(part, CultureInfo.InvariantCulture));
        }
        else if (body.Length > 1 && body[0] == '0')
        {
            value = body[1..].Aggregate(BigInteger.Zero, (acc, c) => acc * 8 + (c - '0'));
        }
        else
        {
            value = BigInteger.Parse(body, CultureInfo.InvariantCulture);
        }

        if (negative)
        {
            value = -value;
        }

        return value >= long.MinValue && value <= long.MaxValue ? (object)(long)value : value;
    }

    private static double ParseFloat(string text)
    {
        var body = text.Replace("_", "", StringComparison.Ordinal);
        var lower = body.ToLowerInvariant();
        if (lower.EndsWith(".inf", StringComparison.Ordinal))
        {
            return lower.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
        }

        if (lower == ".nan")
        {
            return double.NaN;
        }

        if (body.Contains(':', StringComparison.Ordinal))
        {
            var negative = body.StartsWith('-');
            var parts = body.TrimStart('+', '-').Split(':');
            var whole = parts[..^1].Aggregate(0.0, (acc, part) => acc * 60 + double.Parse(part, CultureInfo.InvariantCulture));
            var value = whole * 60 + double.Parse(parts[^1], CultureInfo.InvariantCulture);
            return negative ? -value : value;
        }

        return double.Parse(body, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static List<Line> SplitLines(string text)
    {
        var lines = new List<Line>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.TrimEnd('\r');
            var content = StripComment(line);
            if (content.Trim().Length == 0)
            {
                continue;
            }

            var indent = content.Length - content.TrimStart().Length;
            lines.Add(new Line(number, indent, content));
        }

        return lines;
    }

    private static string StripComment(string line)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
            }
            else if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
            }
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return line[..i];
            }
        }

        return line;
    }

    private readonly record struct Line(int Number, int Indent, string Text);

    [GeneratedRegex("^(yes|Yes|YES|true|True|TRUE|on|On|ON)$")]
    private static partial Regex BoolTrue();

    [GeneratedRegex("^(no|No|NO|false|False|FALSE|off|Off|OFF)$")]
    private static partial Regex BoolFalse();

    [GeneratedRegex(@"^(?:[-+]?0b[0-1_]+|[-+]?0[0-7_]+|[-+]?(?:0|[1-9][0-9_]*)|[-+]?0x[0-9a-fA-F_]+|[-+]?[1-9][0-9_]*(?::[0-5]?[0-9])+)$")]
    private static partial Regex IntPattern();

    [GeneratedRegex(@"^(?:[-+]?(?:[0-9][0-9_]*)\.[0-9_]*(?:[eE][-+][0-9]+)?|\.[0-9_]+(?:[eE][-+][0-9]+)?|[-+]?[0-9][0-9_]*(?::[0-5]?[0-9])+\.[0-9_]*|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN))$")]
    private static partial Regex FloatPattern();

    /// <summary>Recursive-descent parser for flow collections and quoted scalars.</summary>
    private sealed class FlowParser(string text)
    {
        private int _pos;

        public bool AtEnd => _pos >= text.Length;

        public void SkipWhitespace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
            {
                _pos++;
            }
        }

        public object? ParseValue(bool inFlow)
        {
            SkipWhitespace();
            if (AtEnd)
            {
                return null;
            }

            return text[_pos] switch
            {
                '[' => ParseSequence(),
                '{' => ParseMapping(),
                '"' => ParseDoubleQuoted(),
                '\'' => ParseSingleQuoted(),
                _ => ResolveScalar(ReadPlain(inFlow)),
            };
        }

        public static string KeyOf(object? key) => key switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            string s => s,
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            List<object?> or Dictionary<string, object?> => throw new FormatException("A collection cannot be a mapping key."),
            var other => other.ToString() ?? "",
        };

        private List<object?> ParseSequence()
        {
            _pos++;
            var list = new List<object?>();
            while (true)
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new FormatException($"Unterminated flow sequence in '{text}'.");
                }

                if (text[_pos] == ']')
                {
                    _pos++;
                    return list;
                }

                var item = ParseValue(inFlow: true);
                SkipWhitespace();
                if (!AtEnd && text[_pos] == ':')
                {
                    _pos++;
                    SkipWhitespace();
                    var value = !AtEnd && text[_pos] is ',' or ']' ? null : ParseValue(inFlow: true);
                    item = new Dictionary<string, object?>(StringComparer.Ordinal) { [KeyOf(item)] = value };
                    SkipWhitespace();
                }

                list.Add(item);
                if (AtEnd)
                {
                    throw new FormatException($"Unterminated flow sequence in '{text}'.");
                }

                if (text[_pos] == ',')
                {
                    _pos++;
                }
                else if (text[_pos] != ']')
                {
                    throw new FormatException($"Expected ',' or ']' at position {_pos} in '{text}'.");
                }
            }
        }

        private Dictionary<string, object?> ParseMapping()
        {
            _pos++;
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new FormatException($"Unterminated flow mapping in '{text}'.");
                }

                if (text[_pos] == '}')
                {
                    _pos++;
                    return map;
                }

                var key = KeyOf(ParseValue(inFlow: true));
                SkipWhitespace();
                object? value = null;
                if (!AtEnd && text[_pos] == ':')
                {
                    _pos++;
                    SkipWhitespace();
                    if (!(!AtEnd && text[_pos] is ',' or '}'))
                    {
                        value = ParseValue(inFlow: true);
                    }
                }

                map[key] = value;
                SkipWhitespace();
                if (AtEnd)
                {
                    throw new FormatException($"Unterminated flow mapping in '{text}'.");
                }

                if (text[_pos] == ',')
                {
                    _pos++;
                }
                else if (text[_pos] != '}')
                {
                    throw new FormatException($"Expected ',' or '}}' at position {_pos} in '{text}'.");
                }
            }
        }

        private string ReadPlain(bool inFlow)
        {
            var start = _pos;
            if (text[_pos] is ',' or ']' or '}')
            {
                throw new FormatException($"'{text}' is not a valid YAML value (a plain scalar cannot start with '{text[_pos]}').");
            }

            while (!AtEnd)
            {
                var c = text[_pos];
                if (inFlow && c is ',' or ']' or '}')
                {
                    break;
                }

                if (c == ':' && (_pos + 1 >= text.Length || char.IsWhiteSpace(text[_pos + 1]) || (inFlow && text[_pos + 1] is ',' or ']' or '}')))
                {
                    break;
                }

                _pos++;
            }

            return text[start.._pos].Trim();
        }

        private string ParseSingleQuoted()
        {
            _pos++;
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new FormatException($"Unterminated single-quoted string in '{text}'.");
                }

                var c = text[_pos++];
                if (c == '\'')
                {
                    if (!AtEnd && text[_pos] == '\'')
                    {
                        builder.Append('\'');
                        _pos++;
                        continue;
                    }

                    return builder.ToString();
                }

                builder.Append(c);
            }
        }

        private string ParseDoubleQuoted()
        {
            _pos++;
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw new FormatException($"Unterminated double-quoted string in '{text}'.");
                }

                var c = text[_pos++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (AtEnd)
                {
                    throw new FormatException($"Unterminated escape in '{text}'.");
                }

                var escape = text[_pos++];
                switch (escape)
                {
                    case '0': builder.Append('\0'); break;
                    case 'a': builder.Append('\a'); break;
                    case 'b': builder.Append('\b'); break;
                    case 't': case '\t': builder.Append('\t'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'v': builder.Append('\v'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'r': builder.Append('\r'); break;
                    case 'e': builder.Append('\u001b'); break;
                    case ' ': builder.Append(' '); break;
                    case '"': builder.Append('"'); break;
                    case '/': builder.Append('/'); break;
                    case '\\': builder.Append('\\'); break;
                    case 'N': builder.Append('\u0085'); break;
                    case '_': builder.Append('\u00a0'); break;
                    case 'L': builder.Append('\u2028'); break;
                    case 'P': builder.Append('\u2029'); break;
                    case 'x': builder.Append(ReadCodePoint(2)); break;
                    case 'u': builder.Append(ReadCodePoint(4)); break;
                    case 'U': builder.Append(ReadCodePoint(8)); break;
                    default: throw new FormatException($"Unknown escape '\\{escape}' in '{text}'.");
                }
            }
        }

        private string ReadCodePoint(int digits)
        {
            if (_pos + digits > text.Length)
            {
                throw new FormatException($"Truncated escape in '{text}'.");
            }

            var codePoint = int.Parse(text.AsSpan(_pos, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            _pos += digits;
            return char.ConvertFromUtf32(codePoint);
        }
    }
}
