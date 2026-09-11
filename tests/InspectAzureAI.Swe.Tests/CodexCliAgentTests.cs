using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Eval.Tools.Skills;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.CodexCli;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The <c>execute</c> flow of the Codex CLI agent against <see cref="CliSandbox"/> with a fake bridge: binary lookup,
/// version gate, files, argv and env of the launch, failures, limits, attempts, centaur and the web-search grant.
/// Offline: <c>which codex</c> is answered, the HTTP handler is strict and the cache is per test.
/// </summary>
public sealed class CodexCliAgentTests : IDisposable
{
    private const string CodexPath = "/usr/local/bin/codex";

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", "codex-agent-" + Guid.NewGuid().ToString("N"));

    private readonly string _apiBase = $"https://api.github.test/{Guid.NewGuid():N}/repos/openai/codex";

    private readonly string _catalogBase = $"https://raw.github.test/{Guid.NewGuid():N}/openai/codex";

    private readonly StrictHttpHandler _http = new();

    public CodexCliAgentTests()
    {
        CodexCliBinary.ResetForTests();
        ProviderLogger.Reset();
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
    }

    private sealed class FakeCodexBridge(AgentBridge bridge) : ICodexCliBridge
    {
        private readonly CancellationTokenSource _limit = new();

        private readonly CancellationTokenSource _terminate = new();

        public AgentBridge Bridge { get; } = bridge;

        public string BaseUrl => "http://127.0.0.1:4321";

        public string AuthToken => "tok-123";

        public AgentState State => Bridge.State;

        public IReadOnlyList<McpServerConfigHttp> McpServerConfigs { get; init; } = [];

        public LimitExceededException? LimitError { get; private set; }

        public CancellationToken LimitReached => _limit.Token;

        public TerminateSampleException? TerminateError { get; private set; }

        public CancellationToken TerminateRequested => _terminate.Token;

        public bool Disposed { get; private set; }

        public void HitLimit(LimitExceededException limit)
        {
            LimitError = limit;
            _limit.Cancel();
        }

        public void Terminate(TerminateSampleException terminated)
        {
            TerminateError = terminated;
            _terminate.Cancel();
        }

        /// <summary>Runs while the bridge is disposed, as a generation still in flight when the launch ended would complete.</summary>
        public Action? OnDispose { get; init; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            OnDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A sample context over <see cref="CliSandbox"/> whose Codex launches are answered by <see cref="OnLaunch"/>.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly IDisposable _scope;

        public Harness(Func<TaskState, Task<IReadOnlyList<Score>>>? scorer = null)
        {
            Sandbox.WhichPaths["codex"] = CodexPath;
            Sandbox.OnExec = async (call, cancellationToken) =>
            {
                if (call.Cmd.Count > 4 && call.Cmd[2] == CodexCliCommand.LaunchScript)
                {
                    lock (Launches)
                    {
                        Launches.Add(call);
                    }

                    return await OnLaunch(call, cancellationToken);
                }

                return OnOther?.Invoke(call);
            };
            Context = new SampleContext
            {
                ActiveModel = new Model(new ScriptedModelApi([], "gpt-5.4")),
                Sandboxes = SandboxEnvironments.Single(Sandbox),
                Scorer = scorer,
            };
            _scope = SampleContext.Begin(Context);
        }

        public CliSandbox Sandbox { get; } = new();

        public SampleContext Context { get; }

        public List<FakeExecCall> Launches { get; } = [];

        public Func<FakeExecCall, CancellationToken, Task<ExecResult>> OnLaunch { get; set; } = (_, _) => Task.FromResult(CliSandbox.Ok("done\n"));

        public Func<FakeExecCall, ExecResult?>? OnOther { get; set; }

        public IReadOnlyList<McpServerConfigHttp> BridgeMcpServerConfigs { get; set; } = [];

        public FakeCodexBridge? Bridge { get; private set; }

        public IReadOnlyList<BridgedToolsSpec>? BridgedTools { get; private set; }

        public IModelEventSink? Sink { get; private set; }

        /// <summary>Runs while the fake bridge is disposed.</summary>
        public Action? OnBridgeDispose { get; set; }

        public CodexCliAgent Agent(CodexCliOptions options, Func<CentaurOptions, string, string, AgentState, CancellationToken, Task>? centaurRunner = null) => new(options)
        {
            BridgeFactory = (bridge, sandbox, port, bridgedTools, _) =>
            {
                Assert.Same(Sandbox, sandbox);
                Assert.Equal(options.Port, port);
                Sink = ModelEventSinks.Current;
                BridgedTools = bridgedTools;
                Bridge = new FakeCodexBridge(bridge) { McpServerConfigs = BridgeMcpServerConfigs, OnDispose = () => OnBridgeDispose?.Invoke() };
                return Task.FromResult<ICodexCliBridge>(Bridge);
            },
            CentaurRunner = centaurRunner,
        };

        public void Dispose() => _scope.Dispose();
    }

