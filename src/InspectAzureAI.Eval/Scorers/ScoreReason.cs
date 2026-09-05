namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// Port of <c>ScoreReason</c> (<c>scorer/_metric.py</c>): the standard machine-readable reasons for an abnormal
/// score. The first group attributes the failure to the model under test, the second to the measurement
/// instrument. Any other string is also a legal <see cref="Score.Reason"/>; the detail behind a coarse value
/// belongs in <see cref="Score.Explanation"/>.
/// </summary>
public static class ScoreReason
{
    /// <summary>The output was unparseable or violated the requested format.</summary>
    public const string InvalidResponseFormat = "invalid_response_format";

    /// <summary>The model refused to answer.</summary>
    public const string Refusal = "refusal";

    /// <summary>The completion was empty.</summary>
    public const string NoResponse = "no_response";

    /// <summary>The grader model failed (unparseable verdict, refusal, schema mismatch).</summary>
    public const string GraderFailed = "grader_failed";

    /// <summary>The scorer could not run (missing logprobs, invalid config, no subscores).</summary>
    public const string ScoringFailed = "scoring_failed";

    /// <summary>Every standard reason, in the order Python declares them.</summary>
    public static IReadOnlyList<string> All { get; } = [InvalidResponseFormat, Refusal, NoResponse, GraderFailed, ScoringFailed];
}
