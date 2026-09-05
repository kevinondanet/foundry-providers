using System.Globalization;

namespace InspectAzureAI.Eval.Model.Cache;

/// <summary>
/// Port of <c>model/_cache.py</c> <c>CachePolicy</c>: caching options for model generation. Python accepts
/// <c>cache=True</c> as shorthand for the default policy and <c>cache=False</c> for no caching; the implicit
/// conversion from <see cref="bool"/> reproduces that (<c>true</c> is <see cref="Default"/>, <c>false</c> is null).
/// </summary>
public sealed record CachePolicy
{
    /// <summary>The Python default expiry: one week.</summary>
    public const string DefaultExpiry = "1W";

    private static readonly IReadOnlyDictionary<string, string> NoScopes = new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly string? _expiry = DefaultExpiry;

    /// <summary>The policy <c>cache=True</c> selects: <see cref="DefaultExpiry"/>, per epoch, no scopes.</summary>
    public static CachePolicy Default { get; } = new();

    /// <summary>
    /// How long entries are kept, as a count followed by a unit: <c>s</c> seconds, <c>m</c> minutes, <c>h</c> hours,
    /// <c>D</c> days, <c>W</c> weeks, <c>M</c> months (30 days), <c>Y</c> years (365 days) — for example <c>12h</c> or
    /// <c>1W</c> (the default). An entry accessed after its expiry is cleared. Null caches indefinitely. Unlike
    /// Python, which rejects a malformed expiry at the first generate, an invalid value is an
    /// <see cref="ArgumentException"/> here, when the policy is built.
    /// </summary>
    public string? Expiry
    {
        get => _expiry;
        init
        {
            if (value is not null)
            {
                ParseExpiry(value);
            }

            _expiry = value;
        }
    }

    /// <summary>
    /// Cache responses separately for each epoch (default true): with several epochs the same call is made
    /// repeatedly and scorers aggregate across them. Set false when the call is not itself under test.
    /// </summary>
    public bool PerEpoch { get; init; } = true;

    /// <summary>Additional metadata included in the cache key, for finer-grained control over key generation.</summary>
    public IReadOnlyDictionary<string, string> Scopes { get; init; } = NoScopes;

    /// <summary>Seconds until an entry stored under this policy expires, or null for indefinite caching.</summary>
    public long? ExpirySeconds => _expiry is null ? null : ParseExpiry(_expiry);

    /// <summary>Port of <c>CachePolicy.from_string</c>: a policy with the given expiry, or null when it is not a valid expiry.</summary>
    public static CachePolicy? FromString(string expiry)
    {
        ArgumentNullException.ThrowIfNull(expiry);
        return TryParseExpiry(expiry, out _) ? new CachePolicy { Expiry = expiry } : null;
    }

    /// <summary>
    /// Port of <c>_parse_expiry</c>: the number of seconds in a period such as <c>12h</c> or <c>1W</c>. Like Python's
    /// <c>int()</c>, the count may carry a sign and surrounding whitespace (a negative period expires at once);
    /// anything else is an <see cref="ArgumentException"/> (<c>Invalid expiry: ...</c>).
    /// </summary>
    public static long ParseExpiry(string period)
    {
        ArgumentNullException.ThrowIfNull(period);
        return TryParseExpiry(period, out var seconds) ? seconds : throw new ArgumentException($"Invalid expiry: {period}", nameof(period));
    }

    /// <summary>Non-throwing form of <see cref="ParseExpiry"/>.</summary>
    public static bool TryParseExpiry(string? period, out long seconds)
    {
        seconds = 0;
        if (string.IsNullOrEmpty(period))
        {
            return false;
        }

        var factor = period[^1] switch
        {
            's' => 1L,
            'm' => 60L,
            'h' => 60L * 60,
            'D' => 60L * 60 * 24,
            'W' => 60L * 60 * 24 * 7,
            'M' => 60L * 60 * 24 * 30,
            'Y' => 60L * 60 * 24 * 365,
            _ => 0L,
        };
        if (factor == 0 || !long.TryParse(period[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return false;
        }

        try
        {
            seconds = checked(count * factor);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>Port of the <c>bool | CachePolicy</c> argument shape: <c>true</c> is <see cref="Default"/>, <c>false</c> is no caching.</summary>
    public static implicit operator CachePolicy?(bool enabled) => enabled ? Default : null;
}
