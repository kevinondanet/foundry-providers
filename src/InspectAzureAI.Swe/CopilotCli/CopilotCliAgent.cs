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

namespace InspectAzureAI.Swe.CopilotCli;

/// <summary>The registry entry point of the GitHub Copilot CLI agent, the sibling of <see cref="InspectAzureAI.Swe.ClaudeCode.ClaudeCode"/> (inspect_swe <c>claude_code()</c>).</summary>
public static class CopilotCli
{
    public static AgentDef Agent(CopilotCliOptions? options = null)
    {
        var agent = new CopilotCliAgent(options ?? new CopilotCliOptions());
        return new AgentDef(agent.Options.Name, agent.Options.Description, agent.ExecuteAsync);
    }
}

/// <summary>
/// The debug store record of the Copilot CLI agent, the sibling of <see cref="InspectAzureAI.Swe.ClaudeCode.ClaudeCodeDebug"/>
/// (the <c>ClaudeCodeDebug</c> store model of <c>claude_code.py</c>): raw stdout lines, stderr chunks and the final
/// <c>result</c> line of every launch, kept under <see cref="StoreKey"/> when debugging. Its shape is persisted
/// into the eval log, so it is a contract.
/// </summary>
public sealed record CopilotCliDebug
{
    public const string StoreKey = "copilot_cli_debug";

    private readonly List<string> _stdout = [];

    private readonly List<string> _stderr = [];

    private readonly List<JsonObject> _results = [];

    public IReadOnlyList<string> Stdout => _stdout;

    public IReadOnlyList<string> Stderr => _stderr;

    /// <summary>The final <c>result</c> line of each launch (<c>sessionId</c>, <c>exitCode</c>, <c>usage</c>, <c>codeChanges</c>).</summary>
    public IReadOnlyList<JsonObject> Results => _results;

    /// <summary>The session id the CLI reported last.</summary>
    public string? SessionId { get; internal set; }

    internal void AddStdout(string line) => _stdout.Add(line);

    internal void AddStderr(string chunk) => _stderr.Add(chunk);

    internal void AddResult(JsonObject result) => _results.Add(result);
}

/// <summary>What the agent needs from a started sandbox bridge; lets tests run the agent without an HTTP server (the seam of <c>IClaudeCodeBridge</c>).</summary>
internal interface ICopilotCliBridge : IAsyncDisposable
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

/// <summary>Starts (or, in tests, fakes) the sandbox bridge the CLI talks to; the seam of the Claude Code agent's <c>ClaudeCodeBridgeFactory</c>.</summary>
internal delegate Task<ICopilotCliBridge> CopilotCliBridgeFactory(AgentBridge bridge, ISandboxEnvironment sandbox, int port, CancellationToken cancellationToken);

/// <summary>
/// The <c>execute</c> closure of the Copilot CLI agent, the sibling of <see cref="InspectAzureAI.Swe.ClaudeCode.ClaudeCodeAgent"/>
/// (inspect_swe <c>_claude_code/claude_code.py</c> <c>claude_code()</c>): serves the sample model through a
/// <see cref="SandboxAgentBridge"/>, installs the CLI from its GitHub release, points it at the bridge through the
/// <c>COPILOT_PROVIDER_*</c> variables, runs it non-interactively (<c>--session-id</c> on the first launch of an
/// execution, <c>--resume</c> on every later launch of that execution and, as the Claude Code agent does, on a
/// follow-up turn whose state already carries an assistant response), folds its JSONL output into the transcript, classifies its exit and
/// returns the bridge's reconstructed state — never anything parsed from the CLI's <c>result</c> line. A sample
/// limit hit by a bridged generation ends the run as that limit, never as a CLI failure. Not ported: MCP servers
/// and bridged host tools (the plan's phase 2), sub-agent spans, compaction events.
/// </summary>
public sealed class CopilotCliAgent
{
    public CopilotCliAgent(CopilotCliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        // One session per agent instance so every later launch for a sample can --resume it.
        SessionId = Guid.NewGuid().ToString();
        Binary = new CopilotCliBinary(options.CacheDir, options.ReleaseBaseUrl, options.HttpHandler);
    }

