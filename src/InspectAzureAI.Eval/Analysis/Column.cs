using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>
/// Port of <c>analysis/_dataframe/columns.py</c> <c>ColumnType</c>: the types a column can be coerced to
/// (<c>int | float | bool | str | date | time | datetime</c>). Cells of these types are <see cref="long"/>,
/// <see cref="double"/>, <see cref="bool"/>, <see cref="string"/>, <see cref="DateOnly"/>, <see cref="TimeOnly"/> and
/// <see cref="DateTimeOffset"/>.
/// </summary>
public enum ColumnType
{
    Int,
    Float,
    Bool,
    String,
    Date,
    Time,
    DateTime,
}

/// <summary>
/// Port of <c>analysis/_dataframe/columns.py</c> <c>Column</c>: how one column is read into a <see cref="Table"/>.
/// A column reads either a JSONPath into the record's JSON (the record serialised exactly as the log format
/// writes it: snake_case names, nulls omitted) or a value returned by an extraction function over the typed record.
/// Non-required columns read as null (or <see cref="Default"/>); <see cref="Type"/> both validates and coerces
/// (strings are interpreted YAML-style first, e.g. <c>"true"</c> → <c>true</c>); <see cref="Value"/> transforms the
/// read value before it becomes a cell (e.g. a list to a comma-separated string).
/// </summary>
public abstract class Column
{
    private readonly Func<JsonNode?, JsonNode?>? _value;

    protected Column(string name, string? path, bool required, object? defaultValue, ColumnType? type, Func<JsonNode?, JsonNode?>? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Path = path;
        Required = required;
        Default = ToNode(defaultValue);
        Type = type;
        _value = value;
    }

    /// <summary>Column name; a trailing <c>*</c> (e.g. <c>task_arg_*</c>) expands a dictionary into one column per key.</summary>
    public string Name { get; }

    /// <summary>JSONPath into the record, or null when the column uses an extraction function.</summary>
    public string? Path { get; }

    /// <summary>Whether a missing value is an error.</summary>
    public bool Required { get; }

    /// <summary>Value used when the column reads as null.</summary>
    public JsonNode? Default { get; }

    /// <summary>Type the value is coerced to, if any.</summary>
    public ColumnType? Type { get; }

    /// <summary>Port of <c>Column.value</c>: the transform applied to a non-null read value (identity by default).</summary>
    public JsonNode? Value(JsonNode? x) => _value is null ? x : _value(x);

    /// <summary>Reads the value with the extraction function; called only when <see cref="Path"/> is null.</summary>
    internal abstract JsonNode? Extract(ImportTarget target);

    /// <summary>The JSON the path is evaluated against (the full record unless a subclass reads a summary).</summary>
    internal virtual JsonObject PathRecord(ImportTarget target) => target.Record;

    internal static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), EvalLogWriter.Options),
    };
}

/// <summary>Port of <c>ColumnError</c>: an error that occurred reading one column of one log.</summary>
public sealed record ColumnError(string Column, string? Path, Exception Error, EvalLog Log)
{
    public override string ToString()
    {
        var message = $"Error reading column '{Column}'";
        if (Path is not null)
        {
            message = $"{message} from path '{Path}'";
        }

        return $"{message}: {Error.Message} (log: {Log.Location})";
    }
}

/// <summary>Raised by the strict table readers for the first <see cref="ColumnError"/> (Python raises <c>ValueError(str(error))</c>).</summary>
public sealed class ColumnImportException : Exception
{
    public ColumnImportException(ColumnError error) : base(error.ToString())
    {
        Error = error;
    }

    public ColumnError Error { get; }
}

/// <summary>The result of a non-strict read: the table plus every column error encountered.</summary>
public sealed record TableImport(Table Table, IReadOnlyList<ColumnError> Errors);

/// <summary>
/// The record a column set is imported from: its JSON (the shape Python's <c>model_dump(mode="json",
/// exclude_none=True)</c> produces, i.e. the log format), the sample summary JSON when the record is a sample, and
/// the typed object for extraction functions.
/// </summary>
internal sealed class ImportTarget
{
    private ImportTarget(JsonObject record, JsonObject? summary)
    {
        Record = record;
        Summary = summary;
    }

    public JsonObject Record { get; }

    public JsonObject? Summary { get; }

    public EvalLog? Log { get; private init; }

    public EvalSample? Sample { get; private init; }

    public EvalSampleSummary? SampleSummary { get; private init; }

    public ChatMessage? Message { get; private init; }

    public TranscriptEvent? Event { get; private init; }

    public static ImportTarget ForLog(EvalLog log) =>
        new(Parse(EvalLogWriter.Serialize(log)), null) { Log = log };

    public static ImportTarget ForSample(EvalSample sample)
    {
        var summary = sample.Summary();
        return new ImportTarget(Serialize(sample), Serialize(summary)) { Sample = sample, SampleSummary = summary };
    }

    public static ImportTarget ForSummary(EvalSampleSummary summary)
    {
        var record = Serialize(summary);
        return new ImportTarget(record, record) { SampleSummary = summary };
    }

    public static ImportTarget ForMessage(ChatMessage message) => new(Serialize(message), null) { Message = message };

    public static ImportTarget ForEvent(TranscriptEvent @event) => new(Serialize(@event), null) { Event = @event };

    /// <summary>The record as the log format writes it; non-finite numbers become the sentinels of <c>PythonJsonFormat</c>.</summary>
    internal static JsonObject Serialize(object value) => Parse(JsonSerializer.Serialize(value, value.GetType(), EvalLogWriter.Options));

    private static JsonObject Parse(string json) =>
        JsonNode.Parse(InspectAzureAI.Eval.Log.Json.PythonJsonFormat.SanitizeNonFinite(json)) as JsonObject ?? throw new InvalidOperationException("The record did not serialize to a JSON object.");
}
