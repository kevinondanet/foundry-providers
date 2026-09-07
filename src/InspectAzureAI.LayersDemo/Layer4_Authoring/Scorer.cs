// ============================================================================
//  LAYER 4: THE TASK-AUTHORING API (part 4: scorers and metrics)
//  Python: inspect_ai/scorer
//
//  A scorer is an async function `(state, target) -> Score`. It looks at the
//  finished TaskState (usually the last assistant message) and the sample's
//  target and returns a value plus an explanation. Metrics fold the scores of
//  every sample into a few numbers. Model-graded scorers call the model
//  layer exactly the way solvers do; the demo's two scorers are pure.
// ============================================================================
using inspect_ai._util.display;
using inspect_ai._util.registry;
using inspect_ai.solver;

namespace inspect_ai.scorer;

/// <summary>The sample's expected answer (Python: Target).</summary>
public sealed record Target(string Text);

/// <summary>Value is "C" (correct) or "I" (incorrect) for the demo's scorers.</summary>
public sealed record Score(string Value, string Answer, string Explanation)
{
    public bool IsCorrect => Value == "C";
}

public delegate Task<Score> Scorer(TaskState state, Target target);

/// <summary>Python's `@scorer` decorator.</summary>
public sealed class ScorerAttribute(string name) : RegistryAttribute(RegistryType.Scorer, name);

public static class Scorers
{
    /// <summary>Correct when the target text appears anywhere in the answer (Python: includes()).</summary>
    [Scorer("includes")]
    public static Scorer includes() => (state, target) =>
    {
        var answer = state.Output?.Message.Content ?? "";
        var hit = answer.Contains(target.Text, StringComparison.OrdinalIgnoreCase);
        Display.Step("L4 scorer", $"includes: '{target.Text}' in \"{answer}\" -> {(hit ? "C" : "I")}");
        return Task.FromResult(new Score(hit ? "C" : "I", answer, hit ? "target found in answer" : "target not found"));
    };

    /// <summary>Correct only when the whole answer equals the target (Python: exact()).</summary>
    [Scorer("exact")]
    public static Scorer exact() => (state, target) =>
    {
        var answer = (state.Output?.Message.Content ?? "").Trim();
        var hit = string.Equals(answer, target.Text, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new Score(hit ? "C" : "I", answer, hit ? "exact match" : "differs from target"));
    };
}

public static class Metrics
{
    /// <summary>Fraction of correct scores (Python: accuracy()).</summary>
    public static double accuracy(IReadOnlyList<Score> scores)
        => scores.Count == 0 ? 0 : scores.Count(s => s.IsCorrect) / (double)scores.Count;
}
