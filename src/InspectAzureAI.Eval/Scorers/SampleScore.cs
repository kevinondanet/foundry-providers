namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of <c>scorer/_metric.py</c> <c>SampleScore</c>: a score together with the sample (and scorer) it came from.</summary>
public sealed record SampleScore(
    Score Score,
    object? SampleId = null,
    IReadOnlyDictionary<string, object?>? SampleMetadata = null,
    string? Scorer = null);
