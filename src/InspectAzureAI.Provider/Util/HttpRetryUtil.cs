using System.Globalization;
using System.Text.RegularExpressions;
using Azure;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Provider.Util;

/// <summary>Port of the retry helpers in <c>src/inspect_ai/_util/http.py</c>.</summary>
public static partial class HttpRetryUtil
{
    private static readonly string[] RateLimitResetHeaders =
    [
        "x-ratelimit-reset-requests",
        "x-ratelimit-reset-tokens",
        "anthropic-ratelimit-requests-reset",
        "anthropic-ratelimit-tokens-reset",
        "anthropic-ratelimit-input-tokens-reset",
        "anthropic-ratelimit-output-tokens-reset",
    ];

    private static readonly Dictionary<string, double> DurationUnits = new()
    {
        ["ms"] = 0.001,
        ["s"] = 1.0,
        ["m"] = 60.0,
        ["h"] = 3600.0,
        ["d"] = 86400.0,
    };

    [GeneratedRegex(@"(\d+(?:\.\d+)?)(ms|s|m|h|d)")]
    private static partial Regex DurationRegex();

    /// <summary>Clock used for date-based headers (overridable in tests).</summary>
    public static Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Port of <c>is_retryable_http_status</c>: 408, 429 and 5xx.</summary>
    public static bool IsRetryableHttpStatus(int statusCode) => statusCode is 408 or 429 || (statusCode >= 500 && statusCode < 600);

    /// <summary>Port of <c>status_code_of</c>: the HTTP status carried by a <see cref="RequestFailedException"/>.</summary>
    public static int? StatusCodeOf(Exception ex) => ex is ProviderHttpException direct ? direct.Status : ex is RequestFailedException { Status: > 0 } rfe ? rfe.Status : null;

    /// <summary>Port of <c>parse_retry_after</c> over a case-insensitive header collection.</summary>
    public static double? ParseRetryAfter(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var lower = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in headers)
        {
            lower[k.ToLowerInvariant()] = v;
        }

        if (lower.TryGetValue("retry-after", out var retryAfter))
        {
            var seconds = ParseRetryAfterValue(retryAfter);
            if (seconds is not null)
            {
                return seconds;
            }
        }

        var resets = RateLimitResetHeaders
            .Select(h => lower.TryGetValue(h, out var raw) ? ParseRetryAfterValue(raw) : null)
            .Where(s => s is not null)
            .Select(s => s!.Value)
            .ToList();
        return resets.Count > 0 ? resets.Max() : null;
    }

    /// <summary>Port of <c>parse_retry_after_from_exception</c>: reads the response headers of a <see cref="RequestFailedException"/>.</summary>
    public static double? ParseRetryAfterFromException(Exception ex)
    {
        if (ex is ProviderHttpException direct) return ParseRetryAfter(direct.Headers);
        var response = (ex as RequestFailedException)?.GetRawResponse();
        if (response is null)
        {
            return null;
        }

        try
        {
            return ParseRetryAfter(response.Headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static RetryDecision RetryDecisionFor(Exception ex)
    {
        var status = StatusCodeOf(ex) ?? 0;
        var delay = ParseRetryAfterFromException(ex);
        return status == 429 ? RetryDecision.RateLimit(delay)
            : IsRetryableHttpStatus(status) || ex is HttpRequestException or IOException or ServiceResponseException
                ? RetryDecision.Transient(delay) : RetryDecision.No();
    }

    private static double? PositiveSeconds(double seconds) =>
        seconds > 0 && double.IsFinite(seconds) ? seconds : null;

    /// <summary>Port of <c>_parse_retry_after_value</c>: delta-seconds, duration string, HTTP-date or ISO 8601.</summary>
    public static double? ParseRetryAfterValue(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return PositiveSeconds(seconds);
        }

        var compact = value.Replace(" ", "");
        var matches = DurationRegex().Matches(compact);
        if (matches.Count > 0 && string.Concat(matches.Select(m => m.Value)) == compact)
        {
            var total = matches.Sum(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * DurationUnits[m.Groups[2].Value]);
            return PositiveSeconds(total);
        }

        if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var httpDate)
            || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out httpDate))
        {
            return PositiveSeconds((httpDate - UtcNow()).TotalSeconds);
        }

        return null;
    }
}
