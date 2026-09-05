using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of <c>scorer/_multi.py</c>.</summary>
public static partial class Scorers
{
    /// <summary>
    /// Port of <c>multi_scorer(scorers, reducer)</c>: runs every scorer concurrently and folds their scores with
    /// <paramref name="reducer"/>. With no scorers (Python: every sub-scorer declined) the result is
    /// <c>Score.Unscored(reason: scoring_failed)</c>. The metrics are those of the first scorer, as in Python.
    /// </summary>
    public static ScorerDef MultiScorer(IReadOnlyList<ScorerDef> scorers, ScoreReducer reducer)
    {
        ArgumentNullException.ThrowIfNull(scorers);
        ArgumentNullException.ThrowIfNull(reducer);
        var metrics = scorers.Count > 0 ? scorers[0].Metrics : [];
        return new("multi_scorer", async (state, target, cancellationToken) =>
        {
            var results = await Task.WhenAll(scorers.Select(scorer => scorer.Score(state, target, cancellationToken))).ConfigureAwait(false);
            var resolved = results.Where(score => score is not null).ToList();
            return resolved.Count == 0 ? Score.Unscored(reason: ScoreReason.ScoringFailed) : reducer(resolved);
        }, metrics);
    }

    /// <summary>Port of <c>multi_scorer(scorers, reducer)</c> with the reducer given by its registry name (see <see cref="Reducers.Create"/>).</summary>
    public static ScorerDef MultiScorer(IReadOnlyList<ScorerDef> scorers, string reducer) => MultiScorer(scorers, Reducers.Create(reducer));
}
