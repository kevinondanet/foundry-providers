using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Provider.Util;

/// <summary>
/// Serialises a <see cref="JsonNode"/> the way Python's <c>json.dumps</c> does by default:
/// <c>", "</c> / <c>": "</c> separators, <c>ensure_ascii=True</c> (non-ASCII escaped as
/// <c>\uXXXX</c>), insertion key order, and the <c>indent=N</c> layout. The azureai provider relies on
/// these exact bytes for tool-call arguments (<c>chat_tool_call</c>), the Llama 3.1 prompt
/// (<c>json.dumps(..., indent=2)</c>) and the <c>&lt;tool_call&gt;</c> history rendering.
/// <see cref="Loads(string, int)"/> is the matching <c>json.loads</c> stand-in.
/// </summary>
public static class PythonJson
{
    /// <summary>
    /// Port of <c>json.loads</c> for the tool-call paths: parses with <see cref="JsonDocument"/> (which,
    /// like Python, keeps the <em>last</em> value of a duplicated object key) and materialises a
    /// <see cref="JsonNode"/> tree by assignment, because <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/>
    /// builds a dictionary-backed <see cref="JsonObject"/> that throws <see cref="ArgumentException"/> on
    /// the first access when a key repeats. Nesting is bounded by <paramref name="maxDepth"/> (a
    /// <see cref="JsonException"/> mentioning the configured depth stands in for <c>RecursionError</c>).
    /// Python's acceptance of the non-standard <c>NaN</c> / <c>Infinity</c> tokens is not reproduced.
    /// </summary>
    public static JsonNode? Loads(string json, int maxDepth = 64)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = maxDepth });
        return ToNode(document.RootElement);
    }

    /// <inheritdoc cref="Loads(string, int)"/>
    public static JsonNode? Loads(ReadOnlyMemory<byte> utf8Json, int maxDepth = 64)
    {
        using var document = JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = maxDepth });
        return ToNode(document.RootElement);
    }

    private static JsonNode? ToNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    obj[property.Name] = ToNode(property.Value);
                }

                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    array.Add(ToNode(item));
                }

                return array;
            case JsonValueKind.Null:
                return null;
            default:
                return JsonValue.Create(element.Clone());
        }
    }

    /// <summary>Port of <c>json.dumps(value, indent=indent)</c>.</summary>
    public static string Dumps(JsonNode? value, int? indent = null)
    {
        var sb = new StringBuilder();
        Write(sb, value, indent, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? value, int? indent, int level)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                WriteObject(sb, obj, indent, level);
                break;
            case JsonArray array:
                WriteArray(sb, array, indent, level);
                break;
            case JsonValue scalar:
                WriteScalar(sb, scalar);
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON node {value.GetType().Name}");
        }
    }

    private static void WriteObject(StringBuilder sb, JsonObject obj, int? indent, int level)
    {
        if (obj.Count == 0)
        {
            sb.Append("{}");
            return;
        }

        sb.Append('{');
        var first = true;
        foreach (var (key, item) in obj)
        {
            if (!first)
            {
                sb.Append(indent is null ? ", " : ",");
            }

            first = false;
            NewlineIndent(sb, indent, level + 1);
            WriteString(sb, key);
            sb.Append(": ");
            Write(sb, item, indent, level + 1);
        }

        NewlineIndent(sb, indent, level);
        sb.Append('}');
    }

    private static void WriteArray(StringBuilder sb, JsonArray array, int? indent, int level)
    {
        if (array.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        sb.Append('[');
        var first = true;
        foreach (var item in array)
        {
            if (!first)
            {
                sb.Append(indent is null ? ", " : ",");
            }

            first = false;
            NewlineIndent(sb, indent, level + 1);
            Write(sb, item, indent, level + 1);
        }

        NewlineIndent(sb, indent, level);
        sb.Append(']');
    }

    private static void NewlineIndent(StringBuilder sb, int? indent, int level)
    {
        if (indent is null)
        {
            return;
        }

        sb.Append('\n');
        sb.Append(' ', indent.Value * level);
    }

    private static void WriteScalar(StringBuilder sb, JsonValue scalar)
    {
        var element = scalar.GetValue<object>() switch
        {
            JsonElement e => e,
            _ => JsonSerializer.SerializeToElement(scalar),
        };
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                WriteString(sb, element.GetString()!);
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            case JsonValueKind.Number:
                sb.Append(FormatNumber(element, scalar));
                break;
            default:
                throw new InvalidOperationException($"Unsupported scalar kind {element.ValueKind}");
        }
    }

    private static string FormatNumber(JsonElement element, JsonValue scalar)
    {
        var raw = element.GetRawText();
        // A node created from a CLR double keeps Python's float repr (1.0 stays "1.0").
        if (scalar.TryGetValue<double>(out var d) && !raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E')
            && scalar.GetValue<object>() is double)
        {
            return d.ToString("0.0###############", CultureInfo.InvariantCulture);
        }

        return raw;
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
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
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (ch < 0x20 || ch > 0x7e)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
