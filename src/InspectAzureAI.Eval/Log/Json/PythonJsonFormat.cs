using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Log.Json;

/// <summary>
/// The scalar formats of a Python eval log: pydantic-core's float formatting, <c>datetime.isoformat()</c>
/// and pydantic's default datetime format, and the <c>NaN</c> / <c>Infinity</c> / <c>-Infinity</c> constants
/// that <c>ser_json_inf_nan="constants"</c> emits (see <c>design/nan-serialization.md</c>). The constants are not
/// strict JSON, so <see cref="Utf8JsonReader"/> cannot read them: <see cref="SanitizeNonFinite"/> rewrites bare
/// constants outside strings into sentinel strings before parsing, and every reader of a numeric position maps
/// the sentinels back (<see cref="TryNonFinite"/>).
/// </summary>
public static class PythonJsonFormat
{
    /// <summary>Sentinel string standing in for a bare <c>NaN</c> constant after <see cref="SanitizeNonFinite"/>.</summary>
    public const string NaNSentinel = "\u0001NaN";

    /// <summary>Sentinel string standing in for a bare <c>Infinity</c> constant.</summary>
    public const string PositiveInfinitySentinel = "\u0001Infinity";

    /// <summary>Sentinel string standing in for a bare <c>-Infinity</c> constant.</summary>
    public const string NegativeInfinitySentinel = "\u0001-Infinity";

    // the sentinels as JSON string literals (the control character escaped)
    private const string NaNSentinelJson = "\"\\u0001NaN\"";

    private const string PositiveInfinitySentinelJson = "\"\\u0001Infinity\"";

    private const string NegativeInfinitySentinelJson = "\"\\u0001-Infinity\"";

    /// <summary>
    /// Port of pydantic-core's float serialization (shortest round-trip digits; decimal notation for
    /// 1e-5 ≤ |x| &lt; 1e16, otherwise <c>d.ddde±X</c>; integral values keep a trailing <c>.0</c>) plus the
    /// non-finite constants.
    /// </summary>
    public static string FormatDouble(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        // "R" is the shortest round-trippable form; re-layout its digits with Python's thresholds
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = text[0] == '-';
        if (negative)
        {
            text = text[1..];
        }

        var exponent = 0;
        var exponentAt = text.IndexOfAny(['E', 'e']);
        var mantissa = text;
        if (exponentAt >= 0)
        {
            exponent = int.Parse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mantissa = text[..exponentAt];
        }

        var dot = mantissa.IndexOf('.');
        string digits;
        int integerLength;
        if (dot >= 0)
        {
            digits = mantissa[..dot] + mantissa[(dot + 1)..];
            integerLength = dot;
        }
        else
        {
            digits = mantissa;
            integerLength = mantissa.Length;
        }

        var leadingZeros = 0;
        while (leadingZeros < digits.Length - 1 && digits[leadingZeros] == '0')
        {
            leadingZeros++;
        }

        digits = digits[leadingZeros..].TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }

        // value = 0.<digits> × 10^pointPosition
        var pointPosition = integerLength - leadingZeros + exponent;
        var builder = new StringBuilder(negative ? "-" : "");
        if (pointPosition is > 0 and <= 16)
        {
            if (digits.Length <= pointPosition)
            {
                builder.Append(digits).Append('0', pointPosition - digits.Length).Append(".0");
            }
            else
            {
                builder.Append(digits, 0, pointPosition).Append('.').Append(digits, pointPosition, digits.Length - pointPosition);
            }
        }
        else if (pointPosition is > -5 and <= 0)
        {
            builder.Append("0.").Append('0', -pointPosition).Append(digits);
        }
        else
        {
            builder.Append(digits[0]);
            if (digits.Length > 1)
            {
                builder.Append('.').Append(digits, 1, digits.Length - 1);
            }

            var scientificExponent = pointPosition - 1;
            builder.Append('e').Append(scientificExponent < 0 ? "-" : "+").Append(Math.Abs(scientificExponent).ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>Port of <c>datetime_to_iso_format_safe</c>: UTC, microsecond precision, <c>+00:00</c> suffix.</summary>
    public static string FormatIso(DateTimeOffset value) => FormatUtc(value, "+00:00");

    /// <summary>Pydantic's default datetime serialization (used by <c>ProvenanceData.timestamp</c> and checkpoints): UTC with a <c>Z</c> suffix.</summary>
    public static string FormatPydantic(DateTimeOffset value) => FormatUtc(value, "Z");

    /// <summary>
    /// Port of the <c>UtcDatetime</c> validator: ISO 8601 with an offset or <c>Z</c> is normalised to UTC, a naive
    /// value is taken as UTC.
    /// </summary>
    public static DateTimeOffset ParseDateTime(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parsed = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc), TimeSpan.Zero);
    }

