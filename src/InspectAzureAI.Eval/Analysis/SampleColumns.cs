using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>
/// Port of <c>analysis/_dataframe/samples/columns.py</c> <c>SampleColumn</c>: a column read from an
/// <see cref="EvalSample"/> or its <see cref="EvalSampleSummary"/>. <see cref="Full"/> columns need the full sample
/// (forcing the samples table to load whole logs); it is inferred from the path prefix (<c>messages</c>,
/// <c>store</c>, <c>events</c>, ...) unless given, and true for a column extracted from an <see cref="EvalSample"/>.
/// </summary>
public class SampleColumn : Column
{
    private static readonly string[] FullPrefixes =
    [
        "choices", "sandbox", "files", "setup", "messages", "output", "store", "events", "uuid", "error_retries", "attachments",
    ];

    private readonly Func<EvalSampleSummary, JsonNode?>? _extractSummary;
    private readonly Func<EvalSample, JsonNode?>? _extractSample;

    /// <summary>A column read from a JSONPath into the sample (summary unless <paramref name="full"/>).</summary>
    public SampleColumn(string name, string path, bool required = false, object? defaultValue = null, ColumnType? type = null, Func<JsonNode?, JsonNode?>? value = null, bool? full = null)
        : base(name, path, required, defaultValue, type, value)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Full = full ?? PathRequiresFull(path);
    }

    /// <summary>A column computed from the sample summary (given a summary built from the full sample when full samples were loaded).</summary>
    public SampleColumn(string name, Func<EvalSampleSummary, JsonNode?> extract, bool required = false, object? defaultValue = null, ColumnType? type = null, Func<JsonNode?, JsonNode?>? value = null)
        : base(name, null, required, defaultValue, type, value)
    {
        ArgumentNullException.ThrowIfNull(extract);
        _extractSummary = extract;
        Full = false;
    }

    /// <summary>A column computed from the full sample (forces full samples to be loaded).</summary>
    public SampleColumn(string name, Func<EvalSample, JsonNode?> extract, bool required = false, object? defaultValue = null, ColumnType? type = null, Func<JsonNode?, JsonNode?>? value = null)
        : base(name, null, required, defaultValue, type, value)
    {
        ArgumentNullException.ThrowIfNull(extract);
        _extractSample = extract;
        Full = true;
    }

    private SampleColumn(SampleColumn other, bool full)
        : base(other.Name, other.Path, other.Required, other.Default, other.Type, other.Value)
    {
        _extractSummary = other._extractSummary;
        _extractSample = other._extractSample;
        Full = full;
    }

    /// <summary>Whether the column reads from the full sample rather than the summary.</summary>
    public bool Full { get; }

    /// <summary>Port of <c>sample_path_requires_full</c>: paths into fields that only the full sample carries.</summary>
    public static bool PathRequiresFull(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return FullPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>A copy of this column that reads from the full sample.</summary>
    internal SampleColumn AsFull() => Full ? this : new SampleColumn(this, true);

    internal override JsonObject PathRecord(ImportTarget target) => Full ? target.Record : target.Summary ?? target.Record;

    internal override JsonNode? Extract(ImportTarget target)
    {
        if (_extractSample is not null && target.Sample is { } sample)
        {
            return _extractSample(sample);
        }

        if (_extractSummary is not null && target.SampleSummary is { } summary)
        {
            return _extractSummary(summary);
        }

        throw new InvalidOperationException("column must have path or extract function");
    }
}

/// <summary>Port of the column groups of <c>analysis/_dataframe/samples/columns.py</c> and the extractors of <c>samples/extract.py</c>.</summary>
public static class SampleColumns
{
    /// <summary>Port of <c>SampleSummary</c>: the columns read from sample summaries (the default for the samples table).</summary>
    public static IReadOnlyList<Column> Summary { get; } =
    [
        new SampleColumn("id", "id", required: true, type: ColumnType.String),
        new SampleColumn("epoch", "epoch", required: true),
        new SampleColumn("input", SampleInputAsStr, required: true),
        new SampleColumn("choices", "choices", full: false),
        new SampleColumn("target", "target", required: true, value: Extract.ListAsStr),
        new SampleColumn("metadata_*", "metadata"),
        new SampleColumn("score_*", "scores", value: Extract.ScoreValues),
        new SampleColumn("model_usage", "model_usage"),
        new SampleColumn("total_tokens", SampleTotalTokens),
        new SampleColumn("total_time", "total_time"),
        new SampleColumn("working_time", "working_time"),
        new SampleColumn("message_count", "message_count"),
        new SampleColumn("turn_count", "turn_count"),
        new SampleColumn("token_limit_usage", "token_limit_usage"),
        new SampleColumn("error", "error", defaultValue: ""),
        new SampleColumn("limit", "limit"),
        new SampleColumn("limit_reason", "limit_reason"),
        new SampleColumn("retries", "retries"),
        new SampleColumn("fallbacks", SampleTotalFallbacks),
    ];

    /// <summary>Port of <c>SampleMessages</c>: every message of the sample as one string (needs full samples).</summary>
    public static IReadOnlyList<Column> Messages { get; } =
    [
        new SampleColumn("messages", SampleMessagesAsStr, required: true),
    ];

    /// <summary>Port of <c>SampleScores</c>: score values plus their answer, explanation, reason and metadata (needs full samples).</summary>
    public static IReadOnlyList<Column> Scores { get; } =
    [
        new SampleColumn("score_*", "scores", value: Extract.ScoreValues, full: true),
        new SampleColumn("score_*", "scores", value: Extract.ScoreDetails, full: true),
    ];

    /// <summary>Port of <c>sample_input_as_str</c>.</summary>
    public static JsonNode? SampleInputAsStr(EvalSampleSummary sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return JsonValue.Create(Extract.MessagesAsStr(sample.Input));
    }

    /// <summary>Port of <c>sample_total_tokens</c>: the sum of <c>total_tokens</c> over the sample's model usage.</summary>
    public static JsonNode? SampleTotalTokens(EvalSampleSummary sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return JsonValue.Create(sample.ModelUsage.Values.Sum(usage => (long)usage.TotalTokens));
    }

    /// <summary>Port of <c>sample_total_fallbacks</c>: the number of generate calls served by a fallback model.</summary>
    public static JsonNode? SampleTotalFallbacks(EvalSampleSummary sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return JsonValue.Create((sample.ModelFallbacks ?? []).Sum(fallback => (long)fallback.Count));
    }

    /// <summary>Port of <c>sample_messages_as_str</c>.</summary>
    public static JsonNode? SampleMessagesAsStr(EvalSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return JsonValue.Create(Extract.MessagesAsStr(sample.Messages));
    }

    /// <summary>Port of <c>auto_sample_id</c>: the stable id of a sample without a uuid.</summary>
    public static string AutoSampleId(string evalId, object id, int epoch) => Extract.AutoId(evalId, $"{Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture)}_{epoch}");

    /// <summary>Port of <c>auto_detail_id</c>: the stable id of the <paramref name="index"/>th message or event of a sample.</summary>
    public static string AutoDetailId(string sampleId, string name, int index) => Extract.AutoId(sampleId, $"{name}_{index}");
}
