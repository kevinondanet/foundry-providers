using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Log.Json;

namespace InspectAzureAI.Eval.Log;

/// <summary>Port of <c>log/_edit.py</c> <c>ProvenanceData</c>: who made an edit, when and why.</summary>
public sealed record ProvenanceData(string Author)
{
    /// <summary>Written in pydantic's default datetime format (<c>Z</c> suffix), as Python does.</summary>
    [JsonConverter(typeof(PydanticDateTimeOffsetConverter))]
    [JsonPropertyOrder(-1)]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public string? Reason { get; init; }

    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>Port of <c>log/_edit.py</c> <c>LogEdit</c>: one edit to a log's tags or metadata, discriminated by <see cref="Type"/>.</summary>
public abstract record LogEdit
{
    /// <summary>The JSON discriminator: "tags" or "metadata".</summary>
    public abstract string Type { get; }
}

/// <summary>Port of <c>log/_edit.py</c> <c>TagsEdit</c>.</summary>
public sealed record TagsEdit : LogEdit
{
    public override string Type => "tags";

    public IReadOnlyList<string> TagsAdd { get; init; } = [];

    public IReadOnlyList<string> TagsRemove { get; init; } = [];
}

/// <summary>Port of <c>log/_edit.py</c> <c>MetadataEdit</c>.</summary>
public sealed record MetadataEdit : LogEdit
{
    public override string Type => "metadata";

    public IReadOnlyDictionary<string, object?> MetadataSet { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public IReadOnlyList<string> MetadataRemove { get; init; } = [];
}

/// <summary>Port of <c>log/_edit.py</c> <c>LogUpdate</c>: a group of edits that share provenance.</summary>
public sealed record LogUpdate([property: JsonPropertyOrder(1)] ProvenanceData Provenance)
{
    [JsonPropertyOrder(0)]
    public IReadOnlyList<LogEdit> Edits { get; init; } = [];
}

/// <summary>
/// Port of <c>log/_config_update.py</c> <c>ConfigValueChange</c>: one knob's change within a config update.
/// <see cref="Config"/> is "eval", "generate" or "concurrency".
/// </summary>
public sealed record ConfigValueChange(string Config, string Name)
{
    public JsonNode? Value { get; init; }

    public bool Cleared { get; init; }

