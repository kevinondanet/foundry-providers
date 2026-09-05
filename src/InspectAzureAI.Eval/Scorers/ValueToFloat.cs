using System.Globalization;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// Port of <c>scorer/_metric.py</c> <c>value_to_float</c>: maps a score value to a float for metrics and
/// reducers. The sentinel comparisons use Python <c>==</c> semantics (<c>True == 1.0</c>), which is why
/// they run before the numeric cast — a numeric custom sentinel must map rather than pass through.
/// </summary>
public static class ValueToFloat
{
    /// <summary>The <c>value_to_float()</c> default: C → 1, P → 0.5, I/N → 0.</summary>
    public static Func<ScoreValue, double> Default { get; } = Create();

    /// <summary>Port of <c>value_to_float(correct, incorrect, partial, noanswer)</c>; null arguments take the Python defaults.</summary>
    public static Func<ScoreValue, double> Create(
        ScoreValue? correct = null,
        ScoreValue? incorrect = null,
        ScoreValue? partial = null,
        ScoreValue? noanswer = null)
    {
        var correctValue = correct ?? ScoreConstants.Correct;
        var incorrectValue = incorrect ?? ScoreConstants.Incorrect;
        var partialValue = partial ?? ScoreConstants.Partial;
        var noanswerValue = noanswer ?? ScoreConstants.NoAnswer;

        return value =>
        {
            ArgumentNullException.ThrowIfNull(value);
            if (PythonEquals(value, correctValue))
            {
                return 1.0;
            }

            if (PythonEquals(value, partialValue))
            {
                return 0.5;
            }

            if (PythonEquals(value, incorrectValue) || PythonEquals(value, noanswerValue))
            {
                return 0.0;
            }

            switch (value)
            {
                case ScoreValue.Num num:
                    return num.Value;
                case ScoreValue.Bool b:
                    return b.Value ? 1.0 : 0.0;
                case ScoreValue.Str s:
                    var lowered = s.Value.ToLowerInvariant();
                    if (lowered is "yes" or "true")
                    {
                        return 1.0;
                    }

                    if (lowered is "no" or "false")
                    {
                        return 0.0;
                    }

                    if (TryParseFiniteNumber(lowered, out var parsed))
                    {
                        return parsed;
                    }

                    break;
            }

            ProviderLogger.Warning($"Unable to convert value to float: {Describe(value)}");
            return 0.0;
        };
    }

    /// <summary>Port of <c>is_finite_number</c> (<c>_util/text.py</c>): Python <c>float(s)</c> syntax, finite only.</summary>
    internal static bool TryParseFiniteNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>
    /// Python <c>==</c> over score values: strings compare ordinally, numbers and bools compare as numbers
    /// (<c>True == 1</c>), containers compare element-wise; NaN never equals anything.
    /// </summary>
    internal static bool PythonEquals(ScoreValue? a, ScoreValue? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return (a, b) switch
        {
            (ScoreValue.Str x, ScoreValue.Str y) => string.Equals(x.Value, y.Value, StringComparison.Ordinal),
            (ScoreValue.List x, ScoreValue.List y) => x.Items.Count == y.Items.Count && x.Items.Zip(y.Items).All(pair => PythonEquals(pair.First, pair.Second)),
            (ScoreValue.Dict x, ScoreValue.Dict y) =>
                x.Items.Count == y.Items.Count
                && x.Items.All(pair => y.Items.TryGetValue(pair.Key, out var other) && PythonEquals(pair.Value, other)),
            _ => TryNumber(a, out var na) && TryNumber(b, out var nb) && na == nb,
        };
    }

    /// <summary>Python's numeric view of a scalar: bools are 1/0, numbers themselves.</summary>
    internal static bool TryNumber(ScoreValue value, out double number)
    {
        switch (value)
        {
            case ScoreValue.Num n:
                number = n.Value;
                return true;
            case ScoreValue.Bool b:
                number = b.Value ? 1.0 : 0.0;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    /// <summary>Python <c>str(value)</c> for log messages: scalars as text, containers as JSON.</summary>
    internal static string Describe(ScoreValue? value) => value switch
    {
        null => "None",
        ScoreValue.List or ScoreValue.Dict => value.ToJson()?.ToJsonString() ?? "null",
        _ => value.Text,
    };
}
