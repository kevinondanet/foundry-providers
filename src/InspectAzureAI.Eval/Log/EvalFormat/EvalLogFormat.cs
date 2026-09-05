using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log.Json;

namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>
/// The member names of the <c>.eval</c> zip, as <c>log/_recorders/eval.py</c> lays them out: the run's
/// <c>_journal/start.json</c>, then <c>samples/{id}_epoch_{epoch}.json</c> per sample with a numbered summary batch
/// under <c>_journal/summaries/</c> after every flush (and <c>_journal/config_updates/{n}.json</c> per mid-run
/// retune), and on finish the consolidated <c>summaries.json</c>, <c>reductions.json</c> and <c>header.json</c>.
/// </summary>
public static class EvalLogFormat
{
    public const string JournalDir = "_journal";

    public const string SummaryDir = "summaries";

    public const string ConfigUpdatesDir = "config_updates";

    public const string SamplesDir = "samples";

    public const string StartJson = "start.json";

    public const string ResultsJson = "results.json";

    public const string ReductionsJson = "reductions.json";

    public const string SummariesJson = "summaries.json";

    public const string HeaderJson = "header.json";

    /// <summary>Port of <c>_sample_filename</c>: <c>samples/{id}_epoch_{epoch}.json</c>.</summary>
    public static string SampleFilename(object id, int epoch) => $"{SamplesDir}/{IdText(id)}_epoch_{epoch}.json";

    /// <summary>Port of <c>_journal_path</c>.</summary>
    public static string JournalPath(string file) => JournalDir + "/" + file;

    /// <summary>Port of <c>_journal_summary_path</c>: the summaries directory, or one summary batch inside it.</summary>
    public static string JournalSummaryPath(string? file = null) => file is null ? JournalPath(SummaryDir) : $"{JournalPath(SummaryDir)}/{file}";

    /// <summary>Port of <c>_journal_summary_file</c>.</summary>
    public static string JournalSummaryFile(int index) => $"{index}.json";

    /// <summary>Port of <c>_journal_config_update_path</c>.</summary>
    public static string JournalConfigUpdatePath(string? file = null) => file is null ? JournalPath(ConfigUpdatesDir) : $"{JournalPath(ConfigUpdatesDir)}/{file}";

    /// <summary>Port of <c>_journal_config_update_file</c>.</summary>
    public static string JournalConfigUpdateFile(int index) => $"{index}.json";

    /// <summary>Port of <c>_sorted_config_update_entries</c>: the journaled config updates in write order (by integer index).</summary>
    public static IReadOnlyList<string> SortedConfigUpdateEntries(IEnumerable<string> entryNames)
    {
        ArgumentNullException.ThrowIfNull(entryNames);
        var prefix = JournalConfigUpdatePath() + "/";
        return entryNames
            .Select(name => (Name: name, Index: JournalIndex(name, prefix)))
            .Where(entry => entry.Index is not null)
            .OrderBy(entry => entry.Index)
            .Select(entry => entry.Name)
            .ToList();
    }

    /// <summary>Whether <paramref name="name"/> is a monolith sample member (<c>samples/{id}_epoch_{epoch}.json</c>, nothing nested).</summary>
    public static bool IsSampleEntry(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var prefix = SamplesDir + "/";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            && name.EndsWith(".json", StringComparison.Ordinal)
            && !name.AsSpan(prefix.Length).Contains('/');
    }

    /// <summary>Python's <c>f"{id}"</c> for a sample id: strings as is, integers in invariant form.</summary>
    public static string IdText(object id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id switch
        {
            string text => text,
            bool flag => flag ? "True" : "False",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => id.ToString() ?? "",
        };
    }

    /// <summary>The key Python's <c>(id, epoch)</c> tuples compare by: the int <c>1</c> and the string <c>"1"</c> are different samples.</summary>
    public static string SampleKey(object id, int epoch) => (id is string ? "s:" : "n:") + IdText(id) + "@" + epoch.ToString(CultureInfo.InvariantCulture);