    public JsonNode? Previous { get; init; }
}

/// <summary>Port of <c>log/_config_update.py</c> <c>ConfigUpdate</c>: config changes applied mid-run via the control channel (<see cref="Scope"/> is "task" or "process").</summary>
public sealed record ConfigUpdate(IReadOnlyList<ConfigValueChange> Changes, string Scope, ProvenanceData Provenance);

/// <summary>Port of <c>log/_edit.py</c> <c>edit_eval_log</c> and <c>EvalLog.recompute_tags_and_metadata</c>.</summary>
public static class EvalLogEditing
{
    /// <summary>
    /// Port of <c>edit_eval_log</c>: filters out no-op edits, appends a <see cref="LogUpdate"/> and returns the
    /// log with <see cref="EvalLog.Tags"/> and <see cref="EvalLog.Metadata"/> recomputed. Invalid edits (an empty
    /// tag or key, or one both added and removed) throw <see cref="ArgumentException"/>. The log is not persisted.
    /// </summary>
    public static EvalLog EditEvalLog(EvalLog log, IEnumerable<LogEdit> edits, ProvenanceData provenance)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(provenance);
        var current = RecomputeTagsAndMetadata(log);
        var currentTags = new HashSet<string>(current.Tags, StringComparer.Ordinal);
        var currentMetadata = new Dictionary<string, object?>(current.Metadata, StringComparer.Ordinal);
        var filtered = new List<LogEdit>();
        foreach (var edit in edits)
        {
            switch (edit)
            {
                case TagsEdit tags:
                    foreach (var tag in tags.TagsAdd.Concat(tags.TagsRemove))
                    {
                        if (string.IsNullOrWhiteSpace(tag))
                        {
                            throw new ArgumentException("Tag must be a non-empty string.", nameof(edits));
                        }
                    }

                    var tagOverlap = tags.TagsAdd.Intersect(tags.TagsRemove, StringComparer.Ordinal).ToList();
                    if (tagOverlap.Count > 0)
                    {
                        throw new ArgumentException($"Tag(s) {string.Join(", ", tagOverlap)} appear in both tags_add and tags_remove.", nameof(edits));
                    }

                    var tagsAdd = tags.TagsAdd.Where(tag => !currentTags.Contains(tag)).ToList();
                    var tagsRemove = tags.TagsRemove.Where(currentTags.Contains).ToList();
                    if (tagsAdd.Count > 0 || tagsRemove.Count > 0)
                    {
                        filtered.Add(new TagsEdit { TagsAdd = tagsAdd, TagsRemove = tagsRemove });
                        currentTags.ExceptWith(tagsRemove);
                        currentTags.UnionWith(tagsAdd);
                    }

                    break;
                case MetadataEdit metadata:
                    foreach (var key in metadata.MetadataSet.Keys.Concat(metadata.MetadataRemove))
                    {
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            throw new ArgumentException("Metadata key must be a non-empty string.", nameof(edits));
                        }
                    }

                    var keyOverlap = metadata.MetadataSet.Keys.Intersect(metadata.MetadataRemove, StringComparer.Ordinal).ToList();
                    if (keyOverlap.Count > 0)
                    {
                        throw new ArgumentException($"Metadata key(s) {string.Join(", ", keyOverlap)} appear in both metadata_set and metadata_remove.", nameof(edits));
                    }

                    var metadataSet = metadata.MetadataSet
                        .Where(pair => !currentMetadata.TryGetValue(pair.Key, out var existing) || !PlainJson.ValueEquals(existing, pair.Value))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    var metadataRemove = metadata.MetadataRemove.Where(currentMetadata.ContainsKey).ToList();
                    if (metadataSet.Count > 0 || metadataRemove.Count > 0)
                    {
                        filtered.Add(new MetadataEdit { MetadataSet = metadataSet, MetadataRemove = metadataRemove });
                        foreach (var key in metadataRemove)
                        {
                            currentMetadata.Remove(key);
                        }

                        foreach (var pair in metadataSet)
                        {
                            currentMetadata[pair.Key] = pair.Value;
                        }
                    }

                    break;
                default:
                    throw new ArgumentException($"Unsupported log edit {edit.GetType().Name}.", nameof(edits));
            }
        }

        if (filtered.Count == 0)
        {
            return log;
        }

        var updates = new List<LogUpdate>(current.LogUpdates ?? []) { new LogUpdate(provenance) { Edits = filtered } };
        return RecomputeTagsAndMetadata(current with { LogUpdates = updates });
    }

    /// <summary>
    /// Port of <c>EvalLog.recompute_tags_and_metadata</c>: <see cref="EvalLog.Tags"/> (sorted) and
    /// <see cref="EvalLog.Metadata"/> from the eval-time values with every <see cref="LogUpdate"/> applied in order.
    /// </summary>
    public static EvalLog RecomputeTagsAndMetadata(EvalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var tags = new HashSet<string>(log.Eval.Tags ?? [], StringComparer.Ordinal);
        var metadata = new Dictionary<string, object?>(log.Eval.Metadata ?? new Dictionary<string, object?>(), StringComparer.Ordinal);
        foreach (var update in log.LogUpdates ?? [])
        {
            foreach (var edit in update.Edits)
            {
                switch (edit)
                {
                    case TagsEdit tagsEdit:
                        tags.ExceptWith(tagsEdit.TagsRemove);
                        tags.UnionWith(tagsEdit.TagsAdd);
                        break;
                    case MetadataEdit metadataEdit:
                        foreach (var key in metadataEdit.MetadataRemove)
                        {
                            metadata.Remove(key);
                        }

                        foreach (var pair in metadataEdit.MetadataSet)
                        {
                            metadata[pair.Key] = pair.Value;
                        }

                        break;
                }
            }
        }

        return log with { Tags = tags.Order(StringComparer.Ordinal).ToList(), Metadata = metadata };
    }
}
