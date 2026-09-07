using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Context;

namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>Port of <c>SampleShape</c>: how a sample is stored inside the <c>.eval</c> zip.</summary>
public enum SampleShape
{
    /// <summary>One <c>samples/{id}_epoch_{epoch}.json</c> member.</summary>
    Monolith,

    /// <summary>A <c>samples/{id}_epoch_{epoch}/</c> prefix holding a shell plus chunked sequences.</summary>
    Chunked,
}

/// <summary>
/// Port of <c>log/_recorders/chunked/format.py</c>: entry naming, chunk math and per-sample shape dispatch for
/// chunked samples (<c>design/large-samples.md</c>, Part I). A chunked sample lives under a per-sample prefix as a
/// small <c>sample.json</c> shell (with <c>message_refs</c> into the message sequence) plus four flat, index-addressed
/// sequences — <c>messages/</c>, <c>events/</c>, <c>calls/</c>, <c>attachments/</c> — stored as chunk members named
/// by the index of their first item; <c>metadata.json</c> (when non-empty), <c>skeleton.json</c>,
/// <c>events/stats.json</c> and <c>events/uuids.json</c> are sidecars. A log can mix shapes across samples.
/// </summary>
public static class ChunkedSampleFormat
{
    public const int DefaultChunkSize = 1000;

    public const int DefaultAttachmentsChunkBytes = 10 * 1024 * 1024;

    public const string ShellJson = "sample.json";

    public const string MetadataJson = "metadata.json";

    public const string SkeletonJson = "skeleton.json";

    public const string StatsJson = "stats.json";

    public const string UuidsJson = "uuids.json";

    public const string MessagesSequence = "messages";

    public const string EventsSequence = "events";

    public const string CallsSequence = "calls";

    public const string AttachmentsSequence = "attachments";

    /// <summary>Port of <c>sample_prefix</c>: the zip entry prefix under which all of a chunked sample's entries live.</summary>
    public static string SamplePrefix(object id, int epoch) => $"{EvalLogFormat.SamplesDir}/{EvalLogFormat.IdText(id)}_epoch_{epoch}";

    public static string ShellEntryName(object id, int epoch) => $"{SamplePrefix(id, epoch)}/{ShellJson}";

    public static string MetadataEntryName(object id, int epoch) => $"{SamplePrefix(id, epoch)}/{MetadataJson}";

    public static string SkeletonEntryName(object id, int epoch) => $"{SamplePrefix(id, epoch)}/{SkeletonJson}";

    /// <summary>Port of <c>events_stats_entry_name</c>: the per-chunk event stats sidecar (inside <c>events/</c>; numeric chunk names cannot collide).</summary>
    public static string EventsStatsEntryName(object id, int epoch) => $"{SamplePrefix(id, epoch)}/{EventsSequence}/{StatsJson}";

    /// <summary>Port of <c>events_uuids_entry_name</c>: event uuids in ordinal order.</summary>
    public static string EventsUuidsEntryName(object id, int epoch) => $"{SamplePrefix(id, epoch)}/{EventsSequence}/{UuidsJson}";

    public static string ChunkEntryName(object id, int epoch, string sequence, int start) => $"{SamplePrefix(id, epoch)}/{sequence}/{start.ToString(CultureInfo.InvariantCulture)}.json";

    /// <summary>Port of <c>monolith_entry_name</c>: today's single-entry sample name (the same as <see cref="EvalLogFormat.SampleFilename"/>).</summary>
    public static string MonolithEntryName(object id, int epoch) => EvalLogFormat.SampleFilename(id, epoch);

    /// <summary>Port of <c>classify_sample_shape</c>: the shape of a sample from the zip entry names, or null when it is present in neither.</summary>
    public static SampleShape? ClassifySampleShape(IReadOnlySet<string> entryNames, object id, int epoch)
    {
        ArgumentNullException.ThrowIfNull(entryNames);
        if (entryNames.Contains(MonolithEntryName(id, epoch)))
        {
            return SampleShape.Monolith;
        }

        if (entryNames.Contains(ShellEntryName(id, epoch)))
        {
            return SampleShape.Chunked;
        }

        return null;
    }

