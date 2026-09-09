using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.CopilotCli;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The <c>execute</c> flow of the Copilot CLI agent against the in-memory sandbox: binary lookup, cwd, home
/// preparation, env and argv of the launch, folding of both JSONL shapes, exit handling and the attempts loop.
/// The sandbox bridge is replaced by a fake so no HTTP server is needed.
/// </summary>
[Collection("ClaudeCode")]
public class CopilotCliAgentTests
{
    private const string CopilotPath = "/usr/local/bin/copilot";

    private const string SessionJsonl =
        """{"type":"session.tools_updated","data":{"model":"inspect"},"ephemeral":true,"id":"1","timestamp":"t","parentId":null}""" + "\n"
        + """{"type":"assistant.message","data":{"messageId":"m1","model":"inspect","content":"","toolRequests":[{"toolCallId":"call_1","name":"bash","arguments":{"command":"echo hi"},"type":"function"}]},"id":"2","timestamp":"t","parentId":"1"}""" + "\n"
        + """{"type":"tool.execution_complete","data":{"toolCallId":"call_1","success":true,"result":{"content":"hi\n"}},"id":"3","timestamp":"t","parentId":"2"}""" + "\n"
        + """{"type":"result","timestamp":"t","sessionId":"e2f41e59-2d56-49b0-ab78-462b6b3b88d9","exitCode":0,"usage":{"premiumRequests":0}}""" + "\n";

    private const string FlatJsonl =
        """{"type":"message","role":"assistant","content":"looking","model":"inspect"}""" + "\n"
        + """{"type":"tool_use","name":"bash","input":{"command":"ls"},"id":"toolu_1","model":"inspect"}""" + "\n"
        + """{"type":"tool_result","tool_use_id":"toolu_1","content":"a.py"}""" + "\n"
        + """{"type":"result","timestamp":"t","sessionId":"flat-1","exitCode":0}""" + "\n";

    private sealed class FakeBridge(AgentBridge bridge) : ICopilotCliBridge
    {
        private readonly CancellationTokenSource _limit = new();

        public string BaseUrl => "http://127.0.0.1:4321";

        public string AuthToken => "tok-123";

        public AgentState State => bridge.State;

        private readonly CancellationTokenSource _terminate = new();

        public LimitExceededException? LimitError { get; private set; }

        public CancellationToken LimitReached => _limit.Token;

        public InspectAzureAI.Eval.Approval.TerminateSampleException? TerminateError { get; private set; }

        public CancellationToken TerminateRequested => _terminate.Token;

        public bool Disposed { get; private set; }

        public void HitLimit(LimitExceededException limit)
        {
            LimitError = limit;
            _limit.Cancel();
        }

        public void Terminate(InspectAzureAI.Eval.Approval.TerminateSampleException terminated)
        {
            TerminateError = terminated;
            _terminate.Cancel();
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A sample context over a scripted sandbox whose Copilot CLI launches are answered by <see cref="OnLaunch"/>.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly IDisposable _scope;

        public Harness(Func<TaskState, Task<IReadOnlyList<Score>>>? scorer = null, bool withSandbox = true, bool copilotInstalled = true)
        {
            Sandbox = new FakeSandboxEnvironment(cmd =>
            {
                if (cmd.Count > 4 && cmd[0] == "bash" && cmd[2] == CopilotCliCommand.LaunchScript)
                {
                    Launches.Add(cmd);
                    return OnLaunch(Launches.Count - 1);
                }

                return cmd[0] switch
                {
                    "mkdir" or "tar" or "chmod" or "rm" => FakeSandboxEnvironment.Ok(),
                    "test" => FakeSandboxEnvironment.Fail(1),
                    _ => cmd[2] switch
                    {
                        "which copilot" => copilotInstalled ? FakeSandboxEnvironment.Ok(CopilotPath + "\n") : FakeSandboxEnvironment.Fail(1),
                        "pwd" => FakeSandboxEnvironment.Ok("/workspace\n"),
                        "uname -s" => FakeSandboxEnvironment.Ok("Linux\n"),
                        "uname -m" => FakeSandboxEnvironment.Ok("aarch64\n"),
                        var c when c.StartsWith("if [ -f /lib/libc.musl", StringComparison.Ordinal) => FakeSandboxEnvironment.Ok("glibc\n"),
                        _ => FakeSandboxEnvironment.Fail(1, "unexpected command " + cmd[2]),
                    },
                };
            });
            Context = new SampleContext
            {
                ActiveModel = new Model(new ScriptedModelApi()),
                Sandboxes = withSandbox ? SandboxEnvironments.Single(Sandbox) : null,
                Scorer = scorer,
            };
            _scope = SampleContext.Begin(Context);
        }

        public FakeSandboxEnvironment Sandbox { get; }

        public SampleContext Context { get; }

        public List<IReadOnlyList<string>> Launches { get; } = [];

        public Func<int, ExecResult> OnLaunch { get; set; } = _ => FakeSandboxEnvironment.Ok(SessionJsonl);

        public FakeBridge? Bridge { get; private set; }

        public IModelEventSink? Sink { get; private set; }

        public CopilotCliAgent Agent(CopilotCliOptions? options = null) => new(options ?? new CopilotCliOptions())
        {
            BridgeFactory = (bridge, sandbox, port, _) =>
            {
                Assert.Same(Sandbox, sandbox);
                Assert.Equal(0, port);
                Sink = ModelEventSinks.Current;
                Bridge = new FakeBridge(bridge);
                return Task.FromResult<ICopilotCliBridge>(Bridge);
            },
        };

        public FakeExecCall LaunchCall(int index) => Sandbox.Calls.Where(c => c.Cmd.Count > 2 && c.Cmd[2] == CopilotCliCommand.LaunchScript).ElementAt(index);

        public void Dispose() => _scope.Dispose();
    }

