using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Context.Input;

/// <summary>
/// Port of <c>util/_input/_types.py</c> <c>InputOutcome</c>: how an <c>ask_user</c> interaction concluded —
/// the user answered, explicitly declined, or the interaction was aborted (cancellation, end of input).
/// </summary>
public enum InputOutcome
{
    Accepted,
    Declined,
    Cancelled,
}

public static class InputOutcomes
{
    /// <summary>The Python literal (<c>accepted</c>, <c>declined</c>, <c>cancelled</c>) written to the log.</summary>
    public static string ToPython(this InputOutcome outcome) => outcome switch
    {
        InputOutcome.Accepted => "accepted",
        InputOutcome.Declined => "declined",
        InputOutcome.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown input outcome."),
    };
}

/// <summary>Port of <c>util/_input/_types.py</c> <c>InputRequest</c>: a structured question posted to the user via <see cref="InputHandlers.RequestInputAsync"/>.</summary>
public sealed record InputRequest(string Message, ElicitationSchema Schema);

/// <summary>
/// Port of <c>util/_input/_types.py</c> <c>InputResult</c>. <see cref="Content"/> is keyed by the schema's property
/// names with plain CLR values (string, long, double, bool, or a list of strings for a multi-select) when
/// <see cref="Outcome"/> is <see cref="InputOutcome.Accepted"/>; otherwise null.
/// </summary>
public sealed record InputResult(InputOutcome Outcome, IReadOnlyDictionary<string, object?>? Content = null)
{
    public static InputResult Accepted(IReadOnlyDictionary<string, object?> content) => new(InputOutcome.Accepted, content);

    public static InputResult Declined() => new(InputOutcome.Declined);

    public static InputResult Cancelled() => new(InputOutcome.Cancelled);

    /// <summary>Port of <c>to_json_str_safe(result.content or {})</c>: the answer as a compact JSON object string.</summary>
    public string ContentJson() => ContentToJson(Content ?? new Dictionary<string, object?>()).ToJsonString();

    /// <summary>The answer as a <see cref="JsonObject"/> (an empty object when there is no content).</summary>
    public static JsonObject ContentToJson(IReadOnlyDictionary<string, object?> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var obj = new JsonObject();
        foreach (var pair in content)
        {
            obj[pair.Key] = ToNode(pair.Value);
        }

        return obj;
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        decimal m => JsonValue.Create(m),
        System.Collections.IEnumerable items => new JsonArray(items.Cast<object?>().Select(ToNode).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>Python's <c>str(value)</c> for the content values, used for the event's synthesized text.</summary>
    internal static string PythonText(object? value)
    {
        switch (value)
        {
            case null:
                return "None";
            case bool b:
                return b ? "True" : "False";
            case string s:
                return s;
            case double d:
                return PythonFloat(d);
            case float f:
                return PythonFloat(f);
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            case System.Collections.IEnumerable items:
            {
                var text = new StringBuilder("[");
                var first = true;
                foreach (var item in items)
                {
                    if (!first)
                    {
                        text.Append(", ");
                    }

                    first = false;
                    text.Append(item is string s ? $"'{s}'" : PythonText(item));
                }

                return text.Append(']').ToString();
            }

            default:
                return value.ToString() ?? "";
        }
    }

    /// <summary>Python's <c>str(float)</c>: integral values keep a trailing <c>.0</c>.</summary>
    internal static string PythonFloat(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') || text.Contains('e') ? text : text + ".0";
    }
}
