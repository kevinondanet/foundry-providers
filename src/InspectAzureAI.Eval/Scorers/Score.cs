using System.Globalization;
using InspectAzureAI.Eval.Context;

namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of <c>scorer/_metric.py</c> <c>Score</c>.</summary>
public sealed record Score(ScoreValue Value)
{
    public string? Answer { get; init; }

    public string? Explanation { get; init; }

    public string? Reason { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>Port of <c>Score.history</c>: edits applied to this score, oldest first.</summary>
    public IReadOnlyList<ScoreEdit> History { get; init; } = [];

    /// <summary>Port of <c>Score.unscored</c>: a NaN value that metrics and reducers skip.</summary>
    public static Score Unscored(string? reason = null, string? answer = null, string? explanation = null, IReadOnlyDictionary<string, object?>? metadata = null) =>
        new(ScoreValue.NaN) { Reason = reason, Answer = answer, Explanation = explanation, Metadata = metadata };

    public bool IsUnscored => Value.IsNaN;

    /// <summary>Port of <c>Score.text</c>.</summary>
    public string Text => Value.Text;

    /// <summary>Port of <c>Score.as_str</c>.</summary>
    public string AsStr() => Value.Text;

    /// <summary>Port of <c>Score.as_float</c>: numbers and bools directly, strings parsed (invariant), else <see cref="FormatException"/>.</summary>
    public double AsFloat() => Value switch
    {
        ScoreValue.Num n => n.Value,
        ScoreValue.Bool b => b.Value ? 1.0 : 0.0,
        ScoreValue.Str s => double.Parse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"Cannot convert a {Value.GetType().Name} score value to a float."),
    };

    /// <summary>Port of <c>Score.as_int</c>.</summary>
    public int AsInt() => (int)AsFloat();

    /// <summary>Port of <c>Score.as_bool</c>: Python truthiness of the scalar.</summary>
    public bool AsBool() => Value switch
    {
        ScoreValue.Bool b => b.Value,
        ScoreValue.Num n => n.Value != 0 && !double.IsNaN(n.Value),
        ScoreValue.Str s => s.Value.Length > 0,
        _ => throw new InvalidOperationException($"Cannot convert a {Value.GetType().Name} score value to a bool."),
    };
}