    private static AgentState State(params ChatMessage[] messages) => new(messages);

    private static void Deliver(IModelEventSink sink, ModelOutput output) => sink.OnModelEvent(new ModelEvent
    {
        Model = "m",
        Input = [],
        ToolChoice = ToolChoice.None,
        Config = new GenerateConfig(),
        Output = output,
    });

    [Fact]
    public async Task runs_copilot_with_the_bridge_env_and_argv_and_folds_the_session_shape()
    {
        using var h = new Harness();
        var agent = h.Agent(new CopilotCliOptions { SystemPrompt = "Be terse" });
        var state = State(new ChatMessageSystem("Task system"), new ChatMessageUser("Fix the bug"));

        var result = await agent.ExecuteAsync(state);

        Assert.Same(state, result);
        Assert.Same(h.Bridge!.State, result);
        Assert.True(h.Bridge.Disposed);
        var launch = Assert.Single(h.Launches);
        Assert.Equal(
            [
                "bash", "-c", "exec 0</dev/null; \"$@\"", "bash", CopilotPath, "-p", "Task system\n\nBe terse\n\nFix the bug", "--session-id", agent.SessionId,
                "--output-format", "json", "--model", "inspect", "--no-auto-update", "--no-ask-user", "--disable-builtin-mcps", "--yolo",
                "--log-level", "error", "--log-dir", "/workspace/.copilot/logs",
            ],
            launch);
        var call = h.LaunchCall(0);
        Assert.Equal("/workspace", call.Cwd);
        Assert.Null(call.Input);
        Assert.Null(call.User);
        Assert.Null(call.Timeout);
        Assert.Equal("openai", call.Env!["COPILOT_PROVIDER_TYPE"]);
        Assert.Equal("http://127.0.0.1:4321/v1", call.Env["COPILOT_PROVIDER_BASE_URL"]);
        Assert.Equal("tok-123", call.Env["COPILOT_PROVIDER_API_KEY"]);
        Assert.Equal("inspect", call.Env["COPILOT_MODEL"]);
        Assert.Equal("/workspace/.copilot", call.Env["COPILOT_HOME"]);
        Assert.Equal("true", call.Env["COPILOT_ALLOW_ALL"]);
        Assert.Equal("true", call.Env["COPILOT_OFFLINE"]);
        Assert.Equal(["which copilot", "pwd", "/workspace/.copilot/logs", CopilotCliCommand.LaunchScript], h.Sandbox.Calls.Select(c => c.Cmd[2]));
        var prepare = h.Sandbox.Calls[2];
        Assert.Equal(["mkdir", "-p", "/workspace/.copilot/logs"], prepare.Cmd);
        Assert.Equal("/workspace", prepare.Cwd);

        var infos = h.Context.Transcript.Events.OfType<InfoEvent>().ToArray();
        Assert.Equal(4, infos.Length);
        Assert.All(infos, e => Assert.Equal("copilot_cli", e.Source));
        Assert.Equal("session.tools_updated", infos[0].Data!["type"]!.GetValue<string>());
        Assert.Equal("result", infos[3].Data!["type"]!.GetValue<string>());
        var tool = Assert.Single(h.Context.Transcript.Events.OfType<ToolEvent>());
        Assert.Equal("call_1", tool.Id);
        Assert.Equal("hi\n", tool.Result);
        Assert.False(h.Context.Store.Contains(CopilotCliDebug.StoreKey));
    }

