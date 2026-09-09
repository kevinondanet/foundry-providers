using System.Reflection;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Examples.Bridge;
using InspectAzureAI.Examples.Bridge.Langchain;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Bridge.Langchain;

/// <summary>
/// Tests for the port of <c>examples/bridge/langchain</c> (<see cref="WebResearchAgent"/>, <see cref="ResearchTask"/>,
/// <see cref="LangchainExample"/>) and the shared <see cref="WebResearch"/> support: the task's shape, the search
/// tools (canned and live), the scripted model, and the offline run through <c>Eval.RunAsync</c> and the runner.
/// </summary>
public sealed class LangchainTests : IDisposable
{
    private const string ExampleName = "bridge/langchain";

    private const string FirstQuestion = "What's the difference between tennis and pickleball?";

    private readonly string _logDir = BridgeTestSupport.NewLogDir("bridge-langchain");

    public void Dispose() => BridgeTestSupport.DeleteLogDir(_logDir);

    [Fact]
    public void the_task_is_shaped_like_the_python_research_task()
    {
        var task = ResearchTask.Research();

        Assert.Equal("research", task.Name);
        Assert.Equal(3, task.Dataset.Count);
        // dataset.json inputs are [{role, content}] lists, read as chat messages
        Assert.False(task.Dataset[0].Input.IsText);
        Assert.Equal(FirstQuestion, Assert.IsType<ChatMessageUser>(Assert.Single(task.Dataset[0].Input.Messages!)).Text);
        Assert.StartsWith("While they are similar sports", task.Dataset[0].Target.Text);
        Assert.Equal("Which types of fish contain the lowest levels of mercury?", task.Dataset[1].Input.Messages![0].Text);
        Assert.Equal("List the ten episode titles from the sixth season of \"Game of Thrones\" in broadcast order.", task.Dataset[2].Input.Messages![0].Text);
        Assert.Equal("model_graded_fact", Assert.Single(task.Scorers).Name);
        Assert.Null(task.Sandbox);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli()
    {
        var method = typeof(ResearchTask).GetMethod(nameof(ResearchTask.Research));

        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        Assert.Equal("research", method.GetCustomAttribute<TaskAttribute>()!.Name);
    }

    [Fact]
    public void the_agent_carries_the_python_name_and_prompt()
    {
        var agent = WebResearchAgent.Create(searchTool: CannedWebSearch.Tool());

        Assert.Equal("web_research_agent", agent.Name);
        Assert.Equal("LangChain Tavily search agent.", agent.Description);
        Assert.Equal("You help users find information by searching the web.", WebResearch.Instructions);
    }

    [Fact]
    public async Task the_canned_search_tool_has_the_engine_tools_shape_and_answers_by_keyword()
    {
        var tool = CannedWebSearch.Tool();

        Assert.Equal("web_search", tool.Name);
        Assert.Equal(BuiltinTools.WebSearchDescription, tool.Description);
        Assert.Equal(["query"], tool.Parameters.Required);
        var answer = await tool.Execute(new JsonObject { ["query"] = FirstQuestion }, CancellationToken.None);
        Assert.Contains("half the size of a tennis court", answer.Text);
        Assert.Contains("Oathbreaker", CannedWebSearch.Answer("game of thrones season 6 episodes"));
        Assert.Null(CannedWebSearch.Answer("nothing relevant"));
        var none = await tool.Execute(new JsonObject { ["query"] = "nothing relevant" }, CancellationToken.None);
        Assert.Equal(BuiltinTools.WebSearchNoResults, none.Text);
    }

    [Fact]
    public void the_live_search_tool_is_the_engine_web_search_over_the_chosen_provider()
    {
        // TavilySearch(max_results=5)
        var tavily = WebResearch.LiveSearchTool("tavily", 5);
        Assert.Equal("web_search", tavily.Name);
        Assert.Equal(5, tavily.Options!["tavily"]!["max_results"]!.GetValue<int>());

        var exa = WebResearch.SearchTool(BridgeTestSupport.LiveContext(ExampleName, new Dictionary<string, string> { ["provider"] = "exa" }));
        Assert.True(exa.Options!.ContainsKey("exa"));
        Assert.False(exa.Options.ContainsKey("tavily"));

        // a fake run searches the canned table
        Assert.Same(BuiltinTools.WebSearchDescription, WebResearch.SearchTool(BridgeTestSupport.FakeContext(ExampleName)).Description);
    }

    [Fact]
    public void the_fake_model_recognises_the_grading_prompt()
    {
        Assert.True(FakeResearchModel.IsGradingPrompt([new ChatMessageUser("[BEGIN DATA]\n[Question]: q\n[END DATA]")]));
        Assert.False(FakeResearchModel.IsGradingPrompt([new ChatMessageUser(FirstQuestion)]));
        Assert.False(FakeResearchModel.IsGradingPrompt([]));
    }

    [Fact]
    public void the_example_describes_itself()
    {
        var example = new LangchainExample();

        Assert.Equal(ExampleName, example.Name);
        Assert.Equal(["research"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.FakeSandbox(BridgeTestSupport.FakeContext(ExampleName)));
        Assert.NotEmpty(example.Deviations);
        Assert.IsType<LangchainExample>(ExampleRegistry.Default.Find(ExampleName));
        Assert.Throws<ArgumentException>(() => example.Tasks[0].Build(BridgeTestSupport.FakeContext(ExampleName, new Dictionary<string, string> { ["max_results"] = "many" })));
    }

    [Fact]
    public async Task the_agent_searches_then_answers_and_the_grader_marks_every_sample_correct()
    {
        var example = new LangchainExample();
        var ctx = BridgeTestSupport.FakeContext(ExampleName);
        var model = example.CreateFakeModel(ctx);
        var api = BridgeTestSupport.Scripted(model);

        var log = await BridgeTestSupport.RunAsync(example.Tasks[0].Build(ctx), model, _logDir);

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(3, log.Samples!.Count);
        foreach (var sample in log.Samples)
        {
            Assert.Equal("C", sample.Scores!["model_graded_fact"].Text);
            var question = sample.Input.Messages![0].Text;
            Assert.Contains(CannedWebSearch.Answer(question)!, sample.Output.Completion);
            // system prompt, question, the search call, its result, the answer: the framework's loop tracked into the state
            Assert.Collection(
                sample.Messages,
                message => Assert.Equal(WebResearch.Instructions, Assert.IsType<ChatMessageSystem>(message).Text),
                message => Assert.Equal(question, Assert.IsType<ChatMessageUser>(message).Text),
                message => Assert.Equal("web_search", Assert.Single(Assert.IsType<ChatMessageAssistant>(message).ToolCalls!).Function),
                message => Assert.Equal("web_search", Assert.IsType<ChatMessageTool>(message).Function),
                message => Assert.IsType<ChatMessageAssistant>(message));
            Assert.Equal(["web_search"], sample.Events.OfType<ToolEvent>().Select(e => e.Function));
            // two agent calls and one grading call per sample
            Assert.Equal(3, sample.Events.OfType<ModelEvent>().Count());
        }

        // LangChain's agent has no submit tool: the model only ever saw web_search
        Assert.All(api.Requests.Where(request => !FakeResearchModel.IsGradingPrompt(request.Input)), request => Assert.Equal(["web_search"], request.Tools.Select(tool => tool.Name)));
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync([ExampleName, "--fake", "--log-dir", _logDir, "-T", "max_results=3"], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("status    : success (3/3 samples completed)", text);
        Assert.Contains("model_graded_fact", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }
}
