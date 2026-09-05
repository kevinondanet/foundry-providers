namespace InspectAzureAI.Eval.Runner.Scoring;

/// <summary>Port of <c>_eval/score.py</c> <c>ScoreAction</c>: what a scoring pass does with the scores a sample already has.</summary>
public enum ScoreAction
{
    /// <summary>Keep the existing scores, score events and results; the new scorers are added alongside them (Python <c>"append"</c>).</summary>
    Append,

    /// <summary>Replace the existing scores, score events, results and reductions with those of the new scorers (Python <c>"overwrite"</c>).</summary>
    Overwrite,
}