    [Fact]
    public async Task options_flow_into_the_launch_and_the_bridged_tool_call_is_attached_to_the_tool_event()
    {
        using var h = new Harness();
        var served = new Model(new ScriptedModelApi([], "served"));
        var agent = h.Agent(new CopilotCliOptions
        {
            ModelConfig = "gpt-5",
            Effort = "high",
            Provider = CopilotCliProvider.Anthropic,
            Permission = CopilotCliPermission.AllowList,
            AllowedTools = ["shell"],
            CustomAgent = "hve",
            PluginDirs = ["/opt/hve"],
            User = "agent",
            Cwd = "/srv/app",
            Otel = true,
            Env = new Dictionary<string, string> { ["COPILOT_PROVIDER_API_KEY"] = "custom", ["EXTRA"] = "1" },
        });
        var bridgedArgs = new JsonObject { ["command"] = "echo hi", ["description"] = "from the bridge" };
        h.OnLaunch = _ =>
        {
            Deliver(h.Sink!, new ModelOutput { Model = "served", Choices = [new ChatCompletionChoice(new ChatMessageAssistant("", [new ToolCall("call_1", "bash", bridgedArgs)]), StopReason.ToolCalls)] });
            Deliver(h.Sink!, ModelOutput.FromContent("served", "done"));
            return FakeSandboxEnvironment.Ok(SessionJsonl);
        };

        await agent.ExecuteAsync(State(new ChatMessageUser("go")));

        var launch = Assert.Single(h.Launches);
        Assert.Equal(["-p", "go", "--session-id", agent.SessionId, "--output-format", "json", "--model", "gpt-5", "--effort", "high"], launch.Skip(5).Take(10));
        Assert.Contains("--allow-all-paths", launch);
        Assert.Contains("--allow-tool=shell", launch);
        Assert.DoesNotContain("--yolo", launch);
        Assert.Equal(["--agent", "hve", "--plugin-dir", "/opt/hve"], launch.SkipWhile(a => a != "--agent").Take(4));
        var call = h.LaunchCall(0);
        Assert.Equal("/srv/app", call.Cwd);
        Assert.Equal("agent", call.User);
        Assert.Equal("anthropic", call.Env!["COPILOT_PROVIDER_TYPE"]);
        Assert.Equal("http://127.0.0.1:4321", call.Env["COPILOT_PROVIDER_BASE_URL"]);
        Assert.Equal("custom", call.Env["COPILOT_PROVIDER_API_KEY"]);
        Assert.Equal("1", call.Env["EXTRA"]);
        Assert.Equal("gpt-5", call.Env["COPILOT_MODEL"]);
        Assert.Equal("/srv/app/.copilot", call.Env["COPILOT_HOME"]);
        Assert.Equal("/srv/app/.copilot/otel.jsonl", call.Env["COPILOT_OTEL_FILE_EXPORTER_PATH"]);
        Assert.False(call.Env.ContainsKey("COPILOT_ALLOW_ALL"));
        Assert.DoesNotContain(h.Sandbox.Calls, c => c.Cmd.Count > 2 && c.Cmd[2] == "pwd");
        Assert.Equal("agent", h.Sandbox.Calls.Single(c => c.Cmd[0] == "mkdir").User);
        var tool = Assert.Single(h.Context.Transcript.Events.OfType<ToolEvent>());
        Assert.Same(bridgedArgs, tool.Arguments);
        Assert.Contains("from the bridge", tool.View!.Content);
    }

