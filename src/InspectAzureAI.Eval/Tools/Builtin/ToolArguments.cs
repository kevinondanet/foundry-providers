using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Tools.Builtin;

/// <summary>
/// Port of the argument coercion of <c>tool_params</c> (<c>model/_call_tools.py</c>) for the built-in tools:
/// reads a schema-validated argument by name, applying Python's defaults (an absent parameter takes its
/// default, an optional one becomes null) and conversions (<c>int(2.0)</c> is 2). A value of the wrong JSON
/// type is a <see cref="ToolParsingError"/>, which only arises when a caller skipped
/// <see cref="ToolInputValidator"/>.
/// </summary>
internal static class ToolArguments
{
    public static string String(JsonObject arguments, string name) =>
        arguments.TryGetPropertyValue(name, out var node) ? AsString(node, name) : throw Missing(name);

    public static string String(JsonObject arguments, string name, string @default) =>
        arguments.TryGetPropertyValue(name, out var node) ? AsString(node, name) : @default;

    public static string? OptionalString(JsonObject arguments, string name) =>
        arguments.TryGetPropertyValue(name, out var node) && node is not null ? AsString(node, name) : null;

    public static int Int(JsonObject arguments, string name, int @default) =>
        arguments.TryGetPropertyValue(name, out var node) ? AsInt(node, name) : @default;

    public static int? OptionalInt(JsonObject arguments, string name) =>
        arguments.TryGetPropertyValue(name, out var node) && node is not null ? AsInt(node, name) : null;

    public static bool Bool(JsonObject arguments, string name, bool @default) =>
        arguments.TryGetPropertyValue(name, out var node) ? AsBool(node, name) : @default;

    public static JsonArray Array(JsonObject arguments, string name) =>
        arguments.TryGetPropertyValue(name, out var node)
            ? node as JsonArray ?? throw Unconvertible(node, name, "list")
            : throw Missing(name);

    private static string AsString(JsonNode? node, string name) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : throw Unconvertible(node, name, "str");

    private static int AsInt(JsonNode? node, string name)
    {
        if (node is JsonValue value && ToolInputValidator.TryGetInt64(value, out var i) && i is >= int.MinValue and <= int.MaxValue)
        {
            return (int)i;
        }

        throw Unconvertible(node, name, "int");
    }

    private static bool AsBool(JsonNode? node, string name) =>
        node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : throw Unconvertible(node, name, "bool");

    private static ToolParsingError Missing(string name) => new($"Required parameter {name} not provided to tool call.");

    private static ToolParsingError Unconvertible(JsonNode? node, string name, string type) =>
        new($"Unable to convert '{ToolInputValidator.Repr(node)}' to {type} for parameter {name}");
}
