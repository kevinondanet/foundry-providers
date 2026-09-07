using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>Port of <c>analysis/_dataframe/messages/columns.py</c> <c>MessageColumn</c>: a column read from a <see cref="ChatMessage"/>.</summary>
public class MessageColumn : Column
{
    private readonly Func<ChatMessage, JsonNode?>? _extract;

    /// <summary>A column read from a JSONPath into the message (e.g. <c>role</c>, <c>error.message</c>).</summary>
    public MessageColumn(string name, string path, bool required = false, object? defaultValue = null, ColumnType? type = null, Func<JsonNode?, JsonNode?>? value = null)
        : base(name, path, required, defaultValue, type, value)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
    }

    /// <summary>A column computed from the message.</summary>
    public MessageColumn(string name, Func<ChatMessage, JsonNode?> extract, bool required = false, object? defaultValue = null, ColumnType? type = null, Func<JsonNode?, JsonNode?>? value = null)
        : base(name, null, required, defaultValue, type, value)
    {
        ArgumentNullException.ThrowIfNull(extract);
        _extract = extract;
    }

    internal override JsonNode? Extract(ImportTarget target) =>
        _extract is not null && target.Message is { } message ? _extract(message) : throw new InvalidOperationException("column must have path or extract function");
}

/// <summary>Port of the column groups of <c>analysis/_dataframe/messages/columns.py</c> and the extractors of <c>messages/extract.py</c>.</summary>
public static class MessageColumns
{
    /// <summary>Port of <c>MessageContent</c>: id, role, source and text.</summary>
    public static IReadOnlyList<Column> Content { get; } =
    [
        new MessageColumn("message_id", "id"),
        new MessageColumn("role", "role", required: true),
        new MessageColumn("source", "source"),
        new MessageColumn("content", MessageText),
    ];

    /// <summary>Port of <c>MessageToolCalls</c>: the tool calls of an assistant message and the call a tool message answers.</summary>
    public static IReadOnlyList<Column> ToolCalls { get; } =
    [
        new MessageColumn("tool_calls", MessageToolCalls),
        new MessageColumn("tool_call_id", "tool_call_id"),
        new MessageColumn("tool_call_function", "function"),
        new MessageColumn("tool_call_error", "error.message"),
    ];

    /// <summary>Port of <c>MessageColumns</c>: the default columns of the messages table.</summary>
    public static IReadOnlyList<Column> Default { get; } = [.. Content, .. ToolCalls];

    /// <summary>Port of <c>message_text</c>.</summary>
    public static JsonNode? MessageText(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonValue.Create(message.Text);
    }

    /// <summary>Port of <c>message_tool_calls</c>: an assistant message's tool calls as <c>name(arg=value, ...)</c> lines; null for other messages.</summary>
    public static JsonNode? MessageToolCalls(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message is ChatMessageAssistant { ToolCalls: { } toolCalls })
        {
            return JsonValue.Create(string.Join('\n', toolCalls.Select(call => PythonFormat.FormatFunctionCall(call.Function, call.Arguments, width: 1000))));
        }

        return null;
    }
}
