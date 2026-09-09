using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Swe.CopilotCli;

namespace InspectAzureAI.HveDemo.Components;

/// <summary>
/// What the solvers need to know about this run: how the Copilot CLI is installed and pointed at the bridge, where
/// the plugin lives inside the sandbox, and the baseline agent's command timeout. Built once by <c>Program</c> (or a
/// test) and shared by every sample.
/// </summary>
public sealed record HveSolverOptions
{
    /// <summary>"auto" (a sandbox <c>copilot</c>, else the pinned release), "sandbox" (must be installed) or a version such as "1.0.83".</summary>
    public string CopilotVersion { get; init; } = "auto";

    /// <summary>The <c>--plugin-dir</c> inside the sandbox; the dataset copies the vendored plugin there unless a caller overrides it.</summary>
    public string PluginDir { get; init; } = HveData.PluginSandboxPath;

    /// <summary>The bridge wire the CLI speaks: chat completions (OpenAI) or Messages (Anthropic, for Claude deployments).</summary>
    public CopilotCliProvider Provider { get; init; } = CopilotCliProvider.OpenAI;

    /// <summary>Keep the CLI's raw stdout and stderr in the sample store (<c>copilot_cli_debug</c>).</summary>
    public bool Debug { get; init; }

    /// <summary>Host cache directory for the downloaded release tarball; null means the agent's default.</summary>
    public string? CacheDir { get; init; }

    /// <summary>Wall-clock budget of one <c>bash</c> tool call of the baseline agent.</summary>
    public TimeSpan BashTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// COMPONENT: Solver.
///
/// A solver is a delegate <c>(TaskState, Generate, CancellationToken) -> TaskState</c>; solvers compose with
/// <see cref="Solvers.Chain"/>. Two solvers are offered, chosen by name so the same tasks can compare them:
///
/// <list type="bullet">
///   <item><c>copilot</c> (<see cref="Copilot"/>): <see cref="Solvers.SystemMessage"/> naming the HVE plugin's agents and
///   skills, then the GitHub Copilot CLI agent (<see cref="CopilotCli.Agent"/>) wrapped as a solver with
///   <see cref="Agents.AsSolver"/>. The CLI runs inside the sample's sandbox with <c>--plugin-dir</c> pointing at the
///   vendored HVE Core subset and, when the sample names one, <c>--agent hve-core:&lt;id&gt;</c>; its model calls come back to
///   the sample's model through the sandbox agent bridge, so every generation is in the log.</item>
///   <item><c>basic</c> (<see cref="Basic"/>): the same system message followed by Inspect's <c>basic_agent</c> loop with the
///   sandbox <c>bash</c> tool, a baseline with no HVE plugin at all.</item>
/// </list>
/// </summary>
public static class HveSolvers
{
    public const string CopilotName = "copilot";

    public const string BasicName = "basic";

    public static readonly IReadOnlyList<string> Names = [CopilotName, BasicName];

    /// <summary>
    /// The system message of the Copilot solver. The Copilot CLI has no system-prompt flag, so the agent prepends
    /// every system message to the first prompt of the session; the text therefore reads as a briefing. Braces are
    /// doubled because the template formatter treats <c>{...}</c> as a placeholder.
    /// </summary>
    public const string SystemPrompt =
        "You are working inside a sandboxed repository with the Microsoft HVE Core plugin loaded (plugin `hve-core`, a vendored subset of "
        + "github.com/microsoft/hve-core). It provides the custom agents hve-core:rpi-agent, hve-core:rpi-researcher, hve-core:rpi-planner, "
        + "hve-core:rpi-review-builder, hve-core:code-review, hve-core:code-review-functional and hve-core:code-review-standards; the skills "
        + "code-review, python-foundational, documentation, rpi-quick, rpi-research, rpi-plan, rpi-plan-critique, rpi-implement and rpi-review; "
        + "the git-commit-message.prompt command; and the repository instruction files under .github/instructions/. "
        + "Load the skills and read the instruction files the task names before you write anything, follow them, and verify your work with the "
        + "commands the task gives you. Work autonomously: never ask questions, never wait for confirmation. "
        + "When you are done, reply with a short summary that names every file you created or changed by its path.";

    /// <summary>The baseline's system message: the same repository conventions, without the plugin.</summary>
    public const string BasicSystemPrompt =
        "You are a software engineer working inside a sandboxed repository with a bash tool. Follow the repository instruction files under "
        + ".github/instructions/ for every file type you touch, verify your work with the commands the task gives you, and never ask questions. "
        + "When you are done, submit a short summary that names every file you created or changed by its path.";

    /// <summary>The solver <paramref name="name"/> selects (<c>copilot</c> or <c>basic</c>).</summary>
    public static Solver ByName(string name, HveSolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(options);
        return name.ToLowerInvariant() switch
        {
            CopilotName => Copilot(options),
            BasicName => Basic(options.BashTimeout),
            _ => throw new ArgumentException($"Unknown solver '{name}' (expected one of {string.Join(", ", Names)}).", nameof(name)),
        };
    }

    /// <summary>The whole Copilot solver: the briefing, then the CLI agent.</summary>
    public static Solver Copilot(HveSolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Solvers.Chain(Solvers.SystemMessage(SystemPrompt), CopilotAgent(options));
    }

    /// <summary>
    /// The Copilot CLI agent as a solver. The custom agent is a per-sample choice (<c>metadata.agent</c>), so the
    /// <see cref="AgentDef"/> is built when the sample runs and handed to <see cref="Agents.AsSolver"/>, which runs it
    /// over the sample's messages and copies the agent's conversation back into the task state.
    /// </summary>
    public static Solver CopilotAgent(HveSolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (state, generate, cancellationToken) =>
        {
            var agent = CopilotCli.Agent(CopilotOptions(options, HveDataset.Agent(state.Metadata)));
            return Agents.AsSolver(agent)(state, generate, cancellationToken);
        };
    }

    /// <summary>
    /// The agent options for one sample: the plugin directory, the sample's custom agent, the pinned CLI version,
    /// <c>--yolo</c> permissions (Inspect's own approval runs on the host, at the bridge), the built-in GitHub MCP server
    /// off and <c>--no-ask-user</c>. Attempts stay at one: the checks are the verdict, not a retry signal.
    /// </summary>
    public static CopilotCliOptions CopilotOptions(HveSolverOptions options, string? customAgent)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new CopilotCliOptions
        {
            Version = options.CopilotVersion,
            PluginDirs = [options.PluginDir],
            CustomAgent = customAgent,
            Provider = options.Provider,
            Permission = CopilotCliPermission.Yolo,
            DisableBuiltinMcps = true,
            NoAskUser = true,
            Debug = options.Debug,
            CacheDir = options.CacheDir,
            Attempts = new AgentAttempts(1),
        };
    }

    /// <summary>The baseline: the briefing, then <c>basic_agent</c> with the sandbox <c>bash</c> tool and its <c>submit</c> tool.</summary>
    public static Solver Basic(TimeSpan bashTimeout) => Solvers.Chain(
        Solvers.SystemMessage(BasicSystemPrompt),
        Solvers.BasicAgent(tools: [SandboxTools.Bash(bashTimeout)], maxAttempts: 1));
}
