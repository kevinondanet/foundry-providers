using System.Globalization;

namespace InspectAzureAI.Eval.Context;

/// <summary>Port of <c>util/_limit.py</c> <c>LimitExceededError</c>; <see cref="Type"/> is "message", "token" or "time".</summary>
public sealed class LimitExceededException(string type, string limitStr, double value, string? message = null)
    : Exception(message ?? $"Exceeded {type} limit: {limitStr}")
{
    public string Type { get; } = type;

    /// <summary>The limit formatted like Python (<c>f"{limit:,}"</c>), e.g. "1,000".</summary>
    public string LimitStr { get; } = limitStr;

    /// <summary>The value compared against the limit.</summary>
    public double Value { get; } = value;

    /// <summary>
    /// Port of <c>LimitExceededError.source</c> (named to avoid <c>Exception.Source</c>): the scoped limit (an <c>Agents.LimitScope</c>) responsible for
    /// the error, so <c>run()</c>-style callers can tell their own limits from the sample's (null).
    /// </summary>
    public object? LimitSource { get; init; }

    /// <summary>Port of <c>_format_float_or_int</c>: integral values with thousands separators, others with two decimals.</summary>
    public static string FormatLimit(double limit) =>
        double.IsFinite(limit) && Math.Abs(limit) < 1e15 && limit == Math.Floor(limit)
            ? ((long)limit).ToString("N0", CultureInfo.InvariantCulture)
            : limit.ToString("N2", CultureInfo.InvariantCulture);
}
