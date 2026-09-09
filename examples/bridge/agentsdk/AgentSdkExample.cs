using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Maf;

namespace InspectAzureAI.Examples.Bridge.AgentSdk;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/bridge/agentsdk/agent.py</c> <c>web_research_agent</c>: an OpenAI Agents SDK
/// <c>Agent(name="SearchAssistant", instructions=..., tools=[WebSearchTool()])</c> run by <c>Runner.run</c> with
/// <c>RunConfig(model="inspect")</c> under the bridge. Deviation: the Agents SDK has no .NET release, so the agent
/// is a Microsoft Agent Framework <c>ChatClientAgent</c> named <c>SearchAssistant</c> over
/// <see cref="InspectChatClient"/> (<see cref="AgentFramework.Agent"/>), and the SDK's hosted
/// <c>WebSearchTool()</c> (which the Python bridge maps onto Inspect's <c>web_search</c>) is the engine's
/// <c>web_search</c> tool over an external provider (the Agent Framework bridge does not carry the internal
/// provider marker, so the model's own hosted search cannot be used).
/// </summary>
public static class WebResearchAgent
{
    /// <summary>The Python <c>@agent</c> function's name.</summary>
    public const string AgentName = "web_research_agent";

    /// <summary>The agent's docstring.</summary>
    public const string AgentDescription = "OpenAI Agents SDK search agent.";

    /// <summary>The SDK agent's <c>name</c>.</summary>
    public const string SdkAgentName = "SearchAssistant";

    /// <summary><c>web_research_agent()</c>; <paramref name="searchTool"/> replaces <c>WebSearchTool()</c> (null builds the engine's <c>web_search</c> over Tavily).</summary>
    public static AgentDef Create(ToolDef? searchTool = null)
    {
        var agent = AgentFramework.Agent(new MafAgentOptions
        {
            Name = SdkAgentName,
            Description = AgentDescription,
            Instructions = WebResearch.Instructions,
            Tools = [MafTools.FromToolDef(searchTool ?? WebResearch.LiveSearchTool(WebResearch.DefaultProvider))],
            // Runner.run ends when the agent produces a final output: no submit tool
            Submit = false,
        });
        // the Inspect agent keeps the Python function's name; the framework agent inside it is SearchAssistant
        return agent with { Name = AgentName };
    }
}

/// <summary>Port of <c>examples/bridge/agentsdk/task.py</c> <c>research</c>: <c>dataset.json</c>, the agent, <c>model_graded_fact()</c>.</summary>
public static class ResearchTask
{
    public const string TaskName = "research";

    /// <summary>Where the project copied <c>dataset.json</c> next to the assembly.</summary>
    public static string DefaultDatasetPath => Path.Combine(Path.GetDirectoryName(typeof(ResearchTask).Assembly.Location) ?? AppContext.BaseDirectory, "bridge", "agentsdk", "dataset.json");

    [Task(TaskName, "bridge=agentsdk")]
    public static EvalTask Research() => Build(DefaultDatasetPath, WebResearchAgent.Create());

    public static EvalTask Build(string datasetPath, AgentDef agent) => new()
    {
        Name = TaskName,
        Dataset = WebResearch.Dataset(datasetPath),
        Solver = Agents.AsSolver(agent),
        Scorers = [Scorers.ModelGradedFact()],
    };
}

/// <summary>
/// The <c>bridge/agentsdk</c> example for the runner: the <c>research</c> task with the Agent Framework stand-in
/// for the Agents SDK agent. <c>-T provider=exa</c> swaps the live search provider. Under <c>--fake</c> the model
/// and the search are scripted, and the same scripted model grades (<c>GRADE: C</c>).
/// </summary>
public sealed class AgentSdkExample : IExample
{
    public string Name => "bridge/agentsdk";

    public string Description => "An OpenAI Agents SDK-style SearchAssistant (Agent Framework stand-in) with a web_search tool, scored by model_graded_fact";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(ResearchTask.TaskName, Build, "three web-research questions answered with the web_search tool"),
    ];

    public ExampleDefaults Defaults { get; } = new(ModelHint: "any chat deployment (TAVILY_API_KEY for the live search)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The OpenAI Agents SDK (Agent, Runner, RunConfig, WebSearchTool) has no .NET release and this engine's bridge does not speak the Responses API the SDK uses: the agent is a Microsoft Agent Framework ChatClientAgent named SearchAssistant over InspectChatClient (the in-process agent_bridge), with the same instructions; the Inspect agent keeps the name web_research_agent.",
        "WebSearchTool() is OpenAI's hosted web search, which the Python bridge maps onto Inspect's web_search with the model's own provider. Here the tool is the engine's web_search over an external provider (Tavily by default, TAVILY_API_KEY; -T provider=exa for Exa): the Agent Framework bridge carries no internal-provider marker, so a deployment's server-side search is not used.",
        "Under --fake the search is a canned web_search tool (no key, no network) and one scripted model plays both the agent and the model_graded_fact grader; the Python example only runs live.",
        "Python resolves dataset.json relative to task.py; the [Task] method reads the copy next to the built assembly (bridge/agentsdk/dataset.json).",
        "The three bridge examples share the Python task name research, so in the one examples assembly `inspectai eval research` is ambiguous (the CLI disambiguates by assembly file, not by folder); `inspectai list tasks -F bridge=agentsdk` tells them apart and the examples runner (`-- bridge/agentsdk`) runs this one.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => FakeResearchModel.Create();

    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    private static EvalTask Build(ExampleContext ctx) =>
        ResearchTask.Build(ctx.DataPath("dataset.json"), WebResearchAgent.Create(WebResearch.SearchTool(ctx)));
}
