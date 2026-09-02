namespace InspectAzureAI.Provider.Core;

/// <summary>Missing-prerequisite error (port of <c>PrerequisiteError</c> in <c>src/inspect_ai/_util/error.py</c>).</summary>
public sealed class PrerequisiteError(string message) : Exception(message);

/// <summary>
/// Stand-in for <c>azure.core.exceptions.ServiceResponseError</c> (the response could not be read,
/// e.g. a network timeout or a connection dropped mid-body). The .NET SDK surfaces these as
/// <see cref="IOException"/> / <see cref="TaskCanceledException"/> (possibly inside the
/// <see cref="AggregateException"/> its retry policy throws), which
/// <see cref="AzureAIModelApi.AsAzureError"/> wraps in this type so
/// <see cref="AzureAIModelApi.ShouldRetry"/> can classify them as transient exactly like Python does.
/// </summary>
public sealed class ServiceResponseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Media reference that was not materialised (port of <c>UnresolvedMediaError</c>, <c>src/inspect_ai/_util/images.py</c>).</summary>
public sealed class UnresolvedMediaError(string message) : ArgumentException(message);

/// <summary>
/// Result of <c>AzureAIModelApi.GenerateAsync</c> (port of the
/// <c>tuple[ModelOutput | Exception, ModelCall]</c> return). Exactly one of <see cref="Output"/> or
/// <see cref="Error"/> is set; a returned <see cref="Error"/> is terminal (the Python Model layer
/// wraps it in <c>ModelGenerateError</c> without retrying), whereas retryable failures are thrown.
/// </summary>
public sealed record GenerateResult(ModelOutput? Output, Exception? Error, ModelCall Call)
{
    /// <summary>The output, throwing the recorded error if generation failed.</summary>
    public ModelOutput OutputOrThrow() => Output ?? throw Error!;
}
