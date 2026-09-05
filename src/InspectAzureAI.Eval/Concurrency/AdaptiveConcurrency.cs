using System.Globalization;

namespace InspectAzureAI.Eval.Concurrency;

/// <summary>
/// Port of <c>util/_concurrency.py</c> <c>AdaptiveConcurrency</c>: bounds and tuning for an adaptive concurrency
/// controller. <see cref="Min"/> / <see cref="Start"/> / <see cref="Max"/> bound the range the controller scales
/// within (defaults 10 / 20 / 100); the advanced fields tune the response curve. Python validates in the
/// pydantic constructor; here <see cref="Create"/> and <see cref="Parse"/> validate (and apply Python's implicit
/// clamping of an omitted <c>min</c> / <c>start</c>) and every consumer calls <see cref="Validate"/>, so an
/// instance built with an object initializer is rejected at first use rather than silently coerced.
/// </summary>
public sealed record AdaptiveConcurrency
{
    public const int DefaultMin = 10;

    public const int DefaultStart = 20;

    public const int DefaultMax = 100;

    /// <summary>Minimum concurrency (must be >= 1).</summary>
    public int Min { get; init; } = DefaultMin;

    /// <summary>Maximum concurrency.</summary>
    public int Max { get; init; } = DefaultMax;

    /// <summary>Starting concurrency (must be within [<see cref="Min"/>, <see cref="Max"/>]).</summary>
    public int Start { get; init; } = DefaultStart;

    /// <summary>Minimum seconds between scale-down cuts.</summary>
    public double CooldownSeconds { get; init; } = 15.0;

    /// <summary>Multiplicative factor applied to the limit on each cut (must be in (0, 1)).</summary>
    public double DecreaseFactor { get; init; } = 0.8;

    /// <summary>Steady-state additive growth per clean round, as a fraction of the current limit (must be in (0, 1]).</summary>
    public double ScaleUpPercent { get; init; } = 0.05;

    /// <summary>
    /// Port of the struct-form clamping in <c>AdaptiveConcurrency.parse_shorthand</c>: an omitted <c>min</c> follows
    /// <c>max</c> down below the default, and an omitted <c>start</c> is clamped into [min, max], so
    /// <c>Create(max: 8)</c> and <c>Create(min: 1, max: 15)</c> just work.
    /// </summary>
    public static AdaptiveConcurrency Create(
        int? min = null,
        int? start = null,
        int? max = null,
        double cooldownSeconds = 15.0,
        double decreaseFactor = 0.8,
        double scaleUpPercent = 0.05)
    {
        var maxValue = max ?? DefaultMax;
        var minValue = min ?? (maxValue < DefaultMin ? maxValue : DefaultMin);
        var startValue = start ?? Math.Max(minValue, Math.Min(DefaultStart, maxValue));
        return new AdaptiveConcurrency
        {
            Min = minValue,
            Start = startValue,
            Max = maxValue,
            CooldownSeconds = cooldownSeconds,
            DecreaseFactor = decreaseFactor,
            ScaleUpPercent = scaleUpPercent,
        }.Validate();
    }

    /// <summary>Parses the <c>"min-max"</c> or <c>"min-start-max"</c> shorthand.</summary>
    /// <exception cref="FormatException">The value is not two or three dash-separated integers.</exception>
    public static AdaptiveConcurrency Parse(string shorthand)
    {
        ArgumentNullException.ThrowIfNull(shorthand);
        var parts = shorthand.Split('-');
        var ints = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out ints[i]))
            {
                throw new FormatException(InvalidShorthand(shorthand));
            }
        }

        return ints.Length switch
        {
            2 => Create(min: ints[0], max: ints[1]),
            3 => Create(min: ints[0], start: ints[1], max: ints[2]),
            _ => throw new FormatException(InvalidShorthand(shorthand)),
        };
    }

    /// <summary>Port of <c>validate_bounds</c>; returns this instance so it can be chained.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound or tuning field is outside its documented range.</exception>
    public AdaptiveConcurrency Validate()
    {
        if (Min < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Min), Min, $"AdaptiveConcurrency min must be >= 1 (got {Min})");
        }

        if (Max < Min)
        {
            throw new ArgumentOutOfRangeException(nameof(Max), Max, $"AdaptiveConcurrency max ({Max}) must be >= min ({Min})");
        }

        if (Start < Min || Start > Max)
        {
            throw new ArgumentOutOfRangeException(nameof(Start), Start, $"AdaptiveConcurrency start ({Start}) must be within [min={Min}, max={Max}]");
        }

        if (!(double.IsFinite(CooldownSeconds) && CooldownSeconds >= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(CooldownSeconds), CooldownSeconds, $"AdaptiveConcurrency cooldown_seconds must be a finite value >= 0 (got {CooldownSeconds})");
        }

        if (!(DecreaseFactor > 0 && DecreaseFactor < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(DecreaseFactor), DecreaseFactor, $"AdaptiveConcurrency decrease_factor must be in (0, 1) (got {DecreaseFactor})");
        }

        if (!(ScaleUpPercent > 0 && ScaleUpPercent <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(ScaleUpPercent), ScaleUpPercent, $"AdaptiveConcurrency scale_up_percent must be in (0, 1] (got {ScaleUpPercent})");
        }

        return this;
    }

    private static string InvalidShorthand(string value) =>
        $"Invalid AdaptiveConcurrency shorthand '{value}': expected 'min-max' or 'min-start-max'";
}

