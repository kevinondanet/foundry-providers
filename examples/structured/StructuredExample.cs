using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Structured;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/structured.py</c> as an <see cref="IExample"/>: the four tasks of <see cref="StructuredTasks"/>
/// against either the scripted <see cref="FakeStructuredModel"/> (<c>--fake</c>) or a Foundry deployment.
/// <c>rgb_color</c> (the default task) runs on both Foundry routes, which forward the response schema as
/// <c>response_format</c> / <c>output_format</c>. Deviation: <c>rgb_color_regex</c>, <c>rgb_color_choice</c> and
/// <c>rgb_color_grammar</c> are not runnable on Foundry (they need a vLLM or SGLang server) and are skipped under
/// <c>--fake</c> with a prerequisite error, since nothing can fake guided decoding; <c>-T reply=json|rgb|malformed</c>
/// chooses the shape of the scripted model's reply.
/// </summary>
public sealed class StructuredExample : IExample
{
    public string Name => "structured";

    public string Description => "Structured output: rgb_color asks for an RGB colour as JSON through GenerateConfig(response_schema=...) and validates it in a custom scorer; rgb_color_regex/choice/grammar use vLLM/SGLang guided decoding through extra_body (not runnable on Foundry)";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(StructuredTasks.RgbColorName, ctx => StructuredTasks.RgbColor() with { Sandbox = ctx.Sandbox }, "the RGB colour of white and black as JSON, through a response schema (json_schema(Color))"),
        new ExampleTask(StructuredTasks.RgbColorRegexName, ctx => Guided(ctx, StructuredTasks.RgbColorRegexName, StructuredTasks.RgbColorRegex), "the same through vLLM guided_regex / SGLang regex (not runnable on Foundry)"),
        new ExampleTask(StructuredTasks.RgbColorChoiceName, ctx => Guided(ctx, StructuredTasks.RgbColorChoiceName, StructuredTasks.RgbColorChoice), "the same through vLLM guided_choice (not runnable on Foundry)"),
        new ExampleTask(StructuredTasks.RgbColorGrammarName, ctx => Guided(ctx, StructuredTasks.RgbColorGrammarName, StructuredTasks.RgbColorGrammar), "the same through vLLM guided_grammar / SGLang ebnf (not runnable on Foundry)"),
    ];

    public ExampleDefaults Defaults { get; } = new(ModelHint: "a deployment with structured outputs (the gpt-4o / gpt-5 families on the models route, or claude-* on the anthropic route)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "rgb_color_regex, rgb_color_choice and rgb_color_grammar are not runnable on Foundry: they need a vLLM or SGLang server (guided decoding through extra_body) and this port has only the azureai and anthropic Foundry apis, so against a deployment they raise \"Unsupported provider: azureai\" (a PrerequisiteError in place of Python's ValueError, exit 2) exactly where the Python does for any other provider, and under --fake they are skipped with a prerequisite error because nothing can fake guided decoding. GenerateConfig.ExtraBody is carried on the config but never written into a Foundry request.",
        "Python's get_model() at task-build time has no equivalent under the inspectai CLI, which builds a task before it binds the model: the guided tasks read the api from the model the examples runner resolved, else the active sample's model, else INSPECT_EVAL_MODEL (api/name), else fail with Python's \"No model specified\" error.",
        "Color is a System.Text.Json record (required red/green/blue integers) in place of the pydantic model, and JsonSerializer.Deserialize<Color> stands in for Color.model_validate_json, so the \"Error parsing response: ...\" explanation carries a JsonException message rather than pydantic's ValidationError text. The response schema it produces (integer properties, all required, additionalProperties false) is the one json_schema(Color) gives.",
        "The scripted --fake model and -T reply=json|rgb|malformed are additions for running the demonstration offline (the Python example only runs through inspect eval): json is what a model honouring the response schema returns, rgb what guided decoding would force, malformed a sentence that shows both scorers' error branches.",
    ];

    public Model CreateFakeModel(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return FakeStructuredModel.Create(FakeStructuredModel.ParseReply(ctx.TaskArg("reply")));
    }

    /// <summary>No sandbox: the tasks have no tools.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>A guided-decoding task for the run's model; skipped under <c>--fake</c>, and "Unsupported provider" against a Foundry deployment, as the Python raises for any provider but vllm / sglang.</summary>
    private static EvalTask Guided(ExampleContext ctx, string name, Func<string, EvalTask> build)
    {
        if (ctx.Fake)
        {
            throw new PrerequisiteError($"{name} is skipped under --fake: it needs vLLM or SGLang guided decoding (extra_body), which neither the Foundry routes nor the scripted model support; run rgb_color instead");
        }

        return build(StructuredTasks.ModelApi(ctx.ResolvedModel)) with { Sandbox = ctx.Sandbox };
    }
}