    /// <summary>Whether <paramref name="name"/> is a chunked sample's shell entry; <paramref name="prefix"/> receives the sample prefix.</summary>
    public static bool TryShellPrefix(string name, out string prefix)
    {
        ArgumentNullException.ThrowIfNull(name);
        var samples = EvalLogFormat.SamplesDir + "/";
        var suffix = "/" + ShellJson;
        if (name.StartsWith(samples, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal))
        {
            var candidate = name[..^suffix.Length];
            if (!candidate.AsSpan(samples.Length).Contains('/') && candidate.Length > samples.Length)
            {
                prefix = candidate;
                return true;
            }
        }

        prefix = "";
        return false;
    }

    /// <summary>Port of <c>chunk_ranges</c>: <paramref name="count"/> items split into count-based half-open ranges.</summary>
    public static IReadOnlyList<ChunkRange> ChunkRanges(int count, int chunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        var ranges = new List<ChunkRange>();
        for (var start = 0; start < count; start += chunkSize)
        {
            ranges.Add(new ChunkRange(start, Math.Min(start + chunkSize, count)));
        }

        return ranges;
    }

    /// <summary>Port of <c>chunk_boundaries</c>: cumulative end-exclusive boundaries (the last is the sequence count).</summary>
    public static IReadOnlyList<int> ChunkBoundaries(int count, int chunkSize) => ChunkRanges(count, chunkSize).Select(range => range.EndExclusive).ToList();

    /// <summary>Port of <c>attachment_chunk_boundaries</c>: pack items until about <paramref name="targetBytes"/> per chunk; an oversized item gets a chunk to itself.</summary>
    public static IReadOnlyList<int> AttachmentChunkBoundaries(IReadOnlyList<int> sizes, int targetBytes)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        var boundaries = new List<int>();
        var chunkBytes = 0L;
        for (var index = 0; index < sizes.Count; index++)
        {
            var size = sizes[index];
            if (chunkBytes > 0 && chunkBytes + size > targetBytes)
            {
                boundaries.Add(index);
                chunkBytes = 0;
            }

            chunkBytes += size;
        }

        if (chunkBytes > 0)
        {
            boundaries.Add(sizes.Count);
        }

        return boundaries;
    }

    /// <summary>Port of <c>boundary_ranges</c>: cumulative end-exclusive boundaries to half-open ranges.</summary>
    public static IReadOnlyList<ChunkRange> BoundaryRanges(IReadOnlyList<int> boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        var ranges = new List<ChunkRange>(boundaries.Count);
        var start = 0;
        foreach (var end in boundaries)
        {
            ranges.Add(new ChunkRange(start, end));
            start = end;
        }

        return ranges;
    }

    /// <summary>Port of <c>event_stats</c>: per-chunk stats for an events sequence (chunks are non-empty by construction).</summary>
    public static EventStats EventStatsFor(IReadOnlyList<TranscriptEvent> events, IReadOnlyList<int> boundaries)
    {
        ArgumentNullException.ThrowIfNull(events);
        var chunks = new List<EventChunkStats>();
        foreach (var range in BoundaryRanges(boundaries))
        {
            if (range.EndExclusive <= range.Start || range.EndExclusive > events.Count)
            {
                throw new ArgumentException($"Chunk boundary [{range.Start}, {range.EndExclusive}) does not fit {events.Count} events.", nameof(boundaries));
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = range.Start; i < range.EndExclusive; i++)
            {
                counts[events[i].Event] = counts.GetValueOrDefault(events[i].Event) + 1;
            }

            chunks.Add(new EventChunkStats(range.Start, counts, Edge(events[range.Start]), Edge(events[range.EndExclusive - 1])));
        }

        return new EventStats(chunks);
    }

    private static ChunkEdgeEvent Edge(TranscriptEvent e) => new(e.Event, e.SpanId);
}

/// <summary>Port of <c>ChunkRange</c>: the half-open <c>[start, end_exclusive)</c> extent of one chunk.</summary>
public readonly record struct ChunkRange(int Start, int EndExclusive);

/// <summary>Port of <c>ChunkEdgeEvent</c>: type and span identity of an event at a chunk edge.</summary>
public sealed record ChunkEdgeEvent(string Type, string? SpanId = null);

/// <summary>Port of <c>EventChunkStats</c>: stats for one events chunk (sparse type counts, first and last event).</summary>
public sealed record EventChunkStats(int Start, IReadOnlyDictionary<string, int> TypeCounts, ChunkEdgeEvent First, ChunkEdgeEvent Last);

