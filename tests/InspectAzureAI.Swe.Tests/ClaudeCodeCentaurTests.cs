using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Eval.Tools.Skills;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.ClaudeCode;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Centaur mode of <c>claude_code()</c> (<c>claude_code.py:377-384, 653-676</c>) through the agent's runner seam.</summary>
[Collection("ClaudeCode")]
public class ClaudeCodeCentaurTests
{
    private const string ClaudePath = "/usr/local/bin/claude";

    private sealed class CentaurBridge(AgentBridge bridge) : IClaudeCodeBridge
    {
        private readonly CancellationTokenSource _limit = new();

        public AgentBridge Inner { get; } = bridge;

        public string BaseUrl => "http://127.0.0.1:4321";

        public string AuthToken => "tok-123";

        public AgentState State => Inner.State;

        public LimitExceededException? LimitError { get; private set; }

        public CancellationToken LimitReached => _limit.Token;

        public IReadOnlyList<McpServerConfigHttp> McpServerConfigs { get; init; } = [];

        public void HitLimit(LimitExceededException limit)
        {
            LimitError = limit;
            _limit.Cancel();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record RunnerCall(CentaurOptions Options, string Instructions, string Bashrc, AgentState State);

    private sealed class Harness : IDisposable
    {
        private readonly IDisposable _scope;

        private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));

        public Harness()
        {
            Sandbox.WhichPaths["claude"] = ClaudePath;
            Sandbox.OnExec = (call, _) => Task.FromResult<ExecResult?>(
                CliSandbox.ShellScript(call) is { } script && script.StartsWith("mkdir -p \"$HOME/.claude\"", StringComparison.Ordinal) ? CliSandbox.Ok() : null);
            Context = new SampleContext { ActiveModel = new Model(new ScriptedModelApi()), Sandboxes = SandboxEnvironments.Single(Sandbox) };
            _scope = SampleContext.Begin(Context);
        }

        public CliSandbox Sandbox { get; } = new();

        public SampleContext Context { get; }

        public CentaurBridge? Bridge { get; private set; }

        public List<RunnerCall> Runs { get; } = [];

        public IReadOnlyList<McpServerConfigHttp> BridgedConfigs { get; init; } = [];

        public Func<RunnerCall, CancellationToken, Task>? OnRun { get; set; }

        /// <summary>Serves nothing: binary resolution must never reach the network, which <c>Dispose</c> asserts.</summary>
        public StrictHttpHandler Http { get; } = new();

        public ClaudeCodeAgent Agent(ClaudeCodeOptions options) => new(options with { HttpHandler = options.HttpHandler ?? Http, CacheDir = options.CacheDir ?? _cacheDir })
        {
            BridgeFactory = (bridge, _, _, _, _) =>
            {
                Bridge = new CentaurBridge(bridge) { McpServerConfigs = BridgedConfigs };
                return Task.FromResult<IClaudeCodeBridge>(Bridge);
            },
            CentaurRunner = async (centaur, instructions, bashrc, state, ct) =>
            {
                var run = new RunnerCall(centaur, instructions, bashrc, state);
                Runs.Add(run);
                if (OnRun is not null)
                {
                    await OnRun(run, ct);
                }
            },
        };

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

