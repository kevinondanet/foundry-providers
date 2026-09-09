using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Maf;

namespace InspectAzureAI.Examples.Bridge.Langchain;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/bridge/langchain/agent.py</c> <c>web_research_agent</c>: a web-research agent with a search
/// tool and the system prompt "You help users find information by searching the web.", run under the bridge so
/// every model call reaches the eval model. Deviation: LangChain/LangGraph has no C# form, so the agent is a
/// Microsoft Agent Framework <c>ChatClientAgent</c> over <see cref="InspectChatClient"/>
/// (<see cref="AgentFramework.Agent"/>): the framework's tool loop plays <c>create_agent</c>'s ReAct graph, its
/// session plays the <c>MemorySaver</c> checkpointer keyed by <c>thread_id</c>, and the sample's messages seed the
/// run as <c>messages_to_openai</c> + <c>convert_to_messages</c> do. <c>TavilySearch(max_results=...)</c> is the
/// engine's <c>web_search</c> tool over the Tavily provider with the same <c>max_results</c> option.
/// </summary>
public static class WebResearchAgent
{
    /// <summary>The Python <c>@agent</c> function's name.</summary>
    public const string AgentName = "web_research_agent";

    /// <summary>The agent's docstring.</summary>
    public const string AgentDescription = "LangChain Tavily search agent.";

    /// <summary>
    /// <c>web_research_agent(max_results=5)</c>. <paramref name="searchTool"/> replaces the Tavily search
    /// (the canned tool under <c>--fake</c>); null builds the engine's <c>web_search</c> over Tavily with
    /// <paramref name="maxResults"/>.
    /// </summary>
    public static AgentDef Create(int maxResults = 5, ToolDef? searchTool = null) =>
        AgentFramework.Agent(new MafAgentOptions
        {
            Name = AgentName,
            Description = AgentDescription,
            Instructions = WebResearch.Instructions,
            Tools = [MafTools.FromToolDef(searchTool ?? WebResearch.LiveSearchTool(WebResearch.DefaultProvider, maxResults))],
            // the answer is the agent's final message, as with LangChain: no submit tool
            Submit = false,
        });
}

/// <summary>Port of <c>examples/bridge/langchain/task.py</c> <c>research</c>: <c>dataset.json</c>, the agent, <c>model_graded_fact()</c>.</summary>
public static class ResearchTask
{
    public const string TaskName = "research";

    /// <summary>Where the project copied <c>dataset.json</c> next to the assembly.</summary>
    public static string DefaultDatasetPath => Path.Combine(Path.GetDirectoryName(typeof(ResearchTask).Assembly.Location) ?? AppContext.BaseDirectory, "bridge", "langchain", "dataset.json");

    [Task(TaskName, "bridge=langchain")]
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
/// The <c>bridge/langchain</c> example for the runner: the <c>research</c> task with the Agent Framework stand-in
/// for the LangChain agent. <c>-T max_results=N</c> is the agent's <c>max_results</c>; <c>-T provider=exa</c> swaps
/// the live search provider. Under <c>--fake</c> the model and the search are scripted, and the same scripted model
/// grades (<c>GRADE: C</c>).
/// </summary>
public sealed class LangchainExample : IExample
{
    public const string MaxResultsArg = "max_results";

    public string Name => "bridge/langchain";

    public string Description => "A LangChain-style web research agent (Agent Framework stand-in) with a Tavily web_search tool, scored by model_graded_fact";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(ResearchTask.TaskName, Build, "three web-research questions answered with the web_search tool"),
    ];

    public ExampleDefaults Defaults { get; } = new(ModelHint: "any chat deployment (TAVILY_API_KEY for the live search)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "LangChain, LangGraph, ChatOpenAI and MemorySaver have no C# form: the agent is a Microsoft Agent Framework ChatClientAgent over InspectChatClient (the in-process agent_bridge). Its tool loop stands in for create_agent's ReAct graph and its session for the MemorySaver/thread_id checkpointer; the transcript, limits and log are Inspect's as in Python.",
        "TavilySearch(max_results) is the engine's web_search tool over the Tavily provider with the same max_results option (TAVILY_API_KEY); -T provider=exa selects Exa instead. Tavily's tool returns a JSON results object to LangChain, this tool returns the provider's answer text with citations.",
        "Under --fake the search is a canned web_search tool (no key, no network) and one scripted model plays both the agent and the model_graded_fact grader; the Python example only runs live.",
        "Python resolves dataset.json relative to task.py; the [Task] method reads the copy next to the built assembly (bridge/langchain/dataset.json).",
        "The three bridge examples share the Python task name research, so in the one examples assembly `inspectai eval research` is ambiguous (the CLI disambiguates by assembly file, not by folder); `inspectai list tasks -F bridge=langchain` tells them apart and the examples runner (`-- bridge/langchain`) runs this one.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => FakeResearchModel.Create();

    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    private static EvalTask Build(ExampleContext ctx)
    {
        var maxResults = ctx.TaskArgInt(MaxResultsArg, 5);
        return ResearchTask.Build(ctx.DataPath("dataset.json"), WebResearchAgent.Create(maxResults, WebResearch.SearchTool(ctx, maxResults)));
    }
}
