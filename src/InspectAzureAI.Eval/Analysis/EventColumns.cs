using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>Port of <c>analysis/_dataframe/events/columns.py</c> <c>EventColumn</c>: a column read from a <see cref="TranscriptEvent"/>.</summary>
public class EventColumn : Column
{
    private readonly Func<TranscriptEvent, JsonNode?>? _extract;

    /// <summary>A column read from a JSONPath into the event (e.g. <c>uuid</c>, <c>output.usage</c>).</summary>
    public EventColumn(string name, string path, bool required = false, object? defaultValue = null, ColumnType? type = null, Func<JsonNode?, JsonNode?>? value = null)
        : base(name, path, required, defaultValue, type, value)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
    }

    /// <summary>A column computed from the event.</summary>
    public EventColumn(string name, Func<TranscriptEvent, JsonNode?> extract, bool required = false, object? defaultValue = null, ColumnType? type = null, Func<JsonNode?, JsonNode?>? value = null)
        : base(name, null, required, defaultValue, type, value)
    {
        ArgumentNullException.ThrowIfNull(extract);
        _extract = extract;
    }

    internal override JsonNode? Extract(ImportTarget target) =>
        _extract is not null && target.Event is { } @event ? _extract(@event) : throw new InvalidOperationException("column must have path or extract function");
}

/// <summary>
/// Port of the column groups of <c>analysis/_dataframe/events/columns.py</c> and the extractors of
/// <c>events/extract.py</c>. As in Python, the model and tool extractors fail on events of another type (a
/// <see cref="ColumnError"/>), so pair them with a filter.
/// </summary>
public static partial class EventColumns
{
    /// <summary>Port of <c>EventInfo</c>: id, type and span (the default for the events table).</summary>
    public static IReadOnlyList<Column> Info { get; } =
    [
        new EventColumn("event_id", "uuid"),
        new EventColumn("event", "event"),
        new EventColumn("span_id", "span_id"),
    ];

    /// <summary>Port of <c>EventTiming</c>: clock and working time.</summary>
    public static IReadOnlyList<Column> Timing { get; } =
    [
        new EventColumn("timestamp", "timestamp", type: ColumnType.DateTime),
        new EventColumn("completed", "completed", type: ColumnType.DateTime),
        new EventColumn("working_start", "working_start"),
        new EventColumn("working_time", "working_time"),
    ];

    /// <summary>Port of <c>ModelEventColumns</c>.</summary>
    public static IReadOnlyList<Column> ModelEvent { get; } =
    [
        new EventColumn("model_event_model", "model"),
        new EventColumn("model_event_role", "role"),
        new EventColumn("model_event_input", ModelEventInputAsStr),
        new EventColumn("model_event_tools", "tools"),
        new EventColumn("model_event_tool_choice", ToolChoiceAsStr),
        new EventColumn("model_event_config", "config"),
        new EventColumn("model_event_usage", "output.usage"),
        new EventColumn("model_event_time", "output.time"),
        new EventColumn("model_event_completion", CompletionAsStr),
        new EventColumn("model_event_retries", "retries"),
        new EventColumn("model_event_error", "error"),
        new EventColumn("model_event_cache", "cache"),
        new EventColumn("model_event_call", "call"),
    ];

    /// <summary>Port of <c>ToolEventColumns</c>.</summary>
    public static IReadOnlyList<Column> ToolEvent { get; } =
    [
        new EventColumn("tool_event_function", "function"),
        new EventColumn("tool_event_arguments", "arguments"),
        new EventColumn("tool_event_view", ToolViewAsStr),
        new EventColumn("tool_event_result", "result"),
        new EventColumn("tool_event_truncated", "truncated"),
        new EventColumn("tool_event_error_type", "error.type"),
        new EventColumn("tool_event_error_message", "error.message"),
    ];

    /// <summary>Port of <c>model_event_input_as_str</c>.</summary>
    public static JsonNode? ModelEventInputAsStr(TranscriptEvent @event) => JsonValue.Create(Extract.MessagesAsStr(AsModelEvent(@event).Input));

    /// <summary>Port of <c>tool_choice_as_str</c>: <c>auto</c> / <c>any</c> / <c>none</c>, or the forced function's name.</summary>
    public static JsonNode? ToolChoiceAsStr(TranscriptEvent @event)
    {
        var choice = AsModelEvent(@event).ToolChoice;
        return JsonValue.Create(choice is ToolFunction function ? function.Name : choice.ToString());
    }

    /// <summary>Port of <c>completion_as_str</c>.</summary>
    public static JsonNode? CompletionAsStr(TranscriptEvent @event) => JsonValue.Create(AsModelEvent(@event).Output.Completion);

    /// <summary>Port of <c>tool_view_as_str</c>: the view's title and content with <c>{{param}}</c> placeholders substituted from the arguments; null without a view.</summary>
    public static JsonNode? ToolViewAsStr(TranscriptEvent @event)
    {
        var tool = @event as ToolEvent ?? throw new InvalidOperationException($"'{@event?.GetType().Name}' object has no attribute 'view'");
        if (tool.View is null)
        {
            return null;
        }

        var view = SubstituteToolCallContent(tool.View, tool.Arguments);
        var title = view.Title is not null ? $"{view.Title}\n\n" : "";
        return JsonValue.Create($"{title}{view.Content}");
    }

    /// <summary>Port of <c>substitute_tool_call_content</c>: <c>{{name}}</c> placeholders replaced by <c>str(arguments[name])</c>; unknown names are left as they are.</summary>
    public static ToolCallContent SubstituteToolCallContent(ToolCallContent content, JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(arguments);
        string Replace(string text) => Placeholder().Replace(text, match => arguments.TryGetPropertyValue(match.Groups[1].Value, out var value) ? PythonFormat.Str(value) : match.Value);
        return new ToolCallContent(content.Format, Replace(content.Content)) { Title = content.Title is { Length: > 0 } title ? Replace(title) : content.Title };
    }

    private static ModelEvent AsModelEvent(TranscriptEvent @event) =>
        @event as ModelEvent ?? throw new InvalidOperationException($"'{@event?.GetType().Name}' object has no attribute 'input'");

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Placeholder();
}
