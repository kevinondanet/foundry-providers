using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Tools.Support;

/// <summary>
/// Typed readers over a tool call's parsed JSON arguments. Python validates arguments against the
/// function signature before the tool body runs; here each tool reads what it needs and a value of the
/// wrong JSON type is a <see cref="ToolParsingError"/> (reported to the model), never a crash.
/// </summary>
internal static class ToolArguments
{
    public static string RequiredString(JsonObject arguments, string name) =>
        OptionalString(arguments, name) ?? throw new ToolParsingError($"Required parameter {name} not provided to tool call.");

    public static string? OptionalString(JsonObject arguments, string name)
    {
        var node = arguments[name];
        return node switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => throw new ToolParsingError($"Parameter '{name}' must be a string, got {JsonRpc.PythonTypeName(node)}."),
        };
    }

    /// <summary>A string restricted to <paramref name="choices"/> (a <c>Literal[...]</c> parameter).</summary>
    public static string RequiredChoice(JsonObject arguments, string name, IReadOnlyList<string> choices)
    {
        var value = RequiredString(arguments, name);
        if (!choices.Contains(value, StringComparer.Ordinal))
        {
            throw new ToolParsingError($"Parameter '{name}' must be one of {string.Join(", ", choices.Select(c => $"'{c}'"))}, got '{value}'.");
        }

        return value;
    }

    public static long? OptionalInteger(JsonObject arguments, string name)
    {
        var node = arguments[name];
        return node switch
        {
            null => null,
            JsonValue value when TryGetInteger(value, out var integer) => integer,
            _ => throw new ToolParsingError($"Parameter '{name}' must be an integer, got {JsonRpc.PythonTypeName(node)}."),
        };
    }

    public static IReadOnlyList<long>? OptionalIntegerList(JsonObject arguments, string name)
    {
        var node = arguments[name];
        switch (node)
        {
            case null:
                return null;
            case JsonArray array:
                var items = new List<long>(array.Count);
                foreach (var item in array)
                {
                    if (item is JsonValue value && TryGetInteger(value, out var integer))
                    {
                        items.Add(integer);
                    }
                    else
                    {
                        throw new ToolParsingError($"Parameter '{name}' must be a list of integers, got an item of {JsonRpc.PythonTypeName(item)}.");
                    }
                }

                return items;
            default:
                throw new ToolParsingError($"Parameter '{name}' must be a list of integers, got {JsonRpc.PythonTypeName(node)}.");
        }
    }

    private static bool TryGetInteger(JsonValue value, out long integer)
    {
        integer = 0;
        if (value.TryGetValue<bool>(out _))
        {
            return false;
        }

        if (value.TryGetValue<long>(out integer))
        {
            return true;
        }

        if (value.TryGetValue<int>(out var small))
        {
            integer = small;
            return true;
        }

        return false;
    }
}