/// <summary>
/// Port of the <c>adaptive_connections: bool | int | AdaptiveConcurrency | None</c> setting: <see cref="Disabled"/>
/// is Python's <c>False</c> (the explicit opt-out); <see cref="Default"/> is <c>True</c> / <c>None</c> (adaptive
/// with the default bounds); <see cref="WithMax"/> is the bare-integer <c>max</c> shorthand; <see cref="From"/>
/// carries a full <see cref="AdaptiveConcurrency"/>. A null setting anywhere means <see cref="Default"/>, exactly
/// as Python's <c>None</c> does.
/// </summary>
public sealed record AdaptiveConnections
{
    private AdaptiveConnections(bool enabled, AdaptiveConcurrency? config)
    {
        Enabled = enabled;
        Config = config;
    }

    /// <summary>False only for the explicit opt-out.</summary>
    public bool Enabled { get; }

    /// <summary>The explicit bounds, or null for the defaults.</summary>
    public AdaptiveConcurrency? Config { get; }

    /// <summary>Python <c>adaptive_connections=False</c>.</summary>
    public static AdaptiveConnections Disabled { get; } = new(false, null);

    /// <summary>Python <c>adaptive_connections=True</c> (or <c>None</c>).</summary>
    public static AdaptiveConnections Default { get; } = new(true, null);

    /// <summary>Python's integer shorthand: adaptive with <c>AdaptiveConcurrency(max=value)</c>.</summary>
    public static AdaptiveConnections WithMax(int max) => new(true, AdaptiveConcurrency.Create(max: max));

    /// <summary>Adaptive with explicit bounds.</summary>
    public static AdaptiveConnections From(AdaptiveConcurrency config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new AdaptiveConnections(true, config.Validate());
    }

    /// <summary>
    /// Port of <c>_parse_adaptive_connections_cli</c>: <c>true</c>/<c>yes</c>, <c>false</c>/<c>no</c>
    /// (case-insensitive), a bare integer (the <c>max</c> shorthand — <c>1</c> and <c>0</c> are integers, not
    /// booleans) or the <c>min-max</c> / <c>min-start-max</c> shorthand.
    /// </summary>
    /// <exception cref="FormatException">The value is none of those forms.</exception>
    public static AdaptiveConnections Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return Default;
        }

        if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return Disabled;
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max))
        {
            return WithMax(max);
        }

        if (trimmed.Contains('-', StringComparison.Ordinal))
        {
            try
            {
                return From(AdaptiveConcurrency.Parse(trimmed));
            }
            catch (FormatException ex)
            {
                throw new FormatException($"'{value}' is not a valid value for adaptive connections", ex);
            }
        }

        throw new FormatException($"'{value}' is not a valid value for adaptive connections");
    }

    /// <summary>Port of <c>resolve_adaptive</c>: the concrete bounds (the caller must have checked <see cref="Enabled"/>).</summary>
    /// <exception cref="InvalidOperationException">Adaptive connections are disabled.</exception>
    public AdaptiveConcurrency Resolve() =>
        Enabled ? Config ?? new AdaptiveConcurrency() : throw new InvalidOperationException("Adaptive connections are disabled; there are no bounds to resolve.");

    public override string ToString() => !Enabled ? "disabled" : Config is null ? "default" : $"{Config.Min}-{Config.Start}-{Config.Max}";
}