/// <summary>Port of <c>EventStats</c>: the <c>events/stats.json</c> sidecar.</summary>
public sealed record EventStats([property: JsonPropertyOrder(1)] IReadOnlyList<EventChunkStats> Chunks)
{
    [JsonPropertyOrder(0)]
    public int Version { get; init; } = 1;
}

/// <summary>
/// Reads a chunked sample back into the monolith JSON shape: the shell with its <c>message_refs</c> expanded into
/// <c>messages</c>, <c>metadata.json</c> restored, the events sequence with <c>input_refs</c> / <c>call_refs</c>
/// resolved against the message and call sequences, and the attachments sequence as an <c>{index: content}</c>
/// map (identity is the sequence index, so <c>attachment://&lt;index&gt;</c> references resolve against it).
/// </summary>
internal static class ChunkedSampleReader
{
    public static JsonObject Read(ZipLogReader reader, IReadOnlySet<string> names, string prefix, ISet<string>? excludeFields)
    {
        var shellName = $"{prefix}/{ChunkedSampleFormat.ShellJson}";
        var shell = EvalLogReading.ReadObject(reader, shellName);
        var refs = shell["message_refs"] as JsonArray;
        shell.Remove("message_refs");
        var exclude = excludeFields ?? new HashSet<string>(StringComparer.Ordinal);

        var metadataName = $"{prefix}/{ChunkedSampleFormat.MetadataJson}";
        if (!exclude.Contains("metadata") && names.Contains(metadataName))
        {
            shell["metadata"] = EvalLogReading.ReadMember(reader, metadataName);
        }

        JsonArray? messages = null;
        if (!exclude.Contains("messages") || !exclude.Contains("events"))
        {
            messages = ReadSequence(reader, names, prefix, ChunkedSampleFormat.MessagesSequence);
        }

        if (!exclude.Contains("messages"))
        {
            shell["messages"] = refs is null ? new JsonArray() : new JsonArray(PoolRefs.Expand(refs, messages!).ToArray());
        }

        if (!exclude.Contains("events"))
        {
            var events = ReadSequence(reader, names, prefix, ChunkedSampleFormat.EventsSequence);
            var calls = ReadSequence(reader, names, prefix, ChunkedSampleFormat.CallsSequence);
            PoolRefs.ResolveEvents(events, messages, calls);
            shell["events"] = events;
        }

        if (!exclude.Contains("attachments"))
        {
            var attachments = new JsonObject();
            var sequence = ReadSequence(reader, names, prefix, ChunkedSampleFormat.AttachmentsSequence);
            for (var i = 0; i < sequence.Count; i++)
            {
                attachments[i.ToString(CultureInfo.InvariantCulture)] = sequence[i]?.DeepClone();
            }

            shell["attachments"] = attachments;
        }

        return shell;
    }

    /// <summary>The whole sequence: its chunk members (named by start index) parsed in index order and concatenated.</summary>
    public static JsonArray ReadSequence(ZipLogReader reader, IReadOnlySet<string> names, string prefix, string sequence)
    {
        var chunkPrefix = $"{prefix}/{sequence}/";
        var chunks = names
            .Select(name => (Name: name, Start: EvalLogFormat.JournalIndex(name, chunkPrefix)))
            .Where(chunk => chunk.Start is not null)
            .OrderBy(chunk => chunk.Start)
            .ToList();
        var items = new JsonArray();
        foreach (var chunk in chunks)
        {
            if (EvalLogReading.ReadMember(reader, chunk.Name) is not JsonArray chunkItems)
            {
                throw new InvalidDataException($"Chunk '{chunk.Name}' of the log is not a JSON array.");
            }

            if (chunk.Start != items.Count)
            {
                throw new InvalidDataException($"Chunk '{chunk.Name}' starts at {chunk.Start} but {items.Count} items precede it.");
            }

            // detach from the end (O(1) per item) so the nodes can be re-parented without cloning
            var detached = new JsonNode?[chunkItems.Count];
            for (var i = chunkItems.Count - 1; i >= 0; i--)
            {
                detached[i] = chunkItems[i];
                chunkItems.RemoveAt(i);
            }

            foreach (var node in detached)
            {
                items.Add(node);
            }
        }

        return items;
    }
}
