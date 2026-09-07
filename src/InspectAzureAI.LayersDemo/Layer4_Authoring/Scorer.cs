// ============================================================================
//  LAYER 4: THE TASK-AUTHORING API (part 4: scorers and metrics)
//  Python: inspect_ai/scorer
//
//  A scorer is an async function `(state, target) -> Score`. It looks at the
//  finished TaskState (usually the last assistant message) and the sample's
//  target and returns a value plus an explanation. Metrics fold the scores of
//  every sample into a few numbers. Two of the demo's scorers are pure;
//  the third, model_graded_fact(), calls the model layer exactly the way
//  solvers do: layer 4 reaching down to layer 5, never the other way.
// ============================================================================
using System.Text.RegularExpressions;
using inspect_ai._util.display;
using inspect_ai._util.registry;
using inspect_ai.model;
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

    /// <summary>
    /// Ask a model whether the answer contains the facts in the target
    /// (Python: model_graded_fact()). The grader is the eval's own model,
    /// found through get_model() with no name; a task may pass a different
    /// one in Python, but the mechanism is the same: a scorer holds a Model
    /// and calls generate() on it, and layer 5 records the ModelEvent.
    /// </summary>
    [Scorer("model_graded_fact")]
    public static Scorer model_graded_fact() => async (state, target) =>
    {
        var answer = state.Output?.Message.Content ?? "";
        var grader = Models.get_model();   // -> layer 5: the active model, whichever provider it came from
        Display.Step("L4 scorer", $"model_graded_fact: asking {grader.Name} to compare the answer with the target");

        var prompt = $"""
            You are comparing a submitted answer to an expert answer on a given question. Here is the data:
            [BEGIN DATA]
            ************
            [Question]: {state.Input}
            ************
            [Expert]: {target.Text}
            ************
            [Submission]: {answer}
            ************
            [END DATA]

            Compare the factual content of the submitted answer with the expert answer. Ignore any differences in style, grammar, or punctuation.
            Does the submission contain the content in the expert answer? Numbers may be written with or without trailing zeros.
            First write one sentence of reasoning, then finish with 'GRADE: C' if it does or 'GRADE: I' if it does not.
            """;
        var verdict = await grader.Generate(new[] { ChatMessage.User(prompt) });   // no tools: a plain question
        var grade = GradePattern.Matches(verdict.Message.Content).LastOrDefault();
        var value = grade?.Groups[1].Value ?? "I";
        var explanation = grade is null ? "grader gave no GRADE line" : verdict.Message.Content.Trim();
        Display.Step("L4 scorer", $"model_graded_fact: grader said {value}");
        return new Score(value, answer, explanation);
    };

    private static readonly Regex GradePattern = new(@"GRADE:\s*([CI])\b", RegexOptions.IgnoreCase);
}

public static class Metrics
{
    /// <summary>Fraction of correct scores (Python: accuracy()).</summary>
    public static double accuracy(IReadOnlyList<Score> scores)
        => scores.Count == 0 ? 0 : scores.Count(s => s.IsCorrect) / (double)scores.Count;
}
