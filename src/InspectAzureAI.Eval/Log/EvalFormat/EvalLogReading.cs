using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>
/// Port of the read side of <c>log/_recorders/eval.py</c>: the header from <c>header.json</c> (or, for an
/// in-progress log, from <c>_journal/start.json</c> plus the journaled config updates), <c>reductions.json</c>,
/// the samples (last record wins for a repeated member name), the summaries (consolidated or journaled) and
/// single-sample reads by id/epoch or uuid, with chunked samples dispatched by their on-disk shape.
/// </summary>
internal static class EvalLogReading
{
    /// <summary>Port of <c>_read_log</c> / <c>_read_log_from_bytes</c>.</summary>
    public static EvalLog ReadLog(ZipLogReader reader, string? location, bool headerOnly, ISet<string>? excludeFields = null)
    {
        var names = reader.Names.ToHashSet(StringComparer.Ordinal);
        var log = ReadHeader(reader, names, location);
        if (names.Contains(EvalLogFormat.ReductionsJson))
        {
            var reductions = EvalJson.Deserialize<List<EvalSampleReductions>>(ReadMember(reader, EvalLogFormat.ReductionsJson), EvalLogFormat.ReductionsJson);
            if (log.Results is not null)
            {
                log = log with { Reductions = reductions };
            }
        }

        if (headerOnly)
        {
            return log;
        }

        var samples = new List<EvalSample>();
        foreach (var name in names)
        {
            if (EvalLogFormat.IsSampleEntry(name))
            {
                samples.Add(EvalJson.ParseSample(ReadObject(reader, name), name, excludeFields));
            }
            else if (ChunkedSampleFormat.TryShellPrefix(name, out var prefix))
            {
                samples.Add(EvalJson.ParseSample(ChunkedSampleReader.Read(reader, names, prefix, excludeFields), name, excludeFields));
            }
        }

        return log with { Samples = EvalLogWriter.SortSamples(samples) };
    }

    /// <summary>
    /// Port of <c>_read_header</c>: <c>header.json</c> when the log is finished; otherwise the spec and plan of
    /// <c>_journal/start.json</c> with any journaled config updates (status <see cref="EvalStatus.Started"/>).
    /// Unlike the JSON reader, Python's <c>.eval</c> reader has no version gate: a newer <c>version</c> is kept
    /// as read (<see cref="EvalLog.Version"/>) rather than rejected.
    /// </summary>
    public static EvalLog ReadHeader(ZipLogReader reader, IReadOnlySet<string> names, string? location)
    {
        if (names.Contains(EvalLogFormat.HeaderJson))
        {
            var root = ReadObject(reader, EvalLogFormat.HeaderJson);
            LegacyLogMigrations.Apply(root);
            var header = EvalJson.Deserialize<EvalLog>(root, EvalLogFormat.HeaderJson);
            return EvalLogEditing.RecomputeTagsAndMetadata(header with { Location = location });
        }

        var start = ReadStart(reader, names) ?? throw new InvalidDataException("The log has neither header.json nor _journal/start.json.");
        var updates = new List<ConfigUpdate>();
        foreach (var name in EvalLogFormat.SortedConfigUpdateEntries(names))
        {
            updates.Add(EvalJson.Deserialize<ConfigUpdate>(ReadMember(reader, name), name));
        }

        return EvalLogEditing.RecomputeTagsAndMetadata(new EvalLog
        {
            Version = start.Version,
            Eval = start.Eval,
            Plan = start.Plan,
            ConfigUpdates = updates.Count > 0 ? updates : null,
            Location = location,
        });
    }

    /// <summary>Port of <c>_read_start_async</c>.</summary>
    public static LogStart? ReadStart(ZipLogReader reader, IReadOnlySet<string> names)
    {
        var path = EvalLogFormat.JournalPath(EvalLogFormat.StartJson);
        return names.Contains(path) ? EvalJson.Deserialize<LogStart>(ReadMember(reader, path), path) : null;
    }

    /// <summary>Port of <c>_read_summary_counter</c>: the highest journaled summary batch index.</summary>
    public static int ReadSummaryCounter(IEnumerable<string> names)
    {
        var prefix = EvalLogFormat.JournalSummaryPath() + "/";
        return names.Select(name => EvalLogFormat.JournalIndex(name, prefix) ?? 0).DefaultIfEmpty(0).Max();
    }

