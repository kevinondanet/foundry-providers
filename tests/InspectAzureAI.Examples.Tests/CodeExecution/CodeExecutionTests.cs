using System.Reflection;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.CodeExecution;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.CodeExecution;

using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/code_execution.py</c> (<see cref="CodeExecutionTask"/>,
/// <see cref="CodeExecutionExample"/>): the task's shape and the run end to end offline, through the scripted
/// model and the fake sandbox (asserting the <c>python3 -</c> command the fallback issued), and through the real
/// local sandbox when this host has <c>python3</c>.
/// </summary>
public sealed class CodeExecutionTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "code-execution-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_task()
    {
        var task = CodeExecutionTask.Create();

        Assert.Equal("code_execution_task", task.Name);
        Assert.Equal(new SandboxSpec("docker"), task.Sandbox);
        Assert.Equal(
            "Please use your available tools to execute Python code that adds 435678 + 23457 and then prints the result.",
            Assert.Single(task.Dataset).Input.Text);
        Assert.Equal(Target.Empty, task.Dataset[0].Target);
        // As in Python: no scorer.
        Assert.Empty(task.Scorers);
        Assert.Equal(new SandboxSpec("local"), CodeExecutionTask.Build(new SandboxSpec("local")).Sandbox);
        Assert.Throws<ArgumentNullException>(() => CodeExecutionTask.Build(null!));
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli()
    {
        var attribute = typeof(CodeExecutionTask).GetMethod(nameof(CodeExecutionTask.Create))!.GetCustomAttribute<TaskAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("code_execution_task", attribute!.Name);
    }

    [Fact]
    public void the_example_defaults_to_docker_and_refuses_to_build_without_a_sandbox()
    {
        var example = new CodeExecutionExample();

        Assert.Equal("code_execution", example.Name);
        Assert.Equal(["code_execution_task"], example.Tasks.Select(task => task.Name));
        Assert.Equal("docker", example.Defaults.Sandbox);
        Assert.False(example.Defaults.NeedsDocker);
        Assert.NotEmpty(example.Deviations);
        Assert.NotNull(example.FakeSandbox(Context(null)));
        Assert.Throws<PrerequisiteError>(() => example.Tasks[0].Build(Context(null)));
        Assert.IsType<CodeExecutionExample>(ExampleRegistry.Default.Find("code_execution"));
    }

    [Fact]
    public async Task the_model_runs_the_addition_through_the_python_fallback_in_the_fake_sandbox()
    {
        var example = new CodeExecutionExample();
        var script = example.FakeSandbox(Context(null))!;
        var context = Context(ScriptedSandboxProvider.Register(script));

        var log = await Eval.RunAsync(
            example.Tasks[0].Build(context),
            new EvalOptions { Model = example.CreateFakeModel(context), LogDir = _logDir, LogFormat = LogFormat.Eval },
            CancellationToken.None);

        AssertSuccess(log);
        var sample = Assert.Single(log.Samples!);
        var call = Assert.Single(Assert.Single(sample.Messages.OfType<ChatMessageAssistant>(), message => message.ToolCalls is { Count: > 0 }).ToolCalls!);
        Assert.Equal("code_execution", call.Function);
        Assert.Equal("print(435678 + 23457)", call.Arguments["code"]!.GetValue<string>());
        var result = Assert.Single(sample.Messages.OfType<ChatMessageTool>());
        Assert.Null(result.Error);
        Assert.Equal("459135\n", result.Text);
        Assert.Equal("The result of 435678 + 23457 is 459135.", sample.Output.Completion);
        Assert.True(sample.Scores is null or { Count: 0 }, "the task has no scorer");

        // The fallback piped the code to `python3 -` under a login shell, as python() does.
        var exec = Assert.Single(script.Calls);
        Assert.Equal(["bash", "--login", "-c", "python3 -"], exec.Cmd);
        Assert.Equal("print(435678 + 23457)", exec.Input);
    }

    [Fact]
    public async Task the_local_sandbox_runs_the_code_with_this_hosts_python3()
    {
        if (!HasPython3())
        {
            return;
        }

        var example = new CodeExecutionExample();
        var context = Context(new SandboxSpec("local"));

        var log = await Eval.RunAsync(
            example.Tasks[0].Build(context),
            new EvalOptions { Model = example.CreateFakeModel(context), LogDir = _logDir, LogFormat = LogFormat.Eval },
            CancellationToken.None);

        AssertSuccess(log);
        var sample = Assert.Single(log.Samples!);
        var result = Assert.Single(sample.Messages.OfType<ChatMessageTool>());
        Assert.Null(result.Error);
        Assert.Equal("459135", result.Text.Trim());
        Assert.Equal("The result of 435678 + 23457 is 459135.", sample.Output.Completion);
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_refuses_sandbox_none()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["code_execution", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("sandbox   : fake, scripted by the example", text);
        Assert.Contains("status    : success (1/1 samples completed)", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));

        var error = new StringWriter();
        Assert.Equal(2, await ExampleRunner.MainAsync(["code_execution", "--fake", "--sandbox", "none", "--log-dir", _logDir], ExampleRegistry.Default, TextWriter.Null, error));
        Assert.Contains("needs a sandbox", error.ToString());
    }

    private static bool HasPython3() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, "python3")));

    private static ExampleContext Context(SandboxSpec? sandbox) =>
        new(Path.Combine(AppContext.BaseDirectory, "code_execution"), sandbox, true, new Dictionary<string, string>(), null, null, TextWriter.Null);

    private static void AssertSuccess(EvalLog log)
    {
        if (log.Status != EvalStatus.Success)
        {
            var sampleErrors = string.Join("; ", (log.Samples ?? []).Where(sample => sample.Error is not null).Select(sample => sample.Error!.Message));
            Assert.Fail($"status {log.Status}: {log.Error?.Message} {sampleErrors}");
        }

        Assert.Equal(1, log.Results!.CompletedSamples);
        Assert.NotNull(log.Location);
        Assert.True(File.Exists(log.Location), $"the log was not written: {log.Location}");
    }
}
