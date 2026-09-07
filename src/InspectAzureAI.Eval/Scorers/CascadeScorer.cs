using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of <c>scorer/_cascade.py</c>.</summary>
public static partial class Scorers
{
    /// <summary>
    /// Port of <c>cascade(**scorers)</c> with the default threshold of 1.0: see <see cref="Cascade(double, ValueTuple{string, Scorer}[])"/>.
    /// </summary>
    public static ScorerDef Cascade(params (string Name, Scorer Score)[] stages) => Cascade(1.0, stages);

    /// <summary>
    /// Port of <c>cascade(threshold, **scorers)</c>: runs the named <paramref name="stages"/> in order (cheapest first)
    /// and stops at the first whose <c>value_to_float</c> score is at least <paramref name="threshold"/>. An unscored
    /// (NaN) stage is skipped; when no stage settles the last scored stage's score is returned, and when none scored
    /// the result is <c>Score.Unscored(reason: scoring_failed)</c>. The returned score is a copy with
    /// <c>decided_by</c> (the stage name) added to its metadata; registered with <c>[accuracy(), stderr()]</c>.
    /// </summary>
    public static ScorerDef Cascade(double threshold, params (string Name, Scorer Score)[] stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        foreach (var (name, score) in stages)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentNullException.ThrowIfNull(score);
            if (name == "threshold")
            {
                throw new ArgumentException("A cascade stage cannot be named 'threshold', which is a reserved parameter.", nameof(stages));
            }
        }

        var toFloat = ValueToFloat.Default;
        return new("cascade", async (state, target, cancellationToken) =>
        {
            (string Name, Score Score)? lastScored = null;
            foreach (var (name, subScorer) in stages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await subScorer(state, target, cancellationToken).ConfigureAwait(false);
                if (result is null)
                {
                    continue;
                }

                var value = toFloat(result.Value);
                if (double.IsNaN(value))
                {
                    continue;
                }

                lastScored = (name, result);
                if (value >= threshold)
                {
                    break;
                }
            }

            if (lastScored is null)
            {
                return Score.Unscored(reason: ScoreReason.ScoringFailed);
            }

            var (decidedBy, decided) = lastScored.Value;
            var metadata = decided.Metadata is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(decided.Metadata, StringComparer.Ordinal);
            metadata["decided_by"] = decidedBy;
            return decided with { Metadata = metadata };
        }, [Metrics.Accuracy(), Metrics.Stderr()]);
    }

    /// <summary>A cascade over <see cref="ScorerDef"/> stages, each named by its scorer name.</summary>
    public static ScorerDef Cascade(IReadOnlyList<ScorerDef> stages, double threshold = 1.0)
    {
        ArgumentNullException.ThrowIfNull(stages);
        return Cascade(threshold, stages.Select(stage => (stage.Name, stage.Score)).ToArray());
    }
}
