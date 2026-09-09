using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.Reasoning;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/reasoning.py</c> as an <see cref="IExample"/>: the <c>reasoning</c> task
/// (<see cref="ReasoningTask"/>) against either the scripted <see cref="FakeReasoningModel"/> (<c>--fake</c>) or a
/// reasoning-capable Foundry deployment. <c>--display conversation</c> prints every turn as it happens, reasoning
/// items included (between <c>&lt;think&gt;</c> markers), which is the point of the demonstration; the log keeps them
/// as <c>ContentReasoning</c> items of the assistant messages. Deviation: the scripted model is an addition for the
/// offline run; the Python example only runs through <c>inspect eval</c>.
/// </summary>
public sealed class ReasoningExample : IExample
{
    public string Name => "reasoning";

    public string Description => "Reasoning: a react agent with a validate() tool under GenerateConfig(reasoning_effort=\"medium\", reasoning_tokens=8192, max_tokens=16384), so reasoning content flows through the agent loop and the log";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(ReasoningTask.TaskName, Build, "two algebra problems, each to be reasoned about and then validated with the validate() tool"),
    ];

    public ExampleDefaults Defaults { get; } = new(ModelHint: "a reasoning-capable deployment (gpt-5.4-mini on the models route, claude-* on the anthropic route, or gpt-5.6-* on the responses route, which is auto-selected for those names; --route models reproduces their 400 for function tools combined with reasoning_effort on /chat/completions)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The scripted --fake model is an addition for running the demonstration offline (the Python example only runs through inspect eval): it reasons about each problem in a ContentReasoning item, calls validate twice and submits, and reports reasoning tokens in its usage.",
        "The validate tool's docstring (its description and the answer argument's description) is carried by [Description] attributes, which is what this port's ToolDef.FromMethod reads in place of the docstring Python parses; the tool's name, schema and True result are the same.",
        "Which reasoning controls reach the model is provider-dependent, as in Python: on the models route reasoning_effort is sent to gpt-5 / o-series deployments (Azure usually withholds the reasoning text and reports only reasoning tokens), while on the anthropic route reasoning_tokens becomes the extended-thinking budget and the thinking blocks come back as ContentReasoning items. gpt-5.6-* deployments run on the responses route (auto-selected for those names, or --route responses), where reasoning_effort and function tools combine; --route models reproduces their 400.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => FakeReasoningModel.Create();

    /// <summary>No sandbox: the validate tool runs in-process.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    private static EvalTask Build(ExampleContext ctx) => ReasoningTask.Reasoning() with { Sandbox = ctx.Sandbox };
}