    [Fact]
    public async Task the_flat_shape_is_folded_too_and_its_session_id_is_resumed_on_the_next_attempt()
    {
        var verdicts = new Queue<string>(["I", "C"]);
        using var h = new Harness(scorer: _ => Task.FromResult<IReadOnlyList<Score>>([new Score(verdicts.Dequeue())]));
        h.OnLaunch = _ => FakeSandboxEnvironment.Ok(FlatJsonl);
        var agent = h.Agent(new CopilotCliOptions { Attempts = new AgentAttempts(3, "Try again."), SystemPrompt = "sys" });

        await agent.ExecuteAsync(State(new ChatMessageSystem("task"), new ChatMessageUser("go")));

        Assert.Equal(2, h.Launches.Count);
        Assert.Equal(["-p", "task\n\nsys\n\ngo", "--session-id", agent.SessionId], h.Launches[0].Skip(5).Take(4));
        Assert.Equal(["-p", "Try again.", "--resume=flat-1"], h.Launches[1].Skip(5).Take(3));
        Assert.Equal(2, h.Context.Transcript.Events.OfType<ToolEvent>().Count());
        Assert.Equal(8, h.Context.Transcript.Events.OfType<InfoEvent>().Count());
    }

    [Fact]
    public async Task debug_records_stdout_stderr_and_the_result_line_in_the_store_without_the_token()
    {
        using var h = new Harness { OnLaunch = _ => new ExecResult(true, 0, SessionJsonl + "not json\n", "warn\n") };

        await h.Agent(new CopilotCliOptions { Debug = true }).ExecuteAsync(State(new ChatMessageUser("go")));

        var debug = Assert.IsType<CopilotCliDebug>(h.Context.Store.Get(CopilotCliDebug.StoreKey));
        Assert.Equal(4, debug.Stdout.Count);
        Assert.Equal(["warn\n"], debug.Stderr);
        Assert.Equal("e2f41e59-2d56-49b0-ab78-462b6b3b88d9", debug.SessionId);
        Assert.Equal(0, Assert.Single(debug.Results)["exitCode"]!.GetValue<int>());

        var events = JsonSerializer.Serialize(h.Context.Transcript.Events, EvalLogWriter.Options);
        var store = JsonSerializer.Serialize(h.Context.Store.ToDictionary(), EvalLogWriter.Options);
        Assert.DoesNotContain("tok-123", events);
        Assert.DoesNotContain("tok-123", store);
        Assert.DoesNotContain("tok-123", string.Join("\n", ProviderLogger.Infos.Concat(ProviderLogger.Warnings)));
        Assert.Contains("Copilot CLI Debug Output:", string.Join("\n", ProviderLogger.Infos));
    }

