using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.CodexCli;

/// <summary>The Codex CLI argv and file locations of inspect_swe <c>_codex_cli/codex_cli.py:248-308, 367-388</c>.</summary>
public static class CodexCliCommand
{
    /// <summary>
    /// The launch wrapper: stdin is closed before exec so Codex's non-TTY stdin read (it appends a <c>&lt;stdin&gt;</c>
    /// block to a root prompt) is empty and cannot block on an open exec pipe.
    /// </summary>
    public const string LaunchScript = "exec 0</dev/null; \"$@\"";

    /// <summary>The flag that disables Codex's approvals and sandbox; dropped under auto_review, where it would force <c>approval_policy=never</c>.</summary>
    public const string BypassFlag = "--dangerously-bypass-approvals-and-sandbox";

    /// <summary>
    /// The command before the prompt: <c>[binary]</c>, then <c>exec --color never --skip-git-repo-check</c> unless in
    /// centaur mode, <c>--model &lt;slug&gt;</c>, the <see cref="BypassFlag"/> unless auto_review is on, then
    /// <c>-c key=value</c> for each user override followed by each explicit override (so the explicit ones win).
    /// </summary>
    public static IReadOnlyList<string> Base(
        string codexBinary,
        string slug,
        bool centaur,
        bool autoReview,
        IReadOnlyList<KeyValuePair<string, string>>? userOverrides,
        IReadOnlyList<KeyValuePair<string, string>> explicitOverrides)
    {
        ArgumentNullException.ThrowIfNull(codexBinary);
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(explicitOverrides);
        var cmd = new List<string> { codexBinary };
        if (!centaur)
        {
            cmd.AddRange(["exec", "--color", "never", "--skip-git-repo-check"]);
        }

        cmd.AddRange(["--model", slug]);
        if (!autoReview)
        {
            cmd.Add(BypassFlag);
        }

        foreach (var (key, value) in userOverrides ?? [])
        {
            cmd.AddRange(["-c", $"{key}={value}"]);
        }

        foreach (var (key, value) in explicitOverrides)
        {
            cmd.AddRange(["-c", $"{key}={value}"]);
        }

        return cmd;
    }

    /// <summary>The command for one launch: the prompt appended, then <c>resume --last</c> when resuming.</summary>
    public static IReadOnlyList<string> WithPrompt(IReadOnlyList<string> baseCmd, string prompt, bool resume)
    {
        ArgumentNullException.ThrowIfNull(baseCmd);
        ArgumentNullException.ThrowIfNull(prompt);
        var cmd = new List<string>(baseCmd) { prompt };
        if (resume)
        {
            cmd.AddRange(["resume", "--last"]);
        }

        return cmd;
    }

    /// <summary><c>["bash", "-c", LaunchScript, "bash", ..agentCmd]</c>.</summary>
    public static IReadOnlyList<string> Launch(IReadOnlyList<string> agentCmd)
    {
        ArgumentNullException.ThrowIfNull(agentCmd);
        return ["bash", "-c", LaunchScript, "bash", .. agentCmd];
    }

    /// <summary>Where <c>AGENTS.md</c> is written: <c>CODEX_HOME</c> when a home directory is set, otherwise the agent's working directory.</summary>
    public static string AgentsMdPath(string agentCwd, string codexHome, bool homeDirSet) =>
        homeDirSet ? SandboxUtil.JoinPath(codexHome, "AGENTS.md") : SandboxUtil.JoinPath(agentCwd, "AGENTS.md");

    /// <summary>Where <c>config.toml</c> is written: <c>CODEX_HOME</c> when a home directory is set, otherwise <c>{cwd}/.codex</c>.</summary>
    public static string ConfigTomlPath(string agentCwd, string codexHome, bool homeDirSet) =>
        homeDirSet ? SandboxUtil.JoinPath(codexHome, "config.toml") : SandboxUtil.JoinPath(SandboxUtil.JoinPath(agentCwd, ".codex"), "config.toml");
}
