namespace InspectAzureAI.Eval.Model;

/// <summary>
/// Port of <c>model/_retry.py</c> <c>model_retry_config</c> with bounded defaults: N retries are N+1
/// attempts; <see cref="Timeout"/> caps the whole retry budget; backoff is exponential with full jitter
/// from <see cref="InitialBackoffSeconds"/> capped at <see cref="MaxBackoffSeconds"/> (Python: 30 minutes);
/// <see cref="Delay"/> is injectable so tests do not sleep.
/// </summary>
public sealed record ModelRetryOptions(
    int MaxRetries = 5,
    TimeSpan? Timeout = null,
    double InitialBackoffSeconds = 3,
    double MaxBackoffSeconds = 60,
    Func<TimeSpan, CancellationToken, Task>? Delay = null);
