namespace InspectAzureAI.Eval.Scorers;

/// <summary>
/// The <c>StrEnum</c> form of <c>frequency(categories)</c> / <c>categorical(categories)</c> (<c>scorer/_metrics/categorical.py</c>
/// <c>_category_names</c>). Python reads each member's <c>.value</c>; a C# enum carries no string value, so the label of a
/// member is its name lower-cased (the <c>StrEnum</c> + <c>auto()</c> convention) unless a <c>label</c> selector is given.
/// Members are listed in declaration (value) order, as Python iterates the enum.
/// </summary>
public static partial class Metrics
{
    /// <summary>Port of <c>_category_names</c> for an enum: every member's label (see <see cref="CategoryName{TEnum}(TEnum)"/>).</summary>
    public static IReadOnlyList<string> CategoryNames<TEnum>(Func<TEnum, string>? label = null)
        where TEnum : struct, Enum
    {
        var resolve = label ?? (member => CategoryName(member));
        return Enum.GetValues<TEnum>().Select(resolve).ToList();
    }

    /// <summary>The score-value label of an enum member: its name lower-cased (<c>Verdict.Yes</c> → <c>"yes"</c>); use it when building the <see cref="Score"/> too.</summary>
    public static string CategoryName<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        value.ToString().ToLowerInvariant();

    /// <summary>Port of <c>frequency(categories=SomeStrEnum, normalize)</c>: <see cref="Frequency(IEnumerable{string}?, bool)"/> over <see cref="CategoryNames{TEnum}(Func{TEnum, string}?)"/>.</summary>
    public static MetricDef Frequency<TEnum>(Func<TEnum, string>? label = null, bool normalize = true)
        where TEnum : struct, Enum =>
        Frequency(CategoryNames(label), normalize);

    /// <summary>Port of <c>categorical(SomeStrEnum)</c>: <c>[frequency(categories)]</c> for the enum's members.</summary>
    public static IReadOnlyList<MetricDef> Categorical<TEnum>(Func<TEnum, string>? label = null)
        where TEnum : struct, Enum =>
        Categorical(CategoryNames(label));
}
