namespace InspectAzureAI.Provider.Core;

/// <summary>Retry classification kind (<c>Literal["rate_limit", "transient"]</c>).</summary>
public enum RetryKind
{
    Transient,
    RateLimit,
}

/// <summary>
/// Outcome of <c>should_retry()</c> (port of <c>RetryDecision</c> in <c>src/inspect_ai/model/_model.py</c>).
/// Truthiness in Python is <see cref="Retry"/>.
/// </summary>
public readonly record struct RetryDecision(bool Retry, RetryKind Kind = RetryKind.Transient, double? RetryAfter = null)
{
    /// <summary>Don't retry.</summary>
    public static RetryDecision No() => new(false);

    /// <summary>Retry as a transient error.</summary>
    public static RetryDecision Transient(double? retryAfter = null) => new(true, RetryKind.Transient, retryAfter);

    /// <summary>Retry as a rate-limit error.</summary>
    public static RetryDecision RateLimit(double? retryAfter = null) => new(true, RetryKind.RateLimit, retryAfter);

    public override string ToString() =>
        Retry ? $"retry ({(Kind == RetryKind.RateLimit ? "rate_limit" : "transient")}{(RetryAfter is null ? "" : $", retry_after={RetryAfter}")})" : "no retry";
}