    public CopilotCliOptions Options { get; }

    public string SessionId { get; }

    internal CopilotCliBinary Binary { get; }

    internal CopilotCliBridgeFactory BridgeFactory { get; init; } = StartSandboxBridgeAsync;

    public async Task<AgentState> ExecuteAsync(AgentState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var context = SampleContext.Require();
        var sandbox = context.Sandbox(Options.Sandbox);
        var tracker = new CopilotCliBridgeTracker();
        var models = CopilotCliModels.Resolve(context.ActiveModel, Options.Model, Options.ModelConfig, Options.Effort);
        var bridge = new AgentBridge(state, models.Served, models.Aliases, Options.RetryRefusals, forwardGenerationConfig: false, modelEventSink: tracker, cache: Options.Cache);

        using var sinkScope = ModelEventSinks.Install(tracker);
        var sandboxBridge = await BridgeFactory(bridge, sandbox, Options.Port, cancellationToken).ConfigureAwait(false);
        await using (sandboxBridge.ConfigureAwait(false))
        {
            var copilotBinary = await Binary.EnsureInstalledAsync(sandbox, Options.Version, Options.User, cancellationToken).ConfigureAwait(false);
            var (prompt, hasAssistantResponse) = AgentPrompt.BuildUserPrompt(sandboxBridge.State.Messages);
            var agentCwd = await SandboxUtil.ResolveAgentCwdAsync(sandbox, Options.User, Options.Cwd, cancellationToken).ConfigureAwait(false);
            var flags = CopilotCliCommand.BaseFlags(models.Presented, Options, agentCwd);
            var agentEnv = CopilotCliEnv.Build(sandboxBridge.BaseUrl, sandboxBridge.AuthToken, models, agentCwd, Options.Provider, Options.Permission, Options.Otel, Options.Env);
            var prepare = await sandbox.ExecAsync(CopilotCliCommand.PrepareHomeCommand(agentCwd, Options.Env), user: Options.User, cwd: agentCwd, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!prepare.Success)
            {
                throw new InvalidOperationException($"Error creating the copilot cli home under {agentCwd}: {prepare.Stderr}");
            }

            var debug = Options.Debug ? new CopilotCliDebug() : null;
            if (debug is not null)
            {
                context.Store.Set(CopilotCliDebug.StoreKey, debug);
            }

            var events = new CopilotCliEvents(context.Transcript, tracker.ToolCall, Options.RecordStreamingLines);
            var debugOutput = new List<string>();
            var agentPrompt = prompt;
            var attemptCount = 0;
            var uncaughtErrorCount = 0;
            var launches = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The sibling's rule: a session exists to resume once this execution has launched the CLI, or when the
                // state already holds an assistant turn (a follow-up turn of the same instance in the same sandbox).
                var isResume = hasAssistantResponse || launches > 0;
                var systemTexts = CopilotCliCommand.SystemTexts(sandboxBridge.State.Messages, Options.SystemPrompt);
                var launchPrompt = CopilotCliCommand.Prompt(systemTexts, agentPrompt, isResume);
                var agentCmd = CopilotCliCommand.Build(copilotBinary, events.SessionId ?? SessionId, isResume, flags, launchPrompt);

                tracker.Reset();
                events.BeginRun();
                var result = await LaunchAsync(sandbox, sandboxBridge, agentCmd, agentCwd, agentEnv, cancellationToken).ConfigureAwait(false);
                launches++;

                var stderrData = "";
                var processExitCode = 0;
                var firstStdoutEvent = true;
                foreach (var streamEvent in CopilotCliStream.Parse(result))
                {
                    switch (streamEvent)
                    {
                        case CopilotCliStreamEvent.Jsonl jsonl:
                            events.Fold(jsonl.Raw);
                            debug?.AddStdout(jsonl.Line);
                            firstStdoutEvent = false;
                            break;
                        case CopilotCliStreamEvent.ParseError parseError:
                            if (firstStdoutEvent)
                            {
                                context.Transcript.Info(CopilotCliEvents.Source, TruncatedOutputWarning());
                            }

                            if (debug is not null)
                            {
                                debugOutput.Add($"JSONL parse error: {parseError.Line}");
                            }

                            firstStdoutEvent = false;
                            break;
                        case CopilotCliStreamEvent.Stderr stderr:
                            stderrData += stderr.Data;
                            debug?.AddStderr(stderr.Data);
                            break;
                        case CopilotCliStreamEvent.Exit exit:
                            processExitCode = exit.Code;
                            break;
                    }
                }

                if (debug is not null)
                {
                    debugOutput.Add(stderrData);
                    debug.SessionId = events.SessionId;
                    if (events.Result is { } resultLine)
                    {
                        debug.AddResult(resultLine);
                    }
                }

                // The CLI exits however it likes once its generation was refused; the limit is the outcome.
                if (sandboxBridge.LimitError is { } limit)
                {
                    ExceptionDispatchInfo.Capture(limit).Throw();
                }

                if (sandboxBridge.TerminateError is { } terminated)
                {
                    ExceptionDispatchInfo.Capture(terminated).Throw();
                }

                var exitCode = CopilotCliExit.EffectiveExitCode(processExitCode, events.ResultExitCode);
                var kind = CopilotCliExit.Classify(exitCode, stderrData, tracker.LastStopReason, Options.RetryUncaughtErrors, uncaughtErrorCount);
                if (kind == CopilotCliExitKind.RetryUncaughtError)
                {
                    uncaughtErrorCount++;
                    continue;
                }

                if (kind == CopilotCliExitKind.Failure)
                {
                    throw new InvalidOperationException(CopilotCliExit.ErrorMessage(exitCode, stderrData, events.SessionError));
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
                debugOutput.Insert(0, "Copilot CLI Debug Output:");
                ProviderLogger.Info(string.Join("\n", debugOutput));
            }

            return sandboxBridge.State;
        }
    }

