using System.Runtime.ExceptionServices;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Eval.Tools.Skills;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.Util;
using AgentPrompt = InspectAzureAI.Swe.Util.AgentPrompt;

namespace InspectAzureAI.Swe.CodexCli;

/// <summary>Port of inspect_swe <c>codex_cli()</c>: the registry entry point returning the agent definition.</summary>
public static class CodexCli
{
    public static AgentDef Agent(CodexCliOptions? options = null)
    {
        var agent = new CodexCliAgent(options ?? new CodexCliOptions());
        return new AgentDef(agent.Options.Name, agent.Options.Description, agent.ExecuteAsync);
    }
}

/// <summary>
/// The debug store record of the Codex CLI agent (port-only, deviation D-X6; the sibling of the Claude Code and
/// Copilot CLI records): the stdout and stderr of every launch, kept under <see cref="StoreKey"/> when debugging. Its
/// shape is persisted into the eval log, so it is a contract.
/// </summary>
public sealed record CodexCliDebug
{
    public const string StoreKey = "codex_cli_debug";

    private readonly List<string> _stdout = [];

    private readonly List<string> _stderr = [];

    /// <summary>One entry per launch.</summary>
    public IReadOnlyList<string> Stdout => _stdout;

    /// <summary>One entry per launch.</summary>
    public IReadOnlyList<string> Stderr => _stderr;

    internal void AddLaunch(string stdout, string stderr)
    {
        _stdout.Add(stdout);
        _stderr.Add(stderr);
    }
}

/// <summary>What the agent needs from a started sandbox bridge; lets tests run the agent without an HTTP server.</summary>
internal interface ICodexCliBridge : IAsyncDisposable
{
    string BaseUrl { get; }

    string AuthToken { get; }

    AgentState State { get; }

    /// <summary>The MCP configs of the bridged tools servers (see <see cref="SandboxAgentBridge.McpServerConfigs"/>).</summary>
    IReadOnlyList<McpServerConfigHttp> McpServerConfigs { get; }

    /// <summary>The sample limit a bridged generation hit, if any (see <see cref="SandboxAgentBridge.LimitError"/>).</summary>
    LimitExceededException? LimitError { get; }

    /// <summary>Cancelled once <see cref="LimitError"/> is set, so the running CLI can be torn down.</summary>
    CancellationToken LimitReached { get; }

    /// <summary>The termination a tool call approver requested from a bridged generation, if any (see <see cref="SandboxAgentBridge.TerminateError"/>).</summary>
    InspectAzureAI.Eval.Approval.TerminateSampleException? TerminateError { get; }

    /// <summary>Cancelled once <see cref="TerminateError"/> is set, so the running CLI can be torn down.</summary>
    CancellationToken TerminateRequested { get; }
}

/// <summary>Starts (or, in tests, fakes) the sandbox bridge Codex talks to, serving <paramref name="bridgedTools"/> at <c>/mcp/{name}</c>.</summary>
internal delegate Task<ICodexCliBridge> CodexCliBridgeFactory(AgentBridge bridge, ISandboxEnvironment sandbox, int port, IReadOnlyList<BridgedToolsSpec>? bridgedTools, CancellationToken cancellationToken);

/// <summary>
/// Port of the <c>execute</c> closure of inspect_swe <c>_codex_cli/codex_cli.py</c> <c>codex_cli()</c>: serves the
/// sample model through a <see cref="SandboxAgentBridge"/> (the OpenAI Responses route, plus bridged tools over MCP),
/// installs the Codex release, aligns the <c>--model</c> slug with the served model, writes <c>AGENTS.md</c>, skills
/// and <c>config.toml</c>, then either hands Codex to a human (centaur) or runs <c>codex exec</c>, resuming the last
/// session on every attempt after the first. The returned state is always the bridge's; nothing is read from Codex's
/// output. Sub-agent spans, tool views and compaction markers come from <see cref="CodexCliConsumer"/>. A sample
/// limit or approver termination hit through the bridge ends the run as that signal, never as a CLI failure
/// (deviation D-X7). Not ported: checkpointing (D-X2).
/// </summary>
public sealed class CodexCliAgent
{
    private readonly IReadOnlyList<Skill>? _skills;

    private readonly CodexWebSearch _webSearch;

    public CodexCliAgent(CodexCliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        _webSearch = CodexCliConfig.ResolveWebSearch(options.WebSearch, options.DeprecatedDisallowedTools);
        if (options.Skills is not null)
        {
            // read_skills runs when codex_cli() is called, so a bad skill fails at construction
            _skills = SkillReader.ReadSkills(options.Skills);
            SkillReader.CheckUniqueSkillNames(_skills);
        }

        Binary = new CodexCliBinary(options.CacheDir, options.ReleaseApiBaseUrl, options.CatalogBaseUrl, options.HttpHandler);
    }

    public CodexCliOptions Options { get; }

    internal CodexCliBinary Binary { get; }

    internal CodexCliBridgeFactory BridgeFactory { get; init; } = StartSandboxBridgeAsync;

