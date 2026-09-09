using System.Globalization;
using System.Text.RegularExpressions;

namespace InspectAzureAI.Eval.Sandbox.Docker.Compose;

/// <summary>
/// Port of <c>util/_sandbox/docker/service.py</c>: how long the healthchecks of a compose file can take
/// (the bound <c>compose up --wait</c> is given) and Go-style duration parsing (<c>1h30m</c>, <c>1.5s</c>).
/// </summary>
public static partial class ComposeHealthchecks
{
    private static readonly IReadOnlyDictionary<string, long> DurationUnits = new Dictionary<string, long>(StringComparer.Ordinal)
    {
        ["ns"] = 1,
        ["us"] = 1_000,
        ["µs"] = 1_000, // U+00B5 micro sign
        ["μs"] = 1_000, // U+03BC Greek mu (both accepted by Go's ParseDuration)
        ["ms"] = 1_000_000,
        ["s"] = 1_000_000_000,
        ["m"] = 60_000_000_000,
        ["h"] = 3_600_000_000_000,
    };

    /// <summary>Port of <c>services_healthcheck_time</c>: the longest healthcheck schedule among the services, in seconds.</summary>
    public static int ServicesHealthcheckTime(IEnumerable<ComposeService> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var max = 0;
        foreach (var service in services)
        {
            max = Math.Max(max, ServiceHealthcheckTime(service));
        }

        return max;
    }

    /// <summary>
    /// Port of <c>service_healthcheck_time</c>: <c>start_period + [timeout] + retries * (interval + timeout)</c>
    /// rounded up, with Docker's defaults (start_period 0s, retries 3, interval 30s, timeout 30s). A probe
    /// that starts just inside the start period runs for a further <c>timeout</c> uncounted, so that tail is
    /// included whenever a start period is configured.
    /// </summary>
    public static int ServiceHealthcheckTime(ComposeService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var healthcheck = service.Healthcheck;
        if (healthcheck is null)
        {
            return 0;
        }

        var startPeriod = ParseDuration(healthcheck.StartPeriod ?? "0s").TotalSeconds;
        var retries = healthcheck.Retries ?? 3;
        var interval = ParseDuration(healthcheck.Interval ?? "30s").TotalSeconds;
        var timeout = ParseDuration(healthcheck.Timeout ?? "30s").TotalSeconds;
        var graceBoundaryProbe = startPeriod > 0 ? timeout : 0.0;
        var total = startPeriod + graceBoundaryProbe + retries * (interval + timeout);
        return (int)Math.Ceiling(total);
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?|\.\d+)(ms|ns|us|µs|μs|h|m|s)")]
    private static partial Regex DurationComponent();

    [GeneratedRegex(@"^(?:(?:\d+(?:\.\d+)?|\.\d+)(?:ms|ns|us|µs|μs|h|m|s))+$")]
    private static partial Regex Duration();

    /// <summary>Port of <c>parse_duration</c>: a compose duration string; an empty string is zero, unparseable text is a <see cref="FormatException"/>.</summary>
    public static TimeSpan ParseDuration(string duration)
    {
        ArgumentNullException.ThrowIfNull(duration);
        if (duration.Length == 0)
        {
            return TimeSpan.Zero;
        }

        var stripped = string.Concat(duration.Where(c => !char.IsWhiteSpace(c)));
        if (!Duration().IsMatch(stripped))
        {
            throw new FormatException($"Invalid duration format: {duration}");
        }

        double nanoseconds = 0;
        foreach (Match match in DurationComponent().Matches(stripped))
        {
            var number = double.Parse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            nanoseconds += number * DurationUnits[match.Groups[2].Value];
        }

        return TimeSpan.FromTicks((long)Math.Round(nanoseconds) / 100);
    }
}
