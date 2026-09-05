using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Swe.ClaudeCode;
using InspectAzureAI.Swe.MiniSwe;

namespace InspectAzureAI.SweShowcase;

/// <summary>
/// The agents the showcase can run and how each becomes a task solver: the two Inspect SWE agents go through
/// <c>as_solver</c> (<see cref="Agents.AsSolver"/>), the basic agent is Inspect's <c>basic_agent</c> with the
/// sandbox <c>bash</c> tool.
/// </summary>
internal static class AgentChoice
{
    public const string MiniSweName = "mini-swe";

    public const string ClaudeCodeName = "claude-code";

    public const string BasicName = "basic";

    public static readonly IReadOnlyList<string> Names = [MiniSweName, ClaudeCodeName, BasicName];

    /// <summary>Wall-clock budget of one bash call of the basic agent (the SWE agents carry their own command timeouts).</summary>
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

    public static Solver Solver(string agent, int attempts, bool debug) => agent switch
    {
        MiniSweName => Agents.AsSolver(MiniSwe.Agent(new MiniSweAgentOptions { Attempts = new AgentAttempts(attempts) })),
        ClaudeCodeName => Agents.AsSolver(ClaudeCode.Agent(new ClaudeCodeOptions { Attempts = new AgentAttempts(attempts), Debug = debug })),
        BasicName => Solvers.BasicAgent(tools: [SandboxTools.Bash(BashTimeout)], maxAttempts: attempts),
        _ => throw new UsageError($"--agent expects {string.Join("|", Names)}, got '{agent}'"),
    };
}
