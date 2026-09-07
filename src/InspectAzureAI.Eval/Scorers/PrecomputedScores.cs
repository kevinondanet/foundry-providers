using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log.Json;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of <c>scorer/_precomputed.py</c>.</summary>
public static partial class Scorers
{
    /// <summary>
    /// Port of <c>precomputed_scores(scores, on_missing, metrics)</c>: applies scores computed outside the eval, read
    /// from a JSON array or a <c>.jsonl</c> file of records with <c>id</c>, <c>value</c> and optional <c>epoch</c>,
    /// <c>answer</c>, <c>explanation</c> and <c>metadata</c>. A record with a matching epoch beats one without;
    /// duplicates and malformed records throw when the scorer is created. A sample without a record is left
    /// unscored (<paramref name="onMissing"/> <c>unscored</c>) or fails the eval (<c>error</c>). Metrics default to
    /// <c>[accuracy(), stderr()]</c>. The path is a local path or a <c>file://</c> URI.
    /// </summary>
    public static ScorerDef PrecomputedScores(string scores, string onMissing = "unscored", IReadOnlyList<MetricDef>? metrics = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(scores);
        if (onMissing is not ("unscored" or "error"))
        {
            throw new ArgumentException($"Invalid on_missing value '{onMissing}' (expected 'unscored' or 'error')", nameof(onMissing));
        }

        var lookup = PrecomputedScoreFile.Read(scores);
        return new("precomputed_scores", (state, _, _) =>
        {
            ArgumentNullException.ThrowIfNull(state);
            var id = Metrics.IdText(state.SampleId);
            if (!lookup.TryGetValue((id, state.Epoch), out var found) && !lookup.TryGetValue((id, null), out found))
            {
                if (onMissing == "error")
                {
                    throw new InvalidOperationException($"No score record in {scores} for sample id '{Metrics.IdText(state.SampleId)}' (epoch {state.Epoch})");
                }

                return Task.FromResult(Score.Unscored(explanation: $"No score record in {scores} for sample id '{Metrics.IdText(state.SampleId)}' (epoch {state.Epoch})"));
            }

            return Task.FromResult(found with { Metadata = found.Metadata is null ? null : (IReadOnlyDictionary<string, object?>?)PrecomputedScoreFile.DeepCopy(found.Metadata) });
        }, metrics ?? [Metrics.Accuracy(), Metrics.Stderr()]);
    }
}

/// <summary>Reads the score records of <see cref="Scorers.PrecomputedScores"/> (<c>_read_scores_file</c>, <c>_score_from_record</c>).</summary>
internal static class PrecomputedScoreFile
{
    public static Dictionary<(string Id, int? Epoch), Score> Read(string scoresFile)
    {
        var path = LocalPath(scoresFile);
        var text = File.ReadAllText(path, Encoding.UTF8);
        List<JsonNode?> records;
        if (scoresFile.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            records = text.Split('\n').Where(line => line.Trim().Length > 0).Select(line => PythonJson.Loads(line)).ToList();
        }
        else if (PythonJson.Loads(text) is JsonArray array)
        {
            records = array.ToList();
        }
        else
        {
            throw new ArgumentException($"Scores file {scoresFile} must contain a list of score records", nameof(scoresFile));
        }

        var lookup = new Dictionary<(string Id, int? Epoch), Score>();
        foreach (var record in records)
        {
            var (key, score) = FromRecord(record, scoresFile);
            if (lookup.ContainsKey(key))
            {
                throw new ArgumentException(
                    $"Duplicate score record in {scoresFile} for sample id '{key.Id}'" + (key.Epoch is { } epoch ? $" and epoch {epoch}" : ""),
                    nameof(scoresFile));
            }

            lookup[key] = score;
        }

        return lookup;
    }

    private static ((string Id, int? Epoch) Key, Score Score) FromRecord(JsonNode? record, string scoresFile)
    {
        if (record is not JsonObject obj)
        {
            throw new ArgumentException($"Score records in {scoresFile} must be objects (found {Describe(record)})", nameof(record));
        }

        foreach (var required in new[] { "id", "value" })
        {
            if (!obj.ContainsKey(required))
            {
                throw new ArgumentException($"Score record {Describe(record)} in {scoresFile} has no '{required}' field", nameof(record));
            }
        }

        int? epoch = null;
        if (obj["epoch"] is { } epochNode)
        {
            if (epochNode is not JsonValue epochValue || !epochValue.TryGetValue<double>(out var epochNumber) || epochNumber != Math.Floor(epochNumber)
                || epochValue.TryGetValue<bool>(out _))
            {
                throw new ArgumentException($"Score record {Describe(record)} in {scoresFile} has a non-integer 'epoch'", nameof(record));
            }

            epoch = (int)epochNumber;
        }

        var score = new Score(ScoreValue.FromJson(obj["value"]))
        {
            Answer = obj["answer"]?.GetValue<string>(),
            Explanation = obj["explanation"]?.GetValue<string>(),
            Metadata = obj["metadata"] is JsonObject metadata ? PlainJson.ToDictionary(metadata) : null,
        };
        return ((RecordIdText(obj["id"]), epoch), score);
    }

    /// <summary>Python <c>str(record["id"])</c>: integers without a fraction, floats in repr form, strings as is.</summary>
    private static string RecordIdText(JsonNode? id)
    {
        if (id is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag))
            {
                return flag ? "True" : "False";
            }

            if (value.TryGetValue<long>(out var integer))
            {
                return integer.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<double>(out var number))
            {
                return PythonText.FloatRepr(number);
            }

            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }
        }

        return Describe(id);
    }

    /// <summary>Resolves a <c>file://</c> URI to a local path; any other URI scheme is unsupported here.</summary>
    private static string LocalPath(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme != Uri.UriSchemeFile && uri.Scheme.Length > 1)
        {
            throw new NotSupportedException($"precomputed_scores only reads local files and file:// URIs, not '{uri.Scheme}://'.");
        }

        return uri is { Scheme: var scheme } && scheme == Uri.UriSchemeFile ? uri.LocalPath : path;
    }

    private static string Describe(JsonNode? node) => node is null ? "None" : PythonJson.Dumps(node);

    /// <summary>A deep copy of plain metadata, so a caller mutating the returned score cannot change the cached record (Python's <c>model_copy(deep=True)</c>).</summary>
    public static Dictionary<string, object?> DeepCopy(IReadOnlyDictionary<string, object?> metadata) =>
        metadata.ToDictionary(pair => pair.Key, pair => DeepCopyValue(pair.Value), StringComparer.Ordinal);

    private static object? DeepCopyValue(object? value) => value switch
    {
        IReadOnlyDictionary<string, object?> dict => DeepCopy(dict),
        IReadOnlyList<object?> list => list.Select(DeepCopyValue).ToList(),
        _ => value,
    };
}
