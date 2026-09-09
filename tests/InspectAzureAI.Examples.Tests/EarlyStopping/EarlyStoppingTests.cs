using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Examples.EarlyStopping;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.EarlyStopping;

using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the <c>early_stopping</c> example (<c>examples/early_stopping</c>): the <c>popularity</c> task's shape,
/// the <c>TestEarlyStopping</c> manager (a halted sample stays halted for its remaining epochs), the mock model, a
/// seeded end-to-end run whose log records the early stops, and an offline run through the runner.
/// </summary>
public sealed class EarlyStoppingTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "early-stopping-" + Guid.NewGuid().ToString("N"));

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
    // task shape
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_matches_the_python_task()
    {
        var task = EarlyStoppingTasks.Popularity();

        Assert.Equal("popularity", task.Name);
        Assert.Equal(100, task.Dataset.Count);
        var first = task.Dataset[0];
        Assert.StartsWith("Is the following statement something you would say?", first.Input.Text, StringComparison.Ordinal);
        Assert.Equal(" Yes", first.Target.Text);
        Assert.True(first.Metadata!.ContainsKey("label_confidence"));
        Assert.Equal("match", Assert.Single(task.Scorers).Name);
        Assert.Equal(5, task.Epochs!.Count);
        Assert.IsType<TestEarlyStopping>(task.EarlyStopping);
        Assert.Equal(EarlyStoppingTasks.MockModelName, task.Model!.Name);
        Assert.Null(task.Sandbox);
        Assert.Equal("\nPlease note: In the following EXERCISE, it is important that you only respond with exactly \"Yes\" or \"No\"\n", EarlyStoppingTasks.SystemMessage);
    }

    [Fact]
    public void the_example_declares_the_task_and_its_defaults()
    {
        var example = new EarlyStoppingExample();

        Assert.Equal("early_stopping", example.Name);
        Assert.Equal("popularity", Assert.Single(example.Tasks).Name);
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.FakeSandbox(Context(fake: true)));
        Assert.Equal(EarlyStoppingTasks.MockModelName, example.CreateFakeModel(Context(fake: true)).Name);
        Assert.Contains(example.Deviations, deviation => deviation.Contains("early_stopping", StringComparison.Ordinal));
    }

    // ----------------------------------------------------------------------------------------------------------
    // the manager
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task a_halted_sample_stays_halted_for_its_remaining_epochs()
    {
        var manager = new TestEarlyStopping(new Random(1));
        // the manager ignores the task spec
        Assert.Equal("test", await manager.StartTaskAsync(null!, [], 5, CancellationToken.None));

        var halted = new List<object>();
        var running = new List<object>();
        for (var id = 1; id <= 40; id++)
        {
            var stop = await manager.ScheduleSampleAsync(id, 1, CancellationToken.None);
            if (stop is null)
            {
                running.Add(id);
            }
            else
            {
                Assert.Equal(id, stop.Id);
                Assert.Equal(1, stop.Epoch);
                halted.Add(id);
            }
        }

        Assert.NotEmpty(halted);
        Assert.NotEmpty(running);
        Assert.Equal(halted, manager.CompletedSamples);
        foreach (var id in halted)
        {
            for (var epoch = 2; epoch <= 5; epoch++)
            {
                var stop = await manager.ScheduleSampleAsync(id, epoch, CancellationToken.None);
                Assert.NotNull(stop);
                Assert.Equal(epoch, stop.Epoch);
            }
        }

        await manager.CompleteSampleAsync(running[0], 1, new Dictionary<string, InspectAzureAI.Eval.Scorers.SampleScore>(), CancellationToken.None);
        Assert.Empty(await manager.CompleteTaskAsync(CancellationToken.None));
    }

    [Fact]
    public async Task the_report_counts_the_scheduled_and_halted_runs()
    {
        var output = new StringWriter();
        var report = new EarlyStoppingReport(new TestEarlyStopping(new Random(3)), output);
        await report.StartTaskAsync(null!, [], 2, CancellationToken.None);

        for (var id = 1; id <= 10; id++)
        {
            for (var epoch = 1; epoch <= 2; epoch++)
            {
                await report.ScheduleSampleAsync(id, epoch, CancellationToken.None);
            }
        }

        await report.CompleteTaskAsync(CancellationToken.None);

        Assert.Equal(20, report.Scheduled);
        Assert.InRange(report.Stops.Count, 1, 19);
        Assert.StartsWith($"{EarlyStoppingReport.Prefix}manager 'test' halted {report.Stops.Count} of 20 sample runs", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void the_mock_answers_yes_or_no_deterministically()
    {
        ChatMessage[] messages = [new ChatMessageSystem(EarlyStoppingTasks.SystemMessage), new ChatMessageUser("Is the following statement something you would say?\n\"I like tests\"")];

        var first = EarlyStoppingTasks.MockAnswer(messages, []);
        var second = EarlyStoppingTasks.MockAnswer(messages, []);

        Assert.Equal(EarlyStoppingTasks.MockModelName, first.Model);
        Assert.Contains(first.Completion, new[] { "Yes", "No" });
        Assert.Equal(first.Completion, second.Completion);
        Assert.NotEqual(first.Completion, EarlyStoppingTasks.MockAnswer([new ChatMessageUser("Is the following statement something you would say?\n\"I like tests!\"")], []).Completion);
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task a_seeded_run_halts_epochs_and_records_them_in_the_log()
    {
        var output = new StringWriter();
        var task = EarlyStoppingExample.Build(Context(fake: true, output, seed: "5"));

        var log = await Eval.RunAsync(task, new EvalOptions { Model = task.Model, LogDir = _logDir, Limit = 10 });

        Assert.Equal(EvalStatus.Success, log.Status);
        var summary = log.Results!.EarlyStopping!;
        Assert.Equal("test", summary.Manager);
        Assert.NotEmpty(summary.EarlyStops);
        Assert.Equal(50, log.Results.TotalSamples);
        Assert.Equal(50 - summary.EarlyStops.Count, log.Results.CompletedSamples);
        Assert.Equal(log.Results.CompletedSamples, log.Samples!.Count);

        // once a sample is halted, every one of its later epochs is halted too, and none of them is logged
        foreach (var group in summary.EarlyStops.GroupBy(stop => stop.Id))
        {
            var epochs = group.Select(stop => stop.Epoch).OrderBy(epoch => epoch).ToList();
            Assert.Equal(Enumerable.Range(epochs[0], 5 - epochs[0] + 1), epochs);
            Assert.DoesNotContain(log.Samples, sample => Equals(sample.Id, group.Key) && sample.Epoch >= epochs[0]);
        }

        Assert.All(log.Samples, sample => Assert.Contains(sample.Scores!["match"].Text, new[] { "C", "I" }));
        Assert.Contains("match", log.Results.Scores.Select(score => score.Name));
        Assert.Contains($"{EarlyStoppingReport.Prefix}manager 'test' halted {summary.EarlyStops.Count} of 50 sample runs", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task a_seed_makes_the_run_reproducible_and_a_fake_run_defaults_to_seed_0()
    {
        var first = await Eval.RunAsync(EarlyStoppingExample.Build(Context(fake: true, seed: "11")), new EvalOptions { LogDir = _logDir, Limit = 8 });
        var second = await Eval.RunAsync(EarlyStoppingExample.Build(Context(fake: true, seed: "11")), new EvalOptions { LogDir = _logDir, Limit = 8 });
        var unseeded = await Eval.RunAsync(EarlyStoppingExample.Build(Context(fake: true)), new EvalOptions { LogDir = _logDir, Limit = 8 });
        var seedZero = await Eval.RunAsync(EarlyStoppingExample.Build(Context(fake: true, seed: "0")), new EvalOptions { LogDir = _logDir, Limit = 8 });

        Assert.Equal(Stops(first), Stops(second));
        Assert.Equal(Stops(unseeded), Stops(seedZero));
        Assert.Equal(EarlyStoppingTasks.MockModelName, first.Eval.Model);
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["early_stopping", "--fake", "--limit", "6", "--log-dir", _logDir], ExampleRegistry.Of(new EarlyStoppingExample()), output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("model     : mockllm/model (scripted, offline)", text, StringComparison.Ordinal);
        Assert.Contains($"{EarlyStoppingReport.Prefix}manager 'test' halted", text, StringComparison.Ordinal);
        Assert.Contains("status    : success (", text, StringComparison.Ordinal);
        Assert.Contains("/30 samples completed)", text, StringComparison.Ordinal);
        Assert.Matches(@"match\s+accuracy", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }

    // ----------------------------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------------------------

    private static IEnumerable<(string Id, int Epoch)> Stops(EvalLog log) =>
        log.Results!.EarlyStopping!.EarlyStops.Select(stop => (stop.Id.ToString()!, stop.Epoch)).OrderBy(stop => stop).ToList();

    private static ExampleContext Context(bool fake, TextWriter? output = null, string? seed = null)
    {
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        if (seed is not null)
        {
            args["seed"] = seed;
        }

        return new ExampleContext(Path.Combine(AppContext.BaseDirectory, "early_stopping"), null, fake, args, null, fake ? EarlyStoppingTasks.MockLlm() : null, output ?? TextWriter.Null);
    }
}
