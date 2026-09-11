using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Eval.Tools.Skills;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.Util;
using AgentPrompt = InspectAzureAI.Swe.Util.AgentPrompt;

namespace InspectAzureAI.Swe.ClaudeCode;

/// <summary>Port of inspect_swe <c>claude_code()</c>: the registry entry point returning the agent definition.</summary>
public static class ClaudeCode
{
    public static AgentDef Agent(ClaudeCodeOptions? options = null)
    {
        var agent = new ClaudeCodeAgent(options ?? new ClaudeCodeOptions());
        return new AgentDef(agent.Options.Name, agent.Options.Description, agent.ExecuteAsync);
    }
}

/// <summary>
/// Port of the <c>ClaudeCodeDebug</c> store model of <c>claude_code.py</c>: raw stdout lines and stderr chunks kept
/// under <see cref="StoreKey"/> when debugging. Read-only outside the agent: the record is stored in the sample
/// store and serialized into the eval log, so its shape is a persisted contract.
/// </summary>
public sealed record ClaudeCodeDebug
{
    public const string StoreKey = "claude_code_debug";

    private readonly List<string> _stdout = [];

    private readonly List<string> _stderr = [];

    public IReadOnlyList<string> Stdout => _stdout;

    public IReadOnlyList<string> Stderr => _stderr;

    internal void AddStdout(string line) => _stdout.Add(line);

    internal void AddStderr(string chunk) => _stderr.Add(chunk);
}

/// <summary>What the agent needs from a started sandbox bridge; lets tests run the agent without an HTTP server.</summary>
internal interface IClaudeCodeBridge : IAsyncDisposable
{
    string BaseUrl { get; }

    string AuthToken { get; }

    AgentState State { get; }

    /// <summary>The sample limit a bridged generation hit, if any (see <see cref="SandboxAgentBridge.LimitError"/>).</summary>
    LimitExceededException? LimitError { get; }

    /// <summary>Cancelled once <see cref="LimitError"/> is set, so the running CLI can be torn down.</summary>
    CancellationToken LimitReached { get; }

    /// <summary>The termination a tool call approver requested from a bridged generation, if any (see <see cref="SandboxAgentBridge.TerminateError"/>).</summary>
    InspectAzureAI.Eval.Approval.TerminateSampleException? TerminateError => null;

    /// <summary>Cancelled once <see cref="TerminateError"/> is set, so the running CLI can be torn down.</summary>
    CancellationToken TerminateRequested => CancellationToken.None;

    /// <summary>The MCP configs of the bridged tools servers (see <see cref="SandboxAgentBridge.McpServerConfigs"/>).</summary>
    IReadOnlyList<McpServerConfigHttp> McpServerConfigs => [];
}

internal delegate Task<IClaudeCodeBridge> ClaudeCodeBridgeFactory(AgentBridge bridge, ISandboxEnvironment sandbox, int port, IReadOnlyList<BridgedToolsSpec>? bridgedTools, CancellationToken cancellationToken);

/// <summary>
/// Port of the <c>execute</c> closure of inspect_swe 0.2.70 <c>_claude_code/claude_code.py</c> <c>claude_code()</c>
/// (<c>claude_code.py:239-553</c>). It serves the sample model through a <see cref="SandboxAgentBridge"/> that also
/// hosts the bridged tools as MCP servers, and installs the Claude Code binary and the skills. It writes the MCP
/// configuration and seeds <c>settings.json</c>. Then it either hands the CLI to a human (centaur mode) or runs it
/// unattended, resuming the per-instance session on every attempt after the first. The JSONL output is recorded on
/// the transcript and fed to a <see cref="ClaudeCodeLiveConsumer"/> (sub-agent spans, compaction events). The exit is
/// classified, and the result is the bridge's reconstructed state, never anything parsed from the CLI's
/// <c>result</c> event. A sample limit or approver termination hit by a bridged generation ends the run as that
/// signal (Python's bridge task group cancels the exec), never as a CLI failure. Not ported: checkpointing (D-C7),
/// the batch session converter (D-C9), and live (streaming) JSONL consumption (D-C4).
/// </summary>
public sealed class ClaudeCodeAgent
{
    private readonly IReadOnlyList<Skill>? _skills;

