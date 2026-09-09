using System.Reflection;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.HttpProxy;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.HttpProxy;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace.
using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/http_proxy</c> (<see cref="HttpProxyExample"/>, <see cref="Claude"/>): the task's
/// shape, the verbatim container files, the agent's wiring, and the eval run end to end without a network or Docker —
/// the scripted sandbox plays the container, a stand-in <c>claude</c> drives both routes of the real sandbox agent
/// bridge (its own turns and the script's FutureModel request), and the scripted model answers as Claude Code and
/// as FutureModel.
/// </summary>
public sealed class HttpProxyTests : IDisposable
{
    private static readonly string ExampleDirectory = Path.Combine(AppContext.BaseDirectory, "http_proxy");

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "http-proxy-" + Guid.NewGuid().ToString("N"));

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

    private static ExampleContext Context(SandboxSpec? sandbox = null, TextWriter? output = null) =>
        new(ExampleDirectory, sandbox, true, new Dictionary<string, string>(StringComparer.Ordinal), null, null, output ?? TextWriter.Null);

    // ----------------------------------------------------------------------------------------------------------
    // the task (port of @task def http_proxy_demo)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_http_proxy_demo()
    {
        var task = HttpProxyExample.HttpProxyDemo();

        Assert.Equal("http_proxy_demo", task.Name);
        var sample = Assert.Single(task.Dataset);
        Assert.Equal(
            "Write a script that integrates the FutureModel API (https://api.futuremodel.ai/v1/chat/completions) and run it to generate a haiku about coding. The model name is 'futuremodel-1' and the API key is in the FUTUREMODEL_API_KEY environment variable.",
            sample.Input.Text);
        Assert.Null(sample.Files);
        Assert.Equal(new SandboxSpec("docker", Path.Combine(HttpProxyExample.DefaultDirectory, "compose.yaml")), task.Sandbox);
        Assert.Empty(task.Scorers);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_as_http_proxy_demo()
    {
        var method = typeof(HttpProxyExample).GetMethod(nameof(HttpProxyExample.HttpProxyDemo), BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        var attribute = method!.GetCustomAttribute<TaskAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal("http_proxy_demo", attribute!.Name);
    }

    [Fact]
    public void the_container_files_are_copied_verbatim_next_to_the_assembly()
    {
        var compose = File.ReadAllText(Path.Combine(ExampleDirectory, "compose.yaml"));
        Assert.Contains("network_mode: none", compose);
        Assert.Contains("init: true", compose);
        Assert.Contains("- HTTP_PROXY=http://localhost:8080", compose);
        Assert.Contains("- HTTPS_PROXY=http://localhost:8080", compose);
        Assert.Contains("- NO_PROXY=localhost,127.0.0.1", compose);
        Assert.Contains("- ./remap.py:/remap.py:ro", compose);

        var entrypoint = File.ReadAllText(Path.Combine(ExampleDirectory, "entrypoint.sh"));
        Assert.StartsWith("#!/bin/bash", entrypoint, StringComparison.Ordinal);
        Assert.Contains("mitmdump -s /remap.py --set connection_strategy=lazy -q &", entrypoint);
        Assert.Contains("exec \"$@\"", entrypoint);

        var remap = File.ReadAllText(Path.Combine(ExampleDirectory, "remap.py"));
        Assert.Contains("BRIDGE_PORT = int(os.environ.get(\"BRIDGE_PORT\", \"13131\"))", remap);
        Assert.Contains("FAKE_API_HOST = os.environ.get(\"FAKE_API_HOST\", \"api.futuremodel.ai\")", remap);
        Assert.Contains("403,", remap);
        Assert.Contains("body[\"model\"] = FAKE_MODEL", remap);

        var dockerfile = File.ReadAllText(Path.Combine(ExampleDirectory, "Dockerfile"));
        Assert.Contains("FROM node:20-slim", dockerfile);
        Assert.Contains("RUN pip install --break-system-packages mitmproxy", dockerfile);
        Assert.Contains("update-ca-certificates", dockerfile);
        Assert.Contains("ENTRYPOINT [\"/entrypoint.sh\"]", dockerfile);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the agent (port of claude.py)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_agent_is_registered_as_claude_code_over_the_sandbox_binary_with_the_fake_api_key()
    {
        var agent = Claude.ClaudeCode();
        var options = Claude.Options();

        Assert.Equal("claude_code", agent.Name);
        Assert.Equal("sandbox", options.Version);
        Assert.Null(options.PermissionMode);
        Assert.Equal("fm-fake-key-for-demo", options.Env!["FUTUREMODEL_API_KEY"]);
        Assert.Single(options.Env);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the example
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_example_is_registered_with_docker_compose_defaults_and_a_fake_sandbox()
    {
        var example = Assert.IsType<HttpProxyExample>(ExampleRegistry.Default.Get("http_proxy"));

        Assert.Equal("http_proxy", example.Name);
        Assert.Equal(["http_proxy_demo"], example.Tasks.Select(task => task.Name));
        Assert.Equal("docker", example.Defaults.Sandbox);
        Assert.Equal("compose.yaml", example.Defaults.ComposeFile);
        Assert.True(example.Defaults.NeedsDocker);
        Assert.NotEmpty(example.Deviations);
        Assert.Contains(example.Deviations, deviation => deviation.Contains("network_mode: none", StringComparison.Ordinal));
        Assert.NotNull(example.FakeSandbox(Context()));
        Assert.Equal(HttpProxyExample.FakeModelName, example.CreateFakeModel(Context()).Name);
        Assert.Throws<PrerequisiteError>(() => example.Tasks[0].Build(Context(sandbox: null)));
        Assert.Equal(new SandboxSpec("fake"), example.Tasks[0].Build(Context(new SandboxSpec("fake"))).Sandbox);
    }

    [Fact]
    public void build_notes_the_unreachable_bridge_for_a_docker_sandbox_only()
    {
        var example = new HttpProxyExample();
        var docker = new StringWriter();
        var fake = new StringWriter();

        example.Tasks[0].Build(Context(new SandboxSpec("docker", "compose.yaml"), docker));
        example.Tasks[0].Build(Context(new SandboxSpec("fake"), fake));

        Assert.Contains("network_mode: none", docker.ToString());
        Assert.Equal("", fake.ToString());
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end (port of `inspect eval task.py`, offline)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_eval_runs_claude_code_through_both_bridge_routes_and_reports_the_haiku()
    {
        var example = new HttpProxyExample();
        var script = example.FakeSandbox(Context())!;
        var sandbox = ScriptedSandboxProvider.Register(script);

        var log = await Eval.RunAsync(
            HttpProxyExample.Build(sandbox),
            new EvalOptions { Model = example.CreateFakeModel(Context()), LogDir = _logDir, LogFormat = LogFormat.Eval },
            CancellationToken.None);

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Null(log.Error);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);

        // the report carries the haiku FutureModel (the scripted model, over the bridge's OpenAI route) generated
        Assert.Equal(FakeHttpProxyModel.ReportPrefix + FakeHttpProxyModel.Haiku, sample.Output.Completion);
        var assistants = sample.Messages.OfType<ChatMessageAssistant>().Select(message => message.Text).ToList();
        Assert.Equal([FakeHttpProxyModel.Plan, FakeHttpProxyModel.ReportPrefix + FakeHttpProxyModel.Haiku], assistants);
        Assert.Contains(sample.Events.OfType<InfoEvent>(), info => info.Source == "claude_code");

        // the launch: the image's claude, Python's flags and environment, the fake API key for the agent to find
        var launch = Assert.Single(script.Calls, FakeClaudeCli.IsLaunch);
        Assert.Equal("/usr/local/bin/claude", launch.Cmd[4]);
        Assert.Contains("--dangerously-skip-permissions", launch.Cmd);
        Assert.Equal(HttpProxyExample.Input, FakeClaudeCli.Prompt(launch.Cmd));
        var env = launch.Env!;
        Assert.Equal("fm-fake-key-for-demo", env["FUTUREMODEL_API_KEY"]);
        Assert.StartsWith("http://127.0.0.1:", env["ANTHROPIC_BASE_URL"], StringComparison.Ordinal);
        Assert.Equal("1", env["IS_SANDBOX"]);
        Assert.Equal("1", env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);

        // the agent wrote the script, and its request went to the bridge's OpenAI route as futuremodel-1
        var environment = Assert.Single(script.Environments);
        var scriptText = environment.FileText(FakeClaudeCli.ScriptPath);
        Assert.NotNull(scriptText);
        Assert.Contains("https://api.futuremodel.ai/v1/chat/completions", scriptText);
        Assert.Contains("os.environ[\"FUTUREMODEL_API_KEY\"]", scriptText);
        var requests = environment.FileText(FakeClaudeCli.RequestLog)!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToList();
        Assert.Equal(
            ["/v1/messages", "/v1/chat/completions", "/v1/messages"],
            requests.Select(request => request["path"]!.GetValue<string>()));
        Assert.All(requests, request => Assert.Equal(200, request["status"]!.GetValue<int>()));
        Assert.Equal("futuremodel-1", requests[1]["model"]!.GetValue<string>());
        Assert.True(File.Exists(log.Location), $"the log was not written: {log.Location}");
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["http_proxy", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : http_proxy_demo", text);
        Assert.Contains($"model     : {HttpProxyExample.FakeModelName} (scripted, offline)", text);
        Assert.Contains("sandbox   : fake, scripted by the example", text);
        Assert.Contains("status    : success (1/1 samples completed)", text);
        Assert.DoesNotContain("network_mode: none", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }
}
