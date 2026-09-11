using System.Text;
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
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Eval.Tools.Skills;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The <c>execute</c> flow of inspect_swe <c>claude_code()</c> against the in-memory sandbox: binary lookup,
/// cwd, settings.json seed, env and argv of the launch, transcript capture, exit handling and the attempts loop.
/// The sandbox bridge is replaced by a fake so no HTTP server is needed.
/// </summary>
[Collection("ClaudeCode")]
public class ClaudeCodeAgentTests
{
    private const string ClaudePath = "/usr/local/bin/claude";

    private const string Jsonl = "{\"type\":\"system\",\"subtype\":\"init\"}\n{\"type\":\"assistant\",\"message\":{\"content\":[]}}\n{\"type\":\"result\",\"subtype\":\"success\"}";

    private static readonly McpServerConfigHttp SecretsConfig = new("http", "secrets", "http://127.0.0.1:4321/mcp/secrets", new Dictionary<string, string> { ["Authorization"] = "Bearer tok-123" });

    private sealed class FakeBridge(AgentBridge bridge) : IClaudeCodeBridge
    {
        private readonly CancellationTokenSource _limit = new();

        public AgentBridge Inner { get; } = bridge;

        public string BaseUrl => "http://127.0.0.1:4321";

        public string AuthToken => "tok-123";

        public AgentState State => Inner.State;

        public IReadOnlyList<McpServerConfigHttp> McpServerConfigs { get; init; } = [];

        public LimitExceededException? LimitError { get; private set; }

        public CancellationToken LimitReached => _limit.Token;

        public bool Disposed { get; private set; }

        /// <summary>What the real bridge does when a bridged generation trips a sample limit.</summary>
        public void HitLimit(LimitExceededException limit)
        {
            LimitError = limit;
            _limit.Cancel();
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

    /// <summary>A sample context over a scripted sandbox whose Claude Code launches are answered by <see cref="OnLaunch"/>.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly IDisposable _scope;

        private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));