    /// <summary>Test seam for centaur mode: receives the options, instructions, bashrc and bridge state (default <see cref="InspectAzureAI.Swe.Util.Centaur.RunAsync(CentaurOptions, string, string, AgentState, CancellationToken)"/>).</summary>
    internal Func<CentaurOptions, string, string, AgentState, CancellationToken, Task>? CentaurRunner { get; init; }

    public async Task<AgentState> ExecuteAsync(AgentState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var context = SampleContext.Require();
        var sandbox = context.Sandbox(Options.Sandbox);
        var served = Options.Model ?? context.ActiveModel;
        var consumer = new CodexCliConsumer(context.Transcript);
        var aliases = CodexCliConfig.AutoReviewAliases(Options.AutoReview, Options.ModelAliases);
        var bridge = new AgentBridge(
            state,
            served,
            aliases,
            Options.RetryRefusals,
            forwardGenerationConfig: false,
            modelEventSink: consumer,
            cache: Options.Cache,
            filter: Options.Filter,
            webSearch: _webSearch != CodexWebSearch.Disabled);

        using var sinkScope = ModelEventSinks.Install(consumer);
        var sandboxBridge = await BridgeFactory(bridge, sandbox, Options.Port, Options.BridgedTools, cancellationToken).ConfigureAwait(false);
        try
        {
            await using (sandboxBridge.ConfigureAwait(false))
            {
                var codexBinary = await Binary.EnsureInstalledAsync(sandbox, Options.Version, Options.User, cancellationToken).ConfigureAwait(false);
                var codexVersion = await CodexCliBinary.InstalledVersionAsync(sandbox, codexBinary, Options.User, cancellationToken).ConfigureAwait(false);

                // the floor applies in centaur mode too, so behaviour is consistent across modes
                if (Options.AutoReview is not null)
                {
                    CodexCliConfig.CheckAutoReviewVersion(codexVersion);
                }

                var systemTexts = sandboxBridge.State.Messages.OfType<ChatMessageSystem>().Select(message => message.Text).ToList();
                if (Options.SystemPrompt is not null)
                {
                    systemTexts.Add(Options.SystemPrompt);
                }

                var agentCwd = await SandboxUtil.ResolveAgentCwdAsync(sandbox, Options.User, Options.Cwd, cancellationToken).ConfigureAwait(false);

                var catalog = await Binary.CatalogAsync(codexVersion, cancellationToken).ConfigureAwait(false);
                var resolution = CodexCliModelAlignment.Resolve(served.Api, catalog, Options.ModelConfig);
                ProviderLogger.Info($"Codex model alignment: real model '{served.Name}' (as '{served.Api.ModelName}') → --model '{resolution.Slug}' ({resolution.Reason})");

                var homeDirSet = Options.HomeDir is not null;
                var codexHome = homeDirSet
                    ? await SandboxUtil.ExecAsync(sandbox, $"eval echo \"{Options.HomeDir}\"", Options.User, agentCwd, cancellationToken).ConfigureAwait(false)
                    : SandboxUtil.JoinPath(agentCwd, ".codex");
                var mkdir = await sandbox.ExecAsync(["mkdir", "-p", codexHome], user: Options.User, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!mkdir.Success)
                {
                    throw new InvalidOperationException($"Error executing sandbox command mkdir -p {codexHome}: {mkdir.Stderr}");
                }

                if (systemTexts.Count > 0)
                {
                    await sandbox.WriteFileAsync(CodexCliCommand.AgentsMdPath(agentCwd, codexHome, homeDirSet), string.Join("\n\n", systemTexts), cancellationToken).ConfigureAwait(false);
                }

                if (_skills is not null)
                {
                    await SkillInstaller.InstallSkillsAsync(_skills.Select(SkillSource.FromSkill), sandbox, Options.User, SandboxUtil.JoinPath(codexHome, "skills"), cancellationToken).ConfigureAwait(false);
                }

                var (prompt, hasAssistantResponse) = AgentPrompt.BuildUserPrompt(sandboxBridge.State.Messages);
                var cmd = CodexCliCommand.Base(
                    codexBinary,
                    resolution.Slug,
                    centaur: Options.Centaur is not null,
                    autoReview: Options.AutoReview is not null,
                    Options.ConfigOverrides,
                    CodexCliConfig.CliOverrides(_webSearch, Options.Goals, Options.AutoReview));
                var toml = CodexCliConfig.BuildToml(_webSearch, Options.Goals, Options.AutoReview, Options.McpServers ?? [], sandboxBridge.McpServerConfigs, sandboxBridge.BaseUrl);
                await sandbox.WriteFileAsync(CodexCliCommand.ConfigTomlPath(agentCwd, codexHome, homeDirSet), toml, cancellationToken).ConfigureAwait(false);
                var agentEnv = CodexCliEnv.Build(codexHome, sandboxBridge.BaseUrl, sandboxBridge.AuthToken, Options.Env);

                if (Options.Centaur is { } centaur)
                {
                    await RunCentaurAsync(centaur, cmd, agentEnv, sandboxBridge, cancellationToken).ConfigureAwait(false);
                    return sandboxBridge.State;
                }

                var debug = Options.Debug ? new CodexCliDebug() : null;
                if (debug is not null)
                {
                    context.Store.Set(CodexCliDebug.StoreKey, debug);
                }

                var debugOutput = new List<string>();
                var agentPrompt = prompt;
                var attemptCount = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var agentCmd = CodexCliCommand.WithPrompt(cmd, agentPrompt, resume: hasAssistantResponse || attemptCount > 0);
                    ExecResult result;
                    try
                    {
                        result = await LaunchAsync(sandbox, sandboxBridge, agentCmd, agentCwd, agentEnv, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        // close sub-agent spans this launch left open, so the span tree stays balanced across resumes and errors
                        consumer.Reset();
                    }

                    if (debug is not null)
                    {
                        debugOutput.Add(result.Stdout);
                        debugOutput.Add(result.Stderr);
                        debug.AddLaunch(result.Stdout, result.Stderr);
                    }

                    ThrowSignalled(sandboxBridge);
                    if (!result.Success)
                    {
                        throw new InvalidOperationException(CodexCliExit.ErrorMessage(result.ReturnCode, result.Stdout, result.Stderr));
                    }

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

                if (debug is not null)
                {
                    debugOutput.Insert(0, "Codex CLI Debug Output:");
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
    /// Port of <c>_run_codex_cli_centaur</c>: the human CLI agent with the Codex bashrc and instructions, under a token
    /// that also fires on a bridged sample limit or termination, which is then rethrown.
    /// </summary>
    private async Task RunCentaurAsync(CentaurOptions centaur, IReadOnlyList<string> cmd, IReadOnlyDictionary<string, string> agentEnv, ICodexCliBridge sandboxBridge, CancellationToken cancellationToken)
    {
        var bashrc = InspectAzureAI.Swe.Util.Centaur.CodexBashrc(cmd, agentEnv);
        var runner = CentaurRunner ?? DefaultCentaurRunnerAsync;
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sandboxBridge.LimitReached, sandboxBridge.TerminateRequested);
        try
        {
            await runner(centaur, InspectAzureAI.Swe.Util.Centaur.CodexInstructions, bashrc, sandboxBridge.State, runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sandboxBridge.LimitError is not null || sandboxBridge.TerminateError is not null)
        {
            ThrowSignalled(sandboxBridge);
            throw;
        }

        ThrowSignalled(sandboxBridge);
    }

    private static async Task DefaultCentaurRunnerAsync(CentaurOptions options, string instructions, string bashrc, AgentState state, CancellationToken cancellationToken) =>
        await InspectAzureAI.Swe.Util.Centaur.RunAsync(options, instructions, bashrc, state, cancellationToken).ConfigureAwait(false);

    /// <summary>Runs Codex under a token that also fires when a bridged generation hits a sample limit or termination; that signal, not the interrupted exec, is what the caller sees.</summary>
    private async Task<ExecResult> LaunchAsync(
        ISandboxEnvironment sandbox,
        ICodexCliBridge sandboxBridge,
        IReadOnlyList<string> agentCmd,
        string agentCwd,
        IReadOnlyDictionary<string, string> agentEnv,
        CancellationToken cancellationToken)
    {
        using var execCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sandboxBridge.LimitReached, sandboxBridge.TerminateRequested);
        try
        {
            return await sandbox.ExecAsync(
                CodexCliCommand.Launch(agentCmd),
                input: null,
                cwd: agentCwd,
                env: agentEnv,
                user: Options.User,
                timeout: null,
                cancellationToken: execCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sandboxBridge.LimitError is not null || sandboxBridge.TerminateError is not null)
        {
            ThrowSignalled(sandboxBridge);
            throw;
        }
    }

    /// <summary>Rethrows the limit a bridged generation hit, then an approver's termination (deviation D-X7: they win over the exit code).</summary>
    private static void ThrowSignalled(ICodexCliBridge sandboxBridge)
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

    /// <summary>Port of <c>score(state)</c>: the sample's task state with the agent's messages and output swapped in; outside a runner a bare state carrying the sample store stands in.</summary>
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

    private static async Task<ICodexCliBridge> StartSandboxBridgeAsync(AgentBridge bridge, ISandboxEnvironment sandbox, int port, IReadOnlyList<BridgedToolsSpec>? bridgedTools, CancellationToken cancellationToken)
    {
        var server = await SandboxAgentBridge.StartAsync(bridge, sandbox, port, bridgedTools, cancellationToken).ConfigureAwait(false);
        return new SandboxBridgeAdapter(server);
    }

    private sealed class SandboxBridgeAdapter(SandboxAgentBridge server) : ICodexCliBridge
    {
        public string BaseUrl => server.BaseUrl;

        public string AuthToken => server.AuthToken;

        public AgentState State => server.State;

        public IReadOnlyList<McpServerConfigHttp> McpServerConfigs => server.McpServerConfigs;

        public LimitExceededException? LimitError => server.LimitError;

        public CancellationToken LimitReached => server.LimitReached;

        public InspectAzureAI.Eval.Approval.TerminateSampleException? TerminateError => server.TerminateError;

        public CancellationToken TerminateRequested => server.TerminateRequested;

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }
}
