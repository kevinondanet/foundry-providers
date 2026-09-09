using System.Reflection;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Examples.EvalsInEval;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Examples.Tests.EvalsInEval;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the port of <c>examples/evals_in_eval</c> (<see cref="EvalsInEvalExample"/>, <see cref="Claude"/>,
/// <see cref="FileProbe"/>, <see cref="BashTask"/>): the tasks' shapes, the <c>list_files</c> tool, the agent's
/// wiring, and the eval run end to end without a network or Docker — the scripted sandbox plays the container, a
/// stand-in <c>claude</c> drives the real sandbox agent bridge, and the scripted model answers.
/// </summary>
public sealed class EvalsInEvalTests : IDisposable
{
    private static readonly string ExampleDirectory = Path.Combine(AppContext.BaseDirectory, "evals_in_eval");

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "evals-in-eval-" + Guid.NewGuid().ToString("N"));

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

    private static ExampleContext Context(SandboxSpec? sandbox = null, TextWriter? output = null, params (string Key, string Value)[] taskArgs) =>
        new(ExampleDirectory, sandbox, true, taskArgs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), null, null, output ?? TextWriter.Null);

    // ----------------------------------------------------------------------------------------------------------
    // the tasks (ports of @task evals_in_eval, file_probe, bash_task)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_evals_in_eval()
    {
        var task = EvalsInEvalExample.EvalsInEval();

        Assert.Equal("evals_in_eval", task.Name);
        var sample = Assert.Single(task.Dataset);
        Assert.Equal(
            "Run two evaluations using the inspect CLI:\n1. 'inspect eval file_probe.py'\n2. 'inspect eval bash_task.py'\n\nAfter running both, report the accuracy scores from each evaluation.",
            sample.Input.Text);
        Assert.NotNull(sample.Files);
        Assert.Equal(["bash_task.py", "file_probe.py"], sample.Files!.Keys.Order(StringComparer.Ordinal));
        Assert.True(File.Exists(sample.Files["file_probe.py"]), sample.Files["file_probe.py"]);
        Assert.True(File.Exists(sample.Files["bash_task.py"]), sample.Files["bash_task.py"]);
        Assert.Equal(new SandboxSpec("docker", Path.Combine(EvalsInEvalExample.DefaultDirectory, "compose.yaml")), task.Sandbox);
        Assert.Empty(task.Scorers);
    }

    [Fact]
    public void the_inner_tasks_are_shaped_like_their_python_originals()
    {
        var fileProbe = FileProbe.FileProbeTask();
        Assert.Equal("file_probe", fileProbe.Name);
        var probeSample = Assert.Single(fileProbe.Dataset);
        Assert.Equal("Is there a file named \"foo.txt\" in the current directory?", probeSample.Input.Text);
        Assert.Equal("Yes", probeSample.Target.Text);
        Assert.Equal("hello", probeSample.Files!["foo.txt"]);
        Assert.Equal("includes", Assert.Single(fileProbe.Scorers).Name);
        Assert.Equal(new SandboxSpec("docker"), fileProbe.Sandbox);

        var bashTask = BashTask.BashTaskTask();
        Assert.Equal("bash_task", bashTask.Name);
        var bashSample = Assert.Single(bashTask.Dataset);
        Assert.Equal("Use the bash tool to print 'hello world'.", bashSample.Input.Text);
        Assert.Equal("hello world", bashSample.Target.Text);
        Assert.Null(bashSample.Files);
        Assert.Equal("includes", Assert.Single(bashTask.Scorers).Name);
        Assert.Equal(new SandboxSpec("docker"), bashTask.Sandbox);
    }

    [Theory]
    [InlineData(typeof(EvalsInEvalExample), nameof(EvalsInEvalExample.EvalsInEval), "evals_in_eval")]
    [InlineData(typeof(FileProbe), nameof(FileProbe.FileProbeTask), "file_probe")]
    [InlineData(typeof(BashTask), nameof(BashTask.BashTaskTask), "bash_task")]
    public void the_task_methods_are_discoverable_by_the_cli(Type type, string method, string expectedName)
    {
        var info = type.GetMethod(method, BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(info);
        var attribute = info!.GetCustomAttribute<TaskAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(expectedName, attribute!.Name);
    }

    [Fact]
    public void the_data_files_are_copied_verbatim_next_to_the_assembly()
    {
        Assert.Contains("FROM node:20-slim", File.ReadAllText(Path.Combine(ExampleDirectory, "Dockerfile")));
        Assert.Contains("npm install -g @anthropic-ai/claude-code", File.ReadAllText(Path.Combine(ExampleDirectory, "Dockerfile")));
        var compose = File.ReadAllText(Path.Combine(ExampleDirectory, "compose.yaml"));
        Assert.Contains("image: docker:dind-rootless", compose);
        Assert.Contains("DOCKER_HOST=tcp://docker:2375", compose);
        Assert.Contains("privileged: true", compose);
        Assert.Contains("def list_files():", File.ReadAllText(Path.Combine(ExampleDirectory, "file_probe.py")));
        Assert.Contains("def bash_task():", File.ReadAllText(Path.Combine(ExampleDirectory, "bash_task.py")));
    }

    // ----------------------------------------------------------------------------------------------------------
    // list_files (port of @tool def list_files)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void list_files_has_the_python_name_description_and_parameter()
    {
        var tool = FileProbe.ListFiles();

        Assert.Equal("list_files", tool.Name);
        Assert.Equal("List the files in a directory.", tool.Description);
        var dir = Assert.Single(tool.Parameters.Properties);
        Assert.Equal("dir", dir.Key);
        Assert.Equal("Directory", dir.Value.Description);
        Assert.Equal(["dir"], tool.Parameters.Required);
    }

    [Fact]
    public async Task list_files_returns_the_listing_or_raises_a_tool_error_with_stderr()
    {
        var sandbox = new FakeSandboxEnvironment(cmd => cmd is ["ls", "."]
            ? FakeSandboxEnvironment.Ok("bash_task.py\nfile_probe.py\nfoo.txt\n")
            : FakeSandboxEnvironment.Fail(2, $"ls: cannot access '{cmd[1]}': No such file or directory\n"));
        var context = new SampleContext { ActiveModel = new Model(new ScriptedModelApi()), Sandboxes = SandboxEnvironments.Single(sandbox) };
        using var scope = SampleContext.Begin(context);
        var tool = FileProbe.ListFiles();

        var listing = await tool.Execute(new JsonObject { ["dir"] = "." }, CancellationToken.None);
        var error = await Assert.ThrowsAsync<ToolError>(() => tool.Execute(new JsonObject { ["dir"] = "/nope" }, CancellationToken.None));

        Assert.Equal("bash_task.py\nfile_probe.py\nfoo.txt\n", listing.AsText());
        Assert.Equal("ls: cannot access '/nope': No such file or directory\n", error.Message);
        Assert.Equal([["ls", "."], ["ls", "/nope"]], sandbox.Calls.Select(call => call.Cmd.ToArray()));
    }

    // ----------------------------------------------------------------------------------------------------------
    // the agent (port of claude.py)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_agent_is_registered_as_claude_code_over_the_sandbox_binary_with_the_python_env()
    {
        var agent = Claude.ClaudeCode();
        var options = Claude.Options();

        Assert.Equal("claude_code", agent.Name);
        Assert.Equal("sandbox", options.Version);
        Assert.Null(options.PermissionMode);
        Assert.Equal("anthropic/inspect", options.Env!["INSPECT_EVAL_MODEL"]);
        Assert.Equal("anthropic/claude-3-7-sonnet-latest", Claude.Options("anthropic/claude-3-7-sonnet-latest").Env!["INSPECT_EVAL_MODEL"]);
        Assert.Throws<ArgumentException>(() => Claude.ClaudeCode(" "));
    }

    // ----------------------------------------------------------------------------------------------------------
    // the example
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_example_is_registered_with_docker_compose_defaults_and_a_fake_sandbox()
    {
        var example = Assert.IsType<EvalsInEvalExample>(ExampleRegistry.Default.Get("evals_in_eval"));

        Assert.Equal("evals_in_eval", example.Name);
        Assert.Equal(["evals_in_eval", "file_probe", "bash_task"], example.Tasks.Select(task => task.Name));
        Assert.Equal("docker", example.Defaults.Sandbox);
        Assert.Equal("compose.yaml", example.Defaults.ComposeFile);
        Assert.True(example.Defaults.NeedsDocker);
        Assert.Null(example.Defaults.Approval);
        Assert.NotEmpty(example.Deviations);
        Assert.NotNull(example.FakeSandbox(Context()));
        Assert.Equal(EvalsInEvalExample.FakeModelName, example.CreateFakeModel(Context()).Name);
        foreach (var task in example.Tasks)
        {
            Assert.Throws<PrerequisiteError>(() => task.Build(Context(sandbox: null)));
        }

        var built = example.Tasks[0].Build(Context(new SandboxSpec("fake"), taskArgs: ("inspect_eval_model", "anthropic/x")));
        Assert.Equal(new SandboxSpec("fake"), built.Sandbox);
        Assert.Equal(Path.Combine(ExampleDirectory, "file_probe.py"), built.Dataset[0].Files!["file_probe.py"]);
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end (port of `inspect eval task.py`, offline)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_eval_runs_claude_code_through_the_bridge_and_reports_both_accuracies()
    {
        var example = new EvalsInEvalExample();
        var script = example.FakeSandbox(Context())!;
        var sandbox = ScriptedSandboxProvider.Register(script);

        var log = await Eval.RunAsync(
            EvalsInEvalExample.Build(ExampleDirectory, sandbox),
            new EvalOptions { Model = example.CreateFakeModel(Context()), LogDir = _logDir, LogFormat = LogFormat.Eval },
            CancellationToken.None);

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Null(log.Error);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);

        // the bridge reconstructed the two-turn conversation the stand-in CLI had with the model
        Assert.Equal(FakeEvalsInEvalModel.Report, sample.Output.Completion);
        var assistants = sample.Messages.OfType<ChatMessageAssistant>().Select(message => message.Text).ToList();
        Assert.Equal([FakeEvalsInEvalModel.Plan, FakeEvalsInEvalModel.Report], assistants);
        Assert.Contains(sample.Messages.OfType<ChatMessageUser>(), message => message.Text.StartsWith("Command output:", StringComparison.Ordinal) && message.Text.Contains("bash_task"));
        Assert.Contains(sample.Events.OfType<InfoEvent>(), info => info.Source == "claude_code");

        // the launch: the image's claude, Python's flags, the prompt after --, and claude.py's environment
        var launch = Assert.Single(script.Calls, FakeClaudeCli.IsLaunch);
        Assert.Equal("/usr/local/bin/claude", launch.Cmd[4]);
        Assert.Contains("--dangerously-skip-permissions", launch.Cmd);
        Assert.Contains("--print", launch.Cmd);
        Assert.Equal(EvalsInEvalExample.FakeModelName, FakeClaudeCli.Flag(launch.Cmd, "--model"));
        Assert.Equal(EvalsInEvalExample.Input, FakeClaudeCli.Prompt(launch.Cmd));
        Assert.Equal("/workspace", launch.Cwd);
        var env = launch.Env!;
        Assert.StartsWith("http://127.0.0.1:", env["ANTHROPIC_BASE_URL"], StringComparison.Ordinal);
        Assert.Equal(32, env["ANTHROPIC_AUTH_TOKEN"].Length);
        Assert.Equal("anthropic/inspect", env["INSPECT_EVAL_MODEL"]);
        Assert.Equal(EvalsInEvalExample.FakeModelName, env["ANTHROPIC_MODEL"]);
        Assert.Equal("1", env["IS_SANDBOX"]);
        Assert.Equal("1", env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);

        // the sample files were copied in, and the stand-in logged its two bridged turns
        var environment = Assert.Single(script.Environments);
        Assert.Contains("def list_files():", environment.FileText("file_probe.py"));
        Assert.Contains("def bash_task():", environment.FileText("bash_task.py"));
        var requests = environment.FileText(FakeClaudeCli.RequestLog)!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToList();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal("POST", request["method"]!.GetValue<string>());
            Assert.Equal("/v1/messages", request["path"]!.GetValue<string>());
            Assert.Equal(200, request["status"]!.GetValue<int>());
        });
        Assert.True(File.Exists(log.Location), $"the log was not written: {log.Location}");
    }

    [Theory]
    [InlineData("file_probe", "Yes", "list_files")]
    [InlineData("bash_task", "hello world", "bash")]
    public async Task the_inner_tasks_run_offline_and_score_correct(string taskName, string expectedAnswer, string expectedTool)
    {
        var example = new EvalsInEvalExample();
        var script = example.FakeSandbox(Context())!;
        var sandbox = ScriptedSandboxProvider.Register(script);
        var task = taskName == "file_probe" ? FileProbe.Build(sandbox) : BashTask.Build(sandbox);

        var log = await Eval.RunAsync(
            task,
            new EvalOptions { Model = example.CreateFakeModel(Context()), LogDir = _logDir, LogFormat = LogFormat.Eval },
            CancellationToken.None);

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal(expectedAnswer, sample.Output.Completion);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal(1.0, Assert.Single(log.Results!.Scores).Metrics["accuracy"].Value);
        Assert.Contains(sample.Events.OfType<ToolEvent>(), tool => tool.Function == expectedTool);
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["evals_in_eval", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : evals_in_eval", text);
        Assert.Contains($"model     : {EvalsInEvalExample.FakeModelName} (scripted, offline)", text);
        Assert.Contains("sandbox   : fake, scripted by the example", text);
        Assert.Contains("status    : success (1/1 samples completed)", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }

    [Fact]
    public async Task the_runner_runs_an_inner_task_offline_and_scores_it()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["evals_in_eval", "--task", "file_probe", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : file_probe", text);
        Assert.Contains("status    : success (1/1 samples completed)", text);
        Assert.Contains($"{"includes",-24} {"accuracy",-20} {"1.000",10}", text);
    }

    [Fact]
    public async Task the_fake_sandbox_answers_the_agents_probes_and_refuses_the_unscripted()
    {
        var script = new EvalsInEvalExample().FakeSandbox(Context())!;
        async Task<ExecResult> Resolve(params string[] cmd) =>
            (await script.ResolveAsync(new FakeExecCall(cmd, null, null, null, null, null), CancellationToken.None)).Result!;

        Assert.Equal("/usr/local/bin/claude\n", (await Resolve("bash", "-c", "which claude")).Stdout);
        Assert.Equal("/workspace\n", (await Resolve("bash", "-c", "pwd")).Stdout);
        Assert.True((await Resolve("bash", "-c", ClaudeCodeCommand.SettingsCommand("k"))).Success);
        Assert.Equal("hello world\n", (await Resolve("bash", "--login", "-c", "echo 'hello world'")).Stdout);
        Assert.Equal(127, (await Resolve("rm", "-rf", "/")).ReturnCode);
    }
}
