using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InspectAzureAI.Eval.Tools.Skills;

/// <summary>Raised by <see cref="SkillYaml.Parse"/> for text outside the supported YAML subset (Python's <c>yaml.YAMLError</c>).</summary>
public sealed class SkillYamlException(string message) : Exception(message);

/// <summary>
/// The YAML reader and writer behind SKILL.md front matter, standing in for PyYAML (<c>yaml.safe_load</c> in
/// <c>read.py</c>, <c>yaml.dump</c> in <c>types.py</c>). Deviation: a hand-written subset rather than a YAML
/// package. Reads block mappings and sequences (any nesting), plain / single-quoted / double-quoted scalars,
/// literal (<c>|</c>) and folded (<c>&gt;</c>) block scalars with chomping indicators, empty flow collections
/// and simple one-line flow sequences and mappings, comments, and the YAML core scalar types (null, booleans,
/// integers, floats). Anchors, aliases, tags, multi-document streams, complex keys and nested flow collections
/// are rejected with a <see cref="SkillYamlException"/>.
/// </summary>
internal static partial class SkillYaml
{
    /// <summary>Parses <paramref name="text"/> to a JSON node (null for an empty document).</summary>
    public static JsonNode? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<Line>();
        var raw = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < raw.Length; i++)
        {
            var line = raw[i];
            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }

            if (indent < line.Length && line[indent] == '\t')
            {
                throw new SkillYamlException($"found character '\\t' that cannot start any token (line {i + 1})");
            }

            lines.Add(new Line(i + 1, indent, line));
        }

        var parser = new Parser(lines);
        var node = parser.ParseNode(0, allowSequenceAtIndent: true);
        parser.ExpectEnd();
        return node;
    }

    /// <summary>Renders <paramref name="node"/> in block style (PyYAML's <c>default_flow_style=False</c>, insertion order), ending with a newline.</summary>
    public static string Dump(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteNode(sb, node, 0, inSequence: false);
        return sb.ToString();
    }

    private static void WriteNode(StringBuilder sb, JsonNode? node, int indent, bool inSequence)
    {
        var pad = new string(' ', indent);
        switch (node)
        {
            case JsonObject obj when obj.Count == 0:
                sb.Append(pad).Append("{}\n");
                break;
            case JsonObject obj:
                var first = true;
                foreach (var (key, value) in obj)
                {
                    var prefix = inSequence && first ? "" : pad;
                    first = false;
                    sb.Append(prefix).Append(WriteScalar(JsonValue.Create(key))).Append(':');
                    WriteValue(sb, value, indent);
                }

                break;
            case JsonArray array when array.Count == 0:
                sb.Append(pad).Append("[]\n");
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    sb.Append(pad).Append("- ");
                    if (item is JsonObject { Count: > 0 } or JsonArray { Count: > 0 })
                    {
                        WriteNode(sb, item, indent + 2, inSequence: true);
                    }
                    else
                    {
                        sb.Append(WriteScalar(item)).Append('\n');
                    }
                }

                break;
            default:
                sb.Append(pad).Append(WriteScalar(node)).Append('\n');
                break;
        }
    }

    private static void WriteValue(StringBuilder sb, JsonNode? value, int indent)
    {
        switch (value)
        {
            case JsonObject { Count: > 0 }:
                sb.Append('\n');
                WriteNode(sb, value, indent + 2, inSequence: false);
                break;
            case JsonArray { Count: > 0 }:
                // PyYAML indents a nested block sequence at the parent's indentation
                sb.Append('\n');
                WriteNode(sb, value, indent, inSequence: false);
                break;
            default:
                sb.Append(' ').Append(WriteScalar(value)).Append('\n');
                break;
        }
    }

    private static string WriteScalar(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject:
                return "{}";
            case JsonArray:
                return "[]";
            case JsonValue value:
                if (value.TryGetValue<string>(out var text))
                {
                    return IsPlainSafe(text) ? text : Quote(text);
                }

                if (value.TryGetValue<bool>(out var flag))
                {
                    return flag ? "true" : "false";
                }

                return value.ToJsonString();
            default:
                return Quote(node.ToJsonString());
        }
    }

    private static bool IsPlainSafe(string text)
    {
        if (text.Length == 0 || text != text.Trim() || text.Contains('\n') || text.Contains('\r') || text.Contains('\t'))
        {
            return false;
        }

        if (text.StartsWith('-') || text.StartsWith('?') || text.StartsWith(':'))
        {
            return text.Length > 1 && text[1] != ' ' && !text.Contains(": ") && !text.Contains(" #");
        }

        if ("[]{}#&*!|>'\"%@`,".Contains(text[0]))
        {
            return false;
        }

        if (text.Contains(": ") || text.EndsWith(':') || text.Contains(" #"))
        {
            return false;
        }

        // text that would read back as another type must be quoted
        return ResolveScalar(text) is JsonValue v && v.TryGetValue<string>(out _);
    }

    private static string Quote(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\x{(int)ch:x2}",
                _ => ch.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }

    /// <summary>Types a plain scalar the way the YAML core schema does (PyYAML's resolver, minus sexagesimals and timestamps).</summary>
    internal static JsonNode? ResolveScalar(string text)
    {
        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE" or "yes" or "Yes" or "YES" or "on" or "On" or "ON":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE" or "no" or "No" or "NO" or "off" or "Off" or "OFF":
                return JsonValue.Create(false);
        }

        if (IntRegex().IsMatch(text))
        {
            var digits = text.Replace("_", "");
            if (long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                return JsonValue.Create(value);
            }
        }

        if (HexRegex().IsMatch(text))
        {
            var negative = text.StartsWith('-');
            var hex = text.TrimStart('+', '-')[2..].Replace("_", "");
            if (long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                return JsonValue.Create(negative ? -value : value);
            }
        }

        if (FloatRegex().IsMatch(text))
        {
            var digits = text.Replace("_", "");
            if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return JsonValue.Create(value);
            }
        }

        return text switch
        {
            ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF" => JsonValue.Create(double.PositiveInfinity),
            "-.inf" or "-.Inf" or "-.INF" => JsonValue.Create(double.NegativeInfinity),
            ".nan" or ".NaN" or ".NAN" => JsonValue.Create(double.NaN),
            _ => JsonValue.Create(text),
        };
    }

    [GeneratedRegex(@"^[-+]?(0|[1-9][0-9_]*)$")]
    private static partial Regex IntRegex();

    [GeneratedRegex(@"^[-+]?0x[0-9a-fA-F_]+$")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9][0-9_]*(\.[0-9_]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatRegex();

    private sealed record Line(int Number, int Indent, string Text)
    {
        /// <summary>The line without indentation and trailing comment/whitespace (null when it is blank or a comment).</summary>
        public string? Content
        {
            get
            {
                var body = StripComment(Text[Indent..]).TrimEnd();
                return body.Length == 0 ? null : body;
            }
        }
    }

    private static string StripComment(string text)
    {
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote is null)
            {
                if ((ch == '\'' || ch == '"') && (i == 0 || text[i - 1] == ' ' || text[i - 1] == ':' || text[i - 1] == '-' || text[i - 1] == '[' || text[i - 1] == ',' || text[i - 1] == '{'))
                {
                    quote = ch;
                }
                else if (ch == '#' && (i == 0 || text[i - 1] == ' '))
                {
                    return text[..i];
                }
            }
            else if (ch == quote)
            {
                if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                }
                else
                {
                    quote = null;
                }
            }
            else if (quote == '"' && ch == '\\')
            {
                i++;
            }
        }

        return text;
    }

    private sealed class Parser(List<Line> lines)
    {
        private int _index;

        public void ExpectEnd()
        {
            SkipBlank();
            if (_index < lines.Count)
            {
                throw new SkillYamlException($"unexpected content at line {lines[_index].Number}: '{lines[_index].Content}'");
            }
        }

        private void SkipBlank()
        {
            while (_index < lines.Count && lines[_index].Content is null)
            {
                _index++;
            }
        }

        private Line? Peek()
        {
            SkipBlank();
            return _index < lines.Count ? lines[_index] : null;
        }

        public JsonNode? ParseNode(int minIndent, bool allowSequenceAtIndent)
        {
            var line = Peek();
            if (line is null || line.Indent < minIndent)
            {
                return null;
            }

            var content = line.Content!;
            if (content == "-" || content.StartsWith("- "))
            {
                return ParseSequence(line.Indent);
            }

            if (TryMappingKey(content, out _, out _))
            {
                return ParseMapping(line.Indent);
            }

            _index++;
            return ParseInlineValue(content, line, line.Indent);
        }

        private JsonArray ParseSequence(int indent)
        {
            var array = new JsonArray();
            while (Peek() is { } line && line.Indent == indent)
            {
                var content = line.Content!;
                if (content == "-")
                {
                    _index++;
                    array.Add(ParseNode(indent + 1, allowSequenceAtIndent: true));
                    continue;
                }

                if (!content.StartsWith("- "))
                {
                    break;
                }

                var rest = content[2..].TrimStart();
                var restIndent = indent + 2 + (content.Length - 2 - rest.Length);
                if (TryMappingKey(rest, out _, out _))
                {
                    // "- key: value" starts a mapping whose keys align with the first key
                    lines[_index] = line with { Indent = restIndent, Text = new string(' ', restIndent) + rest };
                    array.Add(ParseMapping(restIndent));
                    continue;
                }

                _index++;
                array.Add(ParseInlineValue(rest, line, indent + 1));
            }

            if (Peek() is { } stray && stray.Indent > indent)
            {
                throw new SkillYamlException($"bad indentation of a sequence entry at line {stray.Number}");
            }

            return array;
        }

        private JsonObject ParseMapping(int indent)
        {
            var obj = new JsonObject();
            while (Peek() is { } line && line.Indent == indent)
            {
                var content = line.Content!;
                if (!TryMappingKey(content, out var key, out var rest))
                {
                    throw new SkillYamlException($"could not find expected ':' at line {line.Number}");
                }

                _index++;
                JsonNode? value;
                if (rest.Length == 0)
                {
                    var next = Peek();
                    if (next is null || next.Indent < indent || (next.Indent == indent && !(next.Content is "-" || next.Content!.StartsWith("- "))))
                    {
                        value = null;
                    }
                    else
                    {
                        value = ParseNode(next.Indent == indent ? indent : indent + 1, allowSequenceAtIndent: true);
                    }
                }
                else
                {
                    value = ParseInlineValue(rest, line, indent + 1);
                }

                obj[key] = value;
            }

            if (Peek() is { } stray && stray.Indent > indent)
            {
                throw new SkillYamlException($"bad indentation of a mapping entry at line {stray.Number}");
            }

            return obj;
        }

        private JsonNode? ParseInlineValue(string text, Line line, int blockIndent)
        {
            if (text.Length == 0)
            {
                return null;
            }

            if (text[0] is '|' or '>')
            {
                return ParseBlockScalar(text, line, blockIndent);
            }

            if (text[0] is '\'' or '"')
            {
                var (value, remainder) = ReadQuoted(text, line.Number);
                if (remainder.Trim().Length > 0)
                {
                    throw new SkillYamlException($"unexpected text after quoted scalar at line {line.Number}: '{remainder.Trim()}'");
                }

                return JsonValue.Create(value);
            }

            if (text[0] == '[')
            {
                return ParseFlowSequence(text, line.Number);
            }

            if (text[0] == '{')
            {
                return ParseFlowMapping(text, line.Number);
            }

            if (text[0] is '&' or '*' or '!' or '%' or '@' or '`')
            {
                throw new SkillYamlException($"unsupported YAML construct '{text[0]}' at line {line.Number}");
            }

            // a plain scalar may continue on more-indented lines (folded with spaces)
            var sb = new StringBuilder(text);
            while (Peek() is { } next && next.Indent >= blockIndent && next.Content is { } more
                && !TryMappingKey(more, out _, out _) && !(more == "-" || more.StartsWith("- ")))
            {
                _index++;
                sb.Append(' ').Append(more.Trim());
            }

            return ResolveScalar(sb.ToString());
        }

        private JsonNode ParseBlockScalar(string header, Line line, int blockIndent)
        {
            var literal = header[0] == '|';
            var chomp = ' ';
            var explicitIndent = 0;
            foreach (var ch in header[1..].Trim())
            {
                if (ch is '-' or '+')
                {
                    chomp = ch;
                }
                else if (char.IsAsciiDigit(ch))
                {
                    explicitIndent = ch - '0';
                }
                else
                {
                    throw new SkillYamlException($"invalid block scalar header '{header}' at line {line.Number}");
                }
            }

            var body = new List<string>();
            var contentIndent = -1;
            while (_index < lines.Count)
            {
                var next = lines[_index];
                var isBlank = next.Text.Trim().Length == 0;
                if (contentIndent < 0 && !isBlank)
                {
                    contentIndent = explicitIndent > 0 ? blockIndent - 1 + explicitIndent : next.Indent;
                    if (contentIndent < blockIndent)
                    {
                        break;
                    }
                }

                if (!isBlank && next.Indent < contentIndent)
                {
                    break;
                }

                body.Add(isBlank ? "" : next.Text[contentIndent..]);
                _index++;
            }

            var trailing = 0;
            while (body.Count > 0 && body[^1].Length == 0)
            {
                body.RemoveAt(body.Count - 1);
                trailing++;
            }

            string text;
            if (literal)
            {
                text = string.Join("\n", body);
            }
            else
            {
                var sb = new StringBuilder();
                for (var i = 0; i < body.Count; i++)
                {
                    var current = body[i];
                    if (i > 0)
                    {
                        var previous = body[i - 1];
                        if (current.Length == 0 || previous.Length == 0 || current.StartsWith(' ') || previous.StartsWith(' '))
                        {
                            sb.Append('\n');
                        }
                        else
                        {
                            sb.Append(' ');
                        }
                    }

                    sb.Append(current);
                }

                text = sb.ToString();
            }

            if (body.Count > 0)
            {
                text += chomp switch
                {
                    '-' => "",
                    '+' => new string('\n', trailing + 1),
                    _ => "\n",
                };
            }

            return JsonValue.Create(text);
        }

        private JsonNode ParseFlowSequence(string text, int lineNumber)
        {
            if (!text.EndsWith(']'))
            {
                throw new SkillYamlException($"flow sequence must close on the same line (line {lineNumber})");
            }

            var array = new JsonArray();
            foreach (var item in SplitFlow(text[1..^1], lineNumber))
            {
                array.Add(FlowScalar(item, lineNumber));
            }

            return array;
        }

        private JsonNode ParseFlowMapping(string text, int lineNumber)
        {
            if (!text.EndsWith('}'))
            {
                throw new SkillYamlException($"flow mapping must close on the same line (line {lineNumber})");
            }

            var obj = new JsonObject();
            foreach (var item in SplitFlow(text[1..^1], lineNumber))
            {
                if (!TryMappingKey(item, out var key, out var rest))
                {
                    throw new SkillYamlException($"could not find expected ':' in flow mapping at line {lineNumber}");
                }

                obj[key] = FlowScalar(rest, lineNumber);
            }

            return obj;
        }

        private static JsonNode? FlowScalar(string item, int lineNumber)
        {
            if (item.Length == 0)
            {
                return null;
            }

            if (item[0] is '\'' or '"')
            {
                var (value, remainder) = ReadQuoted(item, lineNumber);
                if (remainder.Trim().Length > 0)
                {
                    throw new SkillYamlException($"unexpected text after quoted scalar at line {lineNumber}");
                }

                return JsonValue.Create(value);
            }

            if (item[0] is '[' or '{')
            {
                throw new SkillYamlException($"nested flow collections are not supported (line {lineNumber})");
            }

            return ResolveScalar(item);
        }

        private static List<string> SplitFlow(string inner, int lineNumber)
        {
            var items = new List<string>();
            var sb = new StringBuilder();
            char? quote = null;
            foreach (var ch in inner)
            {
                if (quote is null && ch == ',')
                {
                    items.Add(sb.ToString().Trim());
                    sb.Clear();
                    continue;
                }

                if (quote is null && ch is '\'' or '"')
                {
                    quote = ch;
                }
                else if (quote == ch)
                {
                    quote = null;
                }

                sb.Append(ch);
            }

            if (quote is not null)
            {
                throw new SkillYamlException($"unterminated quoted scalar at line {lineNumber}");
            }

            var last = sb.ToString().Trim();
            if (last.Length > 0 || items.Count > 0)
            {
                items.Add(last);
            }

            return items;
        }

        private static (string Value, string Remainder) ReadQuoted(string text, int lineNumber)
        {
            var quote = text[0];
            var sb = new StringBuilder();
            for (var i = 1; i < text.Length; i++)
            {
                var ch = text[i];
                if (quote == '\'')
                {
                    if (ch == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            sb.Append('\'');
                            i++;
                            continue;
                        }

                        return (sb.ToString(), text[(i + 1)..]);
                    }

                    sb.Append(ch);
                    continue;
                }

                if (ch == '"')
                {
                    return (sb.ToString(), text[(i + 1)..]);
                }

                if (ch == '\\' && i + 1 < text.Length)
                {
                    i++;
                    var escaped = text[i];
                    switch (escaped)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '0': sb.Append('\0'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case ' ': sb.Append(' '); break;
                        case 'x' when i + 2 < text.Length:
                            sb.Append((char)int.Parse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 2;
                            break;
                        case 'u' when i + 4 < text.Length:
                            sb.Append((char)int.Parse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default:
                            throw new SkillYamlException($"unknown escape character '\\{escaped}' at line {lineNumber}");
                    }

                    continue;
                }

                sb.Append(ch);
            }

            throw new SkillYamlException($"unterminated quoted scalar at line {lineNumber}");
        }

        private static bool TryMappingKey(string content, out string key, out string rest)
        {
            key = "";
            rest = "";
            if (content.Length == 0)
            {
                return false;
            }

            if (content[0] is '\'' or '"')
            {
                string quoted;
                string remainder;
                try
                {
                    (quoted, remainder) = ReadQuoted(content, 0);
                }
                catch (SkillYamlException)
                {
                    return false;
                }

                var trimmed = remainder.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] != ':' || (trimmed.Length > 1 && trimmed[1] != ' '))
                {
                    return false;
                }

                key = quoted;
                rest = trimmed.Length > 1 ? trimmed[1..].Trim() : "";
                return true;
            }

            if (content[0] is '[' or '{' or '|' or '>' or '&' or '*' or '!')
            {
                return false;
            }

            for (var i = 0; i < content.Length; i++)
            {
                if (content[i] != ':')
                {
                    continue;
                }

                if (i + 1 == content.Length || content[i + 1] == ' ')
                {
                    key = content[..i].TrimEnd();
                    if (key.Length == 0)
                    {
                        return false;
                    }

                    rest = content[(i + 1)..].Trim();
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>A node the way Python's <c>repr()</c> prints the value <c>yaml.safe_load</c> produced (for jsonschema's messages).</summary>
    internal static string PyRepr(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "None";
            case JsonObject obj:
                return "{" + string.Join(", ", obj.Select(kv => $"{PyReprString(kv.Key)}: {PyRepr(kv.Value)}")) + "}";
            case JsonArray array:
                return "[" + string.Join(", ", array.Select(PyRepr)) + "]";
            case JsonValue value:
                if (value.TryGetValue<string>(out var text))
                {
                    return PyReprString(text);
                }

                if (value.TryGetValue<bool>(out var flag))
                {
                    return flag ? "True" : "False";
                }

                if (value.TryGetValue<long>(out var integer))
                {
                    return integer.ToString(CultureInfo.InvariantCulture);
                }

                if (value.TryGetValue<double>(out var number))
                {
                    return double.IsPositiveInfinity(number) ? "inf" : double.IsNegativeInfinity(number) ? "-inf" : double.IsNaN(number) ? "nan"
                        : number == Math.Floor(number) && Math.Abs(number) < 1e16 ? number.ToString("0.0", CultureInfo.InvariantCulture)
                        : number.ToString("R", CultureInfo.InvariantCulture);
                }

                return value.ToJsonString();
            default:
                return node.ToJsonString();
        }
    }

    internal static string PyReprString(string text)
    {
        var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when ch == quote => "\\" + quote,
                < ' ' or '\x7f' => $"\\x{(int)ch:x2}",
                _ => ch.ToString(),
            });
        }

        return sb.Append(quote).ToString();
    }
}