    /// <summary>Runs the CLI under a token that also fires when a bridged generation hits a sample limit; that limit, not the interrupted exec, is what the caller sees.</summary>
    private async Task<ExecResult> LaunchAsync(
        ISandboxEnvironment sandbox,
        ICopilotCliBridge sandboxBridge,
        IReadOnlyList<string> agentCmd,
        string agentCwd,
        IReadOnlyDictionary<string, string> agentEnv,
        CancellationToken cancellationToken)
    {
        using var execCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sandboxBridge.LimitReached, sandboxBridge.TerminateRequested);
        try
        {
            return await sandbox.ExecAsync(
                CopilotCliCommand.Launch(agentCmd),
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

    /// <summary>Recorded when stdout does not begin with a JSON line: the sandbox exec output cap keeps the tail, so a very long session may have lost its first lines.</summary>
    private static JsonObject TruncatedOutputWarning() => new()
    {
        ["type"] = "inspect_warning",
        ["message"] = "Copilot CLI stdout did not start with a complete JSON line; if the session exceeded the sandbox exec output "
            + $"limit ({SandboxLimits.HumanReadableSize(SandboxLimits.MaxExecOutputSize)}, {SandboxLimits.MaxExecOutputSizeVar}) its first events were discarded.",
    };

    /// <summary>The sample's task state with the agent's messages and output swapped in; outside a runner a bare state carrying the sample store stands in.</summary>
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

    private static async Task<ICopilotCliBridge> StartSandboxBridgeAsync(AgentBridge bridge, ISandboxEnvironment sandbox, int port, CancellationToken cancellationToken)
    {
        var server = await SandboxAgentBridge.StartAsync(bridge, sandbox, port, cancellationToken).ConfigureAwait(false);
        return new SandboxBridgeAdapter(server);
    }

    private sealed class SandboxBridgeAdapter(SandboxAgentBridge server) : ICopilotCliBridge
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
