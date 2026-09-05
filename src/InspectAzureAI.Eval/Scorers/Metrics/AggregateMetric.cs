namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of <c>scorer/_metrics/aggregate.py</c>.</summary>
public static partial class Metrics
{
    /// <summary>
    /// Port of <c>aggregate(key, agg, to_float, on_missing)</c>: extracts <paramref name="key"/> from every
    /// dictionary-valued score and hands the resulting scalar sample scores to <paramref name="agg"/>. A missing key or
    /// a null entry follows <paramref name="onMissing"/> (<c>error</c>, <c>skip</c> or <c>zero</c>); a NaN entry (and an
    /// unscored sample) is always skipped. Yields NaN when every sample is skipped; a non-dictionary value throws.
    /// <paramref name="toFloat"/>, when given, pre-converts the extracted value; otherwise <paramref name="agg"/>'s own
    /// conversion applies.
    /// </summary>
    public static MetricDef Aggregate(string key, MetricDef agg, Func<ScoreValue, double>? toFloat = null, string onMissing = "error")
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(agg);
        if (onMissing is not ("error" or "skip" or "zero"))
        {
            throw new ArgumentException($"aggregate() got invalid on_missing='{onMissing}'; expected 'error', 'skip', or 'zero'.", nameof(onMissing));
        }

        return new("aggregate", scores =>
        {
            ArgumentNullException.ThrowIfNull(scores);
            var extracted = new List<SampleScore>();
            foreach (var sampleScore in scores)
            {
                var value = sampleScore.Score.Value;
                if (value.IsNaN)
                {
                    continue;
                }

                if (value is not ScoreValue.Dict dict)
                {
                    throw new ArgumentException(
                        $"Sample {IdText(sampleScore.SampleId)} has non-dict score value ({value.GetType().Name}); aggregate(key=...) requires dict-valued Score.value.",
                        nameof(scores));
                }

                var present = dict.Items.TryGetValue(key, out var raw);
                if (raw is { IsNaN: true })
                {
                    continue;
                }

                ScoreValue extractedValue;
                if (present && raw is not null)
                {
                    extractedValue = toFloat is not null ? toFloat(raw) : raw;
                }
                else if (onMissing == "error")
                {
                    var reason = present ? "None" : "missing";
                    throw new ArgumentException(
                        $"Sample {IdText(sampleScore.SampleId)} score key '{key}' is {reason}. Pass on_missing='skip' or on_missing='zero' to aggregate() to allow missing keys.",
                        nameof(scores));
                }
                else if (onMissing == "skip")
                {
                    continue;
                }
                else
                {
                    extractedValue = 0.0;
                }

                extracted.Add(sampleScore with { Score = sampleScore.Score with { Value = extractedValue } });
            }

            return extracted.Count == 0 ? ScoreValue.NaN : agg.Compute(extracted);
        });
    }
}
