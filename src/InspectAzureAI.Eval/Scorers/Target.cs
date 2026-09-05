namespace InspectAzureAI.Eval.Scorers;

/// <summary>Port of <c>scorer/_target.py</c> <c>Target</c>: one or more strings; <see cref="Text"/> concatenates them like Python's <c>"".join</c>.</summary>
public sealed record Target(IReadOnlyList<string> Values)
{
    public Target(string value) : this([value])
    {
    }

    public static readonly Target Empty = new("");

    public string Text => string.Concat(Values);

    public int Count => Values.Count;

    public string this[int index] => Values[index];

    public static implicit operator Target(string value) => new(value);

    public static implicit operator Target(string[] values) => new((IReadOnlyList<string>)values);

    public bool Equals(Target? other) => other is not null && Values.SequenceEqual(other.Values, StringComparer.Ordinal);

    public override int GetHashCode() => Values.Count;
}