    [Fact]
    public async Task centaur_hands_claude_to_the_human_cli_with_its_bashrc_and_instructions()
    {
        using var h = new Harness();
        var centaur = new CentaurOptions { IntermediateScoring = true, AnswerPattern = "^\\d+$" };
        var agent = h.Agent(new ClaudeCodeOptions { Centaur = centaur, Debug = true, SystemPrompt = "ignored in centaur mode", DisallowedTools = ["WebFetch"] });
        var state = new AgentState([new ChatMessageUser("Fix the bug")]);

        var result = await agent.ExecuteAsync(state);

        Assert.Same(state, result);
        var run = Assert.Single(h.Runs);
        Assert.Same(centaur, run.Options);
        Assert.Same(state, run.State);
        Assert.Equal(Centaur.ClaudeInstructions, run.Instructions);
        var models = ClaudeCodeModels.Resolve(h.Context.ActiveModel);
        var expectedEnv = ClaudeCodeEnv.Build("http://127.0.0.1:4321", "tok-123", models);
        string[] expectedCmd = [ClaudePath, "--dangerously-skip-permissions", "--model", "scripted", "--disallowed-tools", "WebFetch"];
        Assert.Equal(Centaur.ClaudeBashrc(expectedCmd, expectedEnv), run.Bashrc);
        Assert.EndsWith(
            "mkdir -p \"$HOME/.local/bin\"\nexport PATH=\"$HOME/.local/bin:$PATH\"\nln -sf /usr/local/bin/claude \"$HOME/.local/bin/claude\"\n\n"
            + "echo '{\"hasCompletedOnboarding\":true,\"bypassPermissionsModeAccepted\":true}' > \"$HOME\"/.claude.json\n\n"
            + "alias claude='/usr/local/bin/claude --dangerously-skip-permissions --model scripted --disallowed-tools WebFetch'",
            run.Bashrc);
        Assert.StartsWith("export ANTHROPIC_BASE_URL=\"http://127.0.0.1:4321\"\nexport ANTHROPIC_AUTH_TOKEN=\"tok-123\"\n", run.Bashrc);
        Assert.DoesNotContain("--print", run.Bashrc);
        Assert.DoesNotContain("--session-id", run.Bashrc);
        Assert.DoesNotContain("ignored in centaur mode", run.Bashrc);

        // no unattended launch, no debug store; the settings seed still ran
        Assert.DoesNotContain(h.Sandbox.Calls, c => c.Cmd.Count > 2 && c.Cmd[2] == ClaudeCodeCommand.LaunchScript);
        Assert.False(h.Context.Store.Contains(ClaudeCodeDebug.StoreKey));
        Assert.Contains(h.Sandbox.Calls, c => c.Cmd.Count > 2 && c.Cmd[2] == ClaudeCodeCommand.SettingsCommand("tok-123"));
    }

    [Fact]
    public async Task centaur_still_installs_skills_and_writes_the_mcp_config_before_the_settings_seed()
    {
        using var h = new Harness
        {
            BridgedConfigs = [new McpServerConfigHttp("http", "secrets", "http://127.0.0.1:4321/mcp/secrets", new Dictionary<string, string> { ["Authorization"] = "Bearer tok-123" })],
        };
        var agent = h.Agent(new ClaudeCodeOptions
        {
            Centaur = new CentaurOptions(),
            User = "agent",
            Skills = [new Skill("my-skill", "Does things.", "Do the thing.")],
        });

        await agent.ExecuteAsync(new AgentState([new ChatMessageUser("go")]));

        Assert.Contains("name: my-skill", h.Sandbox.TextOf("/workspace/.claude/skills/my-skill/SKILL.md"));
        var path = ClaudeCodeMcp.ConfigPath(agent.SessionId);
        Assert.NotNull(h.Sandbox.TextOf(path));
        var calls = h.Sandbox.Calls.ToList();
        var skillChown = calls.FindIndex(c => c.Cmd is ["chown", "agent", "/workspace/.claude/skills/my-skill/SKILL.md"]);
        var mcpWrite = calls.FindIndex(c => c.Cmd.SequenceEqual(["bash", "-c", ClaudeCodeMcp.WriteConfigScript, "bash", path]));
        var settings = calls.FindIndex(c => CliSandbox.ShellScript(c)?.StartsWith("mkdir -p \"$HOME/.claude\"", StringComparison.Ordinal) == true);
        Assert.True(skillChown >= 0 && mcpWrite > skillChown && settings > mcpWrite, $"order: skills {skillChown}, mcp {mcpWrite}, settings {settings}");
        Assert.Equal("agent", calls[mcpWrite].User);
        // shlex quotes the glob, and the alias body then escapes those quotes as '\''
        Assert.EndsWith($"--mcp-config {path} --allowed-tools '\\''mcp__secrets__*'\\'''", Assert.Single(h.Runs).Bashrc);
    }

    [Fact]
    public async Task a_limit_hit_while_the_human_works_is_rethrown()
    {
        var limit = new LimitExceededException("message", "10", 10, "Message limit reached.");
        using var h = new Harness();
        h.OnRun = (_, ct) =>
        {
            h.Bridge!.HitLimit(limit);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => h.Agent(new ClaudeCodeOptions { Centaur = new CentaurOptions() }).ExecuteAsync(new AgentState([new ChatMessageUser("go")])));

        Assert.Same(limit, ex);
    }
}
