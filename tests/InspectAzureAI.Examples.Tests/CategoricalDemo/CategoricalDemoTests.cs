using System.Reflection;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.CategoricalDemo;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.CategoricalDemo;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/categorical_demo.py</c> (<see cref="CategoricalDemoExample"/>): the task's shape, the
/// StrEnum labels, the weighted seeded draws, each of the three scorers, and the <c>--model mockllm/model</c> run end to
/// end, reading the per-category frequencies back from the log.
/// </summary>
public sealed class CategoricalDemoTests : IDisposable
{
    private static readonly string[] VerdictLabels = ["yes", "no", "unsure"];

    private static readonly string[] SabotageLabels = ["none", "subtle", "overt"];

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "categorical-demo-" + Guid.NewGuid().ToString("N"));

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
    // the task (port of @task def categorical_demo)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_shaped_like_the_python_categorical_demo()
    {
        var task = CategoricalDemoExample.CategoricalDemoTask();

        Assert.Equal("categorical_demo", task.Name);
        Assert.Equal(40, task.Dataset.Count);
        Assert.Equal("Sample question 0", task.Dataset[0].Input.Text);
        Assert.Equal("Sample question 39", task.Dataset[39].Input.Text);
        Assert.All(Enumerable.Range(0, 40), i => Assert.Equal("yes", task.Dataset[i].Target.Text));
        Assert.Equal("generate", Solvers.LogName(task.Solver));
        Assert.Equal(["verdict", "behaviour", "verdict_one_hot"], task.Scorers.Select(scorer => scorer.Name));
        Assert.Equal(3, task.Epochs!.Count);
        Assert.Null(task.Epochs.Reducers);
        Assert.Null(task.Sandbox);

        // @scorer(metrics=categorical(Verdict)): one frequency metric over every epoch
        var verdict = task.Scorers[0];
        var frequency = Assert.Single(verdict.Metrics);
        Assert.Equal("frequency", frequency.Name);
        Assert.Equal(MetricScores.Unreduced, frequency.Scores);
        Assert.Null(verdict.MetricsByKey);

        // @scorer(metrics={"sabotage_type": categorical(SabotageType), "eval_aware": categorical(Verdict)})
        var behaviour = task.Scorers[1];
        Assert.Empty(behaviour.Metrics);
        Assert.Equal(["sabotage_type", "eval_aware"], behaviour.MetricsByKey!.Keys);
        Assert.Equal("frequency", Assert.Single(behaviour.MetricsByKey["sabotage_type"]).Name);
        Assert.Equal("frequency", Assert.Single(behaviour.MetricsByKey["eval_aware"]).Name);

        // @scorer(metrics={"*": [accuracy()]})
        var oneHot = task.Scorers[2];
        Assert.Empty(oneHot.Metrics);
        Assert.Equal(["*"], oneHot.MetricsByKey!.Keys);
        Assert.Equal("accuracy", Assert.Single(oneHot.MetricsByKey["*"]).Name);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_as_categorical_demo()
    {
        var method = typeof(CategoricalDemoExample).GetMethod(nameof(CategoricalDemoExample.CategoricalDemoTask));

        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        Assert.Equal("categorical_demo", method.GetCustomAttribute<TaskAttribute>()!.Name);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void the_example_needs_no_sandbox_and_plays_mockllm()
    {
        var example = new CategoricalDemoExample();

        Assert.Equal("categorical_demo", example.Name);
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.FakeSandbox(Context()));
        Assert.Equal("categorical_demo", Assert.Single(example.Tasks).Name);
        Assert.NotEmpty(example.Deviations);
        Assert.Equal(40, example.Tasks[0].Build(Context()).Dataset.Count);
        Assert.Equal("mockllm/model", example.CreateFakeModel(Context()).Name);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the enums and the draws
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_enums_carry_the_python_strenum_values()
    {
        Assert.Equal(VerdictLabels, Metrics.CategoryNames<Verdict>());
        Assert.Equal(SabotageLabels, Metrics.CategoryNames<SabotageType>());
        Assert.Equal("unsure", Metrics.CategoryName(Verdict.Unsure));
        Assert.Equal("none", Metrics.CategoryName(SabotageType.None));
    }

    [Fact]
    public void the_seed_mixes_the_sample_id_and_the_epoch()
    {
        Assert.Equal(CategoricalDemoExample.Seed(7, 2), CategoricalDemoExample.Seed(7, 2));
        Assert.Equal(CategoricalDemoExample.Seed(7, 2), CategoricalDemoExample.Seed("7", 2));
        Assert.Equal(CategoricalDemoExample.Seed(7, 2), CategoricalDemoExample.Seed(7L, 2));
        Assert.NotEqual(CategoricalDemoExample.Seed(7, 2), CategoricalDemoExample.Seed(7, 3));
        Assert.NotEqual(CategoricalDemoExample.Seed(7, 2), CategoricalDemoExample.Seed(8, 2));
        Assert.Equal(CategoricalDemoExample.Seed("abc", 1), CategoricalDemoExample.Seed("abc", 1));
        Assert.NotEqual(CategoricalDemoExample.Seed("abc", 1), CategoricalDemoExample.Seed("abd", 1));
    }

    [Fact]
    public void the_draws_are_deterministic_and_follow_the_weights()
    {
        const int draws = 20_000;
        Assert.Equal(CategoricalDemoExample.DrawVerdict(42), CategoricalDemoExample.DrawVerdict(42));
        Assert.Equal(CategoricalDemoExample.DrawSabotage(-42), CategoricalDemoExample.DrawSabotage(-42));

        var verdicts = Enumerable.Range(0, draws).Select(CategoricalDemoExample.DrawVerdict).GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count() / (double)draws);
        Assert.Equal(0.55, verdicts[Verdict.Yes], 0.02);
        Assert.Equal(0.30, verdicts[Verdict.No], 0.02);
        Assert.Equal(0.15, verdicts[Verdict.Unsure], 0.02);

        var sabotage = Enumerable.Range(0, draws).Select(CategoricalDemoExample.DrawSabotage).GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count() / (double)draws);
        Assert.Equal(0.60, sabotage[SabotageType.None], 0.02);
        Assert.Equal(0.30, sabotage[SabotageType.Subtle], 0.02);
        Assert.Equal(0.10, sabotage[SabotageType.Overt], 0.02);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scorers
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task verdict_scores_the_seeded_category_and_ignores_the_output()
    {
        var expected = Metrics.CategoryName(CategoricalDemoExample.DrawVerdict(CategoricalDemoExample.Seed(3, 2)));

        var score = await CategoricalDemoExample.VerdictScorer().Score(State(3, 2), new Target("yes"), CancellationToken.None);
        var other = await CategoricalDemoExample.VerdictScorer().Score(State(3, 2, "a completely different completion"), new Target("no"), CancellationToken.None);

        Assert.Contains(expected, VerdictLabels);
        Assert.Equal(expected, score.Value.Text);
        Assert.Equal(expected, score.Answer);
        Assert.Equal($"Grader judged the response as '{expected}'.", score.Explanation);
        Assert.Equal(score.Value, other.Value);
    }

    [Fact]
    public async Task behaviour_scores_two_categorical_dimensions_in_one_dictionary()
    {
        var seed = CategoricalDemoExample.Seed(5, 1);
        var sabotage = Metrics.CategoryName(CategoricalDemoExample.DrawSabotage(seed));
        var aware = Metrics.CategoryName(CategoricalDemoExample.DrawVerdict(unchecked(seed * 31)));

        var score = await CategoricalDemoExample.BehaviourScorer().Score(State(5, 1), new Target("yes"), CancellationToken.None);

        var value = Assert.IsType<ScoreValue.Dict>(score.Value);
        Assert.Equal(["sabotage_type", "eval_aware"], value.Items.Keys);
        Assert.Equal(sabotage, value.Items["sabotage_type"]!.Text);
        Assert.Equal(aware, value.Items["eval_aware"]!.Text);
        Assert.Contains(sabotage, SabotageLabels);
        Assert.Contains(aware, VerdictLabels);
        Assert.Null(score.Answer);
        Assert.Equal($"Classified sabotage_type='{sabotage}', eval_aware='{aware}'.", score.Explanation);
    }

    [Fact]
    public async Task verdict_one_hot_encodes_the_same_verdict_as_a_dictionary()
    {
        var verdict = await CategoricalDemoExample.VerdictScorer().Score(State(11, 3), new Target("yes"), CancellationToken.None);

        var score = await CategoricalDemoExample.VerdictOneHotScorer().Score(State(11, 3), new Target("yes"), CancellationToken.None);

        var value = Assert.IsType<ScoreValue.Dict>(score.Value);
        Assert.Equal(VerdictLabels, value.Items.Keys);
        var hot = value.Items.Where(pair => Assert.IsType<ScoreValue.Num>(pair.Value).Value == 1).Select(pair => pair.Key).ToList();
        Assert.Equal([verdict.Value.Text], hot);
        Assert.Equal(1.0, value.Items.Values.Sum(item => ((ScoreValue.Num)item!).Value));
        Assert.Equal(verdict.Value.Text, score.Answer);
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end (port of `inspect eval examples/categorical_demo.py --model mockllm/model`)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_demo_runs_offline_and_reports_a_frequency_per_category()
    {
        var task = CategoricalDemoExample.CategoricalDemoTask();

        var log = await Eval.RunAsync(task, new EvalOptions
        {
            Model = CategoricalDemoExample.CreateMockModel(),
            LogDir = _logDir,
            LogFormat = LogFormat.Eval,
        });

        if (log.Status != EvalStatus.Success)
        {
            Assert.Fail(Describe(log));
        }

        Assert.Equal("mockllm/model", log.Eval.Model);
        Assert.Equal(120, log.Results!.TotalSamples);
        Assert.Equal(120, log.Results.CompletedSamples);
        Assert.True(File.Exists(log.Location), $"the log was not written: {log.Location}");

        var samples = log.Samples!;
        Assert.Equal(120, samples.Count);
        Assert.All(samples, sample => Assert.Null(sample.Error));
        Assert.All(samples, sample => Assert.Equal(CategoricalDemoExample.MockDefaultOutput, sample.Output.Completion));
        Assert.All(samples, sample => Assert.Equal(["verdict", "behaviour", "verdict_one_hot"], sample.Scores!.Keys));
        // each sample's verdict is the seeded draw for its (id, epoch)
        Assert.All(samples, sample => Assert.Equal(
            Metrics.CategoryName(CategoricalDemoExample.DrawVerdict(CategoricalDemoExample.Seed(sample.Id, sample.Epoch))),
            sample.Scores!["verdict"].Value.Text));

        var scores = log.Results.Scores.ToDictionary(score => (score.Name, score.Scorer));
        Assert.Equal(
            [("verdict", "verdict"), ("sabotage_type", "behaviour"), ("eval_aware", "behaviour"), ("yes", "verdict_one_hot"), ("no", "verdict_one_hot"), ("unsure", "verdict_one_hot")],
            log.Results.Scores.Select(score => (score.Name, score.Scorer)));

        // verdict: frequency() over all 120 (sample, epoch) observations, one entry per category, summing to 1
        var verdict = scores[("verdict", "verdict")];
        Assert.Equal(VerdictLabels, verdict.Metrics.Keys);
        Assert.All(verdict.Metrics.Values, metric => Assert.Equal("frequency", metric.Group));
        Assert.Equal(1.0, verdict.Metrics.Values.Sum(metric => metric.Value), 9);
        Assert.InRange(verdict.Metrics["yes"].Value, 0.35, 0.75);
        Assert.Equal(Fraction(samples, "yes"), verdict.Metrics["yes"].Value, 9);

        // behaviour: one score per key of the dictionary value, each with its own frequency table
        var sabotage = scores[("sabotage_type", "behaviour")];
        Assert.Equal(["frequency_none", "frequency_subtle", "frequency_overt"], sabotage.Metrics.Keys);
        Assert.Equal(SabotageLabels, sabotage.Metrics.Values.Select(metric => metric.Name));
        Assert.All(sabotage.Metrics.Values, metric => Assert.Equal("frequency", metric.Group));
        Assert.Equal(1.0, sabotage.Metrics.Values.Sum(metric => metric.Value), 9);
        var aware = scores[("eval_aware", "behaviour")];
        Assert.Equal(["frequency_yes", "frequency_no", "frequency_unsure"], aware.Metrics.Keys);
        Assert.Equal(1.0, aware.Metrics.Values.Sum(metric => metric.Value), 9);

        // verdict_one_hot: {"*": [accuracy()]} expands to one accuracy per key; the three accuracies partition the samples
        // and (epochs being equal in size) agree with the verdict frequencies
        foreach (var label in VerdictLabels)
        {
            var oneHot = scores[(label, "verdict_one_hot")];
            var accuracy = Assert.Single(oneHot.Metrics.Values);
            Assert.Equal("accuracy", accuracy.Name);
            Assert.Equal(verdict.Metrics[label].Value, accuracy.Value, 9);
        }

        Assert.Equal(1.0, VerdictLabels.Sum(label => scores[(label, "verdict_one_hot")].Metrics["accuracy"].Value), 9);
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["categorical_demo", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("status    : success (120/120 samples completed)", text, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------------------------

    private static ExampleContext Context() =>
        new(ExampleRunner.ExampleDirectory(new CategoricalDemoExample()), null, true, new Dictionary<string, string>(), null, null, TextWriter.Null);

    private static TaskState State(int sampleId, int epoch, string? completion = null) =>
        new(
            "mockllm/model",
            sampleId,
            epoch,
            $"Sample question {sampleId - 1}",
            [new ChatMessageUser($"Sample question {sampleId - 1}")],
            target: new Target("yes"),
            output: ModelOutput.FromContent("mockllm/model", completion ?? CategoricalDemoExample.MockDefaultOutput));

    private static double Fraction(IReadOnlyList<EvalSample> samples, string verdict) =>
        samples.Count(sample => sample.Scores!["verdict"].Value.Text == verdict) / (double)samples.Count;

    private static string Describe(EvalLog log)
    {
        var lines = new List<string> { $"status {log.Status}: {log.Error?.Message ?? "(no error)"}" };
        foreach (var sample in log.Samples ?? [])
        {
            lines.Add($"sample {sample.Id} (epoch {sample.Epoch}): {sample.Error?.Message ?? "ok"}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
