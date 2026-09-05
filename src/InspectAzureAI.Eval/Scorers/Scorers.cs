namespace InspectAzureAI.Eval.Scorers;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The built-in scorers: ports of <c>includes</c> / <c>match</c> (<c>scorer/_match.py</c>), <c>pattern</c>
/// (<c>_pattern.py</c>) and <c>model_graded_qa</c> / <c>model_graded_fact</c> (<c>_model.py</c>), each
/// registered with <c>[accuracy(), stderr()]</c> as in Python.
/// </summary>
public static partial class Scorers
{
    private static readonly string[] MatchLocations = ["begin", "end", "any", "exact"];

    /// <summary>Port of <c>includes(ignore_case)</c>: the target text appears anywhere in the completion.</summary>
    public static ScorerDef Includes(bool ignoreCase = true) =>
        new("includes", MatchScorers.StrMatchScorer((value, target) =>
        {
            if (ignoreCase)
            {
                value = value.ToLowerInvariant();
                target = target.ToLowerInvariant();
            }

            return new MatchScorers.MatchResult(value, value.Contains(target, StringComparison.Ordinal));
        }), DefaultMetrics());

    /// <summary>Port of <c>match(location, ignore_case, numeric)</c>; <paramref name="location"/> is one of begin, end, any, exact.</summary>
    public static ScorerDef Match(string location = "end", bool ignoreCase = true, bool numeric = false)
    {
        if (!MatchLocations.Contains(location, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown match location '{location}' (expected one of {string.Join(", ", MatchLocations)}).", nameof(location));
        }

        return new("match", MatchScorers.StrMatchScorer((value, target) =>
            MatchScorers.MatchStr(value, target, location, ignoreCase, numeric: numeric)), DefaultMetrics());
    }

    /// <summary>A <c>match(location="exact")</c> scorer: the whole completion equals a target (modulo whitespace, case and enclosing punctuation).</summary>
    public static ScorerDef ExactMatch(bool ignoreCase = true) =>
        new("exact_match", MatchScorers.StrMatchScorer((value, target) =>
            MatchScorers.MatchStr(value, target, "exact", ignoreCase)), DefaultMetrics());

    /// <summary>Port of <c>pattern(pattern, ignore_case, match_all)</c>: extracts the answer with a regex.</summary>
    public static ScorerDef Pattern(string pattern, bool ignoreCase = true, bool matchAll = false)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return new("pattern", MatchScorers.Pattern(pattern, ignoreCase, matchAll), DefaultMetrics());
    }

    /// <summary>Port of <c>model_graded_qa</c>; <paramref name="model"/> null grades with the sample's active model at scoring time.</summary>
    public static ScorerDef ModelGradedQa(
        string? template = null,
        string? instructions = null,
        string? gradePattern = null,
        bool includeHistory = false,
        bool partialCredit = false,
        Model? model = null) =>
        new(
            "model_graded_qa",
            ModelGraded.Create(string.IsNullOrEmpty(template) ? ModelGraded.DefaultQaTemplate : template, instructions, gradePattern, includeHistory, partialCredit, model),
            DefaultMetrics());

    /// <summary>Port of <c>model_graded_fact</c>: <see cref="ModelGradedQa"/> with the expert-answer template.</summary>
    public static ScorerDef ModelGradedFact(
        string? template = null,
        string? instructions = null,
        string? gradePattern = null,
        bool includeHistory = false,
        bool partialCredit = false,
        Model? model = null) =>
        new(
            "model_graded_fact",
            ModelGraded.Create(string.IsNullOrEmpty(template) ? ModelGraded.DefaultFactTemplate : template, instructions, gradePattern, includeHistory, partialCredit, model),
            DefaultMetrics());

    /// <summary>The <c>@scorer(metrics=...)</c> decorator for a hand-written scorer.</summary>
    public static ScorerDef Custom(string name, Scorer scorer, params MetricDef[] metrics)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(scorer);
        ArgumentNullException.ThrowIfNull(metrics);
        return new(name, scorer, metrics);
    }

    private static IReadOnlyList<MetricDef> DefaultMetrics() => [Metrics.Accuracy(), Metrics.Stderr()];
}