    [Fact]
    public async Task stdout_that_does_not_start_with_json_records_a_truncation_warning()
    {
        using var h = new Harness { OnLaunch = _ => FakeSandboxEnvironment.Ok("tail of a cut line}\n{\"type\":\"result\",\"exitCode\":0}\n") };

        await h.Agent().ExecuteAsync(State(new ChatMessageUser("go")));

        var infos = h.Context.Transcript.Events.OfType<InfoEvent>().ToArray();
        Assert.Equal(2, infos.Length);
        Assert.Equal("inspect_warning", infos[0].Data!["type"]!.GetValue<string>());
        Assert.Contains("exec output", infos[0].Data!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task a_result_exit_code_of_one_from_a_zero_process_exit_is_retried_then_fails_with_the_session_error()
    {
        const string failing =
            """{"type":"session.error","data":{"errorType":"query","message":"Failed to get response from the AI model; retried 5 times","statusCode":500},"id":"1","timestamp":"t","parentId":null}""" + "\n"
            + """{"type":"result","timestamp":"t","sessionId":"516ba48f","exitCode":1,"usage":{}}""" + "\n";
        using var h = new Harness { OnLaunch = _ => FakeSandboxEnvironment.Ok(failing) };
        var agent = h.Agent(new CopilotCliOptions { RetryUncaughtErrors = 1 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ExecuteAsync(State(new ChatMessageUser("go"))));

        Assert.Equal("Error executing copilot cli agent 1: Failed to get response from the AI model; retried 5 times", ex.Message);
        Assert.Equal(2, h.Launches.Count);
        Assert.Equal(["--session-id", agent.SessionId], h.Launches[0].Skip(7).Take(2));
        Assert.Equal("--resume=516ba48f", h.Launches[1][7]);
        Assert.True(h.Bridge!.Disposed);
    }

    [Fact]
    public async Task refusal_exit_is_treated_as_success()
    {
        using var h = new Harness();
        h.OnLaunch = _ =>
        {
            Deliver(h.Sink!, ModelOutput.FromContent("m", "", StopReason.ContentFilter));
            return FakeSandboxEnvironment.Fail(1, "");
        };
        var state = State(new ChatMessageUser("go"));

        var result = await h.Agent().ExecuteAsync(state);

        Assert.Same(state, result);
        Assert.Single(h.Launches);
    }

    [Fact]
    public async Task hard_failures_raise_with_stderr_and_dispose_the_bridge()
    {
        using var h = new Harness { OnLaunch = _ => FakeSandboxEnvironment.Fail(2, "boom") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Agent().ExecuteAsync(State(new ChatMessageUser("go"))));

        Assert.Equal("Error executing copilot cli agent 2: boom", ex.Message);
        Assert.True(h.Bridge!.Disposed);
    }

    [Fact]
    public async Task a_sample_limit_hit_by_a_bridged_generation_ends_the_run_as_that_limit()
    {
        var limit = new LimitExceededException("message", "200", 200, "Message limit reached. count: 200; limit: 200");
        using var h = new Harness();
        h.OnLaunch = _ =>
        {
            h.Bridge!.HitLimit(limit);
            return FakeSandboxEnvironment.Fail(1, "API Error: 500");
        };

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => h.Agent().ExecuteAsync(State(new ChatMessageUser("go"))));

        Assert.Same(limit, ex);
        Assert.True(h.Bridge!.Disposed);

        using var torn = new Harness();
        torn.OnLaunch = _ =>
        {
            torn.Bridge!.HitLimit(limit);
            throw new OperationCanceledException(torn.Bridge.LimitReached);
        };

        Assert.Same(limit, await Assert.ThrowsAsync<LimitExceededException>(() => torn.Agent().ExecuteAsync(State(new ChatMessageUser("go")))));
    }

    [Fact]
    public async Task attempts_stop_at_the_limit_and_without_a_scorer_are_an_error()
    {
        var calls = 0;
        using var h = new Harness(scorer: _ =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<Score>>([new Score(0.4)]);
        });

        await h.Agent(new CopilotCliOptions { Attempts = new AgentAttempts(2, ScoreValue: v => ((ScoreValue.Num)v).Value >= 0.4 ? 1.0 : 0.0) }).ExecuteAsync(State(new ChatMessageUser("go")));
        await h.Agent(new CopilotCliOptions { Attempts = new AgentAttempts(2) }).ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Equal(3, h.Launches.Count);
        Assert.Equal(2, calls);

        using var none = new Harness();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => none.Agent(new CopilotCliOptions { Attempts = new AgentAttempts(2) }).ExecuteAsync(State(new ChatMessageUser("go"))));
        Assert.Equal("The score() function can only be called while executing a task with a scorer.", ex.Message);
    }

    [Fact]
    public async Task a_follow_up_turn_resumes_the_instance_session_with_only_the_latest_user_turn()
    {
        // The sibling's rule: a state that already holds an assistant turn belongs to a session this instance ran,
        // so the CLI resumes it (and gets no system texts again) instead of handing --session-id the same id twice.
        using var h = new Harness();
        var agent = h.Agent(new CopilotCliOptions { SystemPrompt = "sys" });

        await agent.ExecuteAsync(State(new ChatMessageUser("q"), new ChatMessageAssistant("a"), new ChatMessageUser("more")));

        Assert.Equal(["-p", "more", $"--resume={agent.SessionId}"], h.Launches[0].Skip(5).Take(3));
    }

    [Fact]
    public async Task the_startup_notice_on_stderr_with_exit_one_is_retried_as_an_uncaught_error()
    {
        using var h = new Harness();
        h.OnLaunch = index => index == 0 ? FakeSandboxEnvironment.Fail(1, "Package extraction took 5026ms\n") : FakeSandboxEnvironment.Ok(SessionJsonl);
        var agent = h.Agent();

        var result = await agent.ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Equal(2, h.Launches.Count);
        Assert.Equal(["--session-id", agent.SessionId], h.Launches[0].Skip(7).Take(2));
        Assert.Equal($"--resume={agent.SessionId}", h.Launches[1][7]);
        Assert.NotNull(result);
        Assert.True(h.Bridge!.Disposed);
    }