    private CodexCliOptions Offline(CodexCliOptions? options = null) => (options ?? new CodexCliOptions()) with
    {
        CacheDir = _cacheDir,
        HttpHandler = _http,
        ReleaseApiBaseUrl = _apiBase,
        CatalogBaseUrl = _catalogBase,
    };

    private void SeedCatalog(string version)
    {
        Directory.CreateDirectory(_cacheDir);
        File.WriteAllText(new CodexCliBinary(_cacheDir).CachedCatalogPath(version), CodexCliModelCatalog.Bundled.ToJsonString());
    }

    private static AgentState State(params ChatMessage[] messages) => new(messages);

    private static ModelEvent SpawnEvent(string callId) => new()
    {
        Model = "gpt-5.4",
        Input = [new ChatMessageUser("go")],
        ToolChoice = ToolChoice.Auto,
        Config = new GenerateConfig(),
        Output = new ModelOutput
        {
            Model = "gpt-5.4",
            Choices = [new ChatCompletionChoice(new ChatMessageAssistant("", [new ToolCall(callId, "spawn_agent", new JsonObject { ["message"] = "Survey the repository layout." })]), StopReason.ToolCalls)],
        },
    };

    [Fact]
    public async Task runs_codex_exec_with_the_bridge_env_config_and_argv()
    {
        using var h = new Harness();
        var agent = h.Agent(Offline(new CodexCliOptions
        {
            SystemPrompt = "Be terse",
            ConfigOverrides = [KeyValuePair.Create("model_reasoning_effort", "\"high\"")],
        }));
        var state = State(new ChatMessageSystem("Task system"), new ChatMessageUser("Fix the bug"));

        var result = await agent.ExecuteAsync(state);

        Assert.Same(state, result);
        Assert.True(h.Bridge!.Disposed);
        var launch = Assert.Single(h.Launches);
        Assert.Equal(
            [
                "bash", "-c", CodexCliCommand.LaunchScript, "bash", CodexPath, "exec", "--color", "never", "--skip-git-repo-check",
                "--model", "gpt-5.4", "--dangerously-bypass-approvals-and-sandbox",
                "-c", "model_reasoning_effort=\"high\"", "-c", "web_search=\"live\"", "-c", "features.goals=true", "Fix the bug",
            ],
            launch.Cmd);
        Assert.Equal("/workspace", launch.Cwd);
        Assert.Null(launch.User);
        Assert.Null(launch.Input);
        Assert.Null(launch.Timeout);
        Assert.Equal(["CODEX_HOME", "OPENAI_API_KEY", "OPENAI_BASE_URL", "RUST_LOG"], launch.Env!.Keys);
        Assert.Equal(["/workspace/.codex", "tok-123", "http://127.0.0.1:4321/v1", "warning"], launch.Env.Values);

        Assert.Equal("Task system\n\nBe terse", h.Sandbox.TextOf("/workspace/AGENTS.md"));
        Assert.Equal(CodexCliConfig.BuildToml(CodexWebSearch.Live, true, null, [], [], "http://127.0.0.1:4321"), h.Sandbox.TextOf("/workspace/.codex/config.toml"));
        Assert.Equal(
            ["bash -c which codex", $"{CodexPath} --version", "bash -c pwd", "mkdir -p /workspace/.codex"],
            h.Sandbox.Calls.Take(4).Select(c => string.Join(" ", c.Cmd)));
        Assert.Equal(5, h.Sandbox.Calls.Count);
        Assert.DoesNotContain(h.Sandbox.Calls, c => CliSandbox.ShellScript(c)?.StartsWith("eval echo", StringComparison.Ordinal) == true);

        Assert.True(h.Bridge.Bridge.WebSearch);
        Assert.False(h.Bridge.Bridge.ForwardGenerationConfig);
        var consumer = Assert.IsType<CodexCliConsumer>(h.Bridge.Bridge.ModelEventSink);
        Assert.Same(consumer, h.Sink);
        Assert.Null(h.BridgedTools);
        Assert.False(h.Context.Store.Contains(CodexCliDebug.StoreKey));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task a_home_dir_is_expanded_in_the_sandbox_and_holds_agents_md_and_config_toml()
    {
        using var h = new Harness();
        h.OnOther = call => CliSandbox.ShellScript(call) == "eval echo \"~/.codex-home\"" ? CliSandbox.Ok("/home/agent/.codex-home\n") : null;
        var agent = h.Agent(Offline(new CodexCliOptions { HomeDir = "~/.codex-home", User = "agent", Cwd = "/srv/app", SystemPrompt = "sys" }));

        await agent.ExecuteAsync(State(new ChatMessageUser("go")));

        var echo = Assert.Single(h.Sandbox.Calls, c => CliSandbox.ShellScript(c)?.StartsWith("eval echo", StringComparison.Ordinal) == true);
        Assert.Equal(("agent", "/srv/app"), (echo.User, echo.Cwd));
        var mkdir = Assert.Single(h.Sandbox.Calls, c => c.Cmd[0] == "mkdir");
        Assert.Equal(["mkdir", "-p", "/home/agent/.codex-home"], mkdir.Cmd);
        Assert.Equal("agent", mkdir.User);
        Assert.Equal("sys", h.Sandbox.TextOf("/home/agent/.codex-home/AGENTS.md"));
        Assert.NotNull(h.Sandbox.TextOf("/home/agent/.codex-home/config.toml"));
        Assert.Null(h.Sandbox.TextOf("/srv/app/AGENTS.md"));
        Assert.Null(h.Sandbox.TextOf("/srv/app/.codex/config.toml"));
        var launch = Assert.Single(h.Launches);
        Assert.Equal(("/srv/app", "agent", "/home/agent/.codex-home"), (launch.Cwd, launch.User, launch.Env!["CODEX_HOME"]));
        Assert.DoesNotContain(h.Sandbox.Calls, c => CliSandbox.ShellScript(c) == "pwd");
    }

    [Fact]
    public async Task without_system_text_no_agents_md_is_written()
    {
        using var h = new Harness();

        await h.Agent(Offline()).ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Null(h.Sandbox.TextOf("/workspace/AGENTS.md"));
    }

    [Fact]
    public async Task a_failed_launch_reports_pythons_message_and_resets_open_spans()
    {
        using var h = new Harness();
        h.OnLaunch = (_, _) =>
        {
            var recorded = h.Sink!.OnRecording(SpawnEvent("call_spawn"));
            h.Context.Transcript.Add(recorded);
            h.Sink.OnModelEvent(recorded);
            return Task.FromResult(CliSandbox.Fail(3, stderr: "boom", stdout: "partial"));
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Agent(Offline()).ExecuteAsync(State(new ChatMessageUser("go"))));

        Assert.Equal("Error executing codex cli agent 3: partial\nboom", ex.Message);
        Assert.Equal("agent-call_spawn", Assert.Single(h.Context.Transcript.Events.OfType<SpanBeginEvent>()).Id);
        Assert.Equal("agent-call_spawn", Assert.Single(h.Context.Transcript.Events.OfType<SpanEndEvent>()).Id);
        Assert.True(h.Bridge!.Disposed);
    }

    [Fact]
    public async Task a_limit_hit_through_the_bridge_wins_over_the_exit_code()
    {
        using var h = new Harness();
        var limit = new LimitExceededException("message", "1", 2);
        h.OnLaunch = (_, _) =>
        {
            h.Bridge!.HitLimit(limit);
            return Task.FromResult(CliSandbox.Fail(1, "crashed"));
        };

        var thrown = await Assert.ThrowsAsync<LimitExceededException>(() => h.Agent(Offline()).ExecuteAsync(State(new ChatMessageUser("go"))));

        Assert.Same(limit, thrown);
    }

    [Fact]
    public async Task a_sub_agent_span_opened_while_the_bridge_drains_after_a_limit_is_still_closed()
    {
        using var h = new Harness();
        var limit = new LimitExceededException("token", "10", 12);
        h.OnLaunch = (_, _) =>
        {
            h.Bridge!.HitLimit(limit);
            return Task.FromResult(CliSandbox.Fail(1, "crashed"));
        };

        // a generation still in flight when the limit ended the launch completes while the bridge drains its handlers,
        // after the launch's reset
        h.OnBridgeDispose = () =>
        {
            var recorded = h.Sink!.OnRecording(SpawnEvent("call_late"));
            h.Context.Transcript.Add(recorded);
            h.Sink.OnModelEvent(recorded);
        };

        Assert.Same(limit, await Assert.ThrowsAsync<LimitExceededException>(() => h.Agent(Offline()).ExecuteAsync(State(new ChatMessageUser("go")))));

        var spans = h.Context.Transcript.Events.Where(e => e is SpanBeginEvent or SpanEndEvent).ToList();
        Assert.Equal(2, spans.Count);
        Assert.Equal("agent-call_late", Assert.IsType<SpanBeginEvent>(spans[0]).Id);
        Assert.Equal("agent-call_late", Assert.IsType<SpanEndEvent>(spans[1]).Id);
    }

    [Fact]
    public async Task a_limit_tears_down_the_running_launch_and_a_termination_is_rethrown()
    {
        using (var h = new Harness())
        {
            var limit = new LimitExceededException("token", "10", 12);
            h.OnLaunch = async (_, cancellationToken) =>
            {
                h.Bridge!.HitLimit(limit);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return CliSandbox.Ok();
            };

            Assert.Same(limit, await Assert.ThrowsAsync<LimitExceededException>(() => h.Agent(Offline()).ExecuteAsync(State(new ChatMessageUser("go")))));
        }

        using (var h = new Harness())
        {
            var terminated = new TerminateSampleException("approver said stop");
            h.OnLaunch = (_, _) =>
            {
                h.Bridge!.Terminate(terminated);
                return Task.FromResult(CliSandbox.Ok());
            };

            Assert.Same(terminated, await Assert.ThrowsAsync<TerminateSampleException>(() => h.Agent(Offline()).ExecuteAsync(State(new ChatMessageUser("go")))));
        }
    }

    [Fact]
    public async Task attempts_resume_the_last_session_with_the_incorrect_message_fn()
    {
        var verdicts = new Queue<double>([0, 1]);
        using var h = new Harness(scorer: _ => Task.FromResult<IReadOnlyList<Score>>([new Score(verdicts.Dequeue())]));
        var seen = new List<(AgentState State, IReadOnlyList<Score> Scores)>();
        var agent = h.Agent(Offline(new CodexCliOptions
        {
            Attempts = new AgentAttempts(3)
            {
                IncorrectMessageFn = (state, scores, _) =>
                {
                    seen.Add((state, scores));
                    return Task.FromResult("Not yet: try again.");
                },
            },
        }));
        var state = State(new ChatMessageUser("Fix the bug"));

        await agent.ExecuteAsync(state);

        Assert.Equal(2, h.Launches.Count);
        Assert.Equal("Fix the bug", h.Launches[0].Cmd[^1]);
        Assert.DoesNotContain("resume", h.Launches[0].Cmd);
        Assert.Equal(["Not yet: try again.", "resume", "--last"], h.Launches[1].Cmd.TakeLast(3));
        var (seenState, seenScores) = Assert.Single(seen);
        Assert.Same(state, seenState);
        Assert.Single(seenScores);
        Assert.Empty(verdicts);
    }

    [Fact]
    public async Task attempts_use_the_incorrect_message_and_stop_at_the_attempt_limit()
    {
        var scored = 0;
        using var h = new Harness(scorer: _ =>
        {
            scored++;
            return Task.FromResult<IReadOnlyList<Score>>([new Score(0)]);
        });

        await h.Agent(Offline(new CodexCliOptions { Attempts = new AgentAttempts(2, "Try again.") })).ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Equal(2, h.Launches.Count);
        Assert.Equal(1, scored);
        Assert.Equal(["Try again.", "resume", "--last"], h.Launches[1].Cmd.TakeLast(3));
    }

    [Fact]
    public async Task a_follow_up_turn_resumes_from_the_first_launch()
    {
        using var h = new Harness();

        await h.Agent(Offline()).ExecuteAsync(State(new ChatMessageUser("first"), new ChatMessageAssistant("answer"), new ChatMessageUser("next")));

        Assert.Equal(["next", "resume", "--last"], Assert.Single(h.Launches).Cmd.TakeLast(3));
    }

    [Fact]
    public async Task auto_review_drops_the_bypass_flag_binds_the_guardian_and_writes_the_policy()
    {
        using var h = new Harness();
        h.OnOther = call => call.Cmd is [CodexPath, "--version"] ? CliSandbox.Ok("codex-cli 0.154.0\n") : null;
        SeedCatalog("0.154.0");
        var guardian = new Model(new ScriptedModelApi([], "guardian"));

        await h.Agent(Offline(new CodexCliOptions { AutoReview = new CodexAutoReview { Policy = "Deny network.", Model = guardian } })).ExecuteAsync(State(new ChatMessageUser("go")));

        var launch = Assert.Single(h.Launches).Cmd;
        Assert.DoesNotContain(CodexCliCommand.BypassFlag, launch);
        Assert.Equal(
            ["-c", "web_search=\"live\"", "-c", "features.goals=true", "-c", "approval_policy=\"on-request\"", "-c", "sandbox_mode=\"workspace-write\"",
             "-c", "approvals_reviewer=\"auto_review\"", "-c", "features.guardian_approval=true", "go"],
            launch.Skip(5).SkipWhile(arg => arg != "-c"));
        Assert.Same(guardian, h.Bridge!.Bridge.ModelAliases[CodexCliConfig.GuardianModelSlug]);
        var toml = MiniToml.Parse(h.Sandbox.TextOf("/workspace/.codex/config.toml")!);
        Assert.Equal("Deny network.", toml["auto_review"]["policy"]!.GetValue<string>());
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task auto_review_below_0_137_0_fails_before_anything_is_written()
    {
        using var h = new Harness();
        h.OnOther = call => call.Cmd is [CodexPath, "--version"] ? CliSandbox.Ok("codex-cli 0.136.9\n") : null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Agent(Offline(new CodexCliOptions { AutoReview = new CodexAutoReview() })).ExecuteAsync(State(new ChatMessageUser("go"))));

        Assert.Equal("auto_review requires Codex CLI >= 0.137.0 (found 0.136.9). Pass version='latest' (or an explicit newer version) to codex_cli().", ex.Message);
        Assert.Empty(h.Launches);
        Assert.Empty(h.Sandbox.Files);
        Assert.True(h.Bridge!.Disposed);
    }

    [Fact]
    public async Task auto_review_with_an_unknown_version_passes_the_gate()
    {
        using var h = new Harness();

        await h.Agent(Offline(new CodexCliOptions { AutoReview = new CodexAutoReview() })).ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Single(h.Launches);
        Assert.Null(h.Bridge!.Bridge.ModelAliases.GetValueOrDefault(CodexCliConfig.GuardianModelSlug));
    }

    [Fact]
    public async Task centaur_hands_the_runner_the_golden_bashrc_and_instructions_and_skips_the_loop()
    {
        const string GoldenBashrc = """
            export CODEX_HOME="/workspace/.codex"
            export OPENAI_API_KEY="tok-123"
            export OPENAI_BASE_URL="http://127.0.0.1:4321/v1"
            export RUST_LOG="warning"

            alias codex='/usr/local/bin/codex --model gpt-5.4 --dangerously-bypass-approvals-and-sandbox -c '\''web_search="live"'\'' -c features.goals=true'
            """;
        using var h = new Harness();
        var centaur = new CentaurOptions { IntermediateScoring = true };
        (CentaurOptions Options, string Instructions, string Bashrc, AgentState State)? captured = null;
        var agent = h.Agent(Offline(new CodexCliOptions { Centaur = centaur, SystemPrompt = "sys" }), (options, instructions, bashrc, state, _) =>
        {
            captured = (options, instructions, bashrc, state);
            return Task.CompletedTask;
        });
        var state = State(new ChatMessageUser("go"));

        var result = await agent.ExecuteAsync(state);

        Assert.Same(state, result);
        Assert.Empty(h.Launches);
        var (options, instructions, bashrc, runState) = captured!.Value;
        Assert.Same(centaur, options);
        Assert.Same(h.Bridge!.State, runState);
        Assert.Equal("Codex CLI:\n\n - You may also use Codex CLI via the 'codex' command.\n - Use 'codex resume' if you need to resume a previous codex session.", instructions);
        Assert.Equal(GoldenBashrc, bashrc);
        Assert.Equal("sys", h.Sandbox.TextOf("/workspace/AGENTS.md"));
        Assert.NotNull(h.Sandbox.TextOf("/workspace/.codex/config.toml"));
    }

    [Fact]
    public async Task a_limit_during_centaur_mode_is_rethrown()
    {
        using var h = new Harness();
        var limit = new LimitExceededException("time", "60", 61);
        var agent = h.Agent(Offline(new CodexCliOptions { Centaur = new CentaurOptions() }), async (_, _, _, _, cancellationToken) =>
        {
            h.Bridge!.HitLimit(limit);
            await Task.Delay(Timeout.Infinite, cancellationToken);
        });

        Assert.Same(limit, await Assert.ThrowsAsync<LimitExceededException>(() => agent.ExecuteAsync(State(new ChatMessageUser("go")))));
    }

    [Fact]
    public async Task the_obsolete_disallowed_tools_web_search_withholds_the_grant_and_disables_codex_web_search()
    {
#pragma warning disable CS0618
        var options = new CodexCliOptions { DisallowedTools = ["web_search"] };
#pragma warning restore CS0618
        using var h = new Harness();

        await h.Agent(Offline(options)).ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.False(h.Bridge!.Bridge.WebSearch);
        Assert.Contains("web_search=\"disabled\"", Assert.Single(h.Launches).Cmd);
        Assert.Equal("disabled", MiniToml.Parse(h.Sandbox.TextOf("/workspace/.codex/config.toml")!)[""]["web_search"]!.GetValue<string>());
    }

    [Fact]
    public async Task bridged_tools_reach_the_factory_and_both_kinds_of_mcp_server_are_written()
    {
        using var h = new Harness();
        var spec = new BridgedToolsSpec("secrets", [new ToolDef("secret_lookup", "Look up a secret.", new ToolParams(), (_, _) => Task.FromResult<ToolResult>("x"))]);
        h.BridgeMcpServerConfigs = [new McpServerConfigHttp("http", "secrets", "http://127.0.0.1:4321/mcp/secrets", new Dictionary<string, string> { ["Authorization"] = "Bearer tok-123" })];
        var agent = h.Agent(Offline(new CodexCliOptions
        {
            BridgedTools = [spec],
            McpServers = [new McpServerConfigStdio("fs", "npx") { Args = ["-y", "server-fs"] }],
            Filter = (_, _, _, _, _, _) => Task.FromResult<GenerateFilterResult?>(null),
            RetryRefusals = 2,
        }));

        await agent.ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Same(spec, Assert.Single(h.BridgedTools!));
        var text = h.Sandbox.TextOf("/workspace/.codex/config.toml")!;
        var toml = MiniToml.Parse(text);
        Assert.Equal("npx", toml["mcp_servers.fs"]["command"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:4321/mcp/secrets", toml["mcp_servers.secrets"]["url"]!.GetValue<string>());
        Assert.Equal("OPENAI_API_KEY", toml["mcp_servers.secrets"]["bearer_token_env_var"]!.GetValue<string>());
        Assert.DoesNotContain("tok-123", text, StringComparison.Ordinal);
        Assert.NotNull(h.Bridge!.Bridge.Filter);
        Assert.Equal(2, h.Bridge.Bridge.RetryRefusals);
    }

    [Fact]
    public async Task debug_keeps_every_launch_output_in_the_store_record()
    {
        using var h = new Harness(scorer: _ => Task.FromResult<IReadOnlyList<Score>>([new Score(0)]));
        h.OnLaunch = (call, _) => Task.FromResult(CliSandbox.Ok($"out-{h.Launches.Count}", $"err-{h.Launches.Count}"));

        await h.Agent(Offline(new CodexCliOptions { Debug = true, Attempts = new AgentAttempts(2) })).ExecuteAsync(State(new ChatMessageUser("go")));

        var record = Assert.IsType<CodexCliDebug>(h.Context.Store.Get<CodexCliDebug?>(CodexCliDebug.StoreKey, null));
        Assert.Equal(["out-1", "out-2"], record.Stdout);
        Assert.Equal(["err-1", "err-2"], record.Stderr);
    }

    [Fact]
    public void a_missing_skill_fails_when_the_agent_is_created()
    {
        Assert.Throws<SkillParsingError>(() => new CodexCliAgent(Offline(new CodexCliOptions { Skills = [SkillSource.FromDirectory(Path.Combine(_cacheDir, "no-such-skill"))] })));
    }

    [Fact]
    public void the_registry_entry_point_uses_the_option_name_and_description()
    {
        var def = InspectAzureAI.Swe.CodexCli.CodexCli.Agent(Offline(new CodexCliOptions { Name = "codex" }));

        Assert.Equal(("codex", CodexCliOptions.DefaultDescription), (def.Name, def.Description));
    }
}
