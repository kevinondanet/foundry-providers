using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Prefill;

/// <summary>
/// Port of <c>examples/prefill.py</c>: the <c>arithmetic_prefill</c> task ("Task demonstrating prefilling of
/// assistant messages"), its <c>prefill</c> solver and its <c>score_arithmetic</c> scorer. The solver replaces
/// the conversation with the question and a prefilled assistant message taken from the sample's metadata
/// (<c>1+1=</c>), so the model continues that message; the scorer reads the leading number of the completion.
/// </summary>
public static partial class ArithmeticPrefill
{
    public const string TaskName = "arithmetic_prefill";

    /// <summary>Port of <c>@task def arithmetic_prefill()</c>: three arithmetic questions, each with its <c>prefill</c> in the sample metadata.</summary>
    [Task(TaskName)]
    public static EvalTask ArithmeticPrefillTask() => new()
    {
        Name = TaskName,
        Dataset = new MemoryDataset(
        [
            new Sample("What is 1+1?") { Target = "2", Metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["prefill"] = "1+1=" } },
            new Sample("What is 5+7?") { Target = "12", Metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["prefill"] = "5+7=" } },
            new Sample("What is 3*4?") { Target = "12", Metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["prefill"] = "3*4=" } },
        ]),
        Solver = Solvers.Chain(Prefill(), Solvers.Generate()),
        Scorers = [ScoreArithmetic()],
    };

    /// <summary>
    /// Port of <c>@solver def prefill()</c>: "Solver that prefills assistant messages to guide the model's response."
    /// The state's messages become the user question followed by an assistant message holding the sample's
    /// <c>prefill</c> metadata; the following <c>generate()</c> sends that trailing assistant message to the model.
    /// </summary>
    public static Solver Prefill() => (state, _, _) =>
    {
        // Extract the question from the user prompt
        var question = state.UserPrompt.Content;

        // Create a new set of messages with a prefilled assistant message
        state.Messages =
        [
            new ChatMessageUser(question),
            new ChatMessageAssistant(PrefillOf(state)), // prefilled message
        ];

        return Task.FromResult(state);
    };

    /// <summary>
    /// Port of <c>@scorer(metrics=[accuracy()]) def score_arithmetic()</c>: "Simple scorer that extracts the number
    /// from the output and compares it to the target."
    /// </summary>
    public static ScorerDef ScoreArithmetic() => Scorers.Custom("score_arithmetic", ScoreAsync, Metrics.Accuracy());

    /// <summary>
    /// The scorer's <c>score</c>: 1.0 when the leading integer of the trimmed completion equals the target, 0.0 when it
    /// does not, and 0.0 with the explanation "Could not extract a numerical answer" when the completion does not
    /// start with a number (which is what a model that restates the question instead of continuing the prefill produces).
    /// </summary>
    public static Task<Score> ScoreAsync(TaskState state, Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        var output = state.Output.Completion.Trim();
        // Since we're using prefill, the output should start with a number
        // Extract the first number from the output
        var match = LeadingNumber().Match(output);
        if (match.Success)
        {
            var answer = match.Groups[1].Value;
            var correct = answer == target.Text;
            return Task.FromResult(new Score(correct ? 1.0 : 0.0) { Answer = output });
        }

        return Task.FromResult(new Score(0.0) { Answer = output, Explanation = "Could not extract a numerical answer" });
    }

    /// <summary>Python's <c>state.metadata["prefill"]</c>: the sample's prefill text (a missing key is a <see cref="KeyNotFoundException"/>, as Python's <c>KeyError</c> would fail the sample).</summary>
    private static string PrefillOf(TaskState state) =>
        state.Metadata["prefill"] as string ?? throw new InvalidOperationException("The sample's 'prefill' metadata must be a string.");

    /// <summary>Python's <c>re.match(r"^(\d+)", output)</c>.</summary>
    [GeneratedRegex(@"^(\d+)")]
    private static partial Regex LeadingNumber();
}