    public ClaudeCodeAgent(ClaudeCodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;

        // Skills are read eagerly, as Python reads them when the agent is constructed (claude_code.py:224).
        if (options.Skills is not null)
        {
            _skills = SkillReader.ReadSkills(options.Skills);
            SkillReader.CheckUniqueSkillNames(_skills);
        }

        // One session per agent instance (claude_code.py:237) so every execution for a sample can --resume it.
        SessionId = Guid.NewGuid().ToString();
        Binary = new ClaudeCodeBinary(options.CacheDir, options.DownloadBaseUrl, options.HttpHandler);
    }

    public ClaudeCodeOptions Options { get; }

    public string SessionId { get; }

    internal ClaudeCodeBinary Binary { get; }

    internal ClaudeCodeBridgeFactory BridgeFactory { get; init; } = StartSandboxBridgeAsync;

    /// <summary>Test seam for centaur mode: runs the human CLI agent (<see cref="Centaur.RunAsync(CentaurOptions, string, string, AgentState, CancellationToken)"/> when null).</summary>
    internal Func<CentaurOptions, string, string, AgentState, CancellationToken, Task>? CentaurRunner { get; init; }

    public async Task<AgentState> ExecuteAsync(AgentState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var context = SampleContext.Require();
        var sandbox = context.Sandbox(Options.Sandbox);
        var consumer = new ClaudeCodeLiveConsumer(context.Transcript);
        var models = ClaudeCodeModels.Resolve(
            Options.Model ?? context.ActiveModel,
            Options.ModelConfig,
            Options.Effort,
            Options.ModelAliases,
            Options.OpusModel,
            Options.SonnetModel,
            Options.HaikuModel,
            Options.SubagentModel);
        var bridge = new AgentBridge(
            state,
            models.Served,
            models.Aliases,
            Options.RetryRefusals,
            forwardGenerationConfig: false,
            modelEventSink: consumer,
            cache: Options.Cache,
            filter: Options.Filter,
            webSearch: !WebSearchUtil.ToolDisallowed(Options.DisallowedTools, "WebSearch"));

        // The consumer is handed to the bridge and also installed ambiently before the server starts, so the
        // bridge's request handlers inherit it whichever way the bridge delivers model events.
        using var sinkScope = ModelEventSinks.Install(consumer);
        var sandboxBridge = await BridgeFactory(bridge, sandbox, Options.Port, Options.BridgedTools, cancellationToken).ConfigureAwait(false);
        try
        {
            await using (sandboxBridge.ConfigureAwait(false))
            {
                var claudeBinary = await Binary.EnsureInstalledAsync(sandbox, Options.Version, Options.User, cancellationToken).ConfigureAwait(false);
                var (prompt, hasAssistantResponse) = AgentPrompt.BuildUserPrompt(sandboxBridge.State.Messages);
                var agentCwd = await SandboxUtil.ResolveAgentCwdAsync(sandbox, Options.User, Options.Cwd, cancellationToken).ConfigureAwait(false);
                if (_skills is not null)
                {
                    await SkillInstaller.InstallSkillsAsync(_skills.Select(SkillSource.FromSkill), sandbox, Options.User, SandboxUtil.JoinPath(agentCwd, ".claude/skills"), cancellationToken).ConfigureAwait(false);
                }

                var (mcpArgs, allowedTools) = await WriteMcpConfigAsync(sandbox, sandboxBridge, cancellationToken).ConfigureAwait(false);
                var flags = ClaudeCodeCommand.BaseFlags(models.Presented, Options.PermissionMode, Options.Debug, Options.DisallowedTools, mcpArgs, allowedTools, centaur: Options.Centaur is not null);
                var agentEnv = ClaudeCodeEnv.Build(sandboxBridge.BaseUrl, sandboxBridge.AuthToken, models, Options.Env);
                var apiKey = agentEnv.TryGetValue("ANTHROPIC_AUTH_TOKEN", out var token) ? token : ClaudeCodeEnv.DefaultApiKey;
                await sandbox.ExecAsync(SandboxUtil.BashCommand(ClaudeCodeCommand.SettingsCommand(apiKey)), user: Options.User, cwd: agentCwd, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (Options.Centaur is { } centaur)
                {
                    await RunCentaurAsync(centaur, sandboxBridge, [claudeBinary, .. flags], agentEnv, cancellationToken).ConfigureAwait(false);
                    return sandboxBridge.State;
                }

                var debug = Options.Debug ? new ClaudeCodeDebug() : null;
                if (debug is not null)
                {
                    context.Store.Set(ClaudeCodeDebug.StoreKey, debug);
                }

                var debugOutput = new List<string>();
                var agentPrompt = prompt;
                var attemptCount = 0;
                var uncaughtErrorCount = 0;
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var isResume = hasAssistantResponse || attemptCount > 0 || uncaughtErrorCount > 0;
                        var systemTexts = ClaudeCodeCommand.SystemTexts(sandboxBridge.State.Messages, Options.SystemPrompt);
                        var systemArgs = ClaudeCodeCommand.SystemPromptArgs(systemTexts, Options.ReplaceSystemPrompt, isResume);
                        var agentCmd = ClaudeCodeCommand.Build(claudeBinary, SessionId, isResume, flags, systemArgs, agentPrompt);

                        // Fresh consumer state per attempt; this also closes spans the previous attempt left open.
                        consumer.Reset();
                        var result = await LaunchAsync(sandbox, sandboxBridge, agentCmd, agentCwd, agentEnv, cancellationToken).ConfigureAwait(false);

                        var stderrData = "";
                        var exitCode = 0;
                        var firstStdoutEvent = true;
                        foreach (var streamEvent in ClaudeCodeStream.Parse(result))
                        {
                            switch (streamEvent)
                            {
                                case ClaudeCodeStreamEvent.Jsonl jsonl:
                                    context.Transcript.Info("claude_code", jsonl.Raw);
                                    debug?.AddStdout(jsonl.Line);
                                    consumer.ProcessJsonlLine(jsonl.Raw);
                                    firstStdoutEvent = false;
                                    break;
                                case ClaudeCodeStreamEvent.ParseError parseError:
                                    if (firstStdoutEvent)
                                    {
                                        context.Transcript.Info("claude_code", TruncatedOutputWarning());
                                    }

                                    if (debug is not null)
                                    {
                                        debugOutput.Add($"JSONL parse error: {parseError.Line}");
                                    }

                                    firstStdoutEvent = false;
                                    break;
                                case ClaudeCodeStreamEvent.Stderr stderr:
                                    stderrData += stderr.Data;
                                    debug?.AddStderr(stderr.Data);
                                    break;
                                case ClaudeCodeStreamEvent.Exit exit:
                                    exitCode = exit.Code;
                                    break;
                            }
                        }

                        if (debug is not null)
                        {
                            debugOutput.Add(stderrData);
                        }

                        // The CLI exits however it likes once its generation was refused; the limit is the outcome.
                        // Likewise when an approver terminated the sample from inside a bridged generation.
                        ThrowIfSignalled(sandboxBridge);

                        var kind = ClaudeCodeExit.Classify(exitCode, stderrData, consumer.LastStopReason, Options.RetryUncaughtErrors, uncaughtErrorCount);
                        if (kind == ClaudeCodeExitKind.RetryUncaughtError)
                        {
                            uncaughtErrorCount++;
                            continue;
                        }

                        if (kind == ClaudeCodeExitKind.Failure)
                        {
                            throw new InvalidOperationException(ClaudeCodeExit.ErrorMessage(exitCode, stderrData));
                        }

                        uncaughtErrorCount = 0;
                        attemptCount++;
                        if (attemptCount >= Options.Attempts.Attempts)
                        {
                            break;
                        }

                        var answerScores = await ScoreAsync(context, sandboxBridge.State, cancellationToken).ConfigureAwait(false);
                        if (answerScores.Count == 0)
                        {
                            throw new InvalidOperationException("The task scorer returned no scores for the attempt.");
                        }

                        var toFloat = Options.Attempts.ScoreValue ?? ValueToFloat.Default;
                        if (toFloat(answerScores[0].Value) == 1.0)
                        {
                            break;
                        }

                        agentPrompt = Options.Attempts.IncorrectMessageFn is { } incorrectMessage
                            ? await incorrectMessage(sandboxBridge.State, answerScores, cancellationToken).ConfigureAwait(false)
                            : Options.Attempts.IncorrectMessage;
                    }
                }
                finally
                {
                    // Close any spans the final attempt left open, on normal exit and on an exception alike, so the agent
                    // span tree does not leak past the agent (claude_code.py:539-546).
                    consumer.Reset();
                }

                if (debug is not null)
                {
                    debugOutput.Insert(0, "Claude Code Debug Output:");
                    ProviderLogger.Info(string.Join("\n", debugOutput));
                }

                return sandboxBridge.State;
            }
        }
        finally
        {
            // A generation still in flight when the run ended can complete while the bridge drains its handlers during
            // disposal, and open a sub-agent span after the last reset: close it once no handler can run any more.
            consumer.Reset();
        }
    }

    /// <summary>
    /// The MCP part of <c>claude_code.py:318-331</c>. When there are static or bridged servers, the configuration is
    /// written to a per-session 0600 file, because bridged configs carry the bridge token (D-C5). Root only prepares the
    /// shared directory (<see cref="ClaudeCodeMcp.PrepareDirectoryScript"/>). The file itself is created private by the
    /// agent's own user (<see cref="ClaudeCodeOptions.User"/>, or the sandbox default) through
    /// <see cref="ClaudeCodeMcp.WriteConfigScript"/>, so no other user can ever read it, and no root command names a path
    /// that another sandbox user could replace with a symlink. Returns the <c>--mcp-config</c> arguments and the allow
    /// rules, or nulls.
    /// </summary>
    private async Task<(IReadOnlyList<string>? McpArgs, IReadOnlyList<string>? AllowedTools)> WriteMcpConfigAsync(
        ISandboxEnvironment sandbox,
        IClaudeCodeBridge sandboxBridge,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<McpServerConfig> staticServers = Options.McpServers ?? [];
        IReadOnlyList<McpServerConfig> bridgedServers = sandboxBridge.McpServerConfigs;
        if (staticServers.Count + bridgedServers.Count == 0)
        {
            return (null, null);
        }

        var path = ClaudeCodeMcp.ConfigPath(SessionId);
        await RootExecAsync(sandbox, ["bash", "-c", ClaudeCodeMcp.PrepareDirectoryScript, "bash", ClaudeCodeMcp.ConfigDirectory], cancellationToken).ConfigureAwait(false);
        var written = await sandbox.ExecAsync(
            ["bash", "-c", ClaudeCodeMcp.WriteConfigScript, "bash", path],
            input: ClaudeCodeMcp.ConfigJson(staticServers, bridgedServers),
            user: Options.User,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!written.Success)
        {
            throw new InvalidOperationException($"Error writing the MCP configuration {path}: {written.Stderr}");
        }

        return (ClaudeCodeMcp.ConfigArgs(path), ClaudeCodeMcp.AllowedTools(staticServers, bridgedServers, Options.AllowlistMcpTools));
    }

    /// <summary>
    /// Port of <c>run_claude_code_centaur</c> (<c>claude_code.py:653-676</c>): the human CLI agent with Claude Code's
    /// instructions and a <c>.bashrc</c> aliasing <c>claude</c> to the configured command. It runs under a token that
    /// also fires on a bridged limit or termination, and that signal is what the caller sees.
    /// </summary>
    private async Task RunCentaurAsync(
        CentaurOptions centaur,
        IClaudeCodeBridge sandboxBridge,
        IReadOnlyList<string> claudeCmd,
        IReadOnlyDictionary<string, string> agentEnv,
        CancellationToken cancellationToken)
    {
        var bashrc = Centaur.ClaudeBashrc(claudeCmd, agentEnv);
        var runner = CentaurRunner ?? DefaultCentaurRunnerAsync;
        using var centaurCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sandboxBridge.LimitReached, sandboxBridge.TerminateRequested);
        try
        {
            await runner(centaur, Centaur.ClaudeInstructions, bashrc, sandboxBridge.State, centaurCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sandboxBridge.LimitError is not null || sandboxBridge.TerminateError is not null)
        {
            ThrowIfSignalled(sandboxBridge);
            throw;
        }

        ThrowIfSignalled(sandboxBridge);
    }

    private static Task DefaultCentaurRunnerAsync(CentaurOptions options, string instructions, string bashrc, AgentState state, CancellationToken cancellationToken) =>
        Centaur.RunAsync(options, instructions, bashrc, state, cancellationToken);

    /// <summary>Rethrows a bridged sample limit or approver termination, if one was signalled.</summary>
    private static void ThrowIfSignalled(IClaudeCodeBridge sandboxBridge)
    {
        if (sandboxBridge.LimitError is { } limit)
        {
            ExceptionDispatchInfo.Capture(limit).Throw();
        }

        if (sandboxBridge.TerminateError is { } terminated)
        {
            ExceptionDispatchInfo.Capture(terminated).Throw();
        }
    }

    private static async Task RootExecAsync(ISandboxEnvironment sandbox, IReadOnlyList<string> cmd, CancellationToken cancellationToken)
    {
        var result = await sandbox.ExecAsync(cmd, user: "root", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Error executing sandbox command {string.Join(' ', cmd)}: {result.Stderr}");
        }
    }

    /// <summary>Runs the CLI under a token that also fires when a bridged generation hits a sample limit; that limit, not the interrupted exec, is what the caller sees.</summary>
    private async Task<ExecResult> LaunchAsync(
        ISandboxEnvironment sandbox,
        IClaudeCodeBridge sandboxBridge,
        IReadOnlyList<string> agentCmd,
        string agentCwd,
        IReadOnlyDictionary<string, string> agentEnv,
        CancellationToken cancellationToken)
    {
        using var execCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sandboxBridge.LimitReached, sandboxBridge.TerminateRequested);
        try
        {
            return await sandbox.ExecAsync(
                ClaudeCodeCommand.Launch(agentCmd),
                input: null,
                cwd: agentCwd,
                env: agentEnv,
                user: Options.User,
                timeout: null,
                cancellationToken: execCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sandboxBridge.LimitError is not null || sandboxBridge.TerminateError is not null)
        {
            ThrowIfSignalled(sandboxBridge);
            throw;
        }
    }

    /// <summary>
    /// Recorded when stdout does not begin with a JSON line: the sandbox exec output cap keeps the tail (Python
    /// streams events live and never truncates), so a very long session may have lost its first lines.
    /// </summary>
    private static JsonObject TruncatedOutputWarning() => new()
    {
        ["type"] = "inspect_warning",
        ["message"] = "Claude Code stdout did not start with a complete JSON line; if the session exceeded the sandbox exec output "
            + $"limit ({SandboxLimits.HumanReadableSize(SandboxLimits.MaxExecOutputSize)}, {SandboxLimits.MaxExecOutputSizeVar}) its first events were discarded.",
    };

    /// <summary>
    /// Port of <c>score(agent_state)</c>: the sample's task state (<see cref="SampleContext.SampleState"/>) with the
    /// agent's messages and output swapped in; outside a runner a bare state carrying the sample store stands in.
    /// </summary>
    private static Task<IReadOnlyList<Score>> ScoreAsync(SampleContext context, AgentState state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scorer = context.Scorer
            ?? throw new InvalidOperationException("The score() function can only be called while executing a task with a scorer.");
        var taskState = context.SampleState?.WithMessages(state.Messages, state.Output) ?? new TaskState(
            context.ActiveModel.Name,
            sampleId: 0,
            epoch: 1,
            input: state.Messages.ToList(),
            messages: state.Messages,
            output: state.Output,
            store: context.Store);
        return scorer(taskState);
    }

    private static async Task<IClaudeCodeBridge> StartSandboxBridgeAsync(
        AgentBridge bridge,
        ISandboxEnvironment sandbox,
        int port,
        IReadOnlyList<BridgedToolsSpec>? bridgedTools,
        CancellationToken cancellationToken)
    {
        var server = await SandboxAgentBridge.StartAsync(bridge, sandbox, port, bridgedTools, cancellationToken).ConfigureAwait(false);
        return new SandboxBridgeAdapter(server);
    }

    private sealed class SandboxBridgeAdapter(SandboxAgentBridge server) : IClaudeCodeBridge
    {
        public string BaseUrl => server.BaseUrl;

        public string AuthToken => server.AuthToken;

        public AgentState State => server.State;

        public LimitExceededException? LimitError => server.LimitError;

        public CancellationToken LimitReached => server.LimitReached;

        public InspectAzureAI.Eval.Approval.TerminateSampleException? TerminateError => server.TerminateError;

        public CancellationToken TerminateRequested => server.TerminateRequested;

        public IReadOnlyList<McpServerConfigHttp> McpServerConfigs => server.McpServerConfigs;

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }
}
