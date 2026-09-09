using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Surfer;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/surfer.py</c> as an <see cref="IExample"/>: the <c>surfer</c> task (<see cref="Surfer"/>)
/// against either a scripted model and a canned web search (<c>--fake</c>) or a Foundry deployment.
/// Deviation: the Python's bare <c>web_search()</c> only executes on the Anthropic route (Claude's own search);
/// <c>-T search_provider=tavily|exa</c> (an addition) names an external provider for the Azure chat route.
/// </summary>
public sealed class SurferExample : IExample
{
    /// <summary>The scripted model's name.</summary>
    public const string FakeModelName = "surfer-scripted";

    /// <summary>The query the scripted model searches for.</summary>
    public const string FakeQuery = "NHL scores last night";

    /// <summary>A safety net for <c>--fake</c>: the script needs 2 turns (search, then submit).</summary>
    public const int FakeMessageLimit = 20;

    public string Name => "surfer";

    public string Description => "Web surfer: a react agent with the web_search tool reports last night's NHL scores (plus the stateful web_surfer tool over StoreModel and generate_loop)";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(Surfer.TaskName, Build, "asks for last night's NHL scores with react(tools=[web_search()])"),
    ];

    public ExampleDefaults Defaults { get; } = new(
        Sandbox: "none",
        ModelHint: "a claude-* deployment on the Anthropic route (web_search() runs as Claude's own search), or any tool-calling deployment with -T search_provider=tavily|exa and TAVILY_API_KEY / EXA_API_KEY");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "web_search() with no provider (the Python call) is executed server-side only on the Anthropic route; on the Azure chat route the first search fails with \"No valid provider found\", so -T search_provider=tavily|exa (an addition) selects an external provider (its API key must be set).",
        "Under --fake the tool is web_search(\"tavily\") whose HTTP client answers every query with a canned Tavily response (FakeWebSearch); TAVILY_API_KEY is given a placeholder value when unset because the provider requires it.",
        "web_surfer is ported (WebSurfer.Create) with the Python's name, description, parameters and store-backed history, but as in Python the task does not use it; its optional webSearch factory parameter is an addition for testing offline.",
        "The scripted model searches once and submits the answer; a message limit of 20 guards the fake run.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), 8), FakeModelName));

    /// <summary>No sandbox: web search is an HTTP call from this process.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    private static EvalTask Build(ExampleContext ctx)
    {
        var provider = ctx.TaskArg("search_provider");
        var webSearch = ctx.Fake ? FakeWebSearch.Tool() : provider is null ? BuiltinTools.WebSearch() : BuiltinTools.WebSearch(provider);
        var task = Surfer.Build(webSearch);
        return ctx.Fake ? task with { MessageLimit = FakeMessageLimit } : task;
    }

    /// <summary>The scripted agent: one <c>web_search</c> call, then submit the search's answer.</summary>
    internal static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        var step = messages.Count(message => message is ChatMessageAssistant);
        var output = step == 0
            ? ScriptedTurn.ToolCall("web_search", new { query = FakeQuery }, text: "Let me look up last night's scores.").Output!
            : ScriptedTurn.ToolCall(Agents.DefaultSubmitName, new { answer = messages.OfType<ChatMessageTool>().LastOrDefault()?.Text.Trim() ?? "No results." }, text: "Here are the scores.").Output!;
        return output with { Model = FakeModelName };
    }
}