    /// <summary>The non-finite double a sentinel (or a quoted named literal) stands for, if it is one.</summary>
    public static bool TryNonFinite(string? text, out double value)
    {
        switch (text)
        {
            case NaNSentinel or "NaN":
                value = double.NaN;
                return true;
            case PositiveInfinitySentinel or "Infinity":
                value = double.PositiveInfinity;
                return true;
            case NegativeInfinitySentinel or "-Infinity":
                value = double.NegativeInfinity;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>Whether <paramref name="text"/> is one of the three sentinels.</summary>
    public static bool IsSentinel(string? text) => text is NaNSentinel or PositiveInfinitySentinel or NegativeInfinitySentinel;

    /// <summary>
    /// Rewrites bare <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c> tokens outside JSON strings into their
    /// sentinel strings so the text becomes strict JSON. Returns the input unchanged when it contains no constant.
    /// </summary>
    public static string SanitizeNonFinite(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!json.Contains("NaN", StringComparison.Ordinal) && !json.Contains("Infinity", StringComparison.Ordinal))
        {
            return json;
        }

        var builder = new StringBuilder(json.Length + 64);
        var inString = false;
        var i = 0;
        while (i < json.Length)
        {
            var c = json[i];
            if (inString)
            {
                builder.Append(c);
                if (c == '\\' && i + 1 < json.Length)
                {
                    builder.Append(json[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                i++;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                builder.Append(c);
                i++;
            }
            else if (Matches(json, i, "NaN"))
            {
                builder.Append(NaNSentinelJson);
                i += 3;
            }
            else if (Matches(json, i, "Infinity"))
            {
                builder.Append(PositiveInfinitySentinelJson);
                i += 8;
            }
            else if (Matches(json, i, "-Infinity"))
            {
                builder.Append(NegativeInfinitySentinelJson);
                i += 9;
            }
            else
            {
                builder.Append(c);
                i++;
            }
        }

        return builder.ToString();
    }

    /// <summary>Writes a double the way pydantic-core does, including the bare non-finite constants.</summary>
    public static void WriteDouble(Utf8JsonWriter writer, double value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteRawValue(FormatDouble(value), skipInputValidation: true);
    }

    /// <summary>
    /// Writes a <see cref="JsonNode"/> tree in Python form: doubles via <see cref="FormatDouble"/> (so a NaN
    /// value or a sentinel string read from a Python log comes out as the bare constant again); everything else as is.
    /// </summary>
    public static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var pair in obj)
                {
                    writer.WritePropertyName(pair.Key);
                    WriteNode(writer, pair.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteNode(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValue value:
                WriteValue(writer, value);
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// <see cref="WriteNode"/> for a <see cref="JsonElement"/> (a raw member cloned from a sanitized parse, such as the
    /// <c>changes</c> of a state or store event): a sentinel string comes out as the bare non-finite constant again,
    /// everything else is written as is.
    /// </summary>
    public static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String when IsSentinel(element.GetString()):
                TryNonFinite(element.GetString(), out var nonFinite);
                WriteDouble(writer, nonFinite);
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            if (element.ValueKind == JsonValueKind.String && IsSentinel(element.GetString()))
            {
                TryNonFinite(element.GetString(), out var nonFinite);
                WriteDouble(writer, nonFinite);
            }
            else
            {
                element.WriteTo(writer);
            }

            return;
        }

        if (value.TryGetValue<string>(out var text))
        {
            if (IsSentinel(text))
            {
                TryNonFinite(text, out var nonFinite);
                WriteDouble(writer, nonFinite);
            }
            else
            {
                writer.WriteStringValue(text);
            }

            return;
        }

        if (value.TryGetValue<double>(out var d))
        {
            WriteDouble(writer, d);
            return;
        }

        if (value.TryGetValue<float>(out var f))
        {
            WriteDouble(writer, f);
            return;
        }

        if (value.TryGetValue<DateTimeOffset>(out var dto))
        {
            writer.WriteStringValue(FormatIso(dto));
            return;
        }

        if (value.TryGetValue<DateTime>(out var dt))
        {
            writer.WriteStringValue(FormatIso(new DateTimeOffset(DateTime.SpecifyKind(dt, dt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dt.Kind))));
            return;
        }

        value.WriteTo(writer);
    }

    private static string FormatUtc(DateTimeOffset value, string suffix)
    {
        var utc = value.ToUniversalTime();
        var micros = utc.Ticks % TimeSpan.TicksPerSecond / 10;
        var builder = new StringBuilder(32);
        builder.Append(utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        if (micros != 0)
        {
            builder.Append('.').Append(micros.ToString("D6", CultureInfo.InvariantCulture));
        }

        return builder.Append(suffix).ToString();
    }

    private static bool Matches(string text, int index, string token) =>
        index + token.Length <= text.Length && string.CompareOrdinal(text, index, token, 0, token.Length) == 0;
}
