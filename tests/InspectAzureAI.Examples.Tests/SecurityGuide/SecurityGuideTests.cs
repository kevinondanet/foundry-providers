using System.Reflection;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Examples.SecurityGuide;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.SecurityGuide;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/security_guide.py</c> <c>security_guide</c> (<see cref="SecurityGuideExample"/>):
/// the task's shape (the bundled dataset with message-list inputs, the system message, <c>model_graded_fact()</c>),
/// the scripted model that answers and grades, and the eval run end to end without a network, through
/// <c>Eval.RunAsync</c> and through the examples runner.
/// </summary>
public sealed class SecurityGuideTests : IDisposable
{
    private const string SqlQuestion = "How do I prevent SQL Injection attacks?";

    private const string SqlAnswer = "use parameterized queries and prepared statements";

    /// <summary>The three questions the scripted model answers vaguely (they use abbreviations it does not know).</summary>
    private static readonly string[] AbbreviatedQuestions = ["How do I prevent sqli?", "How do I prevent xss?", "How do I prevent cmd injection?"];

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "security-guide-" + Guid.NewGuid().ToString("N"));

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
    // the task (port of @task def security_guide)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_security_guide()
    {
        var task = SecurityGuideExample.SecurityGuide();

        Assert.Equal("security_guide", task.Name);
        Assert.Equal(16, task.Dataset.Count);
        Assert.Equal("security_guide", task.Dataset.Name);
        Assert.Equal("example://security_guide", task.Dataset.Location);
        var scorer = Assert.Single(task.Scorers);
        Assert.Equal("model_graded_fact", scorer.Name);
        Assert.Equal(["accuracy", "stderr"], scorer.Metrics.Select(metric => metric.Name));
        Assert.Null(task.Sandbox);
    }

    [Fact]
    public void the_dataset_records_are_message_lists_with_a_target()
    {
        var task = SecurityGuideExample.SecurityGuide();

        var first = task.Dataset[0];
        Assert.False(first.Input.IsText);
        var message = Assert.Single(first.Input.Messages!);
        Assert.IsType<ChatMessageUser>(message);
        Assert.Equal(SqlQuestion, message.Text);
        Assert.Equal(SqlAnswer, first.Target.Text);
        Assert.All(task.Dataset, sample =>
        {
            Assert.Single(sample.Input.Messages!);
            Assert.NotEmpty(sample.Target.Text);
        });
        Assert.All(AbbreviatedQuestions, question => Assert.Contains(task.Dataset, sample => sample.Input.Messages![0].Text == question));
    }

    [Fact]
    public void the_system_message_is_verbatim()
    {
        Assert.Equal(
            "\nYou are a computer security expert and you are tasked with providing concise response to the following questions. Please assume that the reader is also well versed in computer security and provide a short response in a few words.\n",
            SecurityGuideExample.SystemMessage);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_as_security_guide()
    {
        var method = typeof(SecurityGuideExample).GetMethod(nameof(SecurityGuideExample.SecurityGuide), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        var attribute = method!.GetCustomAttribute<TaskAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal("security_guide", attribute!.Name);
    }

    [Fact]
    public void the_example_is_registered_with_no_sandbox_and_no_fake_sandbox_script()
    {
        var example = Assert.IsType<SecurityGuideExample>(ExampleRegistry.Default.Get("security_guide"));

        Assert.Equal(["security_guide"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.NotEmpty(example.Deviations);
        var ctx = new ExampleContext("/x", null, true, new Dictionary<string, string>(), null, null, TextWriter.Null);
        Assert.Null(example.FakeSandbox(ctx));
        Assert.Equal("security_guide", example.Tasks[0].Build(ctx).Name);
        Assert.Equal(FakeSecurityGuideModel.ModelName, example.CreateFakeModel(ctx).Name);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scripted model (answers and grades)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_fake_answer_is_the_expert_answer_except_for_abbreviated_or_unknown_questions()
    {
        Assert.Equal(SqlAnswer, FakeSecurityGuideModel.Answer(SqlQuestion));
        Assert.Equal("avoid unsafe-eval and unsafe-inline", FakeSecurityGuideModel.Answer("What security attributes should I avoid when using content security policy (CSP)?"));
        Assert.All(AbbreviatedQuestions, question => Assert.Equal(FakeSecurityGuideModel.VagueAnswer, FakeSecurityGuideModel.Answer(question)));
        Assert.Equal(FakeSecurityGuideModel.VagueAnswer, FakeSecurityGuideModel.Answer("What is the meaning of life?"));
    }

    [Fact]
    public void the_fake_grader_reads_the_expert_and_submission_sections_of_the_grading_prompt()
    {
        var output = ModelOutput.FromContent("m", SqlAnswer);
        var metadata = new Dictionary<string, object?>();
        var instructions = ModelGraded.DefaultInstructions(partialCredit: false);
        var matching = ModelGraded.ModelScoringPrompt(ModelGraded.DefaultFactTemplate, SqlQuestion, output, SqlAnswer, instructions, metadata).Text;
        var restyled = ModelGraded.ModelScoringPrompt(ModelGraded.DefaultFactTemplate, SqlQuestion, ModelOutput.FromContent("m", "  Use parameterized queries and prepared statements.  "), SqlAnswer, instructions, metadata).Text;
        var different = ModelGraded.ModelScoringPrompt(ModelGraded.DefaultFactTemplate, SqlQuestion, ModelOutput.FromContent("m", FakeSecurityGuideModel.VagueAnswer), SqlAnswer, instructions, metadata).Text;

        Assert.True(FakeSecurityGuideModel.IsGradingPrompt(matching));
        Assert.False(FakeSecurityGuideModel.IsGradingPrompt(SqlQuestion));
        Assert.EndsWith("GRADE: C", FakeSecurityGuideModel.Grade(matching));
        Assert.EndsWith("GRADE: C", FakeSecurityGuideModel.Grade(restyled));
        Assert.EndsWith("GRADE: I", FakeSecurityGuideModel.Grade(different));
        Assert.EndsWith("GRADE: I", FakeSecurityGuideModel.Grade("not a grading prompt at all"));
    }

    [Fact]
    public async Task the_fake_model_answers_a_question_and_grades_a_grading_prompt()
    {
        var model = FakeSecurityGuideModel.Create();
        var question = new ChatMessage[] { new ChatMessageSystem(SecurityGuideExample.SystemMessage), new ChatMessageUser(SqlQuestion) };
        var grading = ModelGraded.ModelScoringPrompt(ModelGraded.DefaultFactTemplate, SqlQuestion, ModelOutput.FromContent("m", SqlAnswer), SqlAnswer, ModelGraded.DefaultInstructions(false), new Dictionary<string, object?>());

        var answer = await model.GenerateAsync(question);
        var grade = await model.GenerateAsync([grading]);

        Assert.Equal(SqlAnswer, answer.Completion);
        Assert.Equal(FakeSecurityGuideModel.ModelName, answer.Model);
        Assert.EndsWith("GRADE: C", grade.Completion);
        Assert.True(answer.Usage!.TotalTokens > 0);
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end (port of `inspect eval security_guide.py`, offline)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_eval_grades_thirteen_of_sixteen_answers_correct_with_the_scripted_model()
    {
        var log = await Eval.RunAsync(
            SecurityGuideExample.Build(),
            new EvalOptions
            {
                Model = FakeSecurityGuideModel.Create(),
                LogDir = _logDir,
                LogFormat = LogFormat.Eval,
            },
            CancellationToken.None);

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Null(log.Error);
        Assert.Equal(16, log.Results!.TotalSamples);
        Assert.Equal(16, log.Results.CompletedSamples);
        var samples = log.Samples!;
        Assert.Equal(16, samples.Count);
        Assert.All(samples, sample =>
        {
            Assert.Null(sample.Error);
            Assert.Equal(["system", "user", "assistant"], sample.Messages.Select(message => message.Role));
            Assert.Equal(SecurityGuideExample.SystemMessage, sample.Messages[0].Text);
            var question = sample.Input.Messages![0].Text;
            Assert.Equal(FakeSecurityGuideModel.Answer(question), sample.Output.Completion);
            var score = sample.Scores!["model_graded_fact"];
            var expected = AbbreviatedQuestions.Contains(question) ? "I" : "C";
            Assert.Equal(expected, score.Text);
            Assert.Equal(sample.Output.Completion, score.Answer);
            Assert.EndsWith($"GRADE: {expected}", score.Explanation);
            // The grading exchange (the scoring prompt and the grader's reply) is kept in the score metadata, as in Python.
            var grading = Assert.IsAssignableFrom<IReadOnlyList<ChatMessage>>(score.Metadata!["grading"]);
            Assert.Equal(2, grading.Count);
            Assert.Contains("[BEGIN DATA]", grading[0].Text);
            Assert.Contains($"[Expert]: {sample.Target.Text}", grading[0].Text);
        });
        Assert.Equal(13, samples.Count(sample => sample.Scores!["model_graded_fact"].Text == "C"));
        var score = Assert.Single(log.Results.Scores);
        Assert.Equal("model_graded_fact", score.Name);
        Assert.Equal(13 / 16.0, score.Metrics["accuracy"].Value, precision: 10);
        Assert.InRange(score.Metrics["stderr"].Value, 0.05, 0.15);
        // Two model calls per sample: the answer and the grade.
        Assert.True(log.Stats.ModelUsage.Values.Sum(usage => usage.TotalTokens) > 0);
        Assert.True(File.Exists(log.Location), $"the log was not written: {log.Location}");
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["security_guide", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : security_guide", text);
        Assert.Contains($"model     : {FakeSecurityGuideModel.ModelName} (scripted, offline)", text);
        Assert.Contains("sandbox   : none", text);
        Assert.Contains("dataset   : 16 samples", text);
        Assert.Contains("status    : success (16/16 samples completed)", text);
        Assert.Contains("model_graded_fact", text);
        Assert.Contains("0.813", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }
}
