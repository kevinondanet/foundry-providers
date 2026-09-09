using System.Reflection;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Bridge;
using InspectAzureAI.Examples.Bridge.AgentSdk;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Bridge.AgentSdk;

/// <summary>
/// Tests for the port of <c>examples/bridge/agentsdk</c> (<see cref="WebResearchAgent"/>, <see cref="ResearchTask"/>,
/// <see cref="AgentSdkExample"/>): the task's shape, the SearchAssistant agent, and the offline run through
/// <c>Eval.RunAsync</c> and the runner.
/// </summary>
public sealed class AgentSdkTests : IDisposable
{
    private const string ExampleName = "bridge/agentsdk";

    private readonly string _logDir = BridgeTestSupport.NewLogDir("bridge-agentsdk");

    public void Dispose() => BridgeTestSupport.DeleteLogDir(_logDir);

    [Fact]
    public void the_task_is_shaped_like_the_python_research_task()
    {
        var task = ResearchTask.Research();

        Assert.Equal("research", task.Name);
        Assert.Equal(3, task.Dataset.Count);
        Assert.Equal("What's the difference between tennis and pickleball?", Assert.IsType<ChatMessageUser>(Assert.Single(task.Dataset[0].Input.Messages!)).Text);
        Assert.StartsWith("The following types of fish contain low levels of mercury", task.Dataset[1].Target.Text);
        Assert.Equal("The Red Woman, Home, Oathbreaker, Book of the Stranger, The Door, Blood of My Blood, The Broken Man, No One, Battle of the Bastards, The Winds of Winter", task.Dataset[2].Target.Text);
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
    public void the_inspect_agent_keeps_the_python_name_around_the_search_assistant()
    {
        var agent = WebResearchAgent.Create(CannedWebSearch.Tool());

        Assert.Equal("web_research_agent", agent.Name);
        Assert.Equal("OpenAI Agents SDK search agent.", agent.Description);
        Assert.Equal("SearchAssistant", WebResearchAgent.SdkAgentName);
    }

    [Fact]
    public void the_example_describes_itself()
    {
        var example = new AgentSdkExample();

        Assert.Equal(ExampleName, example.Name);
        Assert.Equal(["research"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.FakeSandbox(BridgeTestSupport.FakeContext(ExampleName)));
        Assert.NotEmpty(example.Deviations);
        Assert.IsType<AgentSdkExample>(ExampleRegistry.Default.Find(ExampleName));
        // WebSearchTool() has no max_results: the live tool is the bare provider
        var live = WebResearch.SearchTool(BridgeTestSupport.LiveContext(ExampleName));
        Assert.True(live.Options!.ContainsKey("tavily"));
        Assert.Empty(live.Options["tavily"]!.AsObject());
    }

    [Fact]
    public async Task the_search_assistant_searches_then_answers_and_the_grader_marks_every_sample_correct()
    {
        var example = new AgentSdkExample();
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
            Assert.Equal(WebResearch.Instructions, Assert.IsType<ChatMessageSystem>(sample.Messages[0]).Text);
            Assert.Equal(question, Assert.IsType<ChatMessageUser>(sample.Messages[1]).Text);
            Assert.Equal(["web_search"], sample.Events.OfType<ToolEvent>().Select(e => e.Function));
            Assert.Equal(3, sample.Events.OfType<ModelEvent>().Count());
        }

        // Runner.run ends on the final output: no submit tool was ever offered
        Assert.All(api.Requests.Where(request => !FakeResearchModel.IsGradingPrompt(request.Input)), request => Assert.Equal(["web_search"], request.Tools.Select(tool => tool.Name)));
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync([ExampleName, "--fake", "--log-dir", _logDir, "--display", "conversation"], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("status    : success (3/3 samples completed)", text);
        Assert.Contains("model_graded_fact", text);
        Assert.Contains("web_search", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }
}
