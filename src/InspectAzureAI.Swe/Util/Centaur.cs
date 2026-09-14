using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Human;

namespace InspectAzureAI.Swe.Util;

/// <summary>
/// Port of inspect_swe <c>_util/centaur.py</c> <c>CentaurOptions</c>: centaur mode hands the sandbox to a human
/// through the human CLI agent, with the coding agent's CLI preconfigured in the shell. Python's
/// <c>answer: bool | str</c> is split into <see cref="Answer"/> and <see cref="AnswerPattern"/>.
/// </summary>
public sealed record CentaurOptions
{
    /// <summary>Whether an explicit answer is required (otherwise the task is scored on the files in the container).</summary>
    public bool Answer { get; init; } = true;

    /// <summary>A regex the answer must match (Python passes it as <c>answer: str</c>).</summary>
    public string? AnswerPattern { get; init; }

    /// <summary>Allow the human to check their score while working.</summary>
    public bool IntermediateScoring { get; init; }

    /// <summary>Record all user commands and outputs in the sandbox bash session.</summary>
    public bool RecordSession { get; init; } = true;

    /// <summary>Test seam: the operator view handed to the human CLI agent.</summary>
    internal IHumanAgentView? View { get; init; }

    /// <summary>Test seam: how often the human CLI agent polls the sandbox service.</summary>
    internal TimeSpan? PollingInterval { get; init; }
}

/// <summary>The arguments <see cref="Centaur"/> passes to the human CLI agent factory (<c>user</c> is never passed, as in Python).</summary>
internal sealed record HumanCliArgs(
    bool Answer,
    string? AnswerPattern,
    bool IntermediateScoring,
    bool RecordSession,
    string Instructions,
    string Bashrc,
    IHumanAgentView? View,
    TimeSpan? PollingInterval);

/// <summary>
/// Centaur mode for the CLI agents: the <c>.bashrc</c> that exposes the agent's command to the human (port of
/// <c>_codex_cli/codex_cli.py</c> <c>_run_codex_cli_centaur</c> and <c>_claude_code/claude_code.py</c>
/// <c>run_claude_code_centaur</c>) and <c>run_centaur</c>.
/// </summary>
public static class Centaur
{
    public const string ClaudeInstructions = "Claude Code:\n\n - You may also use Claude Code via the 'claude' command.\n - Use 'claude --resume' if you need to resume a previous claude session.";

    public const string CodexInstructions = "Codex CLI:\n\n - You may also use Codex CLI via the 'codex' command.\n - Use 'codex resume' if you need to resume a previous codex session.";

    /// <summary>The factory the public <c>RunAsync</c> uses: the human CLI agent, with no <c>user</c> (parity).</summary>
    internal static readonly Func<HumanCliArgs, AgentDef> DefaultAgentFactory = args => HumanCli.Agent(
        args.Answer,
        args.AnswerPattern,
        args.IntermediateScoring,
        args.RecordSession,
        user: null,
        instructions: args.Instructions,
        bashrc: args.Bashrc,
        view: args.View,
        pollingInterval: args.PollingInterval);

    /// <summary>
    /// One <c>export K="V"</c> line per entry, in order. Values escape <c>\</c>, <c>"</c> and backtick but keep
    /// <c>$</c>, so <c>$HOME</c>-style values still expand (deviation D-S1: Python writes values unescaped).
    /// </summary>
    public static IReadOnlyList<string> ExportLines(IReadOnlyDictionary<string, string> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return env.Select(kv => $"export {kv.Key}=\"{EscapeExportValue(kv.Value)}\"").ToList();
    }

    /// <summary><c>alias name='&lt;shlex.join(cmd)&gt;'</c>, with each <c>'</c> of the joined command written as <c>'\''</c>.</summary>
    public static string AliasLine(string name, IReadOnlyList<string> cmd)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(cmd);
        return "alias " + name + "='" + ShellQuote.Join(cmd).Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>Port of the Claude Code centaur <c>.bashrc</c>: exports, a <c>claude</c> link on <c>PATH</c>, the onboarding flag file and the alias.</summary>
    public static string ClaudeBashrc(IReadOnlyList<string> claudeCmd, IReadOnlyDictionary<string, string> env)
    {
        ArgumentNullException.ThrowIfNull(claudeCmd);
        if (claudeCmd.Count == 0)
        {
            throw new ArgumentException("The claude command must not be empty.", nameof(claudeCmd));
        }

        var lines = new List<string>(ExportLines(env))
        {
            "mkdir -p \"$HOME/.local/bin\"",
            "export PATH=\"$HOME/.local/bin:$PATH\"",
            $"ln -sf {ShellQuote.Quote(claudeCmd[0])} \"$HOME/.local/bin/claude\"",
            "",
            "echo '{\"hasCompletedOnboarding\":true,\"bypassPermissionsModeAccepted\":true}' > \"$HOME\"/.claude.json",
            "",
            AliasLine("claude", claudeCmd),
        };
        return string.Join("\n", lines);
    }

    /// <summary>Port of the Codex CLI centaur <c>.bashrc</c>: exports, a blank line and the <c>codex</c> alias.</summary>
    public static string CodexBashrc(IReadOnlyList<string> codexCmd, IReadOnlyDictionary<string, string> env)
    {
        ArgumentNullException.ThrowIfNull(codexCmd);
        var lines = new List<string>(ExportLines(env)) { "", AliasLine("codex", codexCmd) };
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Port of <c>run_centaur</c>: runs the human CLI agent with the options, instructions and bashrc on a copy of
    /// <paramref name="state"/>'s messages. As in Python, callers return the bridge state, not the run's.
    /// </summary>
    public static Task<AgentRunResult> RunAsync(CentaurOptions options, string instructions, string bashrc, AgentState state, CancellationToken cancellationToken) =>
        RunAsync(options, instructions, bashrc, state, DefaultAgentFactory, cancellationToken);

    /// <summary>Test seam for the public overload: the agent comes from <paramref name="agentFactory"/>.</summary>
    internal static Task<AgentRunResult> RunAsync(
        CentaurOptions options,
        string instructions,
        string bashrc,
        AgentState state,
        Func<HumanCliArgs, AgentDef> agentFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(bashrc);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(agentFactory);
        var agent = agentFactory(new HumanCliArgs(
            options.Answer,
            options.AnswerPattern,
            options.IntermediateScoring,
            options.RecordSession,
            instructions,
            bashrc,
            options.View,
            options.PollingInterval));
        return Agents.RunAsync(agent, state.Messages, cancellationToken: cancellationToken);
    }

    private static string EscapeExportValue(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);
}
