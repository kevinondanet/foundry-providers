using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
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
}

internal delegate Task<IClaudeCodeBridge> ClaudeCodeBridgeFactory(AgentBridge bridge, ISandboxEnvironment sandbox, int port, CancellationToken cancellationToken);

/// <summary>
/// Port of the <c>execute</c> closure of inspect_swe <c>_claude_code/claude_code.py</c> <c>claude_code()</c>: serves
/// the sample model through a <see cref="SandboxAgentBridge"/>, installs the Claude Code binary, seeds
/// <c>settings.json</c>, runs the CLI (resuming the per-instance session on every attempt after the first),
/// records its JSONL output on the transcript, classifies its exit and returns the bridge's reconstructed state
/// — never anything parsed from the CLI's <c>result</c> event. A sample limit hit by a bridged generation ends
/// the run as that limit (Python's bridge task group cancels the exec), never as a CLI failure. Not ported:
/// skills, MCP servers and bridged tools, the MCP readiness gate, centaur mode, checkpointing, sub-agent span
/// attribution and compaction events.
/// </summary>
public sealed class ClaudeCodeAgent
{
    public ClaudeCodeAgent(ClaudeCodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        // One session per agent instance (claude_code.py:298) so every execution for a sample can --resume it.
        SessionId = Guid.NewGuid().ToString();
        Binary = new ClaudeCodeBinary(options.CacheDir, options.DownloadBaseUrl, options.HttpHandler);
    }

    public ClaudeCodeOptions Options { get; }

    public string SessionId { get; }

    internal ClaudeCodeBinary Binary { get; }

    internal ClaudeCodeBridgeFactory BridgeFactory { get; init; } = StartSandboxBridgeAsync;

    public async Task<AgentState> ExecuteAsync(AgentState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var context = SampleContext.Require();
        var sandbox = context.Sandbox(Options.Sandbox);
        var tracker = new ClaudeCodeStopReasonTracker();
        var models = ClaudeCodeModels.Resolve(Options.Model ?? context.ActiveModel, Options.ModelConfig, Options.Effort, Options.ModelAliases);
        var bridge = new AgentBridge(state, models.Served, models.Aliases, Options.RetryRefusals, forwardGenerationConfig: false, modelEventSink: tracker);

        // The tracker is handed to the bridge and also installed ambiently before the server starts, so the
        // bridge's request handlers inherit it whichever way the bridge delivers model events.
        using var sinkScope = ModelEventSinks.Install(tracker);
        var sandboxBridge = await BridgeFactory(bridge, sandbox, Options.Port, cancellationToken).ConfigureAwait(false);
        await using (sandboxBridge.ConfigureAwait(false))
        {
            var claudeBinary = await Binary.EnsureInstalledAsync(sandbox, Options.Version, Options.User, cancellationToken).ConfigureAwait(false);
            var flags = ClaudeCodeCommand.BaseFlags(models.Presented, Options.PermissionMode, Options.Debug, Options.DisallowedTools);
            var (prompt, hasAssistantResponse) = AgentPrompt.BuildUserPrompt(sandboxBridge.State.Messages);
            var agentCwd = await SandboxUtil.ResolveAgentCwdAsync(sandbox, Options.User, Options.Cwd, cancellationToken).ConfigureAwait(false);
            var agentEnv = ClaudeCodeEnv.Build(sandboxBridge.BaseUrl, sandboxBridge.AuthToken, models, Options.Env);
            var apiKey = agentEnv.TryGetValue("ANTHROPIC_AUTH_TOKEN", out var token) ? token : ClaudeCodeEnv.DefaultApiKey;
            await sandbox.ExecAsync(SandboxUtil.BashCommand(ClaudeCodeCommand.SettingsCommand(apiKey)), user: Options.User, cwd: agentCwd, cancellationToken: cancellationToken).ConfigureAwait(false);

            var debug = Options.Debug ? new ClaudeCodeDebug() : null;
            if (debug is not null)
            {
                context.Store.Set(ClaudeCodeDebug.StoreKey, debug);
            }

            var debugOutput = new List<string>();
            var agentPrompt = prompt;
            var attemptCount = 0;
            var uncaughtErrorCount = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isResume = hasAssistantResponse || attemptCount > 0 || uncaughtErrorCount > 0;
                var systemTexts = ClaudeCodeCommand.SystemTexts(sandboxBridge.State.Messages, Options.SystemPrompt);
                var systemArgs = ClaudeCodeCommand.SystemPromptArgs(systemTexts, Options.ReplaceSystemPrompt, isResume);
                var agentCmd = ClaudeCodeCommand.Build(claudeBinary, SessionId, isResume, flags, systemArgs, agentPrompt);

                tracker.Reset();
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
                if (sandboxBridge.LimitError is { } limit)
                {
                    ExceptionDispatchInfo.Capture(limit).Throw();
                }

                // Likewise when an approver terminated the sample from inside a bridged generation.
                if (sandboxBridge.TerminateError is { } terminated)
                {
                    ExceptionDispatchInfo.Capture(terminated).Throw();
                }

                var kind = ClaudeCodeExit.Classify(exitCode, stderrData, tracker.LastStopReason, Options.RetryUncaughtErrors, uncaughtErrorCount);
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

                agentPrompt = Options.Attempts.IncorrectMessage;
            }

            if (debug is not null)
            {
                debugOutput.Insert(0, "Claude Code Debug Output:");
                ProviderLogger.Info(string.Join("\n", debugOutput));
            }

            return sandboxBridge.State;
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
        catch (OperationCanceledException) when (sandboxBridge.LimitError is { } limit)
        {
            ExceptionDispatchInfo.Capture(limit).Throw();
            throw;
        }
        catch (OperationCanceledException) when (sandboxBridge.TerminateError is { } terminated)
        {
            ExceptionDispatchInfo.Capture(terminated).Throw();
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

    private static async Task<IClaudeCodeBridge> StartSandboxBridgeAsync(AgentBridge bridge, ISandboxEnvironment sandbox, int port, CancellationToken cancellationToken)
    {
        var server = await SandboxAgentBridge.StartAsync(bridge, sandbox, port, cancellationToken).ConfigureAwait(false);
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

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }
}
