using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Provider.Tools;

/// <summary>
/// Structured-output request fields built from a <see cref="ResponseSchema"/>: the chat-completions
/// <c>response_format</c> (port of the block in <c>src/inspect_ai/model/_openai.py</c>
/// <c>openai_completion_params</c>) and the Anthropic Messages <c>output_format</c> (port of the block in
/// <c>src/inspect_ai/model/_providers/anthropic.py</c> <c>completion_params</c>).
/// </summary>
public static class ResponseFormat
{
    /// <summary>The <c>anthropic-beta</c> value Python adds alongside <c>output_format</c>.</summary>
    public const string AnthropicStructuredOutputsBeta = "structured-outputs-2025-11-13";

    /// <summary>
    /// <c>{"type": "json_schema", "json_schema": {"name", "schema", "description", "strict"}}</c>. Python's dict
    /// carries <c>description</c> / <c>strict</c> as <c>None</c> when unset; they are omitted here (the gateway
    /// receives the same fields either way). <paramref name="exclude"/> strips schema keywords recursively
    /// (<see cref="JsonSchemaDump.Dump"/>).
    /// </summary>
    public static JsonObject JsonSchemaResponseFormat(ResponseSchema schema, IReadOnlySet<string>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var jsonSchema = new JsonObject
        {
            ["name"] = schema.Name,
            ["schema"] = JsonSchemaDump.Dump(schema.JsonSchema.ToJson(), exclude),
        };
        if (schema.Description is not null)
        {
            jsonSchema["description"] = schema.Description;
        }

        if (schema.Strict is not null)
        {
            jsonSchema["strict"] = schema.Strict;
        }

        return new JsonObject { ["type"] = "json_schema", ["json_schema"] = jsonSchema };
    }

    /// <summary>
    /// <c>{"type": "json_schema", "schema": ...}</c> with <c>additionalProperties: false</c> set recursively and the
    /// extended validation fields stripped, exactly as the Python Anthropic provider builds it.
    /// </summary>
    public static JsonObject AnthropicOutputFormat(ResponseSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var strict = JsonSchema.SetAdditionalPropertiesFalse(schema.JsonSchema);
        return new JsonObject
        {
            ["type"] = "json_schema",
            ["schema"] = JsonSchemaDump.Dump(strict.ToJson(), JsonSchemaDump.JsonSchemaExtendedFields),
        };
    }
}
