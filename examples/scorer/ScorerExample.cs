using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.ScorerDemo;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/scorer.py</c>: the <c>math</c> task over the Hugging Face <c>HuggingFaceH4/MATH-500</c> test split,
/// solved with a <c>prompt_template</c> that asks for a final <c>ANSWER:</c> line, and scored by the custom model-assisted
/// <c>expression_equivalence</c> scorer, which extracts the answer with <c>AnswerPattern.LINE</c> and asks the model under
/// evaluation (<c>get_model()</c>) whether it is equivalent to the target. Under <c>--fake</c> the dataset is a canned
/// five-row page of the real split served through <see cref="CannedHfHub"/> and the model is <see cref="FakeMathModel"/>.
/// </summary>
public sealed class ScorerExample : IExample
{
    /// <summary>The task name (<c>@task def math</c>).</summary>
    public const string TaskName = "math";

    /// <summary>The Hub dataset and split of <c>hf_dataset("HuggingFaceH4/MATH-500", split="test", ...)</c>.</summary>
    public const string DatasetPath = "HuggingFaceH4/MATH-500";

    public const string DatasetSplit = "test";

    /// <summary>The first five rows of the split, as the datasets-server returns them, for the offline run.</summary>
    public const string SampleRowsFile = "math500-test-sample.jsonl";

    /// <summary>Python's <c>FieldSpec(input="problem", target="solution")</c>.</summary>
    public static readonly FieldSpec Fields = new(Input: "problem", Target: "solution");

    /// <summary><c>PROMPT_TEMPLATE</c>, verbatim (after Python's <c>.strip()</c>).</summary>
    public const string PromptTemplate = """
        Solve the following math problem step by step. The last line
        of your response should be of the form ANSWER: $ANSWER (without
        quotes) where $ANSWER is the answer to the problem.

        {prompt}

        Remember to put your answer on its own line after "ANSWER:",
        and you do not need to use a \boxed command.
        """;

    /// <summary><c>EQUIVALENCE_TEMPLATE</c>, verbatim (after <c>.strip()</c>); the <c>%(expression1)s</c> / <c>%(expression2)s</c> placeholders are filled by <see cref="EquivalencePrompt"/>.</summary>
    public const string EquivalenceTemplate = """
        Look at the following two expressions (answers to a math problem)
        and judge whether they are equivalent. Only perform trivial
        simplifications

        Examples:

            Expression 1: $2x+3$
            Expression 2: $3+2x$

        Yes

            Expression 1: $x^2+2x+1$
            Expression 2: $y^2+2y+1$

        No

            Expression 1: 72 degrees
            Expression 2: 72

        Yes
        (give benefit of the doubt to units)
        ---

        YOUR TASK

        Respond with only "Yes" or "No" (without quotes). Do not include
        a rationale.

            Expression 1: %(expression1)s
            Expression 2: %(expression2)s
        """;

    /// <summary>Where the fake run caches the canned rows (never the user's real <c>hf_datasets</c> cache, which a live run reads).</summary>

    public string Name => "scorer";

