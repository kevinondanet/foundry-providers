using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Evalset;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Evalset;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the <c>evalset</c> example (<c>examples/evalset</c>): the two imported tasks' shape, the scripted
/// models, the port of <c>run()</c> end to end (the scripted outage is retried, a second run in the same directory
/// reuses the completed logs, a set that keeps failing is not a success), the wrapper task's exit semantics and an
/// offline run through the runner.
/// </summary>
public sealed class EvalsetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "evalset-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // task shape
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void security_guide_and_popularity_match_the_python_tasks()
    {
        var securityGuide = EvalsetTasks.SecurityGuide();
        var popularity = EvalsetTasks.Popularity();

        Assert.Equal("security_guide", securityGuide.Name);
        Assert.Equal(16, securityGuide.Dataset.Count);
        Assert.False(securityGuide.Dataset[0].Input.IsText);
        Assert.Equal("How do I prevent SQL Injection attacks?", securityGuide.Dataset[0].Input.Messages![0].Text);
        Assert.Equal("use parameterized queries and prepared statements", securityGuide.Dataset[0].Target.Text);
        Assert.Equal("model_graded_fact", Assert.Single(securityGuide.Scorers).Name);

        Assert.Equal("popularity", popularity.Name);
        Assert.Equal(100, popularity.Dataset.Count);
        Assert.Equal(" Yes", popularity.Dataset[0].Target.Text);
        Assert.True(popularity.Dataset[0].Metadata!.ContainsKey("label_confidence"));
        Assert.Equal("match", Assert.Single(popularity.Scorers).Name);

        Assert.Null(securityGuide.Sandbox);
        Assert.Null(popularity.Sandbox);
    }

    [Fact]
    public void the_example_declares_the_wrapper_task_and_its_defaults()
    {
        var example = new EvalsetExample();

        Assert.Equal("evalset", example.Name);
        Assert.Equal("evalset", Assert.Single(example.Tasks).Name);
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.FakeSandbox(Context(fake: true)));
        Assert.Equal(FakeEvalsetModels.FirstModelName, example.CreateFakeModel(Context(fake: true)).Name);
        Assert.NotEmpty(example.Deviations);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scripted models
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_scripted_models_answer_each_kind_of_prompt_and_the_first_call_of_the_first_one_fails()
    {
        var popularity = FakeEvalsetModels.Answer(FakeEvalsetModels.FirstModelName, [new ChatMessageSystem(EvalsetTasks.PopularitySystemMessage), new ChatMessageUser("Is the following statement something you would say?\n\"I enjoy tests\"")], 0);
        var security = FakeEvalsetModels.Answer(FakeEvalsetModels.SecondModelName, [new ChatMessageSystem(EvalsetTasks.SecurityGuideSystemMessage), new ChatMessageUser("How do I prevent xss?")], 1);
        var grade = FakeEvalsetModels.Answer(FakeEvalsetModels.FirstModelName, [new ChatMessageUser("[BEGIN DATA]\n[Question]: q\n[Expert]: e\n[Submission]: s\n[END DATA]")], 0);

        Assert.Contains(popularity.Completion, new[] { "Yes", "No" });
        Assert.Equal(FakeEvalsetModels.FirstModelName, popularity.Model);
        Assert.DoesNotContain(security.Completion, new[] { "Yes", "No" });
        Assert.NotEmpty(security.Completion);
        Assert.Matches("GRADE: [CI]$", grade.Completion);

        var first = FakeEvalsetModels.First();
        var api = (ScriptedModelApi)first.Api;
        var outage = await Assert.ThrowsAsync<InvalidOperationException>(() => api.GenerateAsync([new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig()));
        Assert.Equal(FakeEvalsetModels.OutageMessage, outage.Message);
        var next = await api.GenerateAsync([new ChatMessageUser("Is the following statement something you would say?\n\"a\"")], [], ToolChoice.Auto, new GenerateConfig());
        Assert.Contains(next.Output!.Completion, new[] { "Yes", "No" });
        Assert.Equal(FakeEvalsetModels.SecondModelName, FakeEvalsetModels.Second().Name);
    }

    // ----------------------------------------------------------------------------------------------------------
    // run(): the eval set end to end
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_set_retries_the_scripted_outage_and_a_second_run_reuses_the_completed_logs()
    {
        var logDir = Path.Combine(_root, "set");
        var first = FakeEvalsetModels.First();
        var second = FakeEvalsetModels.Second();
        var reporter = new RecordingReporter();

        var result = await EvalsetRun.RunAsync(logDir, [first, second], reporter: reporter);

        Assert.True(result.Success);
        Assert.Equal(4, result.Logs.Count);
        Assert.All(result.Logs, log => Assert.Equal(EvalStatus.Success, log.Status));
        Assert.Equal(
            new[] { ("popularity", FakeEvalsetModels.FirstModelName), ("popularity", FakeEvalsetModels.SecondModelName), ("security_guide", FakeEvalsetModels.FirstModelName), ("security_guide", FakeEvalsetModels.SecondModelName) }.OrderBy(pair => pair).ToArray(),
            result.Logs.Select(log => (log.Eval.Task, log.Eval.Model)).OrderBy(pair => pair).ToArray());
        Assert.All(result.Logs, log => Assert.Equal(log.Eval.Task == "popularity" ? 100 : 16, log.Results!.CompletedSamples));
        Assert.All(result.Logs, log => Assert.True(log.Results!.Scores[0].Metrics["accuracy"].Value is > 0 and < 1));
        var retried = result.Logs.SelectMany(log => log.Samples!).Where(sample => sample.ErrorRetries is { Count: > 0 }).ToList();
        var retry = Assert.Single(retried);
        Assert.Contains(FakeEvalsetModels.OutageMessage, Assert.Single(retry.ErrorRetries!).Message, StringComparison.Ordinal);
        Assert.Contains(reporter.Messages, message => message.StartsWith("Retrying task", StringComparison.Ordinal));
        Assert.Equal(4, Directory.GetFiles(logDir, "*.eval").Length);

        var summary = EvalsetRun.Summarize(result, logDir);
        Assert.Contains($"Completed all tasks in '{logDir}' successfully", summary, StringComparison.Ordinal);
        Assert.Contains("1 sample retried after an error", summary, StringComparison.Ordinal);

        // the same directory again: every log is complete, nothing runs and no model is called
        var idleFirst = Idle(FakeEvalsetModels.FirstModelName);
        var idleSecond = Idle(FakeEvalsetModels.SecondModelName);
        var resumed = await EvalsetRun.RunAsync(logDir, [idleFirst, idleSecond]);

        Assert.True(resumed.Success);
        Assert.Equal(4, resumed.Logs.Count);
        Assert.All(resumed.Logs, log => Assert.Null(log.Samples));
        Assert.Empty(((ScriptedModelApi)idleFirst.Api).Requests);
        Assert.Empty(((ScriptedModelApi)idleSecond.Api).Requests);
        Assert.Contains("already complete, reused from the log directory", EvalsetRun.Summarize(resumed, logDir), StringComparison.Ordinal);
    }

    [Fact]
    public async Task a_set_whose_model_keeps_failing_is_not_a_success()
    {
        var logDir = Path.Combine(_root, "failing");

        var result = await EvalsetRun.RunAsync(logDir, [Down()], retryAttempts: 0);

        Assert.False(result.Success);
        Assert.Equal(2, result.Logs.Count);
        Assert.All(result.Logs, log => Assert.Equal(EvalStatus.Error, log.Status));
        Assert.Contains($"Did not successfully complete all tasks in '{logDir}'.", EvalsetRun.Summarize(result, logDir), StringComparison.Ordinal);
    }

    [Fact]
    public async Task run_needs_a_log_dir_and_a_model()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => EvalsetRun.RunAsync("", [FakeEvalsetModels.Second()]));
        await Assert.ThrowsAsync<ArgumentException>(() => EvalsetRun.RunAsync(Path.Combine(_root, "x"), []));
    }

    // ----------------------------------------------------------------------------------------------------------
    // the wrapper task
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_wrapper_succeeds_when_the_set_does_and_errors_when_it_does_not()
    {
        var output = new StringWriter();
        var wrapper = EvalsetExample.Build(Path.Combine(_root, "wrapped"), FakeEvalsetModels.Second(), maxTasks: 2, retryAttempts: 3, output);
        Assert.Equal("evalset", wrapper.Name);
        Assert.Equal(EvalsetExample.SampleInput, Assert.Single(wrapper.Dataset).Input.Text);

        var log = await Eval.RunAsync(wrapper, new EvalOptions { Model = FakeEvalsetModels.First(), LogDir = Path.Combine(_root, "wrapper-logs") });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Contains("Completed all tasks in", log.Samples![0].Output.Completion, StringComparison.Ordinal);
        Assert.Contains("eval set  : security_guide, popularity on scripted/gpt-4o-mini, scripted/claude-3-5-haiku-latest", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Retrying task", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(4, Directory.GetFiles(Path.Combine(_root, "wrapped"), "*.eval").Length);

        var failing = EvalsetExample.Build(Path.Combine(_root, "wrapped-failing"), null, null, 0, TextWriter.Null);
        var failed = await Eval.RunAsync(failing, new EvalOptions { Model = Down(), LogDir = Path.Combine(_root, "wrapper-logs") });

        Assert.Equal(EvalStatus.Error, failed.Status);
        Assert.Contains("Did not successfully complete all tasks in", failed.Samples![0].Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task a_second_model_with_the_same_name_runs_the_set_on_one_model()
    {
        var output = new StringWriter();
        var wrapper = EvalsetExample.Build(Path.Combine(_root, "same"), FakeEvalsetModels.First(outage: false), null, EvalsetRun.DefaultRetryAttempts, output);

        var log = await Eval.RunAsync(wrapper, new EvalOptions { Model = FakeEvalsetModels.First(outage: false), LogDir = Path.Combine(_root, "wrapper-logs") });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Contains("an eval set needs distinct models, running on one", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "same"), "*.eval").Length);
    }

    [Fact]
    public void the_build_reads_the_task_args()
    {
        var ctx = Context(fake: true, args: new Dictionary<string, string> { ["log_dir"] = Path.Combine(_root, "args"), ["max_tasks"] = "3", ["retry_attempts"] = "2" });

        var task = EvalsetExample.Build(ctx);

        Assert.Equal("evalset", task.Name);
        Assert.Throws<ArgumentException>(() => EvalsetExample.Build(Context(fake: true, args: new Dictionary<string, string> { ["max_tasks"] = "many" })));
    }

    // ----------------------------------------------------------------------------------------------------------
    // the runner
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_runner_runs_the_example_offline_twice_showing_the_retry_then_the_reuse()
    {
        var registry = ExampleRegistry.Of(new EvalsetExample());
        var logDir = Path.Combine(_root, "runner");
        var setDir = Path.Combine(logDir, "set");

        var first = new StringWriter();
        var exit = await ExampleRunner.MainAsync(["evalset", "--fake", "--log-dir", logDir, "-T", $"log_dir={setDir}"], registry, first, first);
        var text = first.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("model     : scripted/gpt-4o-mini (scripted, offline)", text, StringComparison.Ordinal);
        Assert.Contains("Retrying task", text, StringComparison.Ordinal);
        Assert.Contains($"Completed all tasks in '{setDir}' successfully", text, StringComparison.Ordinal);
        Assert.Contains("status    : success (1/1 samples completed)", text, StringComparison.Ordinal);
        Assert.Equal(4, Directory.GetFiles(setDir, "*.eval").Length);
        Assert.Single(Directory.GetFiles(logDir, "*.eval"));

        var second = new StringWriter();
        exit = await ExampleRunner.MainAsync(["evalset", "--fake", "--log-dir", logDir, "-T", $"log_dir={setDir}"], registry, second, second);
        Assert.True(exit == 0, second.ToString());
        Assert.Contains("already complete, reused from the log directory", second.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Retrying task", second.ToString(), StringComparison.Ordinal);
        Assert.Equal(4, Directory.GetFiles(setDir, "*.eval").Length);
    }

    // ----------------------------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------------------------

    private static Model Idle(string name) => new(new ScriptedModelApi([], name) { ThrowWhenExhausted = true });

    private static Model Down() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.Throw(new InvalidOperationException("scripted model is down")), 200), "scripted/down"));

    private static ExampleContext Context(bool fake, IReadOnlyDictionary<string, string>? args = null) =>
        new(Path.Combine(AppContext.BaseDirectory, "evalset"), null, fake, args ?? new Dictionary<string, string>(), null, fake ? FakeEvalsetModels.First() : null, TextWriter.Null);

    private sealed class RecordingReporter : IEvalReporter
    {
        private readonly object _sync = new();
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_sync)
                {
                    return _messages.ToArray();
                }
            }
        }

        public void SampleStarted(object id, int epoch)
        {
        }

        public void SampleCompleted(EvalSample sample)
        {
        }

        public void Message(string text)
        {
            lock (_sync)
            {
                _messages.Add(text);
            }
        }
    }
}
