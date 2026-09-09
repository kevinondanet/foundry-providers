using System.Reflection;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Examples.Simpleqa;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Simpleqa;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/simpleqa.py</c> (<see cref="SimpleqaExample"/>): the task's shape over the canned
/// SimpleQA-Verified page, the canned datasets-server, the scripted model and its grader, and the offline run end to end
/// through <c>model_graded_qa</c>.
/// </summary>
public sealed class SimpleqaTests : IDisposable
{
    private const string FirstQuestion = "How much money, in euros, was the surgeon held responsible for Stella Obasanjo's death ordered to pay her son?";

    private static readonly string RowsPath = Path.Combine(AppContext.BaseDirectory, "simpleqa", SimpleqaExample.SampleRowsFile);

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "simpleqa-" + Guid.NewGuid().ToString("N"));

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "simpleqa-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var directory in new[] { _logDir, _cacheDir })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // the task (port of @task def simpleqa)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_shaped_like_the_python_simpleqa_task()
    {
        Assert.True(File.Exists(RowsPath), $"the canned rows were not copied next to the test assembly: {RowsPath}");
        var task = SimpleqaExample.Build(SimpleqaExample.SampleDataset(RowsPath));

        Assert.Equal("simpleqa", task.Name);
        Assert.Equal(5, task.Dataset.Count);
        Assert.Equal(SimpleqaExample.DatasetPath, task.Dataset.Name);
        Assert.False(task.Dataset.Shuffled);
        // FieldSpec(input="problem", target="answer")
        Assert.Equal(FirstQuestion, task.Dataset[0].Input.Text);
        Assert.Equal("120,000 euros", task.Dataset[0].Target.Text);
        Assert.Equal("Jóhanna Sigurðardóttir", task.Dataset[1].Target.Text);

        Assert.Equal("generate", Solvers.LogName(task.Solver));
        var scorer = Assert.Single(task.Scorers);
        Assert.Equal("model_graded_qa", scorer.Name);
        Assert.Equal(["accuracy", "stderr"], scorer.Metrics.Select(metric => metric.Name));
        Assert.Null(task.Sandbox);
        Assert.Null(task.Epochs);
        Assert.Null(task.Config.Temperature);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_as_simpleqa()
    {
        var method = typeof(SimpleqaExample).GetMethod(nameof(SimpleqaExample.SimpleqaTask));

        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        Assert.Equal("simpleqa", method.GetCustomAttribute<TaskAttribute>()!.Name);
        Assert.Empty(method.GetParameters());
    }

    [Fact]
    public void the_example_declares_no_sandbox_and_lists_its_deviations()
    {
        var example = new SimpleqaExample();

        Assert.Equal("simpleqa", example.Name);
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.FakeSandbox(Context()));
        Assert.Equal("simpleqa", Assert.Single(example.Tasks).Name);
        Assert.NotEmpty(example.Deviations);
        Assert.Equal("simpleqa-scripted", example.CreateFakeModel(Context()).Name);
        Assert.Equal(5, example.Tasks[0].Build(Context()).Dataset.Count);
    }

    // ----------------------------------------------------------------------------------------------------------
    // hf_dataset over the canned datasets-server
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_canned_hub_answers_splits_and_rows_like_the_datasets_server()
    {
        using var hub = new CannedHfHub(SimpleqaExample.DatasetPath, SimpleqaExample.DatasetSplit, RowsPath);
        using var loader = new HfDatasetLoader(handler: hub, cacheDir: _cacheDir, endpoint: "https://fake-datasets-server.test");

        var dataset = await loader.LoadAsync(new HfDatasetRequest(SimpleqaExample.DatasetPath, SimpleqaExample.DatasetSplit) { SampleFields = SimpleqaExample.Fields, Cached = false });

        Assert.Equal(5, dataset.Count);
        Assert.Equal(2, hub.Requests.Count);
        Assert.Contains("/splits?dataset=codelion%2FSimpleQA-Verified", hub.Requests[0], StringComparison.Ordinal);
        Assert.Contains("/rows?dataset=codelion%2FSimpleQA-Verified&config=default&split=train&offset=0&length=100", hub.Requests[1], StringComparison.Ordinal);
        // the other columns of the split travel with the row (the FieldSpec only maps problem and answer)
        Assert.Equal("Politics", hub.Rows[0]["topic"]!.GetValue<string>());
        Assert.Null(dataset[0].Metadata);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scripted model
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_fake_grader_grades_c_when_the_submission_contains_the_criterion()
    {
        Assert.EndsWith("GRADE: C", FakeSimpleqaModel.Grade(GradingPrompt("The surgeon was ordered to pay 120,000 euros.", "120,000 euros")), StringComparison.Ordinal);
        Assert.EndsWith("GRADE: C", FakeSimpleqaModel.Grade(GradingPrompt("the coast guard", "The Coast Guard")), StringComparison.Ordinal);
        Assert.EndsWith("GRADE: I", FakeSimpleqaModel.Grade(GradingPrompt("Omar Abdullah", "Hasnain Masoodi")), StringComparison.Ordinal);
        Assert.EndsWith("GRADE: I", FakeSimpleqaModel.Grade("[BEGIN DATA] nothing to grade [END DATA]"), StringComparison.Ordinal);
    }

    [Fact]
    public void the_fake_model_answers_rows_three_and_five_wrongly()
    {
        Assert.Equal([false, false, true, false, true], Enumerable.Range(0, 5).Select(FakeSimpleqaModel.AnswersWrongly));
        Assert.Equal("120,000 euros", FakeSimpleqaModel.Answer(0, "120,000 euros"));
        Assert.Equal("Omar Abdullah", FakeSimpleqaModel.Answer(2, "Hasnain Masoodi"));
        Assert.Equal("The Environmental Protection Agency", FakeSimpleqaModel.Answer(4, "The Coast Guard"));
        Assert.Equal("2023", FakeSimpleqaModel.Answer(3, "2023"));
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end (offline)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_task_runs_offline_and_model_graded_qa_scores_three_of_five_correct()
    {
        var example = new SimpleqaExample();
        var context = Context();
        var task = example.Tasks[0].Build(context);

        var log = await Eval.RunAsync(task, new EvalOptions
        {
            Model = example.CreateFakeModel(context),
            LogDir = _logDir,
            LogFormat = LogFormat.Eval,
        });

        if (log.Status != EvalStatus.Success)
        {
            Assert.Fail(Describe(log));
        }

        Assert.Equal(5, log.Results!.TotalSamples);
        Assert.Equal(5, log.Results.CompletedSamples);
        Assert.True(File.Exists(log.Location), $"the log was not written: {log.Location}");

        var evalScore = Assert.Single(log.Results.Scores);
        Assert.Equal("model_graded_qa", evalScore.Name);
        Assert.Equal(0.6, evalScore.Metrics["accuracy"].Value, 6);
        Assert.Equal(0.2449, evalScore.Metrics["stderr"].Value, 3);

        var samples = log.Samples!;
        Assert.Equal(5, samples.Count);
        Assert.All(samples, sample => Assert.Null(sample.Error));
        Assert.Equal(["C", "C", "I", "C", "I"], samples.Select(sample => sample.Scores!["model_graded_qa"].Value.Text));

        // model_graded_qa records the submission as the answer and the grader's reply as the explanation
        var first = samples[0].Scores!["model_graded_qa"];
        Assert.Equal(FirstQuestion, samples[0].Input.Text);
        Assert.Equal("120,000 euros", first.Answer);
        Assert.EndsWith("GRADE: C", first.Explanation, StringComparison.Ordinal);
        Assert.True(first.Metadata!.ContainsKey("grading"));
        var third = samples[2].Scores!["model_graded_qa"];
        Assert.Equal("Omar Abdullah", third.Answer);
        Assert.EndsWith("GRADE: I", third.Explanation, StringComparison.Ordinal);

        // the model was asked the bare question (generate() with no template) and then consulted as the grader
        Assert.All(samples, sample => Assert.Equal(sample.Input.Text, Assert.IsType<ChatMessageUser>(sample.Messages[0]).Text));
        Assert.All(samples, sample => Assert.Equal(sample.Output.Completion, sample.Scores!["model_graded_qa"].Answer));
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["simpleqa", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("status    : success (5/5 samples completed)", text, StringComparison.Ordinal);
        Assert.Contains("model_graded_qa", text, StringComparison.Ordinal);
        Assert.Contains("0.600", text, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------------------------

    private static ExampleContext Context() =>
        new(ExampleRunner.ExampleDirectory(new SimpleqaExample()), null, true, new Dictionary<string, string>(), null, null, TextWriter.Null);

    /// <summary>The <c>DEFAULT_MODEL_GRADED_QA_TEMPLATE</c> data block as <c>model_graded_qa</c> fills it.</summary>
    private static string GradingPrompt(string submission, string criterion) =>
        ModelGraded.DefaultQaTemplate
            .Replace("{question}", FirstQuestion, StringComparison.Ordinal)
            .Replace("{answer}", submission, StringComparison.Ordinal)
            .Replace("{criterion}", criterion, StringComparison.Ordinal)
            .Replace("{instructions}", "Answer with GRADE: C or GRADE: I.", StringComparison.Ordinal);

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
