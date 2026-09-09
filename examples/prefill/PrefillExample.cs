using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.Prefill;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/prefill.py</c> as an <see cref="IExample"/>: the <c>arithmetic_prefill</c> task
/// (<see cref="ArithmeticPrefill"/>) against either the scripted <see cref="FakePrefillModel"/> (<c>--fake</c>) or a
/// Foundry deployment. Both Foundry routes forward the trailing assistant message the <c>prefill</c> solver adds: the
/// anthropic route as a native prefill (the completion continues <c>1+1=</c>), the models route as an ordinary
/// assistant turn that OpenAI-family models may or may not continue, exactly as with Python's openai provider.
/// Deviation: <c>-T prose=true</c> makes the scripted model answer in a sentence so the scorer's
/// "Could not extract a numerical answer" branch can be seen offline; the Python example only runs through
/// <c>inspect eval</c>.
/// </summary>
public sealed class PrefillExample : IExample
{
    public string Name => "prefill";

    public string Description => "Assistant prefill: a solver puts a prefilled assistant message (\"1+1=\") after the question so the model continues it; a custom scorer reads the leading number of the completion";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(ArithmeticPrefill.TaskName, Build, "three arithmetic questions, each prefilled with its expression (\"1+1=\", \"5+7=\", \"3*4=\")"),
    ];

    public ExampleDefaults Defaults { get; } = new(ModelHint: "any deployment; a claude-* deployment on the anthropic route continues the prefill natively on models before Claude 4.6 (Claude 4.6 and later reject a trailing assistant message with HTTP 400)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The scripted --fake model is an addition for running the demonstration offline (the Python example only runs through inspect eval): it evaluates the prefilled expression and answers its continuation (\"2\"), as a native prefill does; -T prose=true makes it answer in a sentence (\"The answer is 2.\") so the scorer's \"Could not extract a numerical answer\" branch can be seen.",
        "Whether a live model continues the prefill is provider-dependent, as in Python: a claude-* deployment on the anthropic route treats the trailing assistant message as a native prefill (the Anthropic API rejects a prefill ending in whitespace), while OpenAI-family deployments on the models route receive it as an ordinary assistant turn and may restate the answer in prose, which the scorer marks 0.0.",
        "The prefill solver reads the sample's prefill metadata as a string (anything else fails the sample with an InvalidOperationException, where pydantic would reject the message content).",
    ];

    public Model CreateFakeModel(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return FakePrefillModel.Create(prose: ctx.TaskArgBool("prose", false));
    }

    /// <summary>No sandbox: the task has no tools.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    private static EvalTask Build(ExampleContext ctx) => ArithmeticPrefill.ArithmeticPrefillTask() with { Sandbox = ctx.Sandbox };
}
