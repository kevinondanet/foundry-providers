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
/// </summary>
public static class PythonJson
{
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