    /// <summary>Port of <c>_read_all_summaries_async</c>: the consolidated <c>summaries.json</c>, or the journal batches in order; one row per (id, epoch), last wins.</summary>
    public static (List<EvalSampleSummary> Summaries, int Counter) ReadAllSummaries(ZipLogReader reader)
    {
        var names = reader.Names.ToHashSet(StringComparer.Ordinal);
        var count = ReadSummaryCounter(names);
        if (names.Contains(EvalLogFormat.SummariesJson))
        {
            return (DedupeSummaries(ParseSummaries(ReadMember(reader, EvalLogFormat.SummariesJson), EvalLogFormat.SummariesJson)), count);
        }

        var summaries = new List<EvalSampleSummary>();
        for (var i = 1; i <= count; i++)
        {
            var file = EvalLogFormat.JournalSummaryFile(i);
            summaries.AddRange(ParseSummaries(ReadMember(reader, EvalLogFormat.JournalSummaryPath(file)), file));
        }

        return (DedupeSummaries(summaries), count);
    }

    /// <summary>Port of <c>_read_config_updates_async</c>: the journaled updates (and highest index), else those of <c>header.json</c>.</summary>
    public static (List<ConfigUpdate> Updates, int Counter) ReadConfigUpdates(ZipLogReader reader)
    {
        var names = reader.Names.ToHashSet(StringComparer.Ordinal);
        var entries = EvalLogFormat.SortedConfigUpdateEntries(names);
        if (entries.Count > 0)
        {
            var updates = entries.Select(name => EvalJson.Deserialize<ConfigUpdate>(ReadMember(reader, name), name)).ToList();
            var counter = EvalLogFormat.JournalIndex(entries[^1], EvalLogFormat.JournalConfigUpdatePath() + "/") ?? 0;
            return (updates, counter);
        }

        if (names.Contains(EvalLogFormat.HeaderJson))
        {
            var header = ReadObject(reader, EvalLogFormat.HeaderJson);
            var raw = header["config_updates"] as JsonArray;
            return (raw is null ? [] : EvalJson.Deserialize<List<ConfigUpdate>>(raw, EvalLogFormat.HeaderJson), 0);
        }

        return ([], 0);
    }

    /// <summary>Port of <c>_read_log_sample_impl</c>: one sample by id and epoch, or by uuid via the summaries.</summary>
    public static EvalSample ReadSample(ZipLogReader reader, string? location, object? id, int epoch, string? uuid, ISet<string>? excludeFields)
    {
        if (id is null)
        {
            if (uuid is null)
            {
                throw new ArgumentException("You must specify an 'id' or 'uuid' to read", nameof(id));
            }

            var (summaries, _) = ReadAllSummaries(reader);
            var summary = summaries.FirstOrDefault(s => s.Uuid == uuid) ?? throw new KeyNotFoundException($"Sample with uuid '{uuid}' not found in log {location}");
            id = summary.Id;
            epoch = summary.Epoch;
        }

        var names = reader.Names.ToHashSet(StringComparer.Ordinal);
        switch (ChunkedSampleFormat.ClassifySampleShape(names, id, epoch))
        {
            case SampleShape.Monolith:
                var member = ChunkedSampleFormat.MonolithEntryName(id, epoch);
                return EvalJson.ParseSample(ReadObject(reader, member), member, excludeFields);
            case SampleShape.Chunked:
                var prefix = ChunkedSampleFormat.SamplePrefix(id, epoch);
                return EvalJson.ParseSample(ChunkedSampleReader.Read(reader, names, prefix, excludeFields), prefix, excludeFields);
            default:
                throw new KeyNotFoundException($"Sample id {EvalLogFormat.IdText(id)} for epoch {epoch} not found in log {location}");
        }
    }

    /// <summary>Port of <c>_dedupe_summaries</c>: the last row per (id, epoch), in first-seen order.</summary>
    public static List<EvalSampleSummary> DedupeSummaries(IEnumerable<EvalSampleSummary> summaries)
    {
        var order = new List<string>();
        var byKey = new Dictionary<string, EvalSampleSummary>(StringComparer.Ordinal);
        foreach (var summary in summaries)
        {
            var key = EvalLogFormat.SampleKey(summary.Id, summary.Epoch);
            if (byKey.TryAdd(key, summary))
            {
                order.Add(key);
            }
            else
            {
                byKey[key] = summary;
            }
        }

        return order.Select(key => byKey[key]).ToList();
    }

