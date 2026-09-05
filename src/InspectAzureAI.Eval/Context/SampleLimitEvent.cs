namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>event/_sample_limit.py</c> <c>SampleLimitEvent</c>: the sample was unable to finish processing due
/// to a limit. <see cref="Type"/> is "message", "time", "working", "token", "turn", "cost", "operator" or "custom";
/// <see cref="Limit"/> is the limit value when there is one.
/// </summary>
public sealed record SampleLimitEvent(string Type, string Message, double? Limit = null) : TranscriptEvent
{
    public override string Event => "sample_limit";
}