    /// <summary>The integer index of a journal member named <c>{prefix}{n}.json</c>, or null for any other name.</summary>
    internal static int? JournalIndex(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal))
        {
            return null;
        }

        var stem = name.AsSpan(prefix.Length, name.Length - prefix.Length - ".json".Length);
        return stem.Length > 0 && !stem.Contains('/') && int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : null;
    }
}

/// <summary>Port of <c>LogStart</c>: the <c>_journal/start.json</c> member written when the eval begins.</summary>
public sealed record LogStart(int Version, EvalSpec Eval, EvalPlan Plan);

/// <summary>Port of <c>LogResults</c>: the outcome folded into <c>header.json</c> on finish.</summary>
public sealed record LogResults(EvalStatus Status, EvalStats Stats, EvalResults? Results = null, EvalError? Error = null);

/// <summary>
/// The JSON of <c>.eval</c> members: Python's <c>to_json_safe(data, indent=None)</c> (compact, <c>exclude_none</c>,
/// non-ASCII verbatim, <c>NaN</c> constants) on the same converters as <see cref="EvalLogWriter.Options"/>, and the
/// tolerant parse back (non-finite constants rewritten before strict parsing, legacy sample shapes migrated).
/// </summary>
internal static class EvalJson
{
    /// <summary><see cref="EvalLogWriter.Options"/> without indentation.</summary>
    public static JsonSerializerOptions Compact { get; } = CreateCompact();

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Compact);

    /// <summary>Parses a member's bytes into a node tree, tolerating Python's bare <c>NaN</c> / <c>Infinity</c> constants.</summary>
    public static JsonNode ParseNode(byte[] utf8, string member)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        return JsonNode.Parse(PythonJsonFormat.SanitizeNonFinite(Encoding.UTF8.GetString(utf8))) ?? throw new JsonException($"Member '{member}' of the log is empty.");
    }

    public static T Deserialize<T>(JsonNode node, string member) =>
        node.Deserialize<T>(EvalLogWriter.Options) ?? throw new JsonException($"Member '{member}' of the log is null.");

    /// <summary>
    /// Port of <c>EvalSample.model_validate</c> on a zip member plus the read-time steps of <c>_file.py</c>:
    /// excluded top-level fields are dropped (<c>exclude_fields</c>), legacy shapes are migrated, and the
    /// <c>events_data</c> pools are resolved back into the model events (<c>resolve_sample_events_data</c>).
    /// </summary>
    public static EvalSample ParseSample(JsonObject node, string member, ISet<string>? excludeFields = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (excludeFields is not null)
        {
            foreach (var field in excludeFields)
            {
                node.Remove(field);
            }
        }

        var root = new JsonObject { ["samples"] = new JsonArray(node) };
        LegacyLogMigrations.Apply(root);
        var migrated = (JsonObject)root["samples"]![0]!;
        PoolRefs.ResolveSample(migrated);
        migrated.Parent?.AsArray().Remove(migrated);
        return Deserialize<EvalSample>(migrated, member);
    }

    /// <summary>Port of <c>_normalize_excluded_fields</c>: <c>events_data</c> stays whenever <c>events</c> is read and goes whenever it is not.</summary>
    public static ISet<string>? NormalizeExcludedFields(ISet<string>? excludeFields)
    {
        if (excludeFields is null || excludeFields.Count == 0)
        {
            return null;
        }

        var normalized = new HashSet<string>(excludeFields, StringComparer.Ordinal);
        if (normalized.Contains("events"))
        {
            normalized.Add("events_data");
        }
        else
        {
            normalized.Remove("events_data");
        }

        return normalized;
    }

    private static JsonSerializerOptions CreateCompact()
    {
        var options = new JsonSerializerOptions(EvalLogWriter.Options) { WriteIndented = false };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