    public static JsonNode ReadMember(ZipLogReader reader, string name) => EvalJson.ParseNode(reader.Read(name), name);

    public static JsonObject ReadObject(ZipLogReader reader, string name) =>
        ReadMember(reader, name) as JsonObject ?? throw new InvalidDataException($"Member '{name}' of the log is not a JSON object.");

    /// <summary>Port of <c>_parse_summaries</c>: a member that is not a list is an <see cref="InvalidDataException"/> (Python's <c>ValueError</c>).</summary>
    private static List<EvalSampleSummary> ParseSummaries(JsonNode data, string source) =>
        data is JsonArray
            ? EvalJson.Deserialize<List<EvalSampleSummary>>(data, source)
            : throw new InvalidDataException($"Expected a list of summaries when reading {source}");
}

/// <summary>
/// Port of <c>log/_resolve.py</c> <c>resolve_sample_events_data</c> and <c>event/_pool.py</c>
/// <c>resolve_model_event_inputs</c> / <c>resolve_model_event_calls</c> / <c>_expand_refs</c> on the JSON of a
/// sample: range-encoded <c>input_refs</c> and <c>call_refs</c> are expanded against the message and call pools
/// (<c>events_data</c>, or a chunked sample's sequences) so every model event carries its full input.
/// </summary>
internal static class PoolRefs
{
    /// <summary>Port of <c>resolve_sample_events_data</c>: resolves the events against <c>events_data</c> and drops the pools.</summary>
    public static void ResolveSample(JsonObject sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample["events_data"] is not JsonObject eventsData)
        {
            return;
        }

        if (sample["events"] is JsonArray events)
        {
            ResolveEvents(events, eventsData["messages"] as JsonArray, eventsData["calls"] as JsonArray);
        }

        sample.Remove("events_data");
    }

    /// <summary>Resolves the model events of <paramref name="events"/> in place; an empty pool leaves its refs untouched, as in Python.</summary>
    public static void ResolveEvents(JsonArray events, JsonArray? messages, JsonArray? calls)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var e in events.OfType<JsonObject>())
        {
            if (e["event"] is not JsonValue type || !type.TryGetValue<string>(out var eventType) || eventType != "model")
            {
                continue;
            }

            if (messages is { Count: > 0 } && e["input_refs"] is JsonArray inputRefs)
            {
                e["input"] = new JsonArray(Expand(inputRefs, messages).ToArray());
                e.Remove("input_refs");
            }

            if (calls is { Count: > 0 } && e["call"] is JsonObject call && call["call_refs"] is JsonArray { Count: > 0 } callRefs)
            {
                var key = call["call_key"] is JsonValue callKey && callKey.TryGetValue<string>(out var text) && text.Length > 0 ? text : "messages";
                if (call["request"] is not JsonObject request)
                {
                    request = new JsonObject();
                    call["request"] = request;
                }

                request[key] = new JsonArray(Expand(callRefs, calls).ToArray());
                call.Remove("call_refs");
                call.Remove("call_key");
            }
        }
    }

    /// <summary>Port of <c>_expand_refs</c>: <c>pool[start:end_exclusive]</c> per half-open range, out-of-range parts truncated as a slice would.</summary>
    public static IEnumerable<JsonNode?> Expand(JsonArray refs, JsonArray pool)
    {
        ArgumentNullException.ThrowIfNull(refs);
        ArgumentNullException.ThrowIfNull(pool);
        foreach (var range in refs)
        {
            if (range is not JsonArray { Count: 2 } pair)
            {
                throw new JsonException("A pool reference is a two-element [start, end] array.");
            }

            var start = Math.Max(0, ReadIndex(pair[0]));
            var end = Math.Min(pool.Count, ReadIndex(pair[1]));
            for (var i = start; i < end; i++)
            {
                yield return pool[i]?.DeepClone();
            }
        }
    }

    private static int ReadIndex(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var index)
            ? index
            : throw new JsonException($"A pool reference index must be an integer, not '{node?.ToJsonString(EvalLogWriter.Options) ?? "null"}'.");
}
