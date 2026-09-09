using System.Reflection;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.HelloWorld;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.Tests.HelloWorld;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/hello_world.py</c> <c>hello_world</c> (<see cref="HelloWorldExample"/>): the task's
/// shape, the scripted model, and the eval run end to end without a network, through <c>Eval.RunAsync</c> and through
/// the examples runner.
/// </summary>
public sealed class HelloWorldTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "hello-world-" + Guid.NewGuid().ToString("N"));

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
    // the task (port of @task def hello_world)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_hello_world()
    {
        var task = HelloWorldExample.HelloWorld();

        Assert.Equal("hello_world", task.Name);
        var sample = Assert.Single(task.Dataset);
        Assert.Equal("Just reply with Hello World", sample.Input.Text);
        Assert.Equal("Hello World", sample.Target.Text);
        var scorer = Assert.Single(task.Scorers);
        Assert.Equal("exact", scorer.Name);
        Assert.Equal(["mean", "stderr"], scorer.Metrics.Select(metric => metric.Name));
        Assert.Null(task.Sandbox);
        Assert.Null(task.Approval);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_as_hello_world()
    {
        var method = typeof(HelloWorldExample).GetMethod(nameof(HelloWorldExample.HelloWorld), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        var attribute = method!.GetCustomAttribute<TaskAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal("hello_world", attribute!.Name);
    }

    [Fact]
    public void build_passes_the_sandbox_through()
    {
        Assert.Equal(new SandboxSpec("local"), HelloWorldExample.Build(new SandboxSpec("local")).Sandbox);
        Assert.Null(HelloWorldExample.Build().Sandbox);
    }

    [Fact]
    public void the_example_is_registered_with_no_sandbox_and_no_fake_sandbox_script()
    {
        var example = Assert.IsType<HelloWorldExample>(ExampleRegistry.Default.Get("hello_world"));

        Assert.Equal("hello_world", example.Name);
        Assert.Equal(["hello_world"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.Defaults.Approval);
        Assert.False(example.Defaults.NeedsDocker);
        Assert.NotEmpty(example.Deviations);
        var ctx = new ExampleContext("/x", null, true, new Dictionary<string, string>(), null, null, TextWriter.Null);
        Assert.Null(example.FakeSandbox(ctx));
        Assert.Equal("hello_world", example.Tasks[0].Build(ctx).Name);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scripted model
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_fake_model_answers_hello_world_or_the_fake_answer_task_arg()
    {
        var example = new HelloWorldExample();

        var standard = example.CreateFakeModel(new ExampleContext("/x", null, true, new Dictionary<string, string>(), null, null, TextWriter.Null));
        var custom = example.CreateFakeModel(new ExampleContext("/x", null, true, new Dictionary<string, string> { ["fake_answer"] = "Goodbye World" }, null, null, TextWriter.Null));

        Assert.Equal(HelloWorldExample.FakeModelName, standard.Name);
        Assert.Equal("Hello World", (await standard.GenerateAsync("Just reply with Hello World")).Completion);
        Assert.Equal("Goodbye World", (await custom.GenerateAsync("Just reply with Hello World")).Completion);
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end (port of `inspect eval hello_world.py`, offline)
    // ----------------------------------------------------------------------------------------------------------

    // exact() normalizes case and punctuation before comparing (Python's normalize()), so only a different text is INCORRECT.
    [Theory]
    [InlineData("Hello World", "C", 1.0)]
    [InlineData("hello, world!", "C", 1.0)]
    [InlineData("Goodbye World", "I", 0.0)]
    public async Task the_eval_scores_the_scripted_answer_with_exact(string answer, string expectedScore, double expectedMean)
    {
        var log = await Eval.RunAsync(
            HelloWorldExample.Build(),
            new EvalOptions
            {
                Model = HelloWorldExample.CreateFakeModel(answer),
                LogDir = _logDir,
                LogFormat = LogFormat.Eval,
            },
            CancellationToken.None);

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Null(log.Error);
        Assert.Equal(1, log.Results!.TotalSamples);
        Assert.Equal(1, log.Results.CompletedSamples);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal(answer, sample.Output.Completion);
        var score = sample.Scores!["exact"];
        Assert.Equal(expectedScore, score.Text);
        Assert.Equal(answer, score.Answer);
        var exact = Assert.Single(log.Results.Scores);
        Assert.Equal("exact", exact.Name);
        Assert.Equal(expectedMean, exact.Metrics["mean"].Value);
        Assert.Equal(0.0, exact.Metrics["stderr"].Value);
        Assert.True(File.Exists(log.Location), $"the log was not written: {log.Location}");
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["hello_world", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : hello_world", text);
        Assert.Contains($"model     : {HelloWorldExample.FakeModelName} (scripted, offline)", text);
        Assert.Contains("sandbox   : none", text);
        Assert.Contains("dataset   : 1 samples", text);
        Assert.Contains("status    : success (1/1 samples completed)", text);
        Assert.Contains("exact", text);
        Assert.Contains("mean", text);
        Assert.Contains("1.000", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }

    [Fact]
    public async Task the_runner_shows_the_incorrect_path_with_the_fake_answer_task_arg()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["hello_world", "--fake", "--log-dir", _logDir, "-T", "fake_answer=Goodbye World"], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task args : fake_answer=Goodbye World", text);
        Assert.Contains("status    : success (1/1 samples completed)", text);
        Assert.Contains("exact=I", text);
        Assert.Contains($"{"exact",-24} {"mean",-20} {"0.000",10}", text);
    }
}
