using System.Reflection;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents.Human;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Human;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Human;

/// <summary>
/// Tests for the port of <c>examples/human/human.py</c> (<see cref="HumanExample"/>): the task's shape, the scripted
/// container's command emulation and connection, the scripted operator's protocol, and the example end to end through
/// the examples runner with the fake sandbox (the person's <c>task</c> commands run through the real sandbox-service protocol).
/// </summary>
public sealed class HumanTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "human-" + Guid.NewGuid().ToString("N"));

    private static readonly string ExampleDirectory = Path.Combine(AppContext.BaseDirectory, "human");

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

    private static ExampleContext Context(bool fake = true, SandboxSpec? sandbox = null, TextWriter? output = null, params (string Key, string Value)[] taskArgs) =>
        new(ExampleDirectory, sandbox, fake, taskArgs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), null, null, output ?? TextWriter.Null);

    // ----------------------------------------------------------------------------------------------------------
    // the task (port of @task def human(user))
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_human_with_pythons_default_sample_and_a_docker_compose_sandbox()
    {
        var task = HumanExample.Human();

        Assert.Equal("human", task.Name);
        var sample = Assert.Single(task.Dataset);
        Assert.Equal("prompt", sample.Input.Text);
        Assert.Empty(task.Scorers);
        Assert.Equal(new SandboxSpec("docker", Path.Combine(AppContext.BaseDirectory, "human", "compose.yaml")), task.Sandbox);
        Assert.Null(task.Approval);
        Assert.NotNull(task.Solver);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_with_the_user_parameter()
    {
        var method = typeof(HumanExample).GetMethod(nameof(HumanExample.Human), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal("human", method!.GetCustomAttribute<TaskAttribute>()!.Name);
        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal("user", parameter.Name);
        Assert.Null(parameter.DefaultValue);
        Assert.Equal("human", HumanExample.Human("nonroot").Name);
    }

    [Fact]
    public void the_data_files_are_copied_verbatim()
    {
        var dockerfile = File.ReadAllText(Path.Combine(ExampleDirectory, "Dockerfile"));
        var compose = File.ReadAllText(Path.Combine(ExampleDirectory, "compose.yaml"));

        Assert.Contains("FROM python:3.12-bookworm", dockerfile);
        Assert.Contains("RUN useradd -m nonroot", dockerfile);
        Assert.Contains("build: .", compose);
        Assert.Contains("network_mode: none", compose);
    }

    [Fact]
    public void the_example_is_registered_with_a_docker_default_that_needs_docker()
    {
        var example = Assert.IsType<HumanExample>(ExampleRegistry.Default.Get("human"));

        Assert.Equal("human", example.Name);
        Assert.Equal(["human"], example.Tasks.Select(task => task.Name));
        Assert.Equal("docker", example.Defaults.Sandbox);
        Assert.Equal("compose.yaml", example.Defaults.ComposeFile);
        Assert.True(example.Defaults.NeedsDocker);
        Assert.NotEmpty(example.Deviations);
        Assert.NotNull(example.FakeSandbox(Context()));
        Assert.Equal(HumanExample.FakeModelName, example.CreateFakeModel(Context()).Name);
    }

    [Fact]
    public void build_takes_docker_through_and_refuses_no_sandbox_and_the_local_sandbox()
    {
        var example = new HumanExample();
        var docker = new SandboxSpec("docker", Path.Combine(ExampleDirectory, "compose.yaml"));

        var task = example.Tasks[0].Build(Context(fake: true, sandbox: docker, taskArgs: ("user", "nonroot")));

        Assert.Equal(docker, task.Sandbox);
        Assert.Throws<PrerequisiteError>(() => example.Tasks[0].Build(Context(sandbox: null)));
        Assert.Throws<PrerequisiteError>(() => example.Tasks[0].Build(Context(sandbox: new SandboxSpec("local"))));
    }

    [Fact]
    public void build_with_the_fake_sandbox_registers_the_scripted_container_provider_under_the_fake_type()
    {
        var example = new HumanExample();
        var script = ScriptedSandboxProvider.Register(new FakeSandboxScript());

        var task = example.Tasks[0].Build(Context(fake: true, sandbox: script));

        Assert.Equal(new SandboxSpec("fake"), task.Sandbox);
        var provider = Assert.IsType<HumanFakeSandboxProvider>(SandboxRegistry.Get("fake"));
        Assert.Equal("42", provider.Operator.Answer);
        Assert.Empty(provider.Containers);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scripted container (the person's side of the sandbox-service protocol)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_container_emulates_the_installers_file_commands()
    {
        var container = new HumanFakeContainer(new FakeSandboxScript(), new HumanOperatorScript(TextWriter.Null).NewSession());

        var created = await container.ExecAsync(["mkdir", HumanAgentInstall.HumanAgentDir], user: "root");
        var again = await container.ExecAsync(["mkdir", HumanAgentInstall.HumanAgentDir], user: "root");
        var whoami = await container.ExecAsync(["whoami"]);
        var python = await container.ExecAsync(["which", "python3"]);
        await container.ExecAsync(["mkdir", "-p", HumanAgentInstall.InstallDir], user: "root");
        var written = await container.ExecAsync(["tee", "--", $"{HumanAgentInstall.InstallDir}/task.py"], input: "print('task')");
        var read = await container.ExecAsync(["cat", "--", $"{HumanAgentInstall.InstallDir}/task.py"]);
        var size = await container.ExecAsync(["wc", "-c", "--", $"{HumanAgentInstall.InstallDir}/task.py"]);
        await container.ExecAsync(["bash", "./install.sh"], cwd: HumanAgentInstall.InstallDir);
        await container.ExecAsync(["rm", "-rf", HumanAgentInstall.InstallDir]);
        var missing = await container.ExecAsync(["cat", "--", $"{HumanAgentInstall.InstallDir}/task.py"]);

        Assert.True(created.Success);
        Assert.False(again.Success);
        Assert.Equal("root\n", whoami.Stdout);
        Assert.True(python.Success);
        Assert.Equal("print('task')", written.Stdout);
        Assert.Equal("print('task')", read.Stdout);
        Assert.Equal($"13 {HumanAgentInstall.InstallDir}/task.py\n", size.Stdout);
        Assert.Equal("print('task')", container.FileText($"{HumanAgentInstall.HumanAgentDir}/task.py"));
        Assert.False(missing.Success);
        Assert.False(container.Exists(HumanAgentInstall.InstallDir));
        Assert.Equal(11, container.Calls.Count);
    }

    [Fact]
    public async Task the_container_answers_connection_requests_the_human_agent_needs()
    {
        var container = new HumanFakeContainer(new FakeSandboxScript(), new HumanOperatorScript(TextWriter.Null).NewSession());

        var connection = await container.ConnectionAsync("nonroot");

        Assert.Equal("fake", connection.Type);
        Assert.Equal(HumanFakeContainer.LoginCommand, connection.Command);
        Assert.Equal(HumanFakeContainer.ContainerName, connection.Container);
        var wrapped = await ((ISandboxEnvironment)container).ConnectionAsync();
        Assert.Equal(connection, wrapped);
    }

    [Fact]
    public async Task the_operator_drops_one_request_per_poll_and_prints_the_reply_before_the_next()
    {
        var output = new StringWriter();
        var session = new HumanOperatorScript(output, "7").NewSession();
        var container = new HumanFakeContainer(new FakeSandboxScript(), session);
        var requests = $"{SandboxService.ServicesDir}/human_agent/{SandboxService.RequestsDir}";
        var responses = $"{SandboxService.ServicesDir}/human_agent/{SandboxService.ResponsesDir}";
        string[] find = ["find", requests, "-maxdepth", "1", "-name", "*.json", "-type", "f", "-print0"];

        // first poll: the instructions request appears
        var first = await container.ExecAsync(find);
        var requestFile = Assert.Single(first.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        var request = JsonNode.Parse((await container.ExecAsync(["cat", "--", requestFile])).Stdout)!.AsObject();
        Assert.Equal("instructions", request["method"]!.GetValue<string>());
        var id = request["id"]!.GetValue<string>();

        // nothing new until the service answers
        Assert.Equal(first.Stdout, (await container.ExecAsync(find)).Stdout);

        // the service answers: the reply is printed and the next request (start) goes out
        await container.ExecAsync(["tee", "--", $"{responses}/{id}.json"], input: new JsonObject { ["id"] = id, ["result"] = "the instructions", ["error"] = null }.ToJsonString());
        await container.ExecAsync(["rm", "-f", "--", requestFile]);
        var second = await container.ExecAsync(find);
        var next = JsonNode.Parse((await container.ExecAsync(["cat", "--", Assert.Single(second.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))])).Stdout)!.AsObject();

        Assert.Equal("start", next["method"]!.GetValue<string>());
        Assert.False(container.Exists($"{responses}/{id}.json"));
        Assert.Equal(("instructions", "the instructions"), (session.Replies[0].Method, session.Replies[0].Result!.GetValue<string>()));
        Assert.Contains("$ task instructions", output.ToString());
        Assert.Contains("the instructions", output.ToString());
        Assert.Contains("$ task start", output.ToString());
        Assert.False(session.Finished);
        Assert.Equal(["instructions", "start", "note", "status", "validate", "submit"], session.Script.Steps.Select(step => step.Method));
        Assert.Equal("7", session.Script.Steps[^1].Parameters["answer"]!.GetValue<string>());
    }

    [Fact]
    public async Task the_operator_stops_when_validate_answers_with_text()
    {
        var output = new StringWriter();
        var session = new HumanOperatorScript(output).NewSession();
        var container = new HumanFakeContainer(new FakeSandboxScript(), session);
        var requests = $"{SandboxService.ServicesDir}/human_agent/{SandboxService.RequestsDir}";
        var responses = $"{SandboxService.ServicesDir}/human_agent/{SandboxService.ResponsesDir}";
        string[] find = ["find", requests, "-maxdepth", "1", "-name", "*.json", "-type", "f", "-print0"];

        // answer every step with null until validate, which fails
        for (var step = 0; step < 5; step++)
        {
            var listing = await container.ExecAsync(find);
            var requestFile = Assert.Single(listing.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries));
            var request = JsonNode.Parse((await container.ExecAsync(["cat", "--", requestFile])).Stdout)!.AsObject();
            var id = request["id"]!.GetValue<string>();
            var result = request["method"]!.GetValue<string>() == "validate" ? "FAILED: An explicit answer is required for scoring this task." : null;
            await container.ExecAsync(["tee", "--", $"{responses}/{id}.json"], input: new JsonObject { ["id"] = id, ["result"] = result, ["error"] = null }.ToJsonString());
            await container.ExecAsync(["rm", "-f", "--", requestFile]);
        }

        var after = await container.ExecAsync(find);

        Assert.Equal("", after.Stdout);
        Assert.True(session.Finished);
        Assert.Equal(["instructions", "start", "note", "status", "validate"], session.Replies.Select(reply => reply.Method));
        Assert.Contains("FAILED: An explicit answer is required", output.ToString());
        Assert.DoesNotContain("Thank you for working on this task!", output.ToString());
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end (a person's session, scripted, through the runner)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_runner_runs_the_human_agent_offline_until_the_scripted_operator_submits()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["human", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : human", text);
        Assert.Contains("sandbox   : fake, scripted by the example", text);
        Assert.Contains("Login to the system with the following command:", text);
        Assert.Contains(HumanFakeContainer.LoginCommand, text);
        Assert.Contains("[human_agent] Status: Stopped  Time: 0:00:00", text);
        Assert.Contains("$ task instructions", text);
        Assert.Contains("Human Agent Task", text);
        Assert.Contains("task submit        Submit your final answer for the task.", text);
        Assert.Contains("$ task start", text);
        Assert.Contains("Status: Running  Time: 0:00:0", text);
        Assert.Contains("$ task note", text);
        Assert.Contains("[human_agent] Note recorded:\n## Human Agent Note", text);
        Assert.Contains("$ task status", text);
        Assert.Contains("$ task submit 42", text);
        Assert.Contains("Thank you for working on this task!", text);
        Assert.Contains("[human_agent] Answer submitted: '42'", text);
        Assert.Contains("[human_agent] Final answer: 42", text);
        Assert.Contains("status    : success (1/1 samples completed)", text);

        var provider = Assert.IsType<HumanFakeSandboxProvider>(SandboxRegistry.Get("fake"));
        var container = Assert.Single(provider.Containers);
        Assert.True(container.Operator.Finished);
        Assert.Equal(["instructions", "start", "note", "status", "validate", "submit"], container.Operator.Replies.Select(reply => reply.Method));
        Assert.Contains(container.Calls, call => call.Cmd[0] == "mkdir" && call.Cmd[^1] == HumanAgentInstall.HumanAgentDir && call.User == "root");
        Assert.Contains(container.Calls, call => call.Cmd[0] == "bash" && call.Cmd[1] == "./install.sh");
        Assert.Contains(container.Calls, call => call.Cmd[0] == "which" && call.Cmd[1] == "python3");
        Assert.True(container.Exists($"{SandboxService.ServicesDir}/human_agent/human_agent.py"));
        Assert.True(container.Exists($"{HumanAgentInstall.HumanAgentDir}/task.py"));
        Assert.Contains(provider.Script.Calls, call => call.Cmd[0] == "find");

        var log = await EvalLogWriter.ReadAsync(Assert.Single(Directory.GetFiles(_logDir, "*.eval")));
        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("42", sample.Output.Completion);
        Assert.Equal(HumanCli.ModelName, sample.Output.Model);
        var infos = sample.Events.OfType<InfoEvent>().Where(e => e.Source == "human_agent").ToList();
        Assert.Equal("stop", infos[0].Data!["action"]!.GetValue<string>());
        Assert.Equal("start", infos[1].Data!["action"]!.GetValue<string>());
        Assert.Contains(infos, e => e.Data is JsonValue value && value.TryGetValue<string>(out var note) && note.StartsWith("## Human Agent Note", StringComparison.Ordinal));
        Assert.Contains(sample.Store.Keys, key => key.EndsWith(":answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task the_runner_passes_the_user_and_the_fake_answer_through()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["human", "--fake", "--log-dir", _logDir, "-T", "user=nonroot", "-T", "fake_answer=hello"], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("$ task submit hello", text);
        Assert.Contains("[human_agent] Final answer: hello", text);
        var container = Assert.Single(Assert.IsType<HumanFakeSandboxProvider>(SandboxRegistry.Get("fake")).Containers);
        Assert.Contains(container.Calls, call => call.Cmd.SequenceEqual(["chown", "nonroot", HumanAgentInstall.HumanAgentDir]) && call.User == "root");
        var log = await EvalLogWriter.ReadAsync(Assert.Single(Directory.GetFiles(_logDir, "*.eval")));
        Assert.Equal("hello", Assert.Single(log.Samples!).Output.Completion);
    }

    [Theory]
    [InlineData("none", "human needs a sandbox that supports connections")]
    [InlineData("local", "human cannot use the local sandbox")]
    public async Task the_runner_refuses_sandboxes_the_agent_cannot_use(string sandbox, string message)
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["human", "--fake", "--sandbox", sandbox, "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        Assert.Equal(2, exit);
        Assert.Contains(message, output.ToString());
    }
}
