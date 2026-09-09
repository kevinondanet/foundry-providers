using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Examples.BiologyQa;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.BiologyQa;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/biology_qa.py</c> (<see cref="BiologyQaExample"/>): the task's shape, the
/// five-provider <c>web_search</c> configuration carried verbatim in the tool's options, the scripted Tavily
/// transport driving the real Tavily provider, the scripted model, and the eval run end to end without a network,
/// through <c>Eval.RunAsync</c> and through the examples runner.
/// </summary>
public sealed class BiologyQaTests : IDisposable
{
    /// <summary>The first record of the bundled <c>biology_qa.jsonl</c>, verbatim.</summary>
    private const string FirstQuestion = "Hansen's disease is more commonly known by which name?";

    private const string FirstAnswer = "Leprosy";

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "biology-qa-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_logDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // the task (port of @task def biology_qa)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_task()
    {
        var task = BiologyQaExample.BiologyQa();

        Assert.Equal("biology_qa", task.Name);
        Assert.Equal("biology_qa", task.Dataset.Name);
        Assert.Equal(20, task.Dataset.Count);
        var first = task.Dataset[0];
        Assert.Equal("q1", first.Id);
        Assert.Equal(FirstQuestion, first.Input.ToString());
        Assert.Equal(new Target(FirstAnswer), first.Target);
        Assert.Equal("model_graded_qa", Assert.Single(task.Scorers).Name);
        Assert.Null(task.Sandbox);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli()
    {
        var method = typeof(BiologyQaExample).GetMethod(nameof(BiologyQaExample.BiologyQa));

        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        Assert.Equal("biology_qa", method.GetCustomAttribute<TaskAttribute>()!.Name);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void the_web_search_tool_carries_the_five_providers_and_their_options_verbatim()
    {
        var tool = BiologyQaExample.WebSearchTool();

        Assert.Equal("web_search", tool.Name);
        Assert.Equal(BuiltinTools.WebSearchDescription, tool.Description);
        var options = tool.Options!;
        Assert.Equal("web_search", options["__internal_tool_type__"]!.GetValue<string>());
        Assert.Equal(["__internal_tool_type__", "grok", "openai", "anthropic", "tavily", "gemini"], options.Select(pair => pair.Key));

        Assert.Empty(options["grok"]!.AsObject());
        Assert.Empty(options["anthropic"]!.AsObject());

        var openai = options["openai"]!.AsObject();
        Assert.Equal("high", openai["search_context_size"]!.GetValue<string>());
        Assert.Equal("approximate", openai["user_location"]!["type"]!.GetValue<string>());
        Assert.Equal("US", openai["user_location"]!["country"]!.GetValue<string>());
        Assert.Equal("Boston", openai["user_location"]!["city"]!.GetValue<string>());

        var tavily = options["tavily"]!.AsObject();
        Assert.Equal(5, tavily["max_results"]!.GetValue<int>());
        Assert.Equal(8, tavily["max_connections"]!.GetValue<int>());

        var filter = options["gemini"]!["time_range_filter"]!.AsObject();
        var start = DateTimeOffset.Parse(filter["start_time"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var end = DateTimeOffset.Parse(filter["end_time"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.FromDays(365), end - start);
        Assert.Equal(0, end.Millisecond);
        Assert.InRange(end, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void gemini_options_are_the_last_365_days_in_python_isoformat()
    {
        var now = new DateTimeOffset(2026, 9, 8, 10, 11, 12, 987, TimeSpan.Zero);

        var filter = BiologyQaExample.GeminiOptions(now)["time_range_filter"]!;

        Assert.Equal("2025-09-08T10:11:12+00:00", filter["start_time"]!.GetValue<string>());
        Assert.Equal("2026-09-08T10:11:12+00:00", filter["end_time"]!.GetValue<string>());
    }

    [Fact]
    public void the_example_is_registered_without_a_sandbox()
    {
        var example = Assert.IsType<BiologyQaExample>(ExampleRegistry.Default.Get("biology_qa"));

        Assert.Equal("biology_qa", Assert.Single(example.Tasks).Name);
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Contains("TAVILY_API_KEY", example.Defaults.ModelHint);
        Assert.NotEmpty(example.Deviations);
        var ctx = new ExampleContext("/x", null, true, new Dictionary<string, string>(), null, null, TextWriter.Null);
        Assert.Null(example.FakeSandbox(ctx));
        Assert.Equal(FakeBiologyQaModel.ModelName, example.CreateFakeModel(ctx).Name);
        Assert.Equal("biology_qa", example.Tasks[0].Build(ctx).Name);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scripted Tavily transport and the scripted model
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_scripted_tavily_transport_answers_the_real_provider_with_the_dataset_answer()
    {
        FakeTavilyHandler.EnsureApiKey();
        var handler = new FakeTavilyHandler();
        var tool = BiologyQaExample.WebSearchTool(handler);

        var result = await tool.Execute(new JsonObject { ["query"] = "Botany is the study of what life form?" }, CancellationToken.None);

        Assert.Equal(["Botany is the study of what life form?"], handler.Queries);
        var text = Assert.IsType<ContentText>(Assert.Single(result.Contents!));
        Assert.Equal("Plants", text.Text);
        var citation = Assert.IsType<UrlCitation>(Assert.Single(text.Citations!));
        Assert.Equal("https://example.com/biology/q2", citation.Url);

        var nothing = await tool.Execute(new JsonObject { ["query"] = "not a dataset question" }, CancellationToken.None);
        Assert.Equal(BuiltinTools.WebSearchNoResults, nothing.Text);
    }

    [Fact]
    public void the_scripted_model_searches_then_answers_then_grades()
    {
        var search = FakeBiologyQaModel.Respond([new ChatMessageUser(FirstQuestion)], []);
        var call = Assert.Single(search.Message.ToolCalls!);
        Assert.Equal("web_search", call.Function);
        Assert.Equal(FirstQuestion, call.Arguments["query"]!.GetValue<string>());

        var answer = FakeBiologyQaModel.Respond(
            [new ChatMessageUser(FirstQuestion), search.Message, new ChatMessageTool(FirstAnswer, call.Id, "web_search")],
            []);
        Assert.Equal(FirstAnswer, answer.Completion);

        var empty = FakeBiologyQaModel.Respond(
            [new ChatMessageUser(FirstQuestion), search.Message, new ChatMessageTool(BuiltinTools.WebSearchNoResults, call.Id, "web_search")],
            []);
        Assert.Equal(FakeBiologyQaModel.NoAnswer, empty.Completion);

        var grading = ModelGraded.DefaultQaTemplate
            .Replace("{question}", FirstQuestion, StringComparison.Ordinal)
            .Replace("{answer}", FirstAnswer, StringComparison.Ordinal)
            .Replace("{criterion}", FirstAnswer, StringComparison.Ordinal)
            .Replace("{instructions}", ModelGraded.DefaultInstructions(false), StringComparison.Ordinal);
        Assert.True(FakeBiologyQaModel.IsGradingPrompt(grading));
        Assert.EndsWith("GRADE: C", FakeBiologyQaModel.Respond([new ChatMessageUser(grading)], []).Completion);
        Assert.EndsWith("GRADE: I", FakeBiologyQaModel.Grade(grading.Replace("[Submission]: Leprosy", "[Submission]: Plants", StringComparison.Ordinal)));
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end, offline
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_task_runs_offline_searching_once_per_question()
    {
        FakeTavilyHandler.EnsureApiKey();
        var handler = new FakeTavilyHandler();

        var log = await Eval.RunAsync(
            BiologyQaExample.Build(handler),
            new EvalOptions
            {
                Model = FakeBiologyQaModel.Create(),
                LogDir = _logDir,
                LogFormat = LogFormat.Eval,
            },
            CancellationToken.None);

        Assert.True(log.Status == EvalStatus.Success, $"status {log.Status}: {log.Error?.Message ?? "(no error)"}");
        Assert.Equal(20, log.Results!.CompletedSamples);
        var score = Assert.Single(log.Results.Scores);
        Assert.Equal("model_graded_qa", score.Name);
        Assert.Equal(1.0, score.Metrics["accuracy"].Value);
        Assert.Equal(20, handler.Queries.Count);

        foreach (var sample in log.Samples!)
        {
            Assert.Null(sample.Error);
            Assert.Equal("C", sample.Scores!["model_graded_qa"].Text);
            Assert.Equal(["user", "assistant", "tool", "assistant"], sample.Messages.Select(message => message.Role));
            var question = sample.Input.ToString();
            Assert.Contains(question, handler.Queries);

            var toolEvent = Assert.Single(sample.Events.OfType<ToolEvent>());
            Assert.Equal("web_search", toolEvent.Function);
            Assert.Equal(question, toolEvent.Arguments["query"]!.GetValue<string>());
            Assert.Null(toolEvent.Error);

            var toolMessage = Assert.Single(sample.Messages.OfType<ChatMessageTool>());
            Assert.Null(toolMessage.Error);
            Assert.Equal(sample.Target.Text, toolMessage.Text);
            var text = Assert.IsType<ContentText>(Assert.Single(toolMessage.ContentList));
            Assert.Equal($"https://example.com/biology/{sample.Id}", Assert.IsType<UrlCitation>(Assert.Single(text.Citations!)).Url);

            Assert.Equal(sample.Target.Text, sample.Output.Completion);
            // search, answer, grade
            Assert.Equal(3, sample.Events.OfType<ModelEvent>().Count());
        }
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["biology_qa", "--fake", "--limit", "2", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : biology_qa", text);
        Assert.Contains("sandbox   : none", text);
        Assert.Contains("status    : success (2/2 samples completed)", text);
        Assert.Contains("model_graded_qa", text);
        Assert.Contains("accuracy", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }
}
