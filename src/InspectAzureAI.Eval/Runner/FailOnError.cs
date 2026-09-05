using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InspectAzureAI.Eval.Runner;

/// <summary>
/// Port of the <c>fail_on_error: bool | float</c> option: <see cref="Always"/> (<c>True</c>, the default) fails the
/// eval on the first sample error, <see cref="Never"/> (<c>False</c>) never fails it, and a numeric
/// <see cref="Threshold"/> fails it once the error count reaches a fraction of the total sample runs (a value
/// below 1) or an absolute count (a value of 1 or more). Converts implicitly from <c>bool</c>, <c>int</c> and
/// <c>double</c>; serialises as Python does (a JSON boolean or number).
/// </summary>
[JsonConverter(typeof(FailOnErrorJsonConverter))]
public readonly record struct FailOnError
{
    private readonly bool _isThreshold;

    private readonly bool _flag;

    private readonly double _threshold;

    private FailOnError(bool isThreshold, bool flag, double threshold)
    {
        _isThreshold = isThreshold;
        _flag = flag;
        _threshold = threshold;
    }

    /// <summary>Python <c>True</c>: fail the eval on any sample error.</summary>
    public static FailOnError Always { get; } = new(false, true, 0);

    /// <summary>Python <c>False</c>: never fail the eval on sample errors.</summary>
    public static FailOnError Never { get; } = new(false, false, 0);

    /// <summary>The raw numeric form: below 1 a fraction of the total sample runs, otherwise an absolute error count. Negative or non-finite values are rejected.</summary>
    public static FailOnError Threshold(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "fail_on_error must be a boolean or a non-negative number.");
        }

        return new FailOnError(true, false, value);
    }

    /// <summary>Fail once errors reach <paramref name="fraction"/> (strictly between 0 and 1) of the total sample runs.</summary>
    public static FailOnError Fraction(double fraction) =>
        fraction is > 0 and < 1
            ? Threshold(fraction)
            : throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "A fractional fail_on_error must be greater than 0 and less than 1.");

    /// <summary>Fail once <paramref name="count"/> (at least 1) sample runs have errored.</summary>
    public static FailOnError Count(int count) =>
        count >= 1
            ? Threshold(count)
            : throw new ArgumentOutOfRangeException(nameof(count), count, "An absolute fail_on_error count must be at least 1.");

    /// <summary>True for the numeric form.</summary>
    public bool IsThreshold => _isThreshold;

    /// <summary>The numeric threshold, or null for <see cref="Always"/> / <see cref="Never"/>.</summary>
    public double? ThresholdValue => _isThreshold ? _threshold : null;

    /// <summary>The boolean form, or null for a threshold.</summary>
    public bool? Flag => _isThreshold ? null : _flag;

    /// <summary>Port of <c>_is_fractional</c>: a threshold strictly between 0 and 1.</summary>
    public bool IsFractional => _isThreshold && _threshold is > 0 and < 1;

    /// <summary>
    /// Port of <c>_should_eval_fail</c>: whether <paramref name="errorCount"/> errors out of
    /// <paramref name="totalSamples"/> planned sample runs fail the eval under this policy.
    /// </summary>
    public bool ShouldFail(int errorCount, int totalSamples)
    {
        if (!_isThreshold)
        {
            return _flag && errorCount > 0;
        }

        return _threshold < 1
            ? errorCount >= _threshold * totalSamples
            : errorCount >= _threshold;
    }

    public static implicit operator FailOnError(bool value) => value ? Always : Never;

    public static implicit operator FailOnError(double value) => Threshold(value);

    public static implicit operator FailOnError(int value) => Threshold(value);

    /// <summary>Named alternative to the <c>bool</c> conversion.</summary>
    public static FailOnError FromBoolean(bool value) => value;

    /// <summary>Named alternative to the numeric conversions.</summary>
    public static FailOnError FromDouble(double value) => Threshold(value);

    /// <summary>Named alternative to the numeric conversions.</summary>
    public static FailOnError FromInt32(int value) => Threshold(value);

    public override string ToString() => _isThreshold
        ? _threshold.ToString(CultureInfo.InvariantCulture)
        : _flag ? "True" : "False";
}

/// <summary>Writes a <see cref="FailOnError"/> as Python does: <c>true</c>/<c>false</c>, or a number (integral thresholds without a fraction).</summary>
internal sealed class FailOnErrorJsonConverter : JsonConverter<FailOnError>
{
    public override FailOnError Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.True => FailOnError.Always,
        JsonTokenType.False => FailOnError.Never,
        JsonTokenType.Number => FailOnError.Threshold(reader.GetDouble()),
        _ => throw new JsonException($"fail_on_error must be a boolean or a number, not {reader.TokenType}."),
    };

    public override void Write(Utf8JsonWriter writer, FailOnError value, JsonSerializerOptions options)
    {
        if (value.ThresholdValue is { } threshold)
        {
            if (threshold == Math.Floor(threshold) && Math.Abs(threshold) < 1e15)
            {
                writer.WriteNumberValue((long)threshold);
            }
            else
            {
                writer.WriteNumberValue(threshold);
            }
        }
        else
        {
            writer.WriteBooleanValue(value.Flag == true);
        }
    }
}