        public Harness(Func<TaskState, Task<IReadOnlyList<Score>>>? scorer = null, bool withSandbox = true, bool claudeInstalled = true)
        {
            Sandbox = new FakeSandboxEnvironment(cmd =>
            {
                if (cmd.Count > 4 && cmd[0] == "bash" && cmd[2] == ClaudeCodeCommand.LaunchScript)
                {
                    Launches.Add(cmd);
                    return OnLaunch(Launches.Count - 1);
                }

                if (cmd is ["chmod", "+x", _] or ["chown", _, _] or ["bash", "-c", ClaudeCodeMcp.PrepareDirectoryScript or ClaudeCodeMcp.WriteConfigScript, "bash", _])
                {
                    return FakeSandboxEnvironment.Ok();
                }

                return cmd[2] switch
                {
                    "which claude" => claudeInstalled ? FakeSandboxEnvironment.Ok(ClaudePath + "\n") : FakeSandboxEnvironment.Fail(1),
                    "pwd" => FakeSandboxEnvironment.Ok("/workspace\n"),
                    "uname -s" => FakeSandboxEnvironment.Ok("Linux\n"),
                    "uname -m" => FakeSandboxEnvironment.Ok("aarch64\n"),
                    var c when c.StartsWith("if [ -f /lib/libc.musl", StringComparison.Ordinal) => FakeSandboxEnvironment.Ok("glibc\n"),
                    var c when c.StartsWith("mkdir -p \"$HOME/.claude\"", StringComparison.Ordinal) => FakeSandboxEnvironment.Ok(),
                    _ => FakeSandboxEnvironment.Fail(1, "unexpected command " + cmd[2]),
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

        public Func<int, ExecResult> OnLaunch { get; set; } = _ => FakeSandboxEnvironment.Ok(Jsonl);

        public FakeBridge? Bridge { get; private set; }

        public IModelEventSink? Sink { get; private set; }

        /// <summary>What the fake bridge reports as its bridged tools servers.</summary>
        public IReadOnlyList<McpServerConfigHttp> BridgedConfigs { get; init; } = [];

        /// <summary>The bridged tools the agent handed the bridge factory.</summary>
        public IReadOnlyList<BridgedToolsSpec>? BridgedToolsArg { get; private set; }

        /// <summary>Runs while the fake bridge is disposed.</summary>
        public Action? OnBridgeDispose { get; set; }

        /// <summary>Serves nothing: binary resolution must never reach the network, which <c>Dispose</c> asserts.</summary>
        public StrictHttpHandler Http { get; } = new();

        public ClaudeCodeAgent Agent(ClaudeCodeOptions? options = null) =>
            new((options ?? new ClaudeCodeOptions()) with { HttpHandler = options?.HttpHandler ?? Http, CacheDir = options?.CacheDir ?? _cacheDir })
            {
                BridgeFactory = (bridge, sandbox, port, bridgedTools, _) =>
                {
                    Assert.Same(Sandbox, sandbox);
                    Assert.Equal(0, port);
                    Sink = ModelEventSinks.Current;
                    BridgedToolsArg = bridgedTools;
                    Bridge = new FakeBridge(bridge) { McpServerConfigs = BridgedConfigs, OnDispose = () => OnBridgeDispose?.Invoke() };
                    return Task.FromResult<IClaudeCodeBridge>(Bridge);
                },
            };

        public FakeExecCall LaunchCall(int index) => Sandbox.Calls.Where(c => c.Cmd.Count > 2 && c.Cmd[2] == ClaudeCodeCommand.LaunchScript).ElementAt(index);

        public void Dispose()
        {
            _scope.Dispose();
            if (Directory.Exists(_cacheDir))
            {
                Directory.Delete(_cacheDir, recursive: true);
            }

            Assert.Empty(Http.Requests);
        }
    }

    private static AgentState State(params ChatMessage[] messages) => new(messages);

    private static void DeliverContentFilter(IModelEventSink sink) => sink.OnModelEvent(new ModelEvent
    {
        Model = "m",
        Input = [],
        ToolChoice = ToolChoice.None,
        Config = new GenerateConfig(),
        Output = ModelOutput.FromContent("m", "", StopReason.ContentFilter),
    });

    [Fact]
    public async Task runs_claude_code_with_the_bridge_env_and_argv()
    {
        using var h = new Harness();
        var agent = h.Agent(new ClaudeCodeOptions { SystemPrompt = "Be terse", DisallowedTools = ["WebSearch"] });
        var state = State(new ChatMessageSystem("Task system"), new ChatMessageUser("Fix the bug"));

        var result = await agent.ExecuteAsync(state);

        Assert.Same(state, result);
        Assert.Same(h.Bridge!.State, result);
        Assert.True(h.Bridge.Disposed);
        var launch = Assert.Single(h.Launches);
        Assert.Equal(
            [
                "bash", "-c", "exec 0</dev/null; \"$@\"", "bash", ClaudePath, "--session-id", agent.SessionId,
                "--dangerously-skip-permissions", "--model", "scripted", "--print", "--output-format", "stream-json", "--verbose",
                "--disallowed-tools", "WebSearch", "--append-system-prompt", "Task system\n\nBe terse", "--", "Fix the bug",
            ],
            launch);
        var call = h.LaunchCall(0);
        Assert.Equal("/workspace", call.Cwd);
        Assert.Null(call.Input);
        Assert.Null(call.User);
        Assert.Null(call.Timeout);
        Assert.Equal("http://127.0.0.1:4321", call.Env!["ANTHROPIC_BASE_URL"]);
        Assert.Equal("tok-123", call.Env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("scripted", call.Env["ANTHROPIC_MODEL"]);
        Assert.Equal("false", call.Env["MCP_CONNECTION_NONBLOCKING"]);
        Assert.Equal("1", call.Env["CLAUDE_CODE_DISABLE_AUTO_MEMORY"]);
        Assert.Equal(["which claude", "pwd", ClaudeCodeCommand.SettingsCommand("tok-123"), ClaudeCodeCommand.LaunchScript], h.Sandbox.Calls.Select(c => c.Cmd[2]));
        Assert.Equal("/workspace", h.Sandbox.Calls[2].Cwd);
        var infos = h.Context.Transcript.Events.OfType<InfoEvent>().ToArray();
        Assert.Equal(3, infos.Length);
        Assert.All(infos, e => Assert.Equal("claude_code", e.Source));
        Assert.Equal("system", infos[0].Data!["type"]!.GetValue<string>());
        Assert.Equal("result", infos[2].Data!["type"]!.GetValue<string>());
        Assert.False(h.Context.Store.Contains(ClaudeCodeDebug.StoreKey));
    }

    [Fact]
    public async Task user_cwd_env_permission_mode_and_presented_model_flow_into_the_launch()
    {
        using var h = new Harness();
        var served = new Model(new ScriptedModelApi([], "served"));
        var agent = h.Agent(new ClaudeCodeOptions
        {
            Model = served,
            ModelConfig = "claude-sonnet-4-5",
            Effort = "high",
            PermissionMode = "acceptEdits",
            User = "agent",
            Cwd = "/srv/app",
            Env = new Dictionary<string, string> { ["ANTHROPIC_AUTH_TOKEN"] = "custom", ["EXTRA"] = "1" },
            ReplaceSystemPrompt = "Replacement",
        });

        await agent.ExecuteAsync(State(new ChatMessageUser("go")));

        var launch = Assert.Single(h.Launches);
        Assert.Equal(["--permission-mode", "acceptEdits", "--model", "claude-sonnet-4-5"], launch.Skip(7).Take(4));
        Assert.Equal(["--system-prompt", "Replacement", "--", "go"], launch.TakeLast(4));
        var call = h.LaunchCall(0);
        Assert.Equal("/srv/app", call.Cwd);
        Assert.Equal("agent", call.User);
        Assert.Equal("custom", call.Env!["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("1", call.Env["EXTRA"]);
        Assert.Equal("claude-sonnet-4-5", call.Env["ANTHROPIC_MODEL"]);
        var settings = h.Sandbox.Calls.Single(c => c.Cmd[2].StartsWith("mkdir -p", StringComparison.Ordinal));
        Assert.Equal(ClaudeCodeCommand.SettingsCommand("custom"), settings.Cmd[2]);
        Assert.Equal("agent", settings.User);
        Assert.Equal("/srv/app", settings.Cwd);
        Assert.DoesNotContain(h.Sandbox.Calls, c => c.Cmd[2] == "pwd");
    }

    [Fact]
    public async Task debug_records_stdout_and_stderr_in_the_store_and_adds_the_flag()
    {
        using var h = new Harness { OnLaunch = _ => new ExecResult(true, 0, "{\"type\":\"system\"}\nnot json\n", "warn\n") };

        await h.Agent(new ClaudeCodeOptions { Debug = true }).ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Contains("--debug", h.Launches[0]);
        var debug = Assert.IsType<ClaudeCodeDebug>(h.Context.Store.Get(ClaudeCodeDebug.StoreKey));
        Assert.Equal(["{\"type\":\"system\"}"], debug.Stdout);
        Assert.Equal(["warn\n"], debug.Stderr);
        Assert.Single(h.Context.Transcript.Events.OfType<InfoEvent>());

        // The bridge token must never reach persisted state: not the transcript, the store, nor the debug dump.
        var events = JsonSerializer.Serialize(h.Context.Transcript.Events, EvalLogWriter.Options);
        var store = JsonSerializer.Serialize(h.Context.Store.ToDictionary(), EvalLogWriter.Options);
        Assert.DoesNotContain("tok-123", events);
        Assert.DoesNotContain("tok-123", store);
        Assert.DoesNotContain("tok-123", string.Join("\n", ProviderLogger.Infos.Concat(ProviderLogger.Warnings)));
        Assert.Contains("Claude Code Debug Output:", string.Join("\n", ProviderLogger.Infos));
    }

    [Fact]
    public async Task stdout_that_does_not_start_with_json_records_a_truncation_warning()
    {
        using var h = new Harness { OnLaunch = _ => FakeSandboxEnvironment.Ok("tail of a cut line}\n{\"type\":\"result\"}\n") };

        await h.Agent().ExecuteAsync(State(new ChatMessageUser("go")));

        var infos = h.Context.Transcript.Events.OfType<InfoEvent>().ToArray();
        Assert.Equal(2, infos.Length);
        Assert.Equal("inspect_warning", infos[0].Data!["type"]!.GetValue<string>());
        Assert.Contains("exec output", infos[0].Data!["message"]!.GetValue<string>());
        Assert.Equal("result", infos[1].Data!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task a_sample_limit_hit_by_a_bridged_generation_ends_the_run_as_that_limit()
    {
        var limit = new LimitExceededException("message", "200", 200, "Message limit reached. count: 200; limit: 200");
        using var h = new Harness();
        h.OnLaunch = _ =>
        {
            // The CLI sees a 500, retries, then exits 1 with stderr: on its own that would be a hard failure.
            h.Bridge!.HitLimit(limit);
            return FakeSandboxEnvironment.Fail(1, "API Error: 500");
        };

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => h.Agent().ExecuteAsync(State(new ChatMessageUser("go"))));

        Assert.Same(limit, ex);
        Assert.True(h.Bridge!.Disposed);
        Assert.Single(h.Launches);

        // The exec torn down through LimitReached surfaces the limit too, not the cancellation.
        using var torn = new Harness();
        torn.OnLaunch = _ =>
        {
            torn.Bridge!.HitLimit(limit);
            throw new OperationCanceledException(torn.Bridge.LimitReached);
        };

        var fromCancel = await Assert.ThrowsAsync<LimitExceededException>(() => torn.Agent().ExecuteAsync(State(new ChatMessageUser("go"))));
        Assert.Same(limit, fromCancel);
    }

    [Fact]
    public async Task refusal_exit_is_treated_as_success()
    {
        using var h = new Harness();
        h.OnLaunch = _ =>
        {
            DeliverContentFilter(h.Sink!);
            return FakeSandboxEnvironment.Fail(1, "");
        };
        var state = State(new ChatMessageUser("go"));

        var result = await h.Agent().ExecuteAsync(state);

        Assert.Same(state, result);
        Assert.Single(h.Launches);
        Assert.True(h.Bridge!.Disposed);
    }

    [Fact]
    public async Task uncaught_error_exit_is_retried_as_a_resume_without_re_appending_system_texts()
    {
        using var h = new Harness { OnLaunch = i => i == 0 ? FakeSandboxEnvironment.Fail(1, "  ") : FakeSandboxEnvironment.Ok(Jsonl) };
        var agent = h.Agent(new ClaudeCodeOptions { RetryUncaughtErrors = 1, SystemPrompt = "sys" });

        await agent.ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Equal(2, h.Launches.Count);
        Assert.Equal(["--session-id", agent.SessionId], h.Launches[0].Skip(5).Take(2));
        Assert.Contains("--append-system-prompt", h.Launches[0]);
        Assert.Equal(["--resume", agent.SessionId], h.Launches[1].Skip(5).Take(2));
        Assert.DoesNotContain("--append-system-prompt", h.Launches[1]);
        Assert.Equal("go", h.Launches[1][^1]);
    }

    [Fact]
    public async Task hard_failures_raise_with_the_python_message_and_dispose_the_bridge()
    {
        using var h = new Harness { OnLaunch = _ => FakeSandboxEnvironment.Fail(2, "boom") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Agent().ExecuteAsync(State(new ChatMessageUser("go"))));

        Assert.Equal("Error executing claude code agent 2: boom", ex.Message);
        Assert.True(h.Bridge!.Disposed);
    }

    [Fact]
    public async Task exhausted_uncaught_error_retries_are_a_hard_failure()
    {
        using var h = new Harness { OnLaunch = _ => FakeSandboxEnvironment.Fail(1, "") };

        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Agent(new ClaudeCodeOptions { RetryUncaughtErrors = null }).ExecuteAsync(State(new ChatMessageUser("go"))));
        var two = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Agent(new ClaudeCodeOptions { RetryUncaughtErrors = 2 }).ExecuteAsync(State(new ChatMessageUser("go"))));

        Assert.Equal("Error executing claude code agent 1: ", none.Message);
        Assert.Equal("Error executing claude code agent 1: ", two.Message);
        Assert.Equal(4, h.Launches.Count);
    }

    [Fact]
    public async Task attempts_rescore_and_resume_with_the_incorrect_message()
    {
        var verdicts = new Queue<string>(["I", "C"]);
        var scored = new List<TaskState>();
        using var h = new Harness(scorer: state =>
        {
            scored.Add(state);
            return Task.FromResult<IReadOnlyList<Score>>([new Score(verdicts.Dequeue())]);
        });
        var agent = h.Agent(new ClaudeCodeOptions { Attempts = new AgentAttempts(3, "Try again."), SystemPrompt = "sys" });
        var state = State(new ChatMessageSystem("task"), new ChatMessageUser("go"));

        await agent.ExecuteAsync(state);

        Assert.Equal(2, h.Launches.Count);
        Assert.Equal(2, scored.Count);
        Assert.Equal(state.Messages, scored[0].Messages);
        Assert.Same(h.Context.Store, scored[0].Store);
        Assert.Equal("scripted", scored[0].Model);
        Assert.Equal(["--resume", agent.SessionId], h.Launches[1].Skip(5).Take(2));
        Assert.Equal("Try again.", h.Launches[1][^1]);
        Assert.DoesNotContain("--append-system-prompt", h.Launches[1]);
        Assert.Contains("--append-system-prompt", h.Launches[0]);
    }

    [Fact]
    public async Task attempts_stop_at_the_limit_and_honour_a_custom_score_value()
    {
        var calls = 0;
        using var h = new Harness(scorer: _ =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<Score>>([new Score(0.4)]);
        });
        var options = new ClaudeCodeOptions { Attempts = new AgentAttempts(2, ScoreValue: v => ((ScoreValue.Num)v).Value >= 0.4 ? 1.0 : 0.0) };

        await h.Agent(options).ExecuteAsync(State(new ChatMessageUser("go")));
        var strict = h.Agent(new ClaudeCodeOptions { Attempts = new AgentAttempts(2) });
        await strict.ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Equal(3, h.Launches.Count);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task attempts_without_a_scorer_are_an_error()
    {
        using var h = new Harness();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Agent(new ClaudeCodeOptions { Attempts = new AgentAttempts(2) }).ExecuteAsync(State(new ChatMessageUser("go"))));

        Assert.Equal("The score() function can only be called while executing a task with a scorer.", ex.Message);
        Assert.Single(h.Launches);
    }

    [Fact]
    public async Task an_explicit_version_installs_the_cached_binary_into_the_sandbox()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = Encoding.ASCII.GetBytes("binary");
            new ClaudeCodeBinary(cacheDir).WriteCachedFile(bytes, Path.Combine(cacheDir, "claude-1.2.3-linux-arm64"));
            using var h = new Harness(claudeInstalled: false);
            var agent = h.Agent(new ClaudeCodeOptions { Version = "1.2.3", CacheDir = cacheDir, DownloadBaseUrl = "https://cdn.invalid" });

            await agent.ExecuteAsync(State(new ChatMessageUser("go")));

            const string installed = "/var/tmp/.5c95f967ca830048/claude-1.2.3-linux-arm64";
            Assert.Equal(installed, h.Launches[0][4]);
            Assert.Equal(bytes, h.Sandbox.Files[installed]);
            var chmod = h.Sandbox.Calls.Single(c => c.Cmd[0] == "chmod");
            Assert.Equal(["chmod", "+x", installed], chmod.Cmd);
            Assert.Equal("root", chmod.User);
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task a_conversation_ending_with_an_assistant_message_is_rejected_before_launch()
    {
        using var h = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Agent().ExecuteAsync(State(new ChatMessageUser("q"), new ChatMessageAssistant("a"))));

        Assert.Empty(h.Launches);
        Assert.True(h.Bridge!.Disposed);
    }

    [Fact]
    public async Task a_follow_up_turn_resumes_the_session()
    {
        using var h = new Harness();
        var agent = h.Agent();

        await agent.ExecuteAsync(State(new ChatMessageUser("q"), new ChatMessageAssistant("a"), new ChatMessageUser("more")));

        Assert.Equal(["--resume", agent.SessionId], h.Launches[0].Skip(5).Take(2));
        Assert.Equal("more", h.Launches[0][^1]);
    }

    [Fact]
    public async Task missing_sample_context_or_sandbox_is_an_error()
    {
        var agent = new ClaudeCodeAgent(new ClaudeCodeOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.ExecuteAsync(State(new ChatMessageUser("go"))));
        using var h = new Harness(withSandbox: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Agent().ExecuteAsync(State(new ChatMessageUser("go"))));

        Assert.Empty(h.Launches);
    }

    private static ModelEvent AgentEvent(string id) => new()
    {
        Model = "m",
        Input = [new ChatMessageUser("go")],
        ToolChoice = ToolChoice.Auto,
        Config = new GenerateConfig(),
        Output = new ModelOutput
        {
            Model = "m",
            Choices = [new ChatCompletionChoice(new ChatMessageAssistant("", toolCalls: [new ToolCall(id, "Agent", new JsonObject { ["prompt"] = "A sufficiently long sub-agent prompt." })]), StopReason.ToolCalls)],
        },
    };

    [Fact]
    public async Task skills_are_installed_under_the_agent_cwd_before_the_settings_seed()
    {
        using var h = new Harness();
        var skill = new Skill("my-skill", "Does things.", "Do the thing.") { References = new Dictionary<string, SkillFile> { ["notes.md"] = SkillFile.FromText("n") } };

        await h.Agent(new ClaudeCodeOptions { User = "agent", Skills = [skill] }).ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Contains("Do the thing.", Encoding.UTF8.GetString(h.Sandbox.Files["/workspace/.claude/skills/my-skill/SKILL.md"]));
        Assert.True(h.Sandbox.Files.ContainsKey("/workspace/.claude/skills/my-skill/references/notes.md"));
        var calls = h.Sandbox.Calls.ToList();
        var chown = calls.FindIndex(c => c.Cmd is ["chown", "agent", "/workspace/.claude/skills/my-skill/SKILL.md"]);
        var settings = calls.FindIndex(c => c.Cmd[2].StartsWith("mkdir -p \"$HOME/.claude\"", StringComparison.Ordinal));
        Assert.True(chown >= 0 && chown < settings, $"chown {chown}, settings {settings}");
        Assert.Equal("root", calls[chown].User);
    }

    [Fact]
    public void unreadable_or_duplicate_skills_fail_when_the_agent_is_constructed()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-skill-" + Guid.NewGuid().ToString("N"));
        var skill = new Skill("dup", "d", "i");

        Assert.Throws<SkillParsingError>(() => new ClaudeCodeAgent(new ClaudeCodeOptions { Skills = [missing] }));
        Assert.Throws<ArgumentException>(() => new ClaudeCodeAgent(new ClaudeCodeOptions { Skills = [skill, skill] }));
    }

    [Fact]
    public async Task mcp_servers_are_written_to_a_private_config_file_and_allowlisted()
    {
        using var h = new Harness { BridgedConfigs = [SecretsConfig] };
        var stdio = new McpServerConfigStdio("fs", "npx") { Args = ["server-fs"] };
        IReadOnlyList<BridgedToolsSpec> specs = [new BridgedToolsSpec("secrets", [])];
        var agent = h.Agent(new ClaudeCodeOptions { McpServers = [stdio], BridgedTools = specs, User = "agent", DisallowedTools = ["Bash"] });

        await agent.ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Same(specs, h.BridgedToolsArg);
        var path = ClaudeCodeMcp.ConfigPath(agent.SessionId);
        var calls = h.Sandbox.Calls.ToList();
        var prepare = calls.FindIndex(c => c.Cmd.SequenceEqual(["bash", "-c", ClaudeCodeMcp.PrepareDirectoryScript, "bash", ClaudeCodeMcp.ConfigDirectory]));
        var write = calls.FindIndex(c => c.Cmd.SequenceEqual(["bash", "-c", ClaudeCodeMcp.WriteConfigScript, "bash", path]));
        Assert.True(prepare >= 0 && write > prepare, $"prepare {prepare}, write {write}");
        Assert.Equal("root", calls[prepare].User);

        // the file is created private by the agent's own user: never first written with default permissions, and no root
        // command names a path that another sandbox user could swap for a symlink
        Assert.Equal("agent", calls[write].User);
        Assert.Equal(ClaudeCodeMcp.ConfigJson([stdio], [SecretsConfig]), calls[write].Input);
        Assert.False(h.Sandbox.Files.ContainsKey(path));
        Assert.DoesNotContain(calls, c => c.User == "root" && c.Cmd.Contains(path));
        var launch = Assert.Single(h.Launches);
        Assert.Equal(["--mcp-config", path, "--allowed-tools", "mcp__fs__*,mcp__secrets__*", "--disallowed-tools", "Bash", "--", "go"], launch.Skip(14));

        // the bridge token lives in the 0600 file, never in the argv
        Assert.DoesNotContain(launch, arg => arg.Contains("tok-123", StringComparison.Ordinal));
    }

    [Fact]
    public async Task allowlist_mcp_tools_false_keeps_only_bridged_rules_and_no_servers_means_no_config()
    {
        using var h = new Harness { BridgedConfigs = [SecretsConfig] };

        await h.Agent(new ClaudeCodeOptions { McpServers = [new McpServerConfigStdio("fs", "npx")], AllowlistMcpTools = false }).ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Equal(["--allowed-tools", "mcp__secrets__*"], h.Launches[0].SkipWhile(a => a != "--allowed-tools").Take(2));
        Assert.DoesNotContain(h.Sandbox.Calls, c => c.Cmd[0] == "chown");

        using var none = new Harness();
        await none.Agent().ExecuteAsync(State(new ChatMessageUser("go")));
        Assert.DoesNotContain("--mcp-config", none.Launches[0]);
        Assert.DoesNotContain("--allowed-tools", none.Launches[0]);
        Assert.DoesNotContain(none.Sandbox.Files.Keys, k => k.StartsWith(ClaudeCodeMcp.ConfigDirectory, StringComparison.Ordinal));
    }

    [Fact]
    public async Task disallowing_web_search_withholds_the_bridge_grant()
    {
        using var h = new Harness();

        await h.Agent(new ClaudeCodeOptions { DisallowedTools = ["WebSearch(domain:example.com)"] }).ExecuteAsync(State(new ChatMessageUser("go")));
        Assert.False(h.Bridge!.Inner.WebSearch);

        await h.Agent(new ClaudeCodeOptions { DisallowedTools = ["WebSearchTool", "Bash"] }).ExecuteAsync(State(new ChatMessageUser("go")));
        Assert.True(h.Bridge!.Inner.WebSearch);

        await h.Agent().ExecuteAsync(State(new ChatMessageUser("go")));
        Assert.True(h.Bridge!.Inner.WebSearch);
    }

    [Fact]
    public async Task filter_consumer_and_role_models_reach_the_bridge_and_the_env()
    {
        using var h = new Harness();
        GenerateFilter filter = (_, _, _, _, _, _) => Task.FromResult<GenerateFilterResult?>(null);
        var opus = new Model(new ScriptedModelApi([], "big-model"));

        await h.Agent(new ClaudeCodeOptions { Filter = filter, OpusModel = opus }).ExecuteAsync(State(new ChatMessageUser("go")));

        Assert.Same(filter, h.Bridge!.Inner.Filter);
        Assert.Same(opus, h.Bridge.Inner.ModelAliases["big-model"]);
        Assert.IsType<ClaudeCodeLiveConsumer>(h.Sink);
        Assert.Same(h.Sink, h.Bridge.Inner.ModelEventSink);
        Assert.Equal("big-model", h.LaunchCall(0).Env!["ANTHROPIC_DEFAULT_OPUS_MODEL"]);
        Assert.Equal("scripted", h.LaunchCall(0).Env!["ANTHROPIC_DEFAULT_SONNET_MODEL"]);
    }

    [Fact]
    public async Task an_incorrect_message_function_builds_the_retry_prompt()
    {
        var verdicts = new Queue<string>(["I", "C"]);
        using var h = new Harness(scorer: _ => Task.FromResult<IReadOnlyList<Score>>([new Score(verdicts.Dequeue())]));
        AgentState? seen = null;
        var attempts = new AgentAttempts(3, "static message")
        {
            IncorrectMessageFn = (state, scores, _) =>
            {
                seen = state;
                return Task.FromResult($"Scored {scores.Count}; try again.");
            },
        };
        var state = State(new ChatMessageUser("go"));

        await h.Agent(new ClaudeCodeOptions { Attempts = attempts }).ExecuteAsync(state);

        Assert.Equal(2, h.Launches.Count);
        Assert.Equal("Scored 1; try again.", h.Launches[1][^1]);
        Assert.Same(state, seen);
    }

    [Fact]
    public async Task sub_agent_spans_stay_balanced_across_retries_and_a_failure()
    {
        using var h = new Harness();
        h.OnLaunch = i =>
        {
            h.Sink!.OnModelEvent(AgentEvent($"toolu_{i}"));
            return i == 0 ? FakeSandboxEnvironment.Fail(1, "") : FakeSandboxEnvironment.Fail(2, "boom");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Agent().ExecuteAsync(State(new ChatMessageUser("go"))));

        Assert.Equal(2, h.Launches.Count);
        Assert.Equal(["agent-toolu_0", "agent-toolu_1"], h.Context.Transcript.Events.OfType<SpanBeginEvent>().Select(e => e.Id));
        Assert.Equal(["agent-toolu_0", "agent-toolu_1"], h.Context.Transcript.Events.OfType<SpanEndEvent>().Select(e => e.Id));
    }

    [Fact]
    public async Task a_sub_agent_span_opened_while_the_bridge_drains_after_a_limit_is_still_closed()
    {
        var limit = new LimitExceededException("token", "10", 12);
        using var h = new Harness();
        h.OnLaunch = _ =>
        {
            h.Bridge!.HitLimit(limit);
            return FakeSandboxEnvironment.Fail(1, "API Error: 400");
        };

        // the main turn, still generating when a side request crossed the limit, completes while the bridge drains its
        // handlers, after the loop's final reset
        h.OnBridgeDispose = () => h.Sink!.OnModelEvent(AgentEvent("toolu_late"));

        Assert.Same(limit, await Assert.ThrowsAsync<LimitExceededException>(() => h.Agent().ExecuteAsync(State(new ChatMessageUser("go")))));

        var spans = h.Context.Transcript.Events.Where(e => e is SpanBeginEvent or SpanEndEvent).ToList();
        Assert.Equal(2, spans.Count);
        Assert.Equal("agent-toolu_late", Assert.IsType<SpanBeginEvent>(spans[0]).Id);
        Assert.Equal("agent-toolu_late", Assert.IsType<SpanEndEvent>(spans[1]).Id);
    }

    [Fact]
    public async Task a_compact_boundary_line_records_one_compaction_event()
    {
        using var h = new Harness
        {
            OnLaunch = _ => FakeSandboxEnvironment.Ok("{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"compact_metadata\":{\"trigger\":\"auto\",\"pre_tokens\":4321}}\n{\"type\":\"result\"}\n"),
        };

        await h.Agent().ExecuteAsync(State(new ChatMessageUser("go")));

        var compaction = Assert.Single(h.Context.Transcript.Events.OfType<CompactionEvent>());
        Assert.Equal(4321, compaction.TokensBefore);
        Assert.Equal("claude_code", compaction.Source);
        Assert.Equal(2, h.Context.Transcript.Events.OfType<InfoEvent>().Count());
    }
}
