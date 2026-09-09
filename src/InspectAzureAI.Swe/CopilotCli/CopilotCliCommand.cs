using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Swe.CopilotCli;

/// <summary>
/// The command-line assembly of the Copilot CLI agent, the counterpart of <see cref="InspectAzureAI.Swe.ClaudeCode.ClaudeCodeCommand"/>
/// (inspect_swe <c>claude_code.py</c> argv assembly): the non-interactive flags the 1.0.83 probe verified
/// (<c>-p</c>, <c>--output-format json</c>, <c>--session-id</c> on the first run and <c>--resume=&lt;id&gt;</c>
/// afterwards, <c>--yolo</c> or an allow list), the same stdin-closing <c>bash -c</c> launch wrapper, and the
/// prompt prefix that stands in for a system prompt flag the CLI does not have.
/// </summary>
public static class CopilotCliCommand
{
    /// <summary>Closes stdin before exec'ing the CLI so it cannot block on a tty read; <c>$0</c> is the second "bash".</summary>
    public const string LaunchScript = "exec 0</dev/null; \"$@\"";

    /// <summary>The CLI's own log level; its debug logs would otherwise carry every request body.</summary>
    public const string LogLevel = "error";

    /// <summary>The flags of one launch after the prompt: output format, model, permissions, plugins and the log location.</summary>
    public static IReadOnlyList<string> BaseFlags(string presentedModel, CopilotCliOptions options, string agentCwd)
    {
        ArgumentNullException.ThrowIfNull(presentedModel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(agentCwd);
        var cmd = new List<string> { "--output-format", "json", "--model", presentedModel };
        if (options.Effort is not null)
        {
            cmd.AddRange(["--effort", options.Effort]);
        }

        cmd.Add("--no-auto-update");
        if (options.NoAskUser)
        {
            cmd.Add("--no-ask-user");
        }

        if (options.DisableBuiltinMcps)
        {
            cmd.Add("--disable-builtin-mcps");
        }

        if (options.NoCustomInstructions)
        {
            cmd.Add("--no-custom-instructions");
        }

        if (options.Permission == CopilotCliPermission.Yolo)
        {
            cmd.Add("--yolo");
        }
        else
        {
            cmd.Add("--allow-all-paths");
            cmd.AddRange(options.AllowedTools.Select(tool => $"--allow-tool={tool}"));
        }

        cmd.AddRange(options.DeniedTools.Select(tool => $"--deny-tool={tool}"));
        if (options.CustomAgent is not null)
        {
            cmd.AddRange(["--agent", options.CustomAgent]);
        }

        foreach (var dir in options.PluginDirs)
        {
            cmd.AddRange(["--plugin-dir", dir]);
        }

        foreach (var dir in options.AddDirs)
        {
            cmd.AddRange(["--add-dir", dir]);
        }

        if (options.UsageOutputFile is not null)
        {
            cmd.AddRange(["--usage-output-file", options.UsageOutputFile]);
        }

        cmd.AddRange(["--log-level", LogLevel, "--log-dir", CopilotCliEnv.LogDirOf(CopilotCliEnv.ResolveHome(agentCwd, options.Env))]);
        return cmd;
    }

    /// <summary>The system texts: every system message's text, then the agent's own <c>SystemPrompt</c>.</summary>
    public static IReadOnlyList<string> SystemTexts(IReadOnlyList<ChatMessage> messages, string? systemPrompt)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var texts = messages.OfType<ChatMessageSystem>().Select(m => m.Text).ToList();
        if (systemPrompt is not null)
        {
            texts.Add(systemPrompt);
        }

        return texts;
    }

    /// <summary>
    /// The prompt of one launch: the system texts are prepended to the first prompt of a session only (the CLI has
    /// no system-prompt flag; a resumed session already carries them and the bridge round-trips them into the state).
    /// </summary>
    public static string Prompt(IReadOnlyList<string> systemTexts, string prompt, bool isResume)
    {
        ArgumentNullException.ThrowIfNull(systemTexts);
        ArgumentNullException.ThrowIfNull(prompt);
        return systemTexts.Count > 0 && !isResume ? string.Join("\n\n", systemTexts) + "\n\n" + prompt : prompt;
    }

    /// <summary>The argv: binary, <c>-p</c> prompt, the session flag (<c>--session-id id</c> new, <c>--resume=id</c> resumed), then the flags.</summary>
    public static IReadOnlyList<string> Build(string binary, string sessionId, bool isResume, IReadOnlyList<string> flags, string prompt)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(prompt);
        IReadOnlyList<string> session = isResume ? [$"--resume={sessionId}"] : ["--session-id", sessionId];
        return [binary, "-p", prompt, .. session, .. flags];
    }

    /// <summary>The <c>bash -c 'exec 0&lt;/dev/null; "$@"' bash &lt;argv&gt;</c> wrapper shared with the Claude Code agent.</summary>
    public static IReadOnlyList<string> Launch(IReadOnlyList<string> agentCmd)
    {
        ArgumentNullException.ThrowIfNull(agentCmd);
        return ["bash", "-c", LaunchScript, "bash", .. agentCmd];
    }

    /// <summary>Creates the per-sample home and log directories before the first launch (argv, not a shell string: the cwd is a sandbox path); a caller <c>COPILOT_HOME</c> in <paramref name="env"/> is honoured.</summary>
    public static IReadOnlyList<string> PrepareHomeCommand(string agentCwd, IReadOnlyDictionary<string, string>? env = null)
    {
        ArgumentNullException.ThrowIfNull(agentCwd);
        return ["mkdir", "-p", CopilotCliEnv.LogDirOf(CopilotCliEnv.ResolveHome(agentCwd, env))];
    }
}
