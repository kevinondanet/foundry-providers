using System.Reflection;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Computer;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Computer;

using ComputerTool = InspectAzureAI.Eval.Tools.Computer;
using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the port of <c>examples/computer/computer.py</c> <c>computer_task</c> (<see cref="ComputerExample"/>):
/// the task's shape (three samples, the flag file, the message limit), the scripted desktop of
/// <see cref="FakeComputerSandbox"/>, the scripted model's answers, and the example end to end without Docker (the
/// fake model against the fake desktop, through <c>Eval.RunAsync</c> and the examples runner), including the
/// screenshot truncation the computer tool's <c>model_input</c> hook applies.
/// </summary>
public sealed class ComputerTests : IDisposable
{
    private static readonly string ExampleDirectory = Path.Combine(AppContext.BaseDirectory, "computer");

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "computer-" + Guid.NewGuid().ToString("N"));

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

    private static ExampleContext Context(SandboxSpec? sandbox = null, bool fake = true) =>
        new(ExampleDirectory, sandbox, fake, new Dictionary<string, string>(), null, null, TextWriter.Null);

    private static async Task<ExecResult> Action(ScriptedSandboxEnvironment environment, params string[] argv) =>
        await environment.ExecAsync(["python3", ComputerTool.ToolPath, .. argv]);

    private static string Output(ExecResult result) => JsonNode.Parse(result.Stdout)!["output"]!.GetValue<string>();

    // ----------------------------------------------------------------------------------------------------------
    // the task (port of @task def computer_task)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_computer_task()
    {
        var task = ComputerExample.Build(new SandboxSpec("docker", "compose.yaml"));

        Assert.Equal("computer_task", task.Name);
        Assert.Equal(3, task.Dataset.Count);
        Assert.Equal("Use the computer tool to find the contents of the file '/tmp/flag.txt'. When you've found it, report its contents.", task.Dataset[0].Input.Text);
        Assert.Equal(["Frunobulax"], task.Dataset[0].Target.Values);
        Assert.Equal(new Dictionary<string, string> { ["/tmp/flag.txt"] = "Frunobulax" }, task.Dataset[0].Files);
        Assert.Equal(
            "Use the computer tool to launch a terminal. Type 'Trudging across the tundra. Mile after Mile.' into the terminal. Important: Make sure that the terminal window is active before typing. When you are done, please use the submit tool to record the result of hitting enter in the terminal after entering that text.",
            task.Dataset[1].Input.Text);
        Assert.Equal(["bash: Trudging: command not found"], task.Dataset[1].Target.Values);
        Assert.Null(task.Dataset[1].Files);
        Assert.Equal("Use the computer tool to launch a calculator. Calculate 123 x 456. Report the result.", task.Dataset[2].Input.Text);
        Assert.Equal(["56088"], task.Dataset[2].Target.Values);
        Assert.Equal(100, task.MessageLimit);
        Assert.Equal("includes", Assert.Single(task.Scorers).Name);
        Assert.Equal(new SandboxSpec("docker", "compose.yaml"), task.Sandbox);
        Assert.StartsWith("\nBefore each step, please clearly explain your intent", ComputerExample.SystemMessage);
        Assert.EndsWith("requires a double click.\n", ComputerExample.SystemMessage);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_with_the_example_compose_file()
    {
        var method = typeof(ComputerExample).GetMethod(nameof(ComputerExample.ComputerTask), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal("computer_task", method.GetCustomAttribute<TaskAttribute>()?.Name);
        var task = ComputerExample.ComputerTask();
        Assert.Equal(Path.Combine(ExampleDirectory, "compose.yaml"), task.Sandbox?.Config);
        Assert.True(File.Exists(task.Sandbox!.Config!), $"compose.yaml was not copied next to the assembly: {task.Sandbox.Config}");
        Assert.Contains("image: aisiuk/inspect-computer-tool", File.ReadAllText(task.Sandbox.Config));
        Assert.True(File.Exists(Path.Combine(ExampleDirectory, "moonWeight.ods")), "moonWeight.ods was not copied next to the assembly");
    }

    // ----------------------------------------------------------------------------------------------------------
    // the example
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_example_declares_the_python_task_a_docker_sandbox_and_a_fake_desktop()
    {
        var example = Assert.IsType<ComputerExample>(ExampleRegistry.Default.Get("computer"));

        Assert.Equal("computer", example.Name);
        Assert.Equal(["computer_task"], example.Tasks.Select(task => task.Name));
        Assert.Equal("docker", example.Defaults.Sandbox);
        Assert.Equal("compose.yaml", example.Defaults.ComposeFile);
        Assert.True(example.Defaults.NeedsDocker);
        Assert.Contains("vision", example.Defaults.ModelHint);
        Assert.NotNull(example.FakeSandbox(Context()));
        Assert.Contains(example.Deviations, deviation => deviation.Contains("messsage_limit", StringComparison.Ordinal));
        Assert.Throws<PrerequisiteError>(() => example.Tasks[0].Build(Context(sandbox: null)));
        Assert.Equal(100, example.Tasks[0].Build(Context(new SandboxSpec("fake"))).MessageLimit);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the fake desktop and the fake model
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_fake_desktop_opens_the_terminal_and_runs_what_is_typed()
    {
        var script = FakeComputerSandbox.Create();
        using var environment = new ScriptedSandboxEnvironment(script);
        script.Record(environment);
        await environment.WriteFileAsync("/tmp/flag.txt", "Frunobulax");

        Assert.True((await environment.ExecAsync(["test", "-r", ComputerTool.ToolPath])).Success);

        var desktop = await Action(environment, "screenshot");
        Assert.Equal(FakeComputerSandbox.DesktopScreen, Output(desktop));
        Assert.Equal(FakeComputerSandbox.ScreenshotPng, JsonNode.Parse(desktop.Stdout)!["base64_image"]!.GetValue<string>());

        Assert.Equal(FakeComputerSandbox.TerminalScreen, Output(await Action(environment, "double_click", "--coordinate", "40", "60")));
        Assert.Equal("A 'Terminal' window is open and active. It shows:\n\n$ cat /tmp/flag.txt", Output(await Action(environment, "type", "--text=cat /tmp/flag.txt")));
        Assert.Equal("A 'Terminal' window is open and active. It shows:\n$ cat /tmp/flag.txt\nFrunobulax\n$ ", Output(await Action(environment, "key", "--text", "Return")));
        await Action(environment, "type", "--text=Trudging across the tundra. Mile after Mile.");
        var screen = Output(await Action(environment, "key", "--text", "Return"));
        Assert.Contains("$ Trudging across the tundra. Mile after Mile.\nbash: Trudging: command not found", screen);
        Assert.Equal(screen, Output(await Action(environment, "screenshot")));

        Assert.False((await environment.ExecAsync(["ls"])).Success);
    }

    [Fact]
    public async Task the_fake_desktop_opens_the_calculator_and_multiplies()
    {
        var script = FakeComputerSandbox.Create();
        using var environment = new ScriptedSandboxEnvironment(script);
        script.Record(environment);

        Assert.Equal(FakeComputerSandbox.CalculatorScreen, Output(await Action(environment, "double_click", "--coordinate", "40", "140")));
        await Action(environment, "type", "--text=123*456");
        Assert.Equal("A 'Calculator' window is open and active. Its display shows 56088.", Output(await Action(environment, "key", "--text", "Return")));
        Assert.Equal(["double_click", "type", "key"], FakeComputerSandbox.Actions(environment));
    }

    [Fact]
    public async Task the_fake_desktop_answers_a_call_from_an_environment_it_does_not_know()
    {
        using var environment = new ScriptedSandboxEnvironment(FakeComputerSandbox.Create());

        // not recorded on the script: no history beyond this call, which is replayed on its own
        Assert.Equal(FakeComputerSandbox.TerminalScreen, Output(await Action(environment, "double_click", "--coordinate", "40", "60")));
        Assert.Equal(FakeComputerSandbox.DesktopScreen, Output(await Action(environment, "screenshot")));
    }

    [Fact]
    public void the_fake_model_reads_the_answer_off_the_screen()
    {
        Assert.Equal("The contents of /tmp/flag.txt are: Frunobulax", FakeComputerModel.Answer(ComputerExample.FlagInput, "A 'Terminal' window is open and active. It shows:\n$ cat /tmp/flag.txt\nFrunobulax\n$ "));
        Assert.Equal("bash: Trudging: command not found", FakeComputerModel.Answer(ComputerExample.TerminalInput, "A 'Terminal' window is open and active. It shows:\n$ Trudging across the tundra. Mile after Mile.\nbash: Trudging: command not found\n$ "));
        Assert.Equal("123 x 456 = 56088", FakeComputerModel.Answer(ComputerExample.CalculatorInput, "A 'Calculator' window is open and active. Its display shows 56088."));
        Assert.Contains("not visible", FakeComputerModel.Answer(ComputerExample.FlagInput, FakeComputerSandbox.DesktopScreen));
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end, offline
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_scripted_run_solves_the_three_desktop_tasks_on_the_fake_desktop()
    {
        var script = FakeComputerSandbox.Create();
        var sandbox = ScriptedSandboxProvider.Register(script);
        var api = new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(FakeComputerModel.Respond), 30), FakeComputerModel.ModelName);
        var task = ComputerExample.Build(sandbox);

        var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.True(log.Status == EvalStatus.Success, $"status {log.Status}: {log.Error?.Message ?? "(no error)"}");
        Assert.Equal(3, log.Samples!.Count);
        Assert.All(log.Samples, sample => Assert.Equal("C", sample.Scores!["includes"].Text));

        // every sample: screenshot, double-click the icon, type + Return (press_enter), screenshot, then submit
        Assert.Equal(3, script.Environments.Count);
        Assert.All(script.Environments, environment => Assert.Equal(["screenshot", "double_click", "type", "key", "screenshot"], FakeComputerSandbox.Actions(environment)));
        foreach (var sample in log.Samples)
        {
            var calls = sample.Messages.OfType<ChatMessageAssistant>().SelectMany(message => message.ToolCalls ?? []).ToList();
            Assert.Equal(["computer", "computer", "computer", "computer"], calls.Select(call => call.Function));
            Assert.Equal("double_click", calls[1].Arguments["action"]!.GetValue<string>());
            Assert.True(calls[2].Arguments["press_enter"]!.GetValue<bool>());
            var results = sample.Messages.OfType<ChatMessageTool>().ToList();
            Assert.Equal(4, results.Count);
            Assert.All(results, result => Assert.Null(result.Error));
            // the conversation keeps every screenshot (text + image per result)
            Assert.All(results, result => Assert.Contains(result.Content.Items!, item => item is ContentImage));
        }

        // the flag was written by Sample.files into that sample's sandbox only, and read back through the terminal
        var withFlag = Assert.Single(script.Environments, environment => environment.FileText("/tmp/flag.txt") is not null);
        Assert.Equal("Frunobulax", withFlag.FileText("/tmp/flag.txt"));
        var answers = log.Samples.Select(sample => Assert.IsType<ChatMessageAssistant>(sample.Messages[^1]).Text).ToList();
        Assert.Contains(answers, answer => answer.Contains("Frunobulax", StringComparison.Ordinal));
        Assert.Contains(answers, answer => answer.Contains("bash: Trudging: command not found", StringComparison.Ordinal));
        Assert.Contains(answers, answer => answer.Contains("56088", StringComparison.Ordinal));

        // the model_input hook (max_screenshots=1): only the latest tool result still carries its image when the model is called
        Assert.Equal(15, api.Requests.Count);
        var late = api.Requests.Where(request => request.Input.OfType<ChatMessageTool>().Count() >= 2).ToList();
        Assert.NotEmpty(late);
        foreach (var request in late)
        {
            var seen = request.Input.OfType<ChatMessageTool>().ToList();
            Assert.All(seen[..^1], message => Assert.DoesNotContain(message.Content.Items!, item => item is ContentImage));
            Assert.All(seen[..^1], message => Assert.Contains(message.Content.Items!, item => item is ContentText text && text.Text == ComputerTool.ScreenshotRemovedText));
            Assert.Contains(seen[^1].Content.Items!, item => item is ContentImage);
        }
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_with_the_fake_desktop()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["computer", "--fake", "--sandbox", "fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        Assert.True(exit == 0, output.ToString());
        Assert.Contains("status    : success (3/3 samples completed)", output.ToString());
        Assert.Single(Directory.EnumerateFiles(_logDir, "*.eval"));
    }
}
