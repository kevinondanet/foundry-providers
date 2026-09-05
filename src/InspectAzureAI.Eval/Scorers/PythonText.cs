using System.Globalization;
using System.Text.RegularExpressions;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// Python text and equality semantics the metrics and reducers rely on: <c>str(value)</c> for metadata values
/// (group names, cluster ids), <c>repr(float)</c>, and the hash/equality key Python's <c>Counter</c> and dict
/// use for scalars (<c>1 == 1.0 == True</c>, strings ordinal).
/// </summary>
internal static partial class PythonText
{
    /// <summary>Python <c>str(value)</c> for a plain metadata value: <c>None</c>, <c>True</c>/<c>False</c>, integers, float repr, strings as is.</summary>
    public static string Str(object? value) => value switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        string s => s,
        double d => FloatRepr(d),
        float f => FloatRepr(f),
        decimal m => FloatRepr((double)m),
        sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        ScoreValue scoreValue => ValueToFloat.Describe(scoreValue),
        _ => value.ToString() ?? "",
    };

    /// <summary>Python <c>repr(float)</c>: shortest round-trip digits, <c>.0</c> on integral values, exponent form outside 1e-4 ≤ |x| &lt; 1e16.</summary>
    public static string FloatRepr(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        // "R" is the shortest round-trippable form on .NET Core 3.0+; re-shape it to Python's repr rules.
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var digits = text;
        var exponent = 0;
        var e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            digits = text[..e];
            exponent = int.Parse(text[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }

        var dot = digits.IndexOf('.', StringComparison.Ordinal);
        var mantissa = dot >= 0 ? digits.Remove(dot, 1) : digits;
        var pointPosition = (dot >= 0 ? dot : digits.Length) + exponent;
        var leadingZeros = 0;
        while (leadingZeros < mantissa.Length - 1 && mantissa[leadingZeros] == '0')
        {
            leadingZeros++;
        }

        mantissa = mantissa[leadingZeros..];
        pointPosition -= leadingZeros;
        mantissa = mantissa.TrimEnd('0');
        if (mantissa.Length == 0)
        {
            mantissa = "0";
        }

        var decimalExponent = pointPosition - 1;
        var sign = value < 0 ? "-" : "";
        if (decimalExponent < -4 || decimalExponent >= 16)
        {
            var fraction = mantissa.Length > 1 ? "." + mantissa[1..] : "";
            var expSign = decimalExponent < 0 ? "-" : "+";
            return $"{sign}{mantissa[0]}{fraction}e{expSign}{Math.Abs(decimalExponent).ToString("00", CultureInfo.InvariantCulture)}";
        }

        if (pointPosition <= 0)
        {
            return $"{sign}0.{new string('0', -pointPosition)}{mantissa}";
        }

        if (pointPosition >= mantissa.Length)
        {
            return $"{sign}{mantissa}{new string('0', pointPosition - mantissa.Length)}.0";
        }

        return $"{sign}{mantissa[..pointPosition]}.{mantissa[pointPosition..]}";
    }

    /// <summary>Python <c>float(text)</c> syntax, finite only (underscores and surrounding whitespace allowed as in Python).</summary>
    public static bool TryParseFiniteFloat(string text, out double value)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('_') || trimmed.EndsWith('_') || trimmed.Contains("__", StringComparison.Ordinal))
        {
            value = 0;
            return false;
        }

        return ValueToFloat.TryParseFiniteNumber(trimmed.Replace("_", "", StringComparison.Ordinal), out value);
    }

    /// <summary>Python <c>str.split()</c>: runs of whitespace, no empty tokens.</summary>
    public static string[] SplitWhitespace(string text) => WhitespaceRun().Split(text.Trim()).Where(t => t.Length > 0).ToArray();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    /// <summary>
    /// Python hash/equality of a scalar score value: numbers and bools share a key (<c>1 == True == 1.0</c>), strings
    /// are ordinal. Containers are rejected, as Python's <c>Counter</c> rejects unhashable lists and dicts.
    /// </summary>
    public readonly record struct ScalarKey(bool IsNumber, double Number, string? Text)
    {
        public static ScalarKey Of(ScoreValue? value)
        {
            switch (value)
            {
                case null:
                    return new ScalarKey(false, 0, null);
                case ScoreValue.Str s:
                    return new ScalarKey(false, 0, s.Value);
                case ScoreValue.List or ScoreValue.Dict:
                    throw new ArgumentException($"Cannot use a {value.GetType().Name} score value as a hashable scalar.", nameof(value));
                default:
                    ValueToFloat.TryNumber(value, out var number);
                    return new ScalarKey(true, number, null);
            }
        }

        /// <summary>The key of a plain metadata value (numbers of every width and bools share the numeric key).</summary>
        public static ScalarKey OfObject(object? value) => value switch
        {
            null => new ScalarKey(false, 0, null),
            string s => new ScalarKey(false, 0, s),
            bool b => new ScalarKey(true, b ? 1 : 0, null),
            double or float or decimal or sbyte or byte or short or ushort or int or uint or long or ulong =>
                new ScalarKey(true, Convert.ToDouble(value, CultureInfo.InvariantCulture), null),
            _ => new ScalarKey(false, 0, "\0obj:" + value.GetType().FullName + ":" + value),
        };
    }
}