    [Fact]
    public async Task a_successful_launch_resets_the_uncaught_error_count_before_the_next_attempt()
    {
        // launch 0 gives up (count 1 of 1), launch 1 succeeds (count reset), the attempt scores 0, launch 2 gives up
        // again (allowed only because the count was reset), launch 3 succeeds and the attempts are exhausted (the
        // last attempt is not scored by the agent, so exactly one verdict is consumed).
        var verdicts = new Queue<double>([0.0]);
        using var h = new Harness(scorer: _ => Task.FromResult<IReadOnlyList<Score>>([new Score(verdicts.Dequeue())]));
        h.OnLaunch = index => index % 2 == 0 ? FakeSandboxEnvironment.Fail(1, "") : FakeSandboxEnvironment.Ok(SessionJsonl);
        var agent = h.Agent(new CopilotCliOptions { RetryUncaughtErrors = 1, Attempts = new AgentAttempts(2) });

        await agent.ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Equal(4, h.Launches.Count);
        Assert.Empty(verdicts);
    }

    [Fact]
    public async Task a_termination_requested_by_an_approver_surfaces_from_execute()
    {
        var terminated = new InspectAzureAI.Eval.Approval.TerminateSampleException("operator said stop");
        using var h = new Harness();
        h.OnLaunch = _ =>
        {
            h.Bridge!.Terminate(terminated);
            return FakeSandboxEnvironment.Fail(1, "API Error: 400");
        };

        var ex = await Assert.ThrowsAsync<InspectAzureAI.Eval.Approval.TerminateSampleException>(() => h.Agent().ExecuteAsync(State(new ChatMessageUser("go"))));

        Assert.Same(terminated, ex);
        Assert.True(h.Bridge!.Disposed);

        using var torn = new Harness();
        torn.OnLaunch = _ =>
        {
            torn.Bridge!.Terminate(terminated);
            throw new OperationCanceledException(torn.Bridge.TerminateRequested);
        };

        Assert.Same(terminated, await Assert.ThrowsAsync<InspectAzureAI.Eval.Approval.TerminateSampleException>(() => torn.Agent().ExecuteAsync(State(new ChatMessageUser("go")))));
    }

    [Fact]
    public async Task an_explicit_version_installs_the_cached_tarball_into_the_sandbox()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var archive = new byte[] { 1, 2, 3 };
            new CopilotCliBinary(cacheDir).WriteCachedArchive(archive, "1.0.83", "linux-arm64", CopilotCliBinary.Sha256Hex(archive));
            using var h = new Harness(copilotInstalled: false);
            var agent = h.Agent(new CopilotCliOptions { Version = "1.0.83", CacheDir = cacheDir, ReleaseBaseUrl = "https://releases.invalid" });

            await agent.ExecuteAsync(State(new ChatMessageUser("go")));

            const string installDir = "/var/tmp/.5c95f967ca830048/copilot-1.0.83-linux-arm64";
            Assert.Equal(installDir + "/copilot", h.Launches[0][4]);
            Assert.Equal(archive, h.Sandbox.Files[installDir + ".tar.gz"]);
            Assert.Equal(["tar", "-xzf", installDir + ".tar.gz", "-C", installDir], h.Sandbox.Calls.Single(c => c.Cmd[0] == "tar").Cmd);
            Assert.Equal("root", h.Sandbox.Calls.Single(c => c.Cmd[0] == "chmod").User);
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task missing_sample_context_sandbox_or_a_trailing_assistant_message_is_an_error()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CopilotCliAgent(new CopilotCliOptions()).ExecuteAsync(State(new ChatMessageUser("go"))));
        using var noSandbox = new Harness(withSandbox: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => noSandbox.Agent().ExecuteAsync(State(new ChatMessageUser("go"))));
        using var h = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Agent().ExecuteAsync(State(new ChatMessageUser("q"), new ChatMessageAssistant("a"))));

        Assert.Empty(h.Launches);
        Assert.True(h.Bridge!.Disposed);
    }
}
