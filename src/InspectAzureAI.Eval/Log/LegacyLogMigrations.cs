using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Log;

/// <summary>
/// Port of the <c>mode="before"</c> validators of <c>log/_log.py</c> that rewrite legacy log shapes on read:
/// <c>EvalResults.convert_scorer_to_scorers</c> (<c>results.scorer</c> + <c>results.metrics</c> →
/// <c>results.scores</c>), <c>EvalLog.resolve_sample_reductions</c> (<c>results.sample_reductions</c> ↔
/// <c>reductions</c>), <c>EvalSample.migrate_deprecated</c> (<c>score</c> → <c>scores</c> under a placeholder,
/// <c>transcript</c> → <c>events</c> + <c>attachments</c>) and <c>EvalLog.populate_scorer_name_for_samples</c>
/// (the placeholder renamed to the first scorer). Applied to the parsed JSON before deserialization.
/// </summary>
public static class LegacyLogMigrations
{
    /// <summary>Port of <c>SCORER_PLACEHOLDER</c>: the key a legacy single <c>score</c> is filed under until the scorer name is known.</summary>
    public const string ScorerPlaceholder = "88F74D2C";

    /// <summary>Rewrites <paramref name="root"/> in place. Throws <see cref="JsonException"/> where Python raises <c>TypeError</c> (old and new forms both present).</summary>
    public static void Apply(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root["results"] is JsonObject results)
        {
            MigrateResults(results);
            var sampleReductions = results["sample_reductions"];
            if (sampleReductions is not null)
            {
                results.Remove("sample_reductions");
                if (!root.ContainsKey("reductions"))
                {
                    root["reductions"] = sampleReductions;
                }
            }
        }

        var scorerName = root["results"]?["scores"] is JsonArray { Count: > 0 } scores && scores[0]?["name"] is JsonValue name && name.TryGetValue<string>(out var text)
            ? text
            : null;
        if (root["samples"] is JsonArray samples)
        {
            foreach (var sample in samples.OfType<JsonObject>())
            {
                MigrateSample(sample, scorerName);
            }
        }
    }

    private static void MigrateResults(JsonObject results)
    {
        if (!results.ContainsKey("scorer"))
        {
            return;
        }

        if (results.ContainsKey("scores"))
        {
            throw new JsonException("Unexpected value `scores` present when `scorer` has already been specified.");
        }

        var metrics = results["metrics"];
        results.Remove("metrics");
        var scorer = results["scorer"];
        results.Remove("scorer");
        if (scorer is JsonObject score)
        {
            if (metrics is JsonObject { Count: > 0 })
            {
                score["metrics"] = metrics;
            }

            score["scorer"] = score["name"]?.DeepClone();
            results["scores"] = new JsonArray(score);
        }
    }

    private static void MigrateSample(JsonObject sample, string? scorerName)
    {
        if (sample.ContainsKey("score"))
        {
            if (sample.ContainsKey("scores"))
            {
                throw new JsonException("Unexpected value `scores` present when `score` has already been specified.");
            }

            var score = sample["score"];
            sample.Remove("score");
            sample["scores"] = new JsonObject { [ScorerPlaceholder] = score };
        }

        if (sample["transcript"] is JsonObject transcript)
        {
            sample.Remove("transcript");
            var events = transcript["events"];
            transcript.Remove("events");
            var content = transcript["content"];
            transcript.Remove("content");
            sample["events"] = events ?? new JsonArray();
            sample["attachments"] = content ?? new JsonObject();
        }

        if (scorerName is not null && sample["scores"] is JsonObject sampleScores && sampleScores.ContainsKey(ScorerPlaceholder))
        {
            var placeholder = sampleScores[ScorerPlaceholder];
            sampleScores.Remove(ScorerPlaceholder);
            sampleScores[scorerName] = placeholder;
        }
    }
}
