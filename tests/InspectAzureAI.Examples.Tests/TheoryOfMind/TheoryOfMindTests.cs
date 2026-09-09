using System.Reflection;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Examples.TheoryOfMind;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.TheoryOfMind;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/theory_of_mind.py</c> (<see cref="TheoryOfMindExample"/>): the task's shape
/// with and without <c>critique</c>, the scripted model's handling of the four prompts of the task, and the eval
/// run end to end without a network, through <c>Eval.RunAsync</c> and through the examples runner.
/// </summary>
public sealed class TheoryOfMindTests : IDisposable
{
    /// <summary>The first record of the bundled <c>theory_of_mind.jsonl</c>, verbatim.</summary>
    private const string FirstQuestion =
        "Jackson entered the hall. Chloe entered the hall. The boots is in the bathtub. Jackson exited the hall. Jackson entered the dining_room. Chloe moved the boots to the pantry. Where was the boots at the beginning?";

    private const string FirstTarget = "bathtub";

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "theory-of-mind-" + Guid.NewGuid().ToString("N"));

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
    // the task (port of @task def theory_of_mind(critique=False))
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_task()
    {
        var task = TheoryOfMindExample.TheoryOfMind();

        Assert.Equal("theory_of_mind", task.Name);
        Assert.Equal("theory_of_mind", task.Dataset.Name);
        Assert.Equal(100, task.Dataset.Count);
        var first = task.Dataset[0];
        Assert.Equal(FirstQuestion, first.Input.ToString());
        Assert.Equal(new Target(FirstTarget), first.Target);
        Assert.Equal("model_graded_fact", Assert.Single(task.Scorers).Name);
        Assert.Null(task.Sandbox);
        Assert.Equal(false, task.TaskArgs!["critique"]);
        Assert.Equal(true, TheoryOfMindExample.TheoryOfMind(critique: true).TaskArgs!["critique"]);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_with_a_critique_parameter()
    {
        var method = typeof(TheoryOfMindExample).GetMethod(nameof(TheoryOfMindExample.TheoryOfMind));

        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        Assert.Equal("theory_of_mind", method.GetCustomAttribute<TaskAttribute>()!.Name);
        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal("critique", parameter.Name);
        Assert.Equal(typeof(bool), parameter.ParameterType);
        Assert.Equal(false, parameter.DefaultValue);
    }

    [Fact]
    public void the_example_is_registered_without_a_sandbox_and_reads_critique_from_the_task_args()
    {
        var example = Assert.IsType<TheoryOfMindExample>(ExampleRegistry.Default.Get("theory_of_mind"));

        Assert.Equal("theory_of_mind", Assert.Single(example.Tasks).Name);
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.NotEmpty(example.Deviations);
        var ctx = new ExampleContext("/x", null, true, new Dictionary<string, string> { ["critique"] = "true" }, null, null, TextWriter.Null);
        Assert.Null(example.FakeSandbox(ctx));
        Assert.Equal(true, example.Tasks[0].Build(ctx).TaskArgs!["critique"]);
        Assert.Equal(false, example.Tasks[0].Build(ctx with { TaskArgs = new Dictionary<string, string>() }).TaskArgs!["critique"]);
        Assert.Equal(FakeTheoryOfMindModel.ModelName, example.CreateFakeModel(ctx).Name);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scripted model
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_scripted_model_answers_the_four_prompts_of_the_task()
    {
        var cot = Fill(Solvers.DefaultCotTemplate, ("prompt", FirstQuestion));
        var first = Respond(cot);
        Assert.Equal(FakeTheoryOfMindModel.PromptKind.ChainOfThought, FakeTheoryOfMindModel.Classify(cot));
        Assert.EndsWith("ANSWER: bathtub", first);

        var critique = Fill(Solvers.DefaultCritiqueTemplate, ("question", FirstQuestion), ("completion", first));
        Assert.Equal(FakeTheoryOfMindModel.PromptKind.Critique, FakeTheoryOfMindModel.Classify(critique));
        Assert.Equal("The original answer is fully correct", Respond(critique));

        var improved = Fill(Solvers.DefaultCritiqueCompletionTemplate, ("question", FirstQuestion), ("completion", first), ("critique", "The original answer is fully correct"));
        Assert.Equal(FakeTheoryOfMindModel.PromptKind.ImprovedAnswer, FakeTheoryOfMindModel.Classify(improved));
        Assert.Equal("ANSWER: bathtub", Respond(improved));

        var grading = Fill(ModelGraded.DefaultFactTemplate, ("question", FirstQuestion), ("answer", first), ("criterion", FirstTarget), ("instructions", ModelGraded.DefaultInstructions(false)));
        Assert.Equal(FakeTheoryOfMindModel.PromptKind.Grade, FakeTheoryOfMindModel.Classify(grading));
        Assert.EndsWith("GRADE: C", Respond(grading));

        var wrong = Fill(ModelGraded.DefaultFactTemplate, ("question", FirstQuestion), ("answer", "ANSWER: pantry"), ("criterion", FirstTarget), ("instructions", ModelGraded.DefaultInstructions(false)));
        Assert.EndsWith("GRADE: I", Respond(wrong));

        Assert.Equal(FakeTheoryOfMindModel.UnknownAnswer, FakeTheoryOfMindModel.TargetFor("not a question of the dataset"));
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end, offline
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_task_runs_offline_without_critique()
    {
        var log = await Run(critique: false, limit: 5);

        Assert.Equal(5, log.Results!.CompletedSamples);
        Assert.Equal(1.0, Accuracy(log));
        var samples = log.Samples!;
        Assert.Equal(5, samples.Count);
        Assert.Equal(false, log.Eval.TaskArgs["critique"]);
        foreach (var sample in samples)
        {
            Assert.Null(sample.Error);
            Assert.Equal("C", sample.Scores!["model_graded_fact"].Text);
            // chain_of_thought rewrote the user prompt, generate answered it; the grader's call is not part of the conversation.
            Assert.Equal(["user", "assistant"], sample.Messages.Select(message => message.Role));
            Assert.Contains("Before answering, reason in a step-by-step manner", sample.Messages[0].Text);
            Assert.Contains(sample.Input.ToString(), sample.Messages[0].Text);
            Assert.EndsWith($"ANSWER: {sample.Target.Text}", sample.Output.Completion);
            Assert.Equal(2, sample.Events.OfType<ModelEvent>().Count());
        }
    }

    [Fact]
    public async Task the_task_runs_offline_with_critique()
    {
        var log = await Run(critique: true, limit: 4);

        Assert.Equal(4, log.Results!.CompletedSamples);
        Assert.Equal(1.0, Accuracy(log));
        Assert.Equal(true, log.Eval.TaskArgs["critique"]);
        foreach (var sample in log.Samples!)
        {
            Assert.Null(sample.Error);
            Assert.Equal("C", sample.Scores!["model_graded_fact"].Text);
            // self_critique appended the critique-completion prompt and generated again.
            Assert.Equal(["user", "assistant", "user", "assistant"], sample.Messages.Select(message => message.Role));
            Assert.Contains("[Critique]: The original answer is fully correct", sample.Messages[2].Text);
            Assert.Equal($"ANSWER: {sample.Target.Text}", sample.Output.Completion);
            // answer, critique, improved answer, grade
            Assert.Equal(4, sample.Events.OfType<ModelEvent>().Count());
        }
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(
            ["theory_of_mind", "--fake", "--limit", "3", "-T", "critique=true", "--log-dir", _logDir],
            ExampleRegistry.Default,
            output,
            output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : theory_of_mind", text);
        Assert.Contains("sandbox   : none", text);
        Assert.Contains("task args : critique=true", text);
        Assert.Contains("status    : success (3/3 samples completed)", text);
        Assert.Contains("model_graded_fact", text);
        Assert.Contains("accuracy", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }

    // ----------------------------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------------------------

    private static string Respond(string prompt) => FakeTheoryOfMindModel.Respond([new ChatMessageUser(prompt)], []).Completion;

    /// <summary>Python's <c>str.format</c> for the templates' simple <c>{name}</c> placeholders.</summary>
    private static string Fill(string template, params (string Name, string Value)[] variables)
    {
        foreach (var (name, value) in variables)
        {
            template = template.Replace("{" + name + "}", value, StringComparison.Ordinal);
        }

        return template;
    }

    private async Task<EvalLog> Run(bool critique, int limit)
    {
        var log = await Eval.RunAsync(
            TheoryOfMindExample.TheoryOfMind(critique),
            new EvalOptions
            {
                Model = FakeTheoryOfMindModel.Create(),
                Limit = limit,
                LogDir = _logDir,
                LogFormat = LogFormat.Eval,
            },
            CancellationToken.None);

        Assert.True(log.Status == EvalStatus.Success, $"status {log.Status}: {log.Error?.Message ?? "(no error)"}");
        Assert.NotNull(log.Location);
        Assert.True(File.Exists(log.Location), $"the log was not written: {log.Location}");
        return log;
    }

    private static double Accuracy(EvalLog log)
    {
        var score = Assert.Single(log.Results!.Scores);
        Assert.Equal("model_graded_fact", score.Name);
        return score.Metrics["accuracy"].Value;
    }
}
