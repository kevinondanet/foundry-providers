using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Model.Compaction;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Maf;
using InspectAzureAI.Swe.ClaudeCode;
using InspectAzureAI.Swe.CopilotCli;
using InspectAzureAI.Swe.MiniSwe;

namespace InspectAzureAI.SweShowcase;

/// <summary>
/// The agents the showcase can run and how each becomes a task solver: the three Inspect SWE agents (mini-swe,
/// Claude Code and the Copilot CLI) and the Agent Framework agent go through <c>as_solver</c>
/// (<see cref="Agents.AsSolver"/>), the basic agent is Inspect's <c>basic_agent</c> with the sandbox <c>bash</c> tool.
/// </summary>
internal static class AgentChoice
{
    public const string MiniSweName = "mini-swe";

    public const string ClaudeCodeName = "claude-code";

    public const string CopilotName = "copilot";

    public const string BasicName = "basic";

    public const string MafName = "maf";

    public static readonly IReadOnlyList<string> Names = [MiniSweName, ClaudeCodeName, CopilotName, BasicName, MafName];

    /// <summary>Wall-clock budget of one bash call of the basic and Agent Framework agents (the SWE agents carry their own command timeouts).</summary>
    private static readonly TimeSpan BashTimeout = TimeSpan.FromMinutes(3);

    public static string Resolve(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            throw new UsageError($"run requires --agent {string.Join("|", Names)}");
        }

        var name = agent.Trim().ToLowerInvariant();
        return Names.Contains(name, StringComparer.Ordinal) ? name : throw new UsageError($"--agent expects {string.Join("|", Names)}, got '{agent}'");
    }

    /// <summary>
    /// The solver for an agent. <paramref name="cache"/> reaches every model call of the loop (the bridge for Claude
    /// Code and the Copilot CLI, the Inspect chat client for the Agent Framework agent); <paramref name="compaction"/>
    /// applies to the two native loops only — the CLIs and Agent Framework manage their own context, so asking for
    /// it there is a usage error (see <see cref="RejectCompaction"/>). Approval policies are not passed here: the
    /// runner installs the eval's policies ambiently and all five agents pick them up.
    /// </summary>
    public static Solver Solver(string agent, int attempts, bool debug, CachePolicy? cache = null, CompactionHook? compaction = null)
    {
        if (compaction is not null)
        {
            RejectCompaction(agent);
        }

        return agent switch
        {
            MiniSweName => Agents.AsSolver(MiniSwe.Agent(new MiniSweAgentOptions { Attempts = new AgentAttempts(attempts), Cache = cache, Compaction = compaction })),
            ClaudeCodeName => Agents.AsSolver(ClaudeCode.Agent(new ClaudeCodeOptions { Attempts = new AgentAttempts(attempts), Debug = debug, Cache = cache })),
            CopilotName => Agents.AsSolver(CopilotCli.Agent(new CopilotCliOptions { Attempts = new AgentAttempts(attempts), Debug = debug, Cache = cache })),
            BasicName => Solvers.BasicAgent(tools: [SandboxTools.Bash(BashTimeout)], maxAttempts: attempts, compaction: compaction, cache: cache),
            MafName => Agents.AsSolver(AgentFramework.Agent(new MafAgentOptions
            {
                Tools = MafTools.FromToolDefs([SandboxTools.Bash(BashTimeout)]),
                Attempts = new AgentAttempts(attempts),
                Cache = cache,
            })),
            _ => throw new UsageError($"--agent expects {string.Join("|", Names)}, got '{agent}'"),
        };
    }

    /// <summary>Compaction is a property of the native loops; the Claude Code and Copilot CLIs and Agent Framework manage their own context windows.</summary>
    public static void RejectCompaction(string agent)
    {
        if (agent == ClaudeCodeName)
        {
            throw new UsageError("--compaction does not apply to claude-code: the Claude Code CLI compacts its own context; use it with --agent mini-swe or --agent basic");
        }

        if (agent == CopilotName)
        {
            throw new UsageError("--compaction does not apply to copilot: the Copilot CLI compacts its own context; use it with --agent mini-swe or --agent basic");
        }

        if (agent == MafName)
        {
            throw new UsageError("--compaction does not apply to maf: Agent Framework keeps the conversation in its own session; use it with --agent mini-swe or --agent basic");
        }
    }
}
