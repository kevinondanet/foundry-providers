namespace InspectAzureAI.Eval.Sandbox;

/// <summary>Port of <c>util/_sandbox/events.py</c> <c>SandboxTimeoutError</c>: a sandbox command exceeded its timeout.</summary>
public sealed class SandboxTimeoutException(string message, string truncatedOutput) : TimeoutException(message)
{
    /// <summary>Output captured before the timeout fired (tool calls surface it to the model).</summary>
    public string TruncatedOutput { get; } = truncatedOutput;
}

/// <summary>
/// Port of <c>util/_sandbox/environment.py</c> <c>SandboxUnavailableError</c>: the provider could not run
/// the request at all (daemon down, container gone), as opposed to a command that ran and failed.
/// </summary>
public sealed class SandboxUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Port of <c>util/_sandbox/limits.py</c> <c>OutputLimitExceededError</c>: a sandbox read exceeded its size limit.</summary>
public sealed class OutputLimitExceededException(string limitDescription, string? truncatedOutput)
    : Exception($"The sandbox output stream limit of {limitDescription} was exceeded.")
{
    /// <summary>Human readable limit, e.g. "100 MiB".</summary>
    public string LimitDescription { get; } = limitDescription;

    /// <summary>Output kept up to the limit, when the operation produced any.</summary>
    public string? TruncatedOutput { get; } = truncatedOutput;
}
