using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Provider.Tools;

/// <summary>Port of <c>parse_tool_call</c> and <c>tool_parse_error_message</c> in <c>src/inspect_ai/model/_call_tools.py</c>.</summary>
public static class ToolCallParsing
{
    /// <summary>Port of <c>MAX_TOOL_CALL_ARGUMENTS_DEPTH</c>.</summary>
    public const int MaxToolCallArgumentsDepth = 100;

    /// <summary>
    /// Depth at which the JSON reader gives up, standing in for Python's recursion limit: deeper
    /// documents report the same "nesting depth" parse error a <c>RecursionError</c> produces.
    /// </summary>
    internal const int ParserMaxDepth = 1024;

    private const int MaxShownArgumentBytes = 16 * 1024;

    /// <summary>UTF-8 decoding with <c>errors="ignore"</c>: bytes of a sequence split at a cut point are dropped.</summary>
    private static readonly Encoding Utf8IgnoringInvalid =
        Encoding.GetEncoding("utf-8", EncoderFallback.ReplacementFallback, new DecoderReplacementFallback(string.Empty));

    /// <summary>
    /// Parses a provider tool call into a <see cref="ToolCall"/>. Arguments starting with <c>{</c> are
    /// parsed as JSON with <c>json.loads</c> semantics (duplicate keys: last wins; recovering an object
    /// trailed only by stray double quotes; bounding nesting at <see cref="MaxToolCallArgumentsDepth"/>;
    /// recording <see cref="ToolCall.ParseError"/> on failure — including for the non-standard
    /// <c>NaN</c>/<c>Infinity</c> tokens Python would accept). Anything else yields empty arguments: the
    /// Python YAML fallback for non-JSON arguments only matters for prompt-emulated tool calls, which this
    /// lite port does not do — native function calling always returns a JSON object.
    /// </summary>
    public static ToolCall ParseToolCall(string id, string function, string? arguments, string type = "function")
    {
        string? error = null;
        var argumentsDict = new JsonObject();

        void ReportParseError(Exception ex)
        {
            error = ToolParseErrorMessage(arguments, ex);
            ProviderLogger.Info(error);
        }

        arguments = (arguments ?? "").Trim();
        if (arguments.StartsWith('{'))
        {
            JsonObject? parsed = null;
            try
            {
                parsed = ParseObject(arguments);
            }
            catch (JsonException ex) when (IsDepthExceeded(ex))
            {
                ReportParseError(MaxDepthParseError());
            }
            catch (JsonException ex)
            {
                parsed = ObjectWithTrailingQuotes(arguments);
                if (parsed is not null)
                {
                    var truncated = TruncateStringToBytes(arguments, 256);
                    var shown = truncated?.Output ?? arguments;
                    ProviderLogger.Info(
                        $"Recovered arguments for tool call '{function}' from a complete JSON object trailed by stray quote characters: {shown}");
                }
                else
                {
                    ReportParseError(ex);
                }
            }

            if (parsed is not null)
            {
                if (ExceedsMaxDepth(parsed))
                {
                    ReportParseError(MaxDepthParseError());
                }
                else
                {
                    argumentsDict = parsed;
                }
            }
        }

        return new ToolCall(id, function, argumentsDict) { ParseError = error, Type = type };
    }

    /// <summary>Port of <c>tool_parse_error_message</c> (arguments middle-truncated at 16 KiB).</summary>
    public static string ToolParseErrorMessage(string? arguments, Exception ex)
    {
        var truncated = TruncateStringToBytes(arguments ?? "", MaxShownArgumentBytes);
        var shown = truncated is not null
            ? $"{truncated.Output}\n\n(arguments middle-truncated from {truncated.OriginalBytes} bytes)"
            : arguments ?? "";
        return $"Error parsing the following tool call arguments:\n\n{shown}\n\nError details: {ex.Message}";
    }

    /// <summary>Port of <c>_max_depth_parse_error</c>.</summary>
    public static ArgumentException MaxDepthParseError() =>
        new($"arguments exceed the maximum supported nesting depth of {MaxToolCallArgumentsDepth}");

    internal static bool IsDepthExceeded(JsonException ex) =>
        ex.Message.Contains("maximum configured depth", StringComparison.OrdinalIgnoreCase);

    private static JsonObject ParseObject(string json)
    {
        var node = PythonJson.Loads(json, ParserMaxDepth);
        return node as JsonObject ?? throw new JsonException("The provided arguments are not a JSON object.");
    }

    /// <summary>Port of <c>_object_with_trailing_quotes</c>.</summary>
    internal static JsonObject? ObjectWithTrailingQuotes(string arguments)
    {
        var bytes = Encoding.UTF8.GetBytes(arguments);
        long consumed;
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = ParserMaxDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject || !reader.TrySkip())
            {
                return null;
            }

            consumed = reader.BytesConsumed;
        }
        catch (JsonException)
        {
            return null;
        }

        var remainder = Encoding.UTF8.GetString(bytes, (int)consumed, bytes.Length - (int)consumed);
        if (remainder.Any(ch => ch != '"' && !PythonSemantics.Whitespace.Contains(ch)))
        {
            return null;
        }

        try
        {
            return PythonJson.Loads(bytes.AsMemory(0, (int)consumed), ParserMaxDepth) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Port of <c>exceeds_max_depth</c> (<c>src/inspect_ai/_util/json.py</c>): iterative walk with an explicit stack.</summary>
    public static bool ExceedsMaxDepth(JsonNode? value, int maxDepth = MaxToolCallArgumentsDepth)
    {
        var stack = new Stack<(JsonNode Node, int Depth)>();
        if (value is not null)
        {
            stack.Push((value, 1));
        }

        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            IEnumerable<JsonNode?> children;
            switch (current)
            {
                case JsonObject obj:
                    children = obj.Select(kv => kv.Value);
                    break;
                case JsonArray array:
                    children = array;
                    break;
                default:
                    continue;
            }

            if (depth > maxDepth)
            {
                return true;
            }

            foreach (var child in children)
            {
                if (child is not null)
                {
                    stack.Push((child, depth + 1));
                }
            }
        }

        return false;
    }

    /// <summary>Result of <see cref="TruncateStringToBytes"/> (port of <c>TruncatedOutput</c>).</summary>
    public sealed record TruncatedOutput(string Output, int OriginalBytes);

    /// <summary>Port of <c>truncate_string_to_bytes</c>: middle truncation, half from the front and half from the back.</summary>
    public static TruncatedOutput? TruncateStringToBytes(string input, int maxBytes)
    {
        if (string.IsNullOrEmpty(input) || maxBytes <= 0)
        {
            return null;
        }

        if (Ascii.IsValid(input))
        {
            if (input.Length <= maxBytes)
            {
                return null;
            }

            var half = maxBytes / 2;
            return new TruncatedOutput(input[..half] + input[^(maxBytes - half)..], input.Length);
        }

        if (input.Length * 4 <= maxBytes)
        {
            return null;
        }

        var encoded = Encoding.UTF8.GetBytes(input);
        if (encoded.Length <= maxBytes)
        {
            return null;
        }

        var halfBytes = maxBytes / 2;
        var start = Utf8IgnoringInvalid.GetString(encoded, 0, halfBytes);
        var end = Utf8IgnoringInvalid.GetString(encoded, encoded.Length - (maxBytes - halfBytes), maxBytes - halfBytes);
        return new TruncatedOutput(start + end, encoded.Length);
    }
}
