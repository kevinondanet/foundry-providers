using System.Globalization;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// Port of <c>scorer/_metric.py</c> <c>Value</c>: a scalar (string, number, bool), a list of values or a
/// string-keyed dictionary. Scalars convert implicitly; <see cref="Text"/> is Python's <c>str(value)</c>.
/// </summary>
public abstract record ScoreValue
{
    public sealed record Str(string Value) : ScoreValue;

    public sealed record Num(double Value) : ScoreValue;

    public sealed record Bool(bool Value) : ScoreValue;

    public sealed record List(IReadOnlyList<ScoreValue> Items) : ScoreValue
    {
        public bool Equals(List? other) => other is not null && Items.SequenceEqual(other.Items);

        public override int GetHashCode() => Items.Count;
    }

    public sealed record Dict(IReadOnlyDictionary<string, ScoreValue?> Items) : ScoreValue
    {
        public bool Equals(Dict? other) =>
            other is not null
            && Items.Count == other.Items.Count
            && Items.All(pair => other.Items.TryGetValue(pair.Key, out var value) && Equals(pair.Value, value));

        public override int GetHashCode() => Items.Count;
    }

    /// <summary>The <c>float("nan")</c> sentinel of an unscored sample.</summary>
    public static ScoreValue NaN => new Num(double.NaN);

    public static implicit operator ScoreValue(string value) => new Str(value);

    public static implicit operator ScoreValue(double value) => new Num(value);

    public static implicit operator ScoreValue(int value) => new Num(value);

    public static implicit operator ScoreValue(bool value) => new Bool(value);

    /// <summary>True for the unscored sentinel (a NaN number).</summary>
    public bool IsNaN => this is Num { Value: double.NaN };

    /// <summary>Port of <c>Score.text</c> / <c>_as_scalar</c>: scalars only; lists and dictionaries throw.</summary>
    public string Text => this switch
    {
        Str s => s.Value,
        Num n => FormatNumber(n.Value),
        Bool b => b.Value ? "True" : "False",
        _ => throw new InvalidOperationException($"Cannot convert a {GetType().Name} score value to a scalar."),
    };

    public JsonNode? ToJson() => this switch
    {
        Str s => JsonValue.Create(s.Value),
        Num n => double.IsFinite(n.Value) ? JsonValue.Create(n.Value) : JsonValue.Create(n.Value.ToString(CultureInfo.InvariantCulture)),
        Bool b => JsonValue.Create(b.Value),
        List l => new JsonArray(l.Items.Select(i => i.ToJson()).ToArray()),
        Dict d => new JsonObject(d.Items.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value?.ToJson()))),
        _ => throw new InvalidOperationException($"Unknown score value {GetType().Name}."),
    };

    /// <summary>Inverse of <see cref="ToJson"/>; a null root is an error (dictionary entries may be null).</summary>
    public static ScoreValue FromJson(JsonNode? node) => node switch
    {
        null => throw new ArgumentException("A score value cannot be null.", nameof(node)),
        JsonArray array => new List(array.Select(item => item is null ? throw new ArgumentException("A score list cannot contain null.", nameof(node)) : FromJson(item)).ToArray()),
        JsonObject obj => new Dict(obj.ToDictionary(pair => pair.Key, pair => pair.Value is null ? null : FromJson(pair.Value), StringComparer.Ordinal)),
        JsonValue value => FromScalar(value),
        _ => throw new ArgumentException($"Unsupported JSON node {node.GetType().Name}.", nameof(node)),
    };

    private static ScoreValue FromScalar(JsonValue value)
    {
        if (value.TryGetValue<bool>(out var b))
        {
            return new Bool(b);
        }

        if (value.TryGetValue<double>(out var d))
        {
            return new Num(d);
        }

        if (value.TryGetValue<string>(out var s))
        {
            return s.ToLowerInvariant() switch
            {
                "nan" => NaN,
                "infinity" => new Num(double.PositiveInfinity),
                "-infinity" => new Num(double.NegativeInfinity),
                _ => new Str(s),
            };
        }

        throw new ArgumentException($"Unsupported JSON scalar {value.ToJsonString()}.", nameof(value));
    }

    private static string FormatNumber(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        return value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
    }
}
