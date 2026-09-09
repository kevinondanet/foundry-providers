using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Prefill;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Prefill;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/prefill.py</c> (<see cref="ArithmeticPrefill"/>, <see cref="PrefillExample"/>):
/// the task's shape, the <c>prefill</c> solver's message rewrite, both branches of <c>score_arithmetic</c>, and the
/// offline run with the scripted model (continuing the prefill, and answering in prose) through <c>Eval.RunAsync</c>
/// and the examples runner.
/// </summary>
public sealed class PrefillTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "prefill-" + Guid.NewGuid().ToString("N"));

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
    // the task (port of @task def arithmetic_prefill)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_arithmetic_prefill()
    {
        var task = ArithmeticPrefill.ArithmeticPrefillTask();

        Assert.Equal("arithmetic_prefill", task.Name);
        Assert.Equal(3, task.Dataset.Count);
        Assert.Equal(["What is 1+1?", "What is 5+7?", "What is 3*4?"], task.Dataset.Select(sample => sample.Input.Text));
        Assert.Equal(["2", "12", "12"], task.Dataset.Select(sample => sample.Target.Text));
        Assert.Equal(["1+1=", "5+7=", "3*4="], task.Dataset.Select(sample => sample.Metadata!["prefill"]));
        var scorer = Assert.Single(task.Scorers);
        Assert.Equal("score_arithmetic", scorer.Name);
        Assert.Equal(["accuracy"], scorer.Metrics.Select(metric => metric.Name));
        Assert.Null(task.Sandbox);
        Assert.NotNull(task.Solver);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_as_arithmetic_prefill()
    {
        var method = typeof(ArithmeticPrefill).GetMethod(nameof(ArithmeticPrefill.ArithmeticPrefillTask))!;

        var attribute = Assert.IsType<TaskAttribute>(Assert.Single(method.GetCustomAttributes(typeof(TaskAttribute), inherit: false)));
        Assert.Equal("arithmetic_prefill", attribute.Name);
        Assert.Empty(attribute.Attribs);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the prefill solver
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task prefill_replaces_the_messages_with_the_question_and_the_prefilled_assistant_message()
    {
        var state = new TaskState(
            "scripted",
            1,
            1,
            "What is 1+1?",
            [new ChatMessageSystem("Be terse."), new ChatMessageUser("What is 1+1?")],
            target: new Target("2"),
            metadata: new Dictionary<string, object?> { ["prefill"] = "1+1=" });

        var result = await ArithmeticPrefill.Prefill()(state, NoGenerate, CancellationToken.None);

        Assert.Same(state, result);
        Assert.Equal(2, result.Messages.Count);
        var user = Assert.IsType<ChatMessageUser>(result.Messages[0]);
        Assert.Equal("What is 1+1?", user.Text);
        var assistant = Assert.IsType<ChatMessageAssistant>(result.Messages[1]);
        Assert.Equal("1+1=", assistant.Text);
        Assert.Null(assistant.ToolCalls);
        Assert.False(result.Completed);
    }

    [Fact]
    public async Task prefill_without_the_metadata_fails_the_sample_like_pythons_keyerror()
    {
        var state = new TaskState("scripted", 1, 1, "What is 1+1?", [new ChatMessageUser("What is 1+1?")]);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => ArithmeticPrefill.Prefill()(state, NoGenerate, CancellationToken.None));
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scorer (port of @scorer(metrics=[accuracy()]) def score_arithmetic)
    // ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2", "2", 1.0, null)]
    [InlineData("  2  ", "2", 1.0, null)]
    [InlineData("12 is the answer", "12", 1.0, null)]
    [InlineData("1+1=2", "2", 0.0, null)]
    [InlineData("3", "2", 0.0, null)]
    [InlineData("The answer is 2", "2", 0.0, "Could not extract a numerical answer")]
    [InlineData("", "2", 0.0, "Could not extract a numerical answer")]
    public async Task score_arithmetic_reads_the_leading_number_of_the_completion(string completion, string target, double value, string? explanation)
    {
        var state = new TaskState("scripted", 1, 1, "q", [new ChatMessageUser("q")], output: ModelOutput.FromContent("scripted", completion));

        var score = await ArithmeticPrefill.ScoreAsync(state, new Target(target), CancellationToken.None);

        Assert.Equal(new ScoreValue.Num(value), score.Value);
        Assert.Equal(completion.Trim(), score.Answer);
        Assert.Equal(explanation, score.Explanation);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scripted model
    // ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1+1=", 2L)]
    [InlineData("5+7=", 12L)]
    [InlineData("3*4=", 12L)]
    [InlineData("10-4=", 6L)]
    [InlineData("What is 1+1?", null)]
    [InlineData("", null)]
    public void the_fake_model_evaluates_the_prefilled_expression(string prefill, long? expected) =>
        Assert.Equal(expected, FakePrefillModel.Evaluate(prefill));

    // ----------------------------------------------------------------------------------------------------------
    // end to end, offline
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_scripted_model_continues_the_prefill_and_every_sample_scores_1()
    {
        var model = FakePrefillModel.Create();
        var api = (ScriptedModelApi)model.Api;

        var log = await Eval.RunAsync(ArithmeticPrefill.ArithmeticPrefillTask(), new EvalOptions { Model = model, LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(3, log.Samples!.Count);
        foreach (var sample in log.Samples)
        {
            var score = sample.Scores!["score_arithmetic"];
            Assert.Equal(1.0, score.AsFloat());
            Assert.Equal(sample.Target.Text, score.Answer);
            Assert.Null(score.Explanation);
            Assert.Equal(sample.Target.Text, sample.Output.Completion);
            // The conversation the model saw: the question, the prefill, then its continuation.
            Assert.Equal(3, sample.Messages.Count);
            Assert.IsType<ChatMessageUser>(sample.Messages[0]);
            Assert.EndsWith("=", Assert.IsType<ChatMessageAssistant>(sample.Messages[1]).Text);
            Assert.Equal(sample.Target.Text, Assert.IsType<ChatMessageAssistant>(sample.Messages[2]).Text);
        }

        var results = Assert.Single(log.Results!.Scores);
        Assert.Equal("score_arithmetic", results.Name);
        Assert.Equal(1.0, results.Metrics["accuracy"].Value);

        // Every request ended with the prefilled assistant message, which is what the providers forward.
        Assert.Equal(3, api.Requests.Count);
        Assert.All(api.Requests, request =>
        {
            Assert.Equal(2, request.Input.Count);
            Assert.IsType<ChatMessageUser>(request.Input[0]);
            var prefill = Assert.IsType<ChatMessageAssistant>(request.Input[1]);
            Assert.Matches(@"^\d+[+*-]\d+=$", prefill.Text);
        });
    }

    [Fact]
    public async Task a_model_that_answers_in_prose_hits_the_could_not_extract_branch()
    {
        var log = await Eval.RunAsync(
            ArithmeticPrefill.ArithmeticPrefillTask(),
            new EvalOptions { Model = FakePrefillModel.Create(prose: true), LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.All(log.Samples!, sample =>
        {
            var score = sample.Scores!["score_arithmetic"];
            Assert.Equal(0.0, score.AsFloat());
            Assert.Equal("Could not extract a numerical answer", score.Explanation);
            Assert.StartsWith("The answer is ", score.Answer);
        });
        Assert.Equal(0.0, Assert.Single(log.Results!.Scores).Metrics["accuracy"].Value);
    }

    [Fact]
    public async Task the_runner_runs_prefill_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["prefill", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : arithmetic_prefill", text);
        Assert.Contains("model     : prefill-scripted (scripted, offline)", text);
        Assert.Contains("sandbox   : none", text);
        Assert.Contains("status    : success (3/3 samples completed)", text);
        Assert.Contains("score_arithmetic         accuracy                  1.000", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }

    [Fact]
    public async Task the_runner_passes_prose_true_to_the_scripted_model()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["prefill", "--fake", "--log-dir", _logDir, "-T", "prose=true"], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task args : prose=true", text);
        Assert.Contains("status    : success (3/3 samples completed)", text);
        Assert.Contains("score_arithmetic         accuracy                  0.000", text);
    }

    [Fact]
    public void the_example_is_registered_with_its_python_name_and_no_sandbox()
    {
        var example = Assert.IsType<PrefillExample>(ExampleRegistry.Default.Find("prefill"));

        Assert.Equal(["arithmetic_prefill"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.Defaults.Approval);
        Assert.False(example.Defaults.NeedsDocker);
        Assert.NotNull(example.Defaults.ModelHint);
        Assert.NotEmpty(example.Deviations);
        Assert.Null(example.FakeSandbox(Context()));
        Assert.Equal("prefill-scripted", example.CreateFakeModel(Context()).Name);
        Assert.Throws<ArgumentException>(() => example.CreateFakeModel(Context(new Dictionary<string, string> { ["prose"] = "maybe" })));
    }

    private static ExampleContext Context(IReadOnlyDictionary<string, string>? taskArgs = null) =>
        new("/x", null, true, taskArgs ?? new Dictionary<string, string>(), null, null, TextWriter.Null);

    /// <summary>The prefill solver never generates.</summary>
    private static Task<TaskState> NoGenerate(TaskState state, ToolCallsMode toolCalls, GenerateConfig? config, InspectAzureAI.Eval.Model.Cache.CachePolicy? cache, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("prefill must not generate");
}
