using System.Reflection;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Examples.ScorerDemo;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.ScorerDemo;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the port of <c>examples/scorer.py</c> (<see cref="ScorerExample"/>): the <c>math</c> task's shape over the canned
/// MATH-500 page, the <c>shuffle</c> task argument, the verbatim templates, the <c>expression_equivalence</c> scorer's three
/// branches (equivalent, not equivalent, no answer line), the scripted model, and the offline run end to end.
/// </summary>
public sealed class ScorerTests : IDisposable
{
    private const string Problem196 = "How many positive whole-number divisors does 196 have?";

    private const string Solution196 = "First prime factorize $196=2^2\\cdot7^2$. There are $\\boxed{9}$ divisors of 196.";

    private static readonly string RowsPath = Path.Combine(AppContext.BaseDirectory, "scorer", ScorerExample.SampleRowsFile);

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "scorer-" + Guid.NewGuid().ToString("N"));

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "scorer-cache-" + Guid.NewGuid().ToString("N"));

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
    // the task (port of @task def math(shuffle=True))
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_shaped_like_the_python_math_task()
    {
        Assert.True(File.Exists(RowsPath), $"the canned rows were not copied next to the test assembly: {RowsPath}");
        var task = ScorerExample.Build(ScorerExample.SampleDataset(RowsPath, shuffle: false));

        Assert.Equal("math", task.Name);
        Assert.Equal(5, task.Dataset.Count);
        Assert.Equal(ScorerExample.DatasetPath, task.Dataset.Name);
        Assert.False(task.Dataset.Shuffled);
        // FieldSpec(input="problem", target="solution"): the target is the worked solution, not the boxed answer
        Assert.StartsWith("Convert the point $(0,3)$ in rectangular coordinates to polar coordinates.", task.Dataset[0].Input.Text, StringComparison.Ordinal);
        Assert.StartsWith("We have that $r = \\sqrt{0^2 + 3^2} = 3.$", task.Dataset[0].Target.Text, StringComparison.Ordinal);
        Assert.Equal(Problem196, task.Dataset[3].Input.Text);
        Assert.Contains("\\boxed{9}", task.Dataset[3].Target.Text, StringComparison.Ordinal);

        Assert.True(Solvers.IsChain(task.Solver));
        var scorer = Assert.Single(task.Scorers);
        Assert.Equal("expression_equivalence", scorer.Name);
        Assert.Equal(["accuracy", "stderr"], scorer.Metrics.Select(metric => metric.Name));
        Assert.Null(scorer.MetricsByKey);
        Assert.Equal(0.5, task.Config.Temperature);
        Assert.Null(task.Sandbox);
        Assert.Null(task.Epochs);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_as_math_with_a_shuffle_argument()
    {
        var method = typeof(ScorerExample).GetMethod(nameof(ScorerExample.MathTask));

        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        Assert.Equal("math", method.GetCustomAttribute<TaskAttribute>()!.Name);
        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal("shuffle", parameter.Name);
        Assert.Equal(typeof(bool), parameter.ParameterType);
        Assert.Equal(true, parameter.DefaultValue);
    }

    [Fact]
    public void shuffle_is_the_task_argument_and_defaults_to_true()
    {
        var example = new ScorerExample();

        Assert.True(example.Tasks[0].Build(Context()).Dataset.Shuffled);
        Assert.False(example.Tasks[0].Build(Context(new Dictionary<string, string> { ["shuffle"] = "false" })).Dataset.Shuffled);
        Assert.True(example.Tasks[0].Build(Context(new Dictionary<string, string> { ["shuffle"] = "True" })).Dataset.Shuffled);
        Assert.Throws<ArgumentException>(() => example.Tasks[0].Build(Context(new Dictionary<string, string> { ["shuffle"] = "maybe" })));
    }

    [Fact]
    public void the_example_declares_no_sandbox_and_lists_its_deviations()
    {
        var example = new ScorerExample();

        Assert.Equal("scorer", example.Name);
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.FakeSandbox(Context()));
        Assert.Equal("math", Assert.Single(example.Tasks).Name);
        Assert.NotEmpty(example.Deviations);
        Assert.Equal("math-scripted", example.CreateFakeModel(Context()).Name);
    }

    // ----------------------------------------------------------------------------------------------------------
    // hf_dataset over the canned datasets-server
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_canned_hub_answers_splits_and_rows_like_the_datasets_server()
    {
        using var hub = new CannedHfHub(ScorerExample.DatasetPath, ScorerExample.DatasetSplit, RowsPath);
        using var loader = new HfDatasetLoader(handler: hub, cacheDir: _cacheDir, endpoint: "https://fake-datasets-server.test");

        var dataset = await loader.LoadAsync(new HfDatasetRequest(ScorerExample.DatasetPath, ScorerExample.DatasetSplit) { SampleFields = ScorerExample.Fields, Cached = false });

        Assert.Equal(5, dataset.Count);
        Assert.Equal(5, hub.Rows.Count);
        Assert.Equal(2, hub.Requests.Count);
        Assert.Contains("/splits?dataset=HuggingFaceH4%2FMATH-500", hub.Requests[0], StringComparison.Ordinal);
        Assert.Contains("/rows?dataset=HuggingFaceH4%2FMATH-500&config=default&split=test&offset=0&length=100", hub.Requests[1], StringComparison.Ordinal);

        // a split the canned page does not hold is refused the way the server reports it
        var unknown = await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(new HfDatasetRequest(ScorerExample.DatasetPath, "train") { Cached = false }));
        Assert.Contains("Unknown split \"train\"", unknown.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the templates and the scorer (port of expression_equivalence)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_templates_are_verbatim()
    {
        Assert.StartsWith("Solve the following math problem step by step. The last line\nof your response should be of the form ANSWER: $ANSWER (without\nquotes)", ScorerExample.PromptTemplate, StringComparison.Ordinal);
        Assert.Contains("\n\n{prompt}\n\n", ScorerExample.PromptTemplate, StringComparison.Ordinal);
        Assert.EndsWith("Remember to put your answer on its own line after \"ANSWER:\",\nand you do not need to use a \\boxed command.", ScorerExample.PromptTemplate, StringComparison.Ordinal);

        Assert.StartsWith("Look at the following two expressions (answers to a math problem)\nand judge whether they are equivalent.", ScorerExample.EquivalenceTemplate, StringComparison.Ordinal);
        Assert.Contains("\n(give benefit of the doubt to units)\n---\n\nYOUR TASK\n\n", ScorerExample.EquivalenceTemplate, StringComparison.Ordinal);
        Assert.EndsWith("    Expression 1: %(expression1)s\n    Expression 2: %(expression2)s", ScorerExample.EquivalenceTemplate, StringComparison.Ordinal);

        var prompt = ScorerExample.EquivalencePrompt("$2x+3$", "$3+2x$");
        Assert.EndsWith("    Expression 1: $2x+3$\n    Expression 2: $3+2x$", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("%(", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Yes", "C")]
    [InlineData("yes", "C")]
    [InlineData("No", "I")]
    [InlineData("Yes.", "I")]
    public async Task the_scorer_extracts_the_answer_line_and_asks_the_model_whether_it_is_equivalent(string judgeReply, string expected)
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text(judgeReply));
        var completion = "The divisors are 1, 2, 4, 7, 14, 28, 49, 98 and 196.\n\nANSWER: 9";
        var state = State(completion, Solution196);

        Score score;
        using (SampleContext.Begin(new SampleContext { ActiveModel = new Model(api) }))
        {
            score = await ScorerExample.ExpressionEquivalence().Score(state, state.Target, CancellationToken.None);
        }

        Assert.Equal(expected, score.Value.Text);
        Assert.Equal("9", score.Answer);
        Assert.Equal(completion, score.Explanation);
        // the judge saw the equivalence template with the target as expression 1 and the extracted answer as expression 2
        var request = Assert.Single(api.Requests);
        var judgePrompt = Assert.Single(request.Input).Text;
        Assert.StartsWith(FakeMathModel.JudgePromptMarker, judgePrompt, StringComparison.Ordinal);
        Assert.EndsWith($"    Expression 1: {Solution196}\n    Expression 2: 9", judgePrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task the_scorer_marks_a_missing_answer_line_incorrect_without_calling_the_model()
    {
        // no SampleContext at all: the scorer must not reach for the model
        var state = State("I could not work this one out.", Solution196);

        var score = await ScorerExample.ExpressionEquivalence().Score(state, state.Target, CancellationToken.None);

        Assert.Equal("I", score.Value.Text);
        Assert.Null(score.Answer);
        Assert.Equal("Answer not found in model output: I could not work this one out.", score.Explanation);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scripted model
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_fake_judge_says_yes_only_when_the_reference_solution_contains_the_answer()
    {
        Assert.Equal("Yes", FakeMathModel.Judge(ScorerExample.EquivalencePrompt(Solution196, "9")));
        Assert.Equal("No", FakeMathModel.Judge(ScorerExample.EquivalencePrompt(Solution196, "9 + 1")));
        Assert.Equal("No", FakeMathModel.Judge(ScorerExample.EquivalencePrompt(Solution196, "")));
        Assert.Equal("No", FakeMathModel.Judge("no expressions here"));
    }

    [Fact]
    public void the_fake_model_follows_its_script()
    {
        Assert.Equal(
            [FakeMathModel.Behaviour.Correct, FakeMathModel.Behaviour.Correct, FakeMathModel.Behaviour.WrongAnswer, FakeMathModel.Behaviour.NoAnswerLine, FakeMathModel.Behaviour.Correct],
            Enumerable.Range(0, 5).Select(FakeMathModel.BehaviourFor));
        Assert.Equal(FakeMathModel.Behaviour.Correct, FakeMathModel.BehaviourFor(7));

        Assert.Equal("9", Regex.Match(FakeMathModel.Solve("9", FakeMathModel.Behaviour.Correct), AnswerPattern.Line).Groups[1].Value);
        Assert.Equal("9 + 1", Regex.Match(FakeMathModel.Solve("9", FakeMathModel.Behaviour.WrongAnswer), AnswerPattern.Line).Groups[1].Value);
        Assert.Equal(FakeMathModel.NoAnswerReply, FakeMathModel.Solve("9", FakeMathModel.Behaviour.NoAnswerLine));
        Assert.DoesNotMatch(AnswerPattern.Line, FakeMathModel.NoAnswerReply);
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end (offline)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_task_runs_offline_and_scores_three_of_five_correct()
    {
        var example = new ScorerExample();
        var context = Context();
        var task = ScorerExample.Build(ScorerExample.SampleDataset(RowsPath, shuffle: false));

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
        Assert.Equal("expression_equivalence", evalScore.Name);
        Assert.Equal("expression_equivalence", evalScore.Scorer);
        Assert.Equal(0.6, evalScore.Metrics["accuracy"].Value, 6);
        Assert.Equal(0.2449, evalScore.Metrics["stderr"].Value, 3);

        var samples = log.Samples!;
        Assert.Equal(5, samples.Count);
        Assert.All(samples, sample => Assert.Null(sample.Error));
        Assert.Equal(["C", "C", "I", "I", "C"], samples.Select(sample => sample.Scores!["expression_equivalence"].Value.Text));

        // every sample was asked through PROMPT_TEMPLATE
        Assert.All(samples, sample =>
        {
            var prompt = Assert.IsType<ChatMessageUser>(sample.Messages[0]).Text;
            Assert.StartsWith("Solve the following math problem step by step.", prompt, StringComparison.Ordinal);
            Assert.Contains(sample.Input.Text!, prompt, StringComparison.Ordinal);
        });

        // the extracted answers and explanations follow the script: right, right, wrong, no answer line, right
        Assert.Equal("\\left( 3, \\frac{\\pi}{2} \\right)", samples[0].Scores!["expression_equivalence"].Answer);
        Assert.Equal("p - q", samples[1].Scores!["expression_equivalence"].Answer);
        Assert.Equal("\\frac{14}{3} + 1", samples[2].Scores!["expression_equivalence"].Answer);
        Assert.Equal(samples[2].Output.Completion, samples[2].Scores!["expression_equivalence"].Explanation);
        Assert.Equal(Problem196, samples[3].Input.Text);
        Assert.Null(samples[3].Scores!["expression_equivalence"].Answer);
        Assert.Equal("Answer not found in model output: " + FakeMathModel.NoAnswerReply, samples[3].Scores!["expression_equivalence"].Explanation);
        Assert.Equal("\\text{Evelyn}", samples[4].Scores!["expression_equivalence"].Answer);
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["scorer", "--fake", "-T", "shuffle=false", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("status    : success (5/5 samples completed)", text, StringComparison.Ordinal);
        Assert.Contains("task args : shuffle=false", text, StringComparison.Ordinal);
        Assert.Contains("0.600", text, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------------------------

    private static ExampleContext Context(IReadOnlyDictionary<string, string>? taskArgs = null) =>
        new(ExampleRunner.ExampleDirectory(new ScorerExample()), null, true, taskArgs ?? new Dictionary<string, string>(), null, null, TextWriter.Null);

    private static TaskState State(string completion, string target) =>
        new("math-scripted", 1, 1, Problem196, [new ChatMessageUser(Problem196)], target: new Target(target), output: ModelOutput.FromContent("math-scripted", completion));

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