    public string Description => "A custom model-assisted scorer: MATH-500 problems answered with an ANSWER: line, judged equivalent to the reference by the model itself";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, Build, "HuggingFaceH4/MATH-500 (test) with prompt_template + generate, scored by expression_equivalence; -T shuffle=false keeps the dataset order"),
    ];

    public ExampleDefaults Defaults { get; } = new();

    public IReadOnlyList<string> Deviations { get; } =
    [
        "hf_dataset reads the split through the Hugging Face datasets-server REST API (Datasets.Hf) rather than the datasets package; the shuffle permutation is .NET's, and the rows are cached under the inspect cache directory as rows.jsonl.",
        "--fake serves the first five rows of the real test split (math500-test-sample.jsonl) through a canned datasets-server handler and plays the model with a script: correct answers for rows 1, 2 and 5, a wrong answer for row 3 and no ANSWER: line for row 4; its equivalence judge answers Yes when the reference solution contains the extracted answer.",
        "The Python task argument shuffle=True is the -T shuffle=true|false task argument (and the shuffle parameter of the CLI task method).",
        "The EQUIVALENCE_TEMPLATE placeholders are filled by string replacement rather than Python's % formatting (there are no other % directives in the template).",
    ];

    /// <summary>The scripted model over the canned rows (<see cref="FakeMathModel"/>).</summary>
    public Model CreateFakeModel(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return FakeMathModel.Create(ctx.DataPath(SampleRowsFile));
    }

    /// <summary>No sandbox is involved.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>
    /// Port of <c>@task def math(shuffle=True)</c>: <c>hf_dataset("HuggingFaceH4/MATH-500", split="test", sample_fields=FieldSpec(input="problem", target="solution"), shuffle=shuffle)</c>
    /// solved by <c>prompt_template(PROMPT_TEMPLATE)</c> then <c>generate()</c>, scored by <c>expression_equivalence()</c>,
    /// with <c>GenerateConfig(temperature=0.5)</c>. Discoverable by the <c>inspectai</c> CLI as <c>math</c> (<c>-T shuffle=false</c>).
    /// </summary>
    [Task(TaskName)]
    public static EvalTask MathTask(bool shuffle = true) =>
        Build(Datasets.Hf(DatasetPath, DatasetSplit, sampleFields: Fields, shuffle: shuffle));

    /// <summary>The task over <paramref name="dataset"/> (the live split, or the canned page under <c>--fake</c>).</summary>
    public static EvalTask Build(IDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = dataset,
            Solver = Solvers.Chain(Solvers.PromptTemplate(PromptTemplate), Solvers.Generate()),
            Scorers = [ExpressionEquivalence()],
            Config = new GenerateConfig { Temperature = 0.5 },
        };
    }

    /// <summary>
    /// The canned dataset of <c>--fake</c>: the rows of <paramref name="rowsPath"/> loaded through the real <c>hf_dataset</c>
    /// loader against <see cref="CannedHfHub"/>, with a private cache directory so the user's Hub cache is untouched.
    /// </summary>
    public static IDataset SampleDataset(string rowsPath, bool shuffle = true)
    {
        using var hub = new CannedHfHub(DatasetPath, DatasetSplit, rowsPath);
        using var loader = hub.CreateLoader();
        return loader.LoadAsync(new HfDatasetRequest(DatasetPath, DatasetSplit)
        {
            SampleFields = Fields,
            Shuffle = shuffle,
            Cached = false,
        }).GetAwaiter().GetResult();
    }

    /// <summary>Port of <c>@scorer(metrics=[accuracy(), stderr()]) def expression_equivalence()</c>.</summary>
    public static ScorerDef ExpressionEquivalence() => Scorers.Custom("expression_equivalence", ScoreExpressionEquivalence, Metrics.Accuracy(), Metrics.Stderr());

    /// <summary>Port of <c>EQUIVALENCE_TEMPLATE % {"expression1": ..., "expression2": ...}</c>.</summary>
    public static string EquivalencePrompt(string expression1, string expression2)
    {
        ArgumentNullException.ThrowIfNull(expression1);
        ArgumentNullException.ThrowIfNull(expression2);
        return EquivalenceTemplate
            .Replace("%(expression1)s", expression1, StringComparison.Ordinal)
            .Replace("%(expression2)s", expression2, StringComparison.Ordinal);
    }

    private static EvalTask Build(ExampleContext ctx)
    {
        var shuffle = ctx.TaskArgBool("shuffle", true);
        return ctx.Fake ? Build(SampleDataset(ctx.DataPath(SampleRowsFile), shuffle)) : MathTask(shuffle);
    }

    /// <summary>
    /// The scorer: extract the answer with <c>AnswerPattern.LINE</c>; ask the model under evaluation whether it is
    /// equivalent to the target (<c>result.completion.lower() == "yes"</c>); otherwise INCORRECT with the
    /// "Answer not found in model output" explanation.
    /// </summary>
    private static async Task<Score> ScoreExpressionEquivalence(TaskState state, Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(target);

        // extract answer
        var match = Regex.Match(state.Output.Completion, AnswerPattern.Line);
        if (match.Success)
        {
            // ask the model to judge equivalence
            var answer = match.Groups[1].Value;
            var prompt = EquivalencePrompt(target.Text, answer);
            var result = await SampleContext.Require().ActiveModel.GenerateAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);

            // return the score
            var correct = result.Completion.ToLowerInvariant() == "yes";
            return new Score(correct ? ScoreConstants.Correct : ScoreConstants.Incorrect)
            {
                Answer = answer,
                Explanation = state.Output.Completion,
            };
        }

        return new Score(ScoreConstants.Incorrect)
        {
            Explanation = "Answer not found in model output: " + state.Output.Completion,
        };
    }
}
