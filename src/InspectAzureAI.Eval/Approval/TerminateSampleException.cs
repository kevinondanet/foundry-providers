namespace InspectAzureAI.Eval.Approval;

/// <summary>
/// Port of <c>_util/exception.py</c> <c>TerminateSampleError</c>: an approver asked for the sample to end. The runner
/// records it as an <c>operator</c> sample limit (the sample is still scored), not as an error.
/// </summary>
public sealed class TerminateSampleException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
