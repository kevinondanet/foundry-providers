using System.Reflection;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Support;
using InspectAzureAI.Examples.Intervention;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Intervention;

using ComputerTool = InspectAzureAI.Eval.Tools.Computer;
using InterventionPort = InspectAzureAI.Examples.Intervention.Intervention;

/// <summary>
/// Tests for the port of <c>examples/intervention/intervention.py</c> (<see cref="InterventionExample"/>): the task's shape
/// per mode, the verbatim prompts, the <c>user_prompt</c> and <c>agent_loop</c> solvers against a scripted console, the
/// fake-mode scripts, and the three modes end to end through the examples runner (with and without approval).
/// </summary>
public sealed class InterventionTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "intervention-" + Guid.NewGuid().ToString("N"));

    private static readonly string ExampleDirectory = Path.Combine(AppContext.BaseDirectory, "intervention");

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

    private static InputConsole ScriptedConsole(params string[] lines) => new(new StringReader(string.Join("\n", lines) + "\n"), new StringWriter());

    private static TaskState State(string input = "prompt") => new("scripted", 1, 1, input, [new ChatMessageUser(input)]);

    // ----------------------------------------------------------------------------------------------------------
    // the task (port of @task def intervention(mode, approval))
    // ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("shell", "shell/compose.yaml")]
    [InlineData("computer", "computer/compose.yaml")]
    [InlineData("multi-tool", "multi_tool/compose.yaml")]
    [InlineData("anything-else", "multi_tool/compose.yaml")]
    public void the_task_is_named_intervention_with_one_default_sample_and_the_modes_compose_file(string mode, string compose)
    {
        var task = InterventionPort.Build(mode, exampleDirectory: "/example");

        Assert.Equal("intervention", task.Name);
        var sample = Assert.Single(task.Dataset);
        Assert.Equal("prompt", sample.Input.Text);
        Assert.Empty(task.Scorers);
        Assert.Equal(new SandboxSpec("docker", Path.Combine("/example", compose.Replace('/', Path.DirectorySeparatorChar))), task.Sandbox);
        Assert.Null(task.Approval);
        Assert.NotNull(task.Solver);
    }

    [Fact]
    public void approval_adds_the_human_approver_or_the_computer_policy_file()
    {
        Assert.Equal("human", InterventionPort.Build("shell", approval: true, exampleDirectory: "/example").Approval!.Spec);
        Assert.Equal("human", InterventionPort.Build("multi-tool", approval: true, exampleDirectory: "/example").Approval!.Spec);
        Assert.Equal(Path.Combine("/example", "computer", "approval.json"), InterventionPort.Build("computer", approval: true, exampleDirectory: "/example").Approval!.Spec);
    }

    [Fact]
    public void the_computer_approval_policy_matches_the_python_yaml()
    {
        var policy = JsonNode.Parse(File.ReadAllText(Path.Combine(ExampleDirectory, "computer", "approval.json")))!.AsObject();

        var approvers = policy["approvers"]!.AsArray();
        Assert.Equal(2, approvers.Count);
        Assert.Equal("human", approvers[0]!["name"]!.GetValue<string>());
        Assert.Equal(
            ["computer(action='key'", "computer(action='left_click'", "computer(action='middle_click'", "computer(action='double_click'"],
            approvers[0]!["tools"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal("auto", approvers[1]!["name"]!.GetValue<string>());
        Assert.Equal("*", approvers[1]!["tools"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(ExampleDirectory, "computer", "approval.yaml")), "approval.yaml is copied verbatim next to the JSON policy");
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_with_the_python_parameters()
    {
        var method = typeof(InterventionExample).GetMethod(nameof(InterventionExample.InterventionTask), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal("intervention", method!.GetCustomAttribute<TaskAttribute>()!.Name);
        var parameters = method.GetParameters();
        Assert.Equal(["mode", "approval"], parameters.Select(parameter => parameter.Name));
        Assert.Equal("shell", parameters[0].DefaultValue);
        Assert.Equal(false, parameters[1].DefaultValue);
        Assert.Equal("intervention", InterventionExample.InterventionTask().Name);
    }

    [Fact]
    public void the_data_files_are_copied_verbatim()
    {
        Assert.Contains("FROM ubuntu:24.04", File.ReadAllText(Path.Combine(ExampleDirectory, "shell", "Dockerfile")));
        Assert.Contains("build: .", File.ReadAllText(Path.Combine(ExampleDirectory, "shell", "compose.yaml")));
        Assert.Contains("image: aisiuk/inspect-tool-support", File.ReadAllText(Path.Combine(ExampleDirectory, "multi_tool", "compose.yaml")));
        var computer = File.ReadAllText(Path.Combine(ExampleDirectory, "computer", "compose.yaml"));
        Assert.Contains("image: aisiuk/inspect-computer-tool", computer);
        Assert.Contains("\"127.0.0.1::5900\"", computer);
        Assert.Contains("\"127.0.0.1::6080\"", computer);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the prompts (verbatim)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_prompts_keep_pythons_dedented_text_with_its_leading_and_trailing_newline()
    {
        Assert.StartsWith("\nYou are an extremely capable general purpose agent.", InterventionPort.ShellPrompt, StringComparison.Ordinal);
        Assert.EndsWith("Please reflect on what actions to take before you take them.\n", InterventionPort.ShellPrompt, StringComparison.Ordinal);
        Assert.Contains("\n\nYour \"python\" tool will execute a given script.", InterventionPort.ShellPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("    You", InterventionPort.ShellPrompt, StringComparison.Ordinal);

        Assert.StartsWith("\nYou are an extremely capable", InterventionPort.ComputerPrompt, StringComparison.Ordinal);
        Assert.EndsWith("Only when you confirm a step was executed correctly should you move on to the next one.\n", InterventionPort.ComputerPrompt, StringComparison.Ordinal);

        Assert.Contains("You have a bash_session tool, a text_editor tools, and a set of web browser tools.", InterventionPort.MultiToolPrompt, StringComparison.Ordinal);

        Assert.Equal(
            "The agent has stopped calling tools. Please either:\n\n- Type a message to send to the agent\n- Type nothing and hit enter to ask the agent to continue\n- Type 'exit' to end the conversation\n\n",
            InterventionPort.NextActionPrompt);
        Assert.Equal("Please enter your initial prompt for the model:\n\n", InterventionPort.UserPromptText);
        Assert.Equal("Please continue working on this task.", InterventionPort.ContinueMessage);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the solvers (port of user_prompt, agent_loop and ask_for_next_action)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task user_prompt_replaces_the_user_prompt_content_with_what_the_operator_types()
    {
        var output = new StringWriter();
        var console = new InputConsole(new StringReader("Please list the files\n"), output);
        var state = State();
        var originalId = state.UserPrompt.Id;

        var result = await InterventionPort.UserPrompt(console)(state, NeverGenerate, CancellationToken.None);

        Assert.Same(state, result);
        Assert.Equal("Please list the files", result.UserPrompt.Text);
        Assert.Equal(originalId, result.UserPrompt.Id);
        Assert.Single(result.Messages);
        var text = output.ToString();
        Assert.Contains("── User Prompt ──", text, StringComparison.Ordinal);
        Assert.Contains("Please enter your initial prompt for the model:\n\n: ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task agent_loop_continues_on_enter_sends_messages_and_stops_on_exit()
    {
        var generations = 0;
        Task<TaskState> Generate(TaskState state, ToolCallsMode toolCalls, GenerateConfig? config, CachePolicy? cache, CancellationToken cancellationToken)
        {
            generations++;
            state.Messages.Add(new ChatMessageAssistant($"turn {generations}"));
            return Task.FromResult(state);
        }

        var state = State();
        var result = await InterventionPort.AgentLoop(ScriptedConsole("", "Now check the python version", "  EXIT  "))(state, Generate, CancellationToken.None);

        Assert.Equal(3, generations);
        Assert.Equal(
            ["user:prompt", "assistant:turn 1", "user:Please continue working on this task.", "assistant:turn 2", "user:Now check the python version", "assistant:turn 3"],
            result.Messages.Select(message => $"{message.Role}:{message.Text}"));
    }

    [Fact]
    public async Task agent_loop_ends_without_asking_when_the_state_is_completed()
    {
        var asked = new StringWriter();
        Task<TaskState> Generate(TaskState state, ToolCallsMode toolCalls, GenerateConfig? config, CachePolicy? cache, CancellationToken cancellationToken)
        {
            state.Completed = true;
            return Task.FromResult(state);
        }

        var result = await InterventionPort.AgentLoop(new InputConsole(new StringReader(""), asked))(State(), Generate, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Single(result.Messages);
        Assert.DoesNotContain("Next Action", asked.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ask_for_next_action_shows_the_prompt_with_an_empty_default_and_returns_the_line()
    {
        var output = new StringWriter();

        var typed = await InterventionPort.AskForNextActionAsync(new InputConsole(new StringReader("do this\n"), output));
        var empty = await InterventionPort.AskForNextActionAsync(new InputConsole(new StringReader("\n"), new StringWriter()));

        Assert.Equal("do this", typed);
        Assert.Equal("", empty);
        var text = output.ToString();
        Assert.Contains("── Next Action ──", text, StringComparison.Ordinal);
        Assert.Contains("- Type 'exit' to end the conversation\n\n (): ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task intervention_agent_sets_the_modes_tools()
    {
        var shell = State();
        await InterventionPort.InterventionAgent("shell", ScriptedConsole("hi"))(shell, CompletedGenerate, CancellationToken.None);
        var computer = State();
        await InterventionPort.InterventionAgent("computer", ScriptedConsole("hi"))(computer, CompletedGenerate, CancellationToken.None);
        var multi = State();
        await InterventionPort.InterventionAgent("multi-tool", ScriptedConsole("hi"))(multi, CompletedGenerate, CancellationToken.None);

        Assert.Equal(["bash", "python"], shell.Tools.Select(tool => tool.Name));
        Assert.Equal(["computer"], computer.Tools.Select(tool => tool.Name));
        Assert.Equal(
            ["bash_session", "text_editor", "web_browser_go", "web_browser_click", "web_browser_type_submit", "web_browser_type", "web_browser_scroll", "web_browser_back", "web_browser_forward", "web_browser_refresh"],
            multi.Tools.Select(tool => tool.Name));
        Assert.Equal(InterventionPort.ShellPrompt, Assert.IsType<ChatMessageSystem>(shell.Messages[0]).Text);
        Assert.Equal(InterventionPort.ComputerPrompt, Assert.IsType<ChatMessageSystem>(computer.Messages[0]).Text);
        Assert.Equal(InterventionPort.MultiToolPrompt, Assert.IsType<ChatMessageSystem>(multi.Messages[0]).Text);
        Assert.Equal("hi", shell.UserPrompt.Text);
    }

    private static Task<TaskState> NeverGenerate(TaskState state, ToolCallsMode toolCalls, GenerateConfig? config, CachePolicy? cache, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("generate must not be called");

    private static Task<TaskState> CompletedGenerate(TaskState state, ToolCallsMode toolCalls, GenerateConfig? config, CachePolicy? cache, CancellationToken cancellationToken)
    {
        state.Completed = true;
        return Task.FromResult(state);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the example and its fake-mode scripts
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_example_is_registered_with_a_docker_default_and_a_fake_sandbox_per_mode()
    {
        var example = Assert.IsType<InterventionExample>(ExampleRegistry.Default.Get("intervention"));

        Assert.Equal("intervention", example.Name);
        Assert.Equal(["intervention"], example.Tasks.Select(task => task.Name));
        Assert.Equal("docker", example.Defaults.Sandbox);
        Assert.Equal(Path.Combine("shell", "compose.yaml"), example.Defaults.ComposeFile);
        Assert.True(example.Defaults.NeedsDocker);
        Assert.Null(example.Defaults.Approval);
        Assert.NotEmpty(example.Deviations);
        Assert.NotNull(example.FakeSandbox(Context()));
        Assert.NotNull(example.FakeSandbox(Context(taskArgs: ("mode", "computer"))));
        Assert.Equal(InterventionScript.FakeModelName, example.CreateFakeModel(Context()).Name);
    }

    [Fact]
    public void build_keeps_the_modes_docker_compose_file_but_takes_any_other_resolved_sandbox()
    {
        var example = new InterventionExample();
        var runnerDocker = new SandboxSpec("docker", Path.Combine(ExampleDirectory, "shell", "compose.yaml"));

        var computer = example.Tasks[0].Build(Context(fake: false, sandbox: runnerDocker, taskArgs: ("mode", "computer")));
        var local = example.Tasks[0].Build(Context(fake: false, sandbox: new SandboxSpec("local")));
        var fake = example.Tasks[0].Build(Context(fake: true, sandbox: new SandboxSpec("fake"), taskArgs: ("approval", "true")));

        Assert.Equal(new SandboxSpec("docker", Path.Combine(ExampleDirectory, "computer", "compose.yaml")), computer.Sandbox);
        Assert.Null(computer.MessageLimit);
        Assert.Equal(new SandboxSpec("local"), local.Sandbox);
        Assert.Equal(new SandboxSpec("fake"), fake.Sandbox);
        Assert.Equal(InterventionExample.FakeMessageLimit, fake.MessageLimit);
        Assert.Equal("human", fake.Approval!.Spec);
        Assert.Throws<PrerequisiteError>(() => example.Tasks[0].Build(Context(sandbox: null)));
    }

    [Fact]
    public async Task the_shell_script_answers_the_scripted_commands()
    {
        var script = InterventionScript.For("shell");
        var sandbox = script.Sandbox();
        var environment = new ScriptedSandboxEnvironment(sandbox);

        var listing = await environment.ExecAsync(["bash", "--login", "-c", "ls -la"]);
        var count = await environment.ExecAsync(["bash", "--login", "-c", "python3 -"], input: "import os\nprint(len(os.listdir('.')))");
        var version = await environment.ExecAsync(["bash", "--login", "-c", "python3 --version"]);
        var other = await environment.ExecAsync(["bash", "--login", "-c", "echo hi"]);

        Assert.Equal(InterventionScript.ShellListing, listing.Stdout);
        Assert.Equal("3\n", count.Stdout);
        Assert.Equal("Python 3.12.3\n", version.Stdout);
        Assert.True(other.Success);
        Assert.Equal("", other.Stdout);
        Assert.Equal(["List the files in the working directory and tell me how many entries there are.", "", "Which Python version is installed?", "exit"], script.OperatorLines);
    }

    [Fact]
    public async Task the_computer_script_answers_the_tool_service_with_screenshots()
    {
        var environment = new ScriptedSandboxEnvironment(InterventionScript.For("computer").Sandbox());

        var present = await environment.ExecAsync(["test", "-r", ComputerTool.ToolPath]);
        var screenshot = await environment.ExecAsync(["python3", ComputerTool.ToolPath, "screenshot"]);
        var click = await environment.ExecAsync(["python3", ComputerTool.ToolPath, "double_click", "--coordinate", "40", "60"]);

        Assert.True(present.Success);
        var shot = JsonNode.Parse(screenshot.Stdout)!.AsObject();
        Assert.Equal(InterventionScript.ScreenshotPng, shot["base64_image"]!.GetValue<string>());
        Assert.Null(shot["output"]);
        var clicked = JsonNode.Parse(click.Stdout)!.AsObject();
        Assert.Equal("Performed double_click --coordinate 40 60", clicked["output"]!.GetValue<string>());
        Assert.Equal(InterventionScript.ScreenshotPng, clicked["base64_image"]!.GetValue<string>());
    }

    [Fact]
    public async Task the_multi_tool_script_answers_json_rpc_on_both_tool_clis()
    {
        var environment = new ScriptedSandboxEnvironment(InterventionScript.For("multi-tool").Sandbox());

        var injected = await environment.ExecAsync(["test", "-r", SandboxToolSupport.SandboxCli]);
        var onPath = await environment.ExecAsync(["which", LegacyToolSupport.LegacySandboxCli]);
        var session = await environment.ExecAsync([SandboxToolSupport.SandboxCli, "exec"], input: """{"jsonrpc":"2.0","method":"bash_session_new_session","params":{},"id":7}""");
        var bash = await environment.ExecAsync([SandboxToolSupport.SandboxCli, "exec"], input: """{"jsonrpc":"2.0","method":"bash_session","params":{"session_name":"s","input":"echo hi\n"},"id":8}""");
        var version = await environment.ExecAsync([LegacyToolSupport.LegacySandboxCli, "exec"], input: """{"jsonrpc":"2.0","method":"version","params":null,"id":9}""");
        var go = await environment.ExecAsync([LegacyToolSupport.LegacySandboxCli, "exec"], input: """{"jsonrpc":"2.0","method":"web_go","params":{"url":"https://example.com","session_name":"b"},"id":10}""");
        var unknown = await environment.ExecAsync([LegacyToolSupport.LegacySandboxCli, "exec"], input: """{"jsonrpc":"2.0","method":"nope","params":{},"id":11}""");

        Assert.True(injected.Success);
        Assert.True(onPath.Success);
        Assert.Equal("fake-bash-session", JsonNode.Parse(session.Stdout)!["result"]!["session_name"]!.GetValue<string>());
        Assert.Equal(7, JsonNode.Parse(session.Stdout)!["id"]!.GetValue<int>());
        Assert.Equal("$ echo hi\n$ ", JsonNode.Parse(bash.Stdout)!["result"]!.GetValue<string>());
        Assert.Equal("1.0.0", JsonNode.Parse(version.Stdout)!["result"]!.GetValue<string>());
        var page = JsonNode.Parse(go.Stdout)!["result"]!.AsObject();
        Assert.Equal("https://example.com", page["web_url"]!.GetValue<string>());
        Assert.Equal(InterventionScript.ExampleMainContent, page["main_content"]!.GetValue<string>());
        Assert.Equal(-32601, JsonNode.Parse(unknown.Stdout)!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void the_operator_console_cycles_its_lines_and_echoes_them()
    {
        var echo = new StringWriter();
        var reader = new InterventionScript.CyclicLineReader(["a", "b"], echo);

        Assert.Equal(["a", "b", "a"], new[] { reader.ReadLine()!, reader.ReadLine()!, reader.ReadLine()! });
        Assert.Equal(3, reader.LinesRead);
        Assert.Equal("a\nb\na\n".ReplaceLineEndings(), echo.ToString().ReplaceLineEndings());
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end (port of `inspect eval examples/intervention -T mode=... --display conversation`, offline)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_runner_runs_the_shell_mode_offline_through_the_intervention_loop()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["intervention", "--fake", "--log-dir", _logDir, "--display", "conversation"], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : intervention", text);
        Assert.Contains("sandbox   : fake, scripted by the example", text);
        Assert.Contains("── User Prompt ──", text);
        Assert.Contains("List the files in the working directory and tell me how many entries there are.", text);
        Assert.Equal(3, Occurrences(text, "── Next Action ──"));
        Assert.Contains("── Assistant ──", text);
        Assert.Contains("── Tool Output: python ──", text);
        Assert.Contains("status    : success (1/1 samples completed)", text);

        var log = await ReadLogAsync();
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        var calls = sample.Messages.OfType<ChatMessageAssistant>().SelectMany(message => message.ToolCalls ?? []).ToList();
        Assert.Equal(["bash", "python", "bash"], calls.Select(call => call.Function));
        Assert.Equal("ls -la", calls[0].Arguments["cmd"]!.GetValue<string>());
        Assert.Equal("The installed interpreter is Python 3.12.3.", sample.Output.Completion);
        Assert.Equal(
            ["Please continue working on this task.", "Which Python version is installed?"],
            sample.Messages.OfType<ChatMessageUser>().Skip(1).Select(message => message.Text));
        // one screen for the prompt, then an ask screen and a blank screen per next action (three of them)
        Assert.Equal(7, sample.Events.OfType<InputEvent>().Count());
        Assert.Contains(sample.Events.OfType<InputEvent>(), e => e.Input.Contains("Which Python version is installed?", StringComparison.Ordinal));
        Assert.Empty(sample.Events.OfType<ApprovalEvent>());
    }

    [Fact]
    public async Task the_runner_runs_the_computer_mode_offline_with_the_policy_gating_clicks()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["intervention", "--fake", "--log-dir", _logDir, "-T", "mode=computer", "-T", "approval=true"], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("[human approver] computer(double_click)", text);
        Assert.Contains("status    : success (1/1 samples completed)", text);

        var log = await ReadLogAsync();
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        var calls = sample.Messages.OfType<ChatMessageAssistant>().SelectMany(message => message.ToolCalls ?? []).ToList();
        Assert.Equal(["computer", "computer", "computer"], calls.Select(call => call.Function));
        Assert.Equal(["screenshot", "double_click", "screenshot"], calls.Select(call => call.Arguments["action"]!.GetValue<string>()));
        Assert.Contains(sample.Messages.OfType<ChatMessageTool>(), message => message.Content.Items?.OfType<ContentImage>().Any() == true);
        Assert.Equal(
            ["auto:approve", "human:reject", "auto:approve"],
            sample.Events.OfType<ApprovalEvent>().Select(e => $"{e.Approver}:{e.Decision}"));
        Assert.StartsWith("I have evaluated step 2", sample.Output.Completion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task the_runner_runs_the_multi_tool_mode_offline_over_the_fake_tool_services()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["intervention", "--fake", "--log-dir", _logDir, "-T", "mode=multi-tool"], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("status    : success (1/1 samples completed)", text);

        var log = await ReadLogAsync();
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        var calls = sample.Messages.OfType<ChatMessageAssistant>().SelectMany(message => message.ToolCalls ?? []).ToList();
        Assert.Equal(["bash_session", "text_editor", "web_browser_go"], calls.Select(call => call.Function));
        var tools = sample.Messages.OfType<ChatMessageTool>().ToList();
        Assert.Equal(3, tools.Count);
        Assert.Contains("echo 'hello world' > /tmp/hello.txt", tools[0].Text, StringComparison.Ordinal);
        Assert.Contains("hello world", tools[1].Text, StringComparison.Ordinal);
        Assert.Contains("main content:\nExample Domain", tools[2].Text, StringComparison.Ordinal);
        Assert.Contains("accessibility tree:\nRootWebArea \"Example Domain\"", tools[2].Text, StringComparison.Ordinal);
        Assert.StartsWith("Done: /tmp/hello.txt contains 'hello world'", sample.Output.Completion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task the_runner_rejects_every_shell_call_under_the_human_approver_and_still_completes()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["intervention", "--fake", "--log-dir", _logDir, "-T", "approval=true"], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("[human approver] bash(ls -la)", text);
        Assert.Contains("3 approval decisions", text);
        var log = await ReadLogAsync();
        var sample = Assert.Single(log.Samples!);
        Assert.Equal(["human:reject", "human:reject", "human:reject"], sample.Events.OfType<ApprovalEvent>().Select(e => $"{e.Approver}:{e.Decision}"));
    }

    [Fact]
    public async Task the_runner_refuses_to_run_without_a_sandbox()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["intervention", "--fake", "--sandbox", "none", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        Assert.Equal(2, exit);
        Assert.Contains("intervention needs a sandbox", output.ToString());
    }

    private async Task<EvalLog> ReadLogAsync()
    {
        var file = Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
        return await EvalLogWriter.ReadAsync(file);
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        for (var index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
