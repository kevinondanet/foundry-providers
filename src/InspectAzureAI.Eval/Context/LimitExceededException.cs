using System.Globalization;

namespace InspectAzureAI.Eval.Context;

/// <summary>Port of <c>util/_limit.py</c> <c>LimitExceededError</c>; <see cref="Type"/> is "message", "token", "time", "working", "turn" or "cost".</summary>
public sealed class LimitExceededException(string type, string limitStr, double value, string? message = null)
    : Exception(message ?? $"Exceeded {type} limit: {limitStr}")
{
    /// <summary>
    /// Port of the keyword form (<c>value=</c>, <c>limit=</c>, <c>source=</c>): the value compared, the limit applied and the
    /// <see cref="Context.Limit"/> that raised, which <see cref="LimitScope"/> uses to tell its own errors from an outer scope's.
    /// </summary>
    public LimitExceededException(string type, double value, double limit, string? message = null, Limit? source = null)
        : this(type, FormatLimit(limit), value, message ?? $"Exceeded {type} limit: {FormatLimit(limit)}")
    {
        Limit = limit;
        SourceLimit = source;
    }

    public string Type { get; } = type;

    /// <summary>The limit applied (null for errors built from a formatted limit only).</summary>
    public double? Limit { get; init; }

    /// <summary>Python's <c>source</c>: the scoped <see cref="Context.Limit"/> responsible for this error, when one was (named apart from <see cref="Exception.Source"/>).</summary>
    public Limit? SourceLimit { get; init; }

    /// <summary>The value formatted like Python's <c>value_str</c>.</summary>
    public string ValueStr => FormatLimit(Value);

    /// <summary>The limit formatted like Python (<c>f"{limit:,}"</c>), e.g. "1,000".</summary>
    public string LimitStr { get; } = limitStr;

    /// <summary>The value compared against the limit.</summary>
    public double Value { get; } = value;

    /// <summary>Port of <c>_format_float_or_int</c>: integral values with thousands separators, others with two decimals.</summary>
    public static string FormatLimit(double limit) =>
        double.IsFinite(limit) && Math.Abs(limit) < 1e15 && limit == Math.Floor(limit)
            ? ((long)limit).ToString("N0", CultureInfo.InvariantCulture)
            : limit.ToString("N2", CultureInfo.InvariantCulture);
}
