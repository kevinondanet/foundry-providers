namespace InspectAzureAI.Eval.Model;

/// <summary>
/// Port of <c>AttemptTimeoutError</c> (<c>model/_model.py</c>): one provider attempt exceeded
/// <c>GenerateConfig.AttemptTimeout</c> and was abandoned. Always retried (classified as transient), so
/// <c>max_retries</c> and the retry <c>Timeout</c> budget decide termination.
/// </summary>
public sealed class AttemptTimeoutException(int? timeout) : Exception($"attempt_timeout '{timeout ?? 0}' exceeded.")
{
    /// <summary>The configured attempt timeout in seconds.</summary>
    public int? Timeout { get; } = timeout;
}

/// <summary>
/// Port of <c>StreamIdleTimeoutError</c> (<c>model/_model.py</c>): a streaming attempt delivered no chunk for
/// <c>GenerateConfig.StreamIdleTimeout</c> seconds and was abandoned. Retried exactly like
/// <see cref="AttemptTimeoutException"/> (see <c>design/stream-idle-timeout.md</c>).
/// </summary>
public sealed class StreamIdleTimeoutException(int? timeout) : Exception($"stream_idle_timeout '{timeout ?? 0}' exceeded (streaming response stalled).")
{
    /// <summary>The configured idle timeout in seconds.</summary>
    public int? Timeout { get; } = timeout;
}
