using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InspectAzureAI.Eval.Sandbox.Docker.Compose;

/// <summary>
/// The subset of YAML 1.1 that compose files use, standing in for the <c>yaml.safe_load</c> calls of
/// <c>util/_sandbox/docker/{compose,config,util}.py</c>: block and flow mappings and sequences, plain,
/// single- and double-quoted scalars, literal (<c>|</c>) and folded (<c>&gt;</c>) block scalars, comments,
/// and PyYAML's implicit typing (null, the YAML 1.1 booleans <c>yes/no/on/off</c>, integers, floats).
/// Mappings come back as insertion-ordered <see cref="OrderedDictionary{TKey, TValue}"/> instances,
/// sequences as <see cref="List{T}"/>, scalars as string / bool / long / double / null. Anchors, aliases,
/// tags and multi-document streams are refused with a <see cref="NotSupportedException"/> rather than
/// misread; malformed structure is a <see cref="FormatException"/> naming the line.
/// </summary>
internal static partial class MiniYaml
{
    /// <summary>Parses one YAML document.</summary>
    public static object? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(Tokenize(text));
        var value = parser.ParseDocument();
        return value;
    }

    /// <summary>Parses a document whose root must be a mapping (the compose file shape).</summary>
    public static OrderedDictionary<string, object?> ParseMapping(string text) =>
        Parse(text) as OrderedDictionary<string, object?>
        ?? throw new FormatException("Expected a YAML mapping at the top level.");

    private sealed record RawLine(int Number, int Indent, string Raw, string Content)
    {
        public bool Blank => Content.Length == 0;
    }

    private static List<RawLine> Tokenize(string text)
    {
        var lines = new List<RawLine>();
        var number = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            number++;
            var indent = 0;
            while (indent < raw.Length && raw[indent] == ' ')
            {
                indent++;
            }

            if (indent < raw.Length && raw[indent] == '\t')
            {
                throw new FormatException($"YAML line {number}: tabs are not allowed for indentation.");
            }

            // Content is the line without its indentation (the parser keys on `- ` and `key:` at column 0 of it)
            lines.Add(new RawLine(number, indent, raw, StripComment(raw).Trim()));
        }

        return lines;
    }

    /// <summary>Removes a trailing <c># comment</c> (one at the start of the line or preceded by whitespace, outside quotes).</summary>
    private static string StripComment(string raw)
    {
        var quote = '\0';
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                // a quote opens a scalar only at the start of a token
                if (i == 0 || char.IsWhiteSpace(raw[i - 1]) || raw[i - 1] is '[' or '{' or ',' or ':' or '-')
                {
                    quote = c;
                }
            }
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(raw[i - 1])))
            {
                return raw[..i];
            }
        }

        return raw;
    }

    private static bool IsSequenceItem(string content) => content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

    [GeneratedRegex(@"^[|>](?:[+-]?\d?|\d?[+-]?)$")]
    private static partial Regex BlockScalarHeader();

    private sealed class Parser(List<RawLine> lines)
    {
        private int _pos;

        public object? ParseDocument()
        {
            var first = Peek();
            if (first is not null && first.Content == "---")
            {
                _pos++;
                first = Peek();
            }

            if (first is null || first.Content == "...")
            {
                return null;
            }

            var value = ParseNode(first.Indent);
            var trailing = Peek();
            if (trailing is not null)
            {
                if (trailing.Content is "---" or "...")
                {
                    if (Peek(skipMarkers: true) is not null)
                    {
                        throw new NotSupportedException($"YAML line {trailing.Number}: multi-document streams are not supported.");
                    }

                    return value;
                }

                throw new FormatException($"YAML line {trailing.Number}: unexpected content '{trailing.Content}' after the document.");
            }

            return value;
        }

        /// <summary>The next structural line, skipping blank and comment-only lines.</summary>
        private RawLine? Peek(bool skipMarkers = false)
        {
            while (_pos < lines.Count)
            {
                var line = lines[_pos];
                if (line.Blank || (skipMarkers && line.Content is "---" or "..."))
                {
                    _pos++;
                    continue;
                }

                return line;
            }

            return null;
        }

        private object? ParseNode(int indent)
        {
            var line = Peek();
            if (line is null || line.Indent < indent)
            {
                return null;
            }

            if (IsSequenceItem(line.Content))
            {
                return ParseSequence(line.Indent);
            }

            if (TryMappingKey(line, out _, out _))
            {
                return ParseMapping(line.Indent);
            }

            _pos++;
            return ParseInlineValue(line.Content, line);
        }

        private OrderedDictionary<string, object?> ParseMapping(int indent)
        {
            var map = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                var line = Peek();
                if (line is null || line.Indent < indent || line.Content is "---" or "...")
                {
                    break;
                }

                if (line.Indent > indent)
                {
                    throw new FormatException($"YAML line {line.Number}: unexpected indentation.");
                }

                if (IsSequenceItem(line.Content))
                {
                    throw new FormatException($"YAML line {line.Number}: a sequence item cannot appear directly inside a mapping.");
                }

                if (!TryMappingKey(line, out var key, out var rest))
                {
                    throw new FormatException($"YAML line {line.Number}: expected a 'key: value' entry, got '{line.Content}'.");
                }

                _pos++;
                object? value;
                if (rest.Length == 0)
                {
                    var next = Peek();
                    if (next is not null && next.Indent > indent)
                    {
                        value = ParseNode(next.Indent);
                    }
                    else if (next is not null && next.Indent == indent && IsSequenceItem(next.Content))
                    {
                        // `key:` followed by a sequence at the same indentation is valid YAML
                        value = ParseSequence(indent);
                    }
                    else
                    {
                        value = null;
                    }
                }
                else if (BlockScalarHeader().IsMatch(rest))
                {
                    value = ParseBlockScalar(rest, indent, line.Number);
                }
                else
                {
                    value = ParseInlineValue(rest, line);
                }

                map[key] = value;
            }

            return map;
        }

        private List<object?> ParseSequence(int indent)
        {
            var list = new List<object?>();
            while (true)
            {
                var line = Peek();
                if (line is null || line.Indent < indent || !IsSequenceItem(line.Content))
                {
                    break;
                }

                if (line.Indent > indent)
                {
                    throw new FormatException($"YAML line {line.Number}: unexpected indentation.");
                }

                _pos++;
                var rest = line.Content.Length == 1 ? "" : line.Content[1..].TrimStart();
                object? value;
                if (rest.Length == 0)
                {
                    var next = Peek();
                    value = next is not null && next.Indent > indent ? ParseNode(next.Indent) : null;
                }
                else if (BlockScalarHeader().IsMatch(rest))
                {
                    value = ParseBlockScalar(rest, indent, line.Number);
                }
                else if (IsSequenceItem(rest) || TryMappingKey(rest, line.Number, out _, out _))
                {
                    // compact nested collection (`- key: value` / `- - item`): re-read the remainder as its
                    // own line at the column where it starts, so following entries align with it
                    var column = line.Indent + (line.Content.Length - rest.Length);
                    lines.Insert(_pos, new RawLine(line.Number, column, new string(' ', column) + rest, rest));
                    value = ParseNode(column);
                }
                else
                {
                    value = ParseInlineValue(rest, line);
                }

                list.Add(value);
            }

            return list;
        }

        private string ParseBlockScalar(string header, int parentIndent, int headerLine)
        {
            var literal = header[0] == '|';
            var chomp = header.Contains('-') ? '-' : header.Contains('+') ? '+' : ' ';
            var explicitIndent = header.Skip(1).FirstOrDefault(char.IsDigit);
            var contentIndent = explicitIndent == default ? -1 : parentIndent + (explicitIndent - '0');

            var collected = new List<string>();
            while (_pos < lines.Count)
            {
                var line = lines[_pos];
                var blankRaw = line.Raw.Trim().Length == 0;
                if (!blankRaw && line.Indent <= parentIndent)
                {
                    break;
                }

                if (contentIndent < 0 && !blankRaw)
                {
                    contentIndent = line.Indent;
                }

                if (!blankRaw && line.Indent < contentIndent)
                {
                    throw new FormatException($"YAML line {line.Number}: block scalar line is less indented than its first line (block starts at line {headerLine}).");
                }

                collected.Add(blankRaw ? "" : line.Raw[contentIndent..].TrimEnd('\r'));
                _pos++;
            }

            // trailing blank lines belong to chomping, not the content
            var trailing = 0;
            while (collected.Count > 0 && collected[^1].Length == 0)
            {
                collected.RemoveAt(collected.Count - 1);
                trailing++;
            }

            string body;
            if (literal)
            {
                body = string.Join("\n", collected);
            }
            else
            {
                var sb = new StringBuilder();
                for (var i = 0; i < collected.Count; i++)
                {
                    var text = collected[i];
                    if (i > 0)
                    {
                        var previous = collected[i - 1];
                        if (text.Length == 0 || previous.Length == 0 || text[0] == ' ' || previous[0] == ' ')
                        {
                            sb.Append('\n');
                        }
                        else
                        {
                            sb.Append(' ');
                        }
                    }

                    sb.Append(text);
                }

                body = sb.ToString();
            }

            return chomp switch
            {
                '-' => body,
                '+' => body + "\n" + new string('\n', trailing),
                _ => collected.Count == 0 ? "" : body + "\n",
            };
        }

        private object? ParseInlineValue(string rest, RawLine line)
        {
            if (rest.Length == 0)
            {
                return null;
            }

            var first = rest[0];
            if (first is '[' or '{')
            {
                var text = rest;
                while (!FlowBalanced(text))
                {
                    if (_pos >= lines.Count)
                    {
                        throw new FormatException($"YAML line {line.Number}: unterminated flow collection.");
                    }

                    var continuation = lines[_pos++];
                    if (!continuation.Blank)
                    {
                        text += " " + continuation.Content.Trim();
                    }
                }

                var reader = new FlowReader(text, line.Number);
                var value = reader.ReadValue();
                reader.ExpectEnd();
                return value;
            }

            if (first is '"' or '\'')
            {
                var reader = new FlowReader(rest, line.Number);
                var value = reader.ReadQuoted();
                reader.ExpectEnd();
                return value;
            }

            if (first is '&' or '*' or '!')
            {
                throw new NotSupportedException($"YAML line {line.Number}: anchors, aliases and tags are not supported ('{rest}').");
            }

            if (first is '%' or '@' or '`')
            {
                throw new FormatException($"YAML line {line.Number}: a plain scalar cannot start with '{first}'.");
            }

            // a plain scalar may continue on more-indented lines that are not entries themselves
            var plain = rest;
            while (_pos < lines.Count)
            {
                var next = lines[_pos];
                if (next.Blank || next.Indent <= line.Indent || IsSequenceItem(next.Content) || TryMappingKey(next, out _, out _))
                {
                    break;
                }

                plain += " " + next.Content.Trim();
                _pos++;
            }

            return Scalar(plain);
        }

        private static bool FlowBalanced(string text)
        {
            var depth = 0;
            var quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quote != '\0')
                {
                    if (c == '\\' && quote == '"')
                    {
                        i++;
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                switch (c)
                {
                    case '"' or '\'':
                        quote = c;
                        break;
                    case '[' or '{':
                        depth++;
                        break;
                    case ']' or '}':
                        depth--;
                        break;
                }
            }

            return depth <= 0;
        }

        private static bool TryMappingKey(RawLine line, out string key, out string rest) =>
            TryMappingKey(line.Content, line.Number, out key, out rest);

        /// <summary>Splits <c>key: rest</c> (quoted or plain key; the colon must end the line or be followed by a space).</summary>
        private static bool TryMappingKey(string content, int number, out string key, out string rest)
        {
            key = "";
            rest = "";
            if (content.Length == 0 || content[0] is '[' or '{' || IsSequenceItem(content))
            {
                return false;
            }

            if (content[0] is '"' or '\'')
            {
                var reader = new FlowReader(content, number);
                var quoted = reader.ReadQuoted();
                var after = reader.Remaining();
                if (after.Length == 0 || after[0] != ':' || (after.Length > 1 && after[1] != ' '))
                {
                    return false;
                }

                key = quoted;
                rest = after.Length > 1 ? after[1..].Trim() : "";
                return true;
            }

            var index = content.IndexOf(": ", StringComparison.Ordinal);
            if (index < 0)
            {
                if (!content.EndsWith(':') || content.Length == 1)
                {
                    return false;
                }

                index = content.Length - 1;
            }

            var candidate = content[..index].TrimEnd();
            if (candidate.Length == 0 || candidate.Contains('#') || candidate[0] is '"' or '\'')
            {
                return false;
            }

            if (candidate == "<<")
            {
                throw new NotSupportedException($"YAML line {number}: merge keys ('<<') are not supported.");
            }

            key = candidate;
            rest = content[(index + 1)..].Trim();
            return true;
        }
    }

    /// <summary>Reads flow collections and quoted scalars from one line of text.</summary>
    private sealed class FlowReader(string text, int number)
    {
        private int _i;

        public object? ReadValue()
        {
            SkipSpaces();
            if (_i >= text.Length)
            {
                return null;
            }

            return text[_i] switch
            {
                '[' => ReadSequence(),
                '{' => ReadMapping(),
                '"' or '\'' => ReadQuoted(),
                '&' or '*' or '!' => throw new NotSupportedException($"YAML line {number}: anchors, aliases and tags are not supported."),
                _ => Scalar(ReadPlain(stopAtColon: false)),
            };
        }

        public string ReadQuoted()
        {
            var quote = text[_i++];
            var sb = new StringBuilder();
            while (true)
            {
                if (_i >= text.Length)
                {
                    throw new FormatException($"YAML line {number}: unterminated quoted string.");
                }

                var c = text[_i++];
                if (quote == '\'')
                {
                    if (c == '\'')
                    {
                        if (_i < text.Length && text[_i] == '\'')
                        {
                            sb.Append('\'');
                            _i++;
                            continue;
                        }

                        return sb.ToString();
                    }

                    sb.Append(c);
                    continue;
                }

                if (c == '"')
                {
                    return sb.ToString();
                }

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (_i >= text.Length)
                {
                    throw new FormatException($"YAML line {number}: unterminated escape sequence.");
                }

                var escaped = text[_i++];
                switch (escaped)
                {
                    case 'n':
                        sb.Append('\n');
                        break;
                    case 't':
                        sb.Append('\t');
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case '0':
                        sb.Append('\0');
                        break;
                    case 'x':
                        sb.Append((char)ReadHex(2));
                        break;
                    case 'u':
                        sb.Append((char)ReadHex(4));
                        break;
                    default:
                        sb.Append(escaped);
                        break;
                }
            }
        }

        public string Remaining() => text[_i..].Trim();

        public void ExpectEnd()
        {
            SkipSpaces();
            if (_i < text.Length)
            {
                throw new FormatException($"YAML line {number}: unexpected '{text[_i..]}' after a value.");
            }
        }

        private int ReadHex(int digits)
        {
            if (_i + digits > text.Length)
            {
                throw new FormatException($"YAML line {number}: truncated escape sequence.");
            }

            var value = int.Parse(text.AsSpan(_i, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            _i += digits;
            return value;
        }

        private List<object?> ReadSequence()
        {
            _i++;
            var list = new List<object?>();
            while (true)
            {
                SkipSpaces();
                if (_i >= text.Length)
                {
                    throw new FormatException($"YAML line {number}: unterminated flow sequence.");
                }

                if (text[_i] == ']')
                {
                    _i++;
                    return list;
                }

                list.Add(ReadValue());
                SkipSpaces();
                if (_i < text.Length && text[_i] == ',')
                {
                    _i++;
                }
                else if (_i >= text.Length || text[_i] != ']')
                {
                    throw new FormatException($"YAML line {number}: expected ',' or ']' in a flow sequence.");
                }
            }
        }

        private OrderedDictionary<string, object?> ReadMapping()
        {
            _i++;
            var map = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                SkipSpaces();
                if (_i >= text.Length)
                {
                    throw new FormatException($"YAML line {number}: unterminated flow mapping.");
                }

                if (text[_i] == '}')
                {
                    _i++;
                    return map;
                }

                var key = text[_i] is '"' or '\'' ? ReadQuoted() : ReadPlain(stopAtColon: true);
                SkipSpaces();
                object? value = null;
                if (_i < text.Length && text[_i] == ':')
                {
                    _i++;
                    value = ReadValue();
                }

                map[key] = value;
                SkipSpaces();
                if (_i < text.Length && text[_i] == ',')
                {
                    _i++;
                }
                else if (_i >= text.Length || text[_i] != '}')
                {
                    throw new FormatException($"YAML line {number}: expected ',' or '}}' in a flow mapping.");
                }
            }
        }

        private string ReadPlain(bool stopAtColon)
        {
            var start = _i;
            while (_i < text.Length)
            {
                var c = text[_i];
                if (c is ',' or ']' or '}')
                {
                    break;
                }

                if (c == ':' && (stopAtColon || _i + 1 >= text.Length || text[_i + 1] is ' ' or ',' or '}' or ']'))
                {
                    break;
                }

                _i++;
            }

            return text[start.._i].Trim();
        }

        private void SkipSpaces()
        {
            while (_i < text.Length && char.IsWhiteSpace(text[_i]))
            {
                _i++;
            }
        }
    }

    [GeneratedRegex(@"^[-+]?(0|[1-9][0-9_]*)$")]
    private static partial Regex DecimalInteger();

    [GeneratedRegex(@"^[-+]?0[0-7_]+$")]
    private static partial Regex OctalInteger();

    [GeneratedRegex(@"^[-+]?0x[0-9a-fA-F_]+$")]
    private static partial Regex HexInteger();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9][0-9_]*(\.[0-9_]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex Float();

    /// <summary>Port of PyYAML's implicit resolvers for a plain scalar (YAML 1.1: <c>yes</c>/<c>no</c>/<c>on</c>/<c>off</c> are booleans).</summary>
    internal static object? Scalar(string plain)
    {
        switch (plain)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE" or "yes" or "Yes" or "YES" or "on" or "On" or "ON":
                return true;
            case "false" or "False" or "FALSE" or "no" or "No" or "NO" or "off" or "Off" or "OFF":
                return false;
            case ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF":
                return double.PositiveInfinity;
            case "-.inf" or "-.Inf" or "-.INF":
                return double.NegativeInfinity;
            case ".nan" or ".NaN" or ".NAN":
                return double.NaN;
        }

        var digits = plain.Replace("_", "", StringComparison.Ordinal);
        if (DecimalInteger().IsMatch(plain) && long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        if (OctalInteger().IsMatch(plain))
        {
            var negative = digits[0] == '-';
            var body = digits.TrimStart('+', '-')[1..];
            try
            {
                var value = Convert.ToInt64(body, 8);
                return negative ? -value : value;
            }
            catch (OverflowException)
            {
                return plain;
            }
        }

        if (HexInteger().IsMatch(plain))
        {
            var negative = digits[0] == '-';
            var body = digits.TrimStart('+', '-')[2..];
            if (long.TryParse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                return negative ? -hex : hex;
            }
        }

        if (Float().IsMatch(plain) && (plain.Contains('.') || plain.Contains('e') || plain.Contains('E'))
            && double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
        {
            return real;
        }

        return plain;
    }
}
