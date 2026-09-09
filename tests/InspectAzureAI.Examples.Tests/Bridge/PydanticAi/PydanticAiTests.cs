using System.Reflection;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Bridge;
using InspectAzureAI.Examples.Bridge.PydanticAi;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;
using Microsoft.Extensions.AI;

namespace InspectAzureAI.Examples.Tests.Bridge.PydanticAi;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the port of <c>examples/bridge/pydantic-ai</c> (<see cref="AnswerToQueryOutput"/>, <see cref="WebResearchAgent"/>,
/// <see cref="ResearchTask"/>, <see cref="PydanticAiExample"/>): the task's shape, the typed output and its
/// validation, the JSON-schema response format reaching the model, the output retries, and the offline run
/// through <c>Eval.RunAsync</c> and the runner.
/// </summary>
public sealed class PydanticAiTests : IDisposable
{
    private const string ExampleName = "bridge/pydantic-ai";

    private const string Question = "What's the difference between tennis and pickleball?";

    private readonly string _logDir = BridgeTestSupport.NewLogDir("bridge-pydantic-ai");

    public void Dispose() => BridgeTestSupport.DeleteLogDir(_logDir);

    [Fact]
    public void the_task_is_shaped_like_the_python_research_task()
    {
        var task = ResearchTask.Research();

        Assert.Equal("research", task.Name);
        Assert.Equal(3, task.Dataset.Count);
        Assert.Equal(Question, Assert.IsType<ChatMessageUser>(Assert.Single(task.Dataset[0].Input.Messages!)).Text);
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
    public void the_typed_output_round_trips_as_pydantic_json()
    {
        var output = new AnswerToQueryOutput("forty-two");

        Assert.Equal("{\"answer\":\"forty-two\"}", output.ToJson());
        Assert.True(AnswerToQueryOutput.TryParse("{\"answer\": \"forty-two\"}", out var parsed, out var error));
        Assert.Equal(output, parsed);
        Assert.Equal("", error);

        Assert.False(AnswerToQueryOutput.TryParse("not json", out parsed, out error));
        Assert.Null(parsed);
        Assert.Contains("json_invalid", error);

        Assert.False(AnswerToQueryOutput.TryParse("{\"reply\": \"x\"}", out parsed, out error));
        Assert.Null(parsed);
        Assert.Equal("answer: Field required [type=missing]", error);
    }

    [Fact]
    public void the_response_format_is_the_outputs_json_schema()
    {
        var format = Assert.IsType<ChatResponseFormatJson>(AnswerToQueryOutput.ResponseFormat);

        Assert.Equal("AnswerToQueryOutput", format.SchemaName);
        Assert.NotNull(format.Schema);
        var schema = format.Schema!.Value.GetRawText();
        Assert.Contains("\"answer\"", schema);
        Assert.Contains("The answer to the query", schema);
    }

    [Fact]
    public void the_example_describes_itself()
    {
        var example = new PydanticAiExample();

        Assert.Equal(ExampleName, example.Name);
        Assert.Equal(["research"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.FakeSandbox(BridgeTestSupport.FakeContext(ExampleName)));
        Assert.NotEmpty(example.Deviations);
        Assert.IsType<PydanticAiExample>(ExampleRegistry.Default.Find(ExampleName));
        Assert.Equal("web_research_agent", WebResearchAgent.Create(CannedWebSearch.Tool()).Name);
        Assert.Throws<ArgumentOutOfRangeException>(() => WebResearchAgent.Create(CannedWebSearch.Tool(), outputRetries: -1));
    }

    [Fact]
    public async Task the_agent_searches_answers_in_the_schema_and_writes_the_typed_json_as_the_completion()
    {
        var example = new PydanticAiExample();
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
            // bridge.state.output.completion = result.output.model_dump_json()
            Assert.True(AnswerToQueryOutput.TryParse(sample.Output.Completion, out var output, out _), sample.Output.Completion);
            Assert.Equal(CannedWebSearch.Answer(question), output!.Answer);
            Assert.Equal(output.ToJson(), sample.Output.Completion);
            Assert.Equal(WebResearch.Instructions, Assert.IsType<ChatMessageSystem>(sample.Messages[0]).Text);
            Assert.Equal(question, Assert.IsType<ChatMessageUser>(sample.Messages[1]).Text);
            Assert.Equal(["web_search"], sample.Events.OfType<ToolEvent>().Select(e => e.Function));
        }

        // output_type=AnswerToQueryOutput reached the model as a JSON-schema response format on every agent call
        var agentRequests = api.Requests.Where(request => !FakeResearchModel.IsGradingPrompt(request.Input)).ToList();
        Assert.Equal(6, agentRequests.Count);
        Assert.All(agentRequests, request =>
        {
            Assert.Equal("AnswerToQueryOutput", request.Config.ResponseSchema!.Name);
            Assert.Equal(["web_search"], request.Tools.Select(tool => tool.Name));
        });
    }

    [Fact]
    public async Task an_invalid_output_is_retried_with_validation_feedback()
    {
        var api = new ScriptedModelApi(
            ScriptedTurn.ToolCall("web_search", new { query = Question }),
            ScriptedTurn.Text("Pickleball courts are smaller."),
            ScriptedTurn.Text("{\"answer\": \"Pickleball courts are smaller.\"}"));

        var log = await BridgeTestSupport.RunAsync(UnscoredTask(WebResearchAgent.Create(CannedWebSearch.Tool())), new Model(api), _logDir);

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("{\"answer\":\"Pickleball courts are smaller.\"}", sample.Output.Completion);
        Assert.Equal(3, api.Requests.Count);
        var feedback = Assert.IsType<ChatMessageUser>(api.Requests[2].Input[^1]);
        Assert.StartsWith("Validation feedback:", feedback.Text);
        Assert.Contains("Fix the errors and try again.", feedback.Text);
        Assert.Contains(sample.Messages, message => message is ChatMessageUser user && user.Text.StartsWith("Validation feedback:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task exhausting_the_output_retries_fails_the_sample()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("no"), ScriptedTurn.Text("still no"));

        var log = await BridgeTestSupport.RunAsync(UnscoredTask(WebResearchAgent.Create(CannedWebSearch.Tool(), outputRetries: 1)), new Model(api), _logDir);

        Assert.NotEqual(EvalStatus.Success, log.Status);
        Assert.Equal(2, api.Requests.Count);
        Assert.Contains("Exceeded maximum retries (1) for output validation", log.Error?.Message ?? log.Samples?.FirstOrDefault()?.Error?.Message ?? "");
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync([ExampleName, "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("status    : success (3/3 samples completed)", text);
        Assert.Contains("model_graded_fact", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }

    private static EvalTask UnscoredTask(AgentDef agent) => new()
    {
        Name = "research",
        Dataset = new MemoryDataset([new Sample(Question)]),
        Solver = Agents.AsSolver(agent),
    };
}
