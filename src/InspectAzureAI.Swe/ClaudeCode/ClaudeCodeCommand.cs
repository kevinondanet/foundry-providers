using System.Text.Encodings.Web;
using System.Text.Json;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Swe.ClaudeCode;

/// <summary>
/// Port of the command-line assembly of inspect_swe <c>_claude_code/claude_code.py</c>: the base flags
/// (<c>claude_code.py:364-375, 404-409</c>), <c>_system_prompt_args</c> (<c>:651-663</c>), the argv with
/// <c>--session-id</c>/<c>--resume</c> and the trailing <c>-- prompt</c> (<c>:493-507</c>), the stdin-closing
/// launch wrapper (<c>:537-547</c>) and the <c>settings.json</c> seed command of <c>_seed_claude_config</c>.
/// </summary>
public static class ClaudeCodeCommand
{
    /// <summary>The unattended-mode flags (centaur mode, which omits them, is not ported).</summary>
    public static readonly IReadOnlyList<string> PrintFlags = ["--print", "--output-format", "stream-json", "--verbose"];

    /// <summary>Closes stdin before exec'ing Claude Code so the CLI cannot block on a tty read; <c>$0</c> is the second "bash".</summary>
    public const string LaunchScript = "exec 0</dev/null; \"$@\"";

    /// <summary>Port of the flags built at <c>claude_code.py:364-375</c> and <c>:406-409</c>: permission flag, cosmetic model, print flags, debug, disallowed tools.</summary>
    public static IReadOnlyList<string> BaseFlags(string presentedModel, string? permissionMode = null, bool debug = false, IReadOnlyList<string>? disallowedTools = null)
    {
        ArgumentNullException.ThrowIfNull(presentedModel);
        var cmd = new List<string>();
        if (permissionMode is not null)
        {
            cmd.AddRange(["--permission-mode", permissionMode]);
        }
        else
        {
            cmd.Add("--dangerously-skip-permissions");
        }

        cmd.AddRange(["--model", presentedModel]);
        cmd.AddRange(PrintFlags);
        if (debug)
        {
            cmd.Add("--debug");
        }

        if (disallowedTools is { Count: > 0 })
        {
            cmd.AddRange(["--disallowed-tools", string.Join(",", disallowedTools)]);
        }

        return cmd;
    }

    /// <summary>The system texts of <c>claude_code.py:480-486</c>: every system message's text, then the agent's own <c>system_prompt</c>.</summary>
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
    /// Port of <c>_system_prompt_args</c>. A replacement is per-invocation so it is re-sent on resume; appended
    /// texts are not, because the bridge round-trips them into the state and re-appending would duplicate them.
    /// </summary>
    public static IReadOnlyList<string> SystemPromptArgs(IReadOnlyList<string> systemTexts, string? replaceSystemPrompt, bool isResume)
    {
        ArgumentNullException.ThrowIfNull(systemTexts);
        var args = new List<string>();
        if (replaceSystemPrompt is not null)
        {
            args.AddRange(["--system-prompt", replaceSystemPrompt]);
        }

        if (systemTexts.Count > 0 && !isResume)
        {
            args.AddRange(["--append-system-prompt", string.Join("\n\n", systemTexts)]);
        }

        return args;
    }

    /// <summary>Port of the argv assembly at <c>claude_code.py:493-507</c>; the prompt is the last positional argument after a bare <c>--</c>, never stdin.</summary>
    public static IReadOnlyList<string> Build(string claudeBinary, string sessionId, bool isResume, IReadOnlyList<string> flags, IReadOnlyList<string> systemArgs, string prompt)
    {
        ArgumentNullException.ThrowIfNull(claudeBinary);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(systemArgs);
        ArgumentNullException.ThrowIfNull(prompt);
        return [claudeBinary, isResume ? "--resume" : "--session-id", sessionId, .. flags, .. systemArgs, "--", prompt];
    }

    /// <summary>Port of the <c>bash -c 'exec 0&lt;/dev/null; "$@"' bash &lt;argv&gt;</c> wrapper of <c>claude_code.py:538</c>.</summary>
    public static IReadOnlyList<string> Launch(IReadOnlyList<string> agentCmd)
    {
        ArgumentNullException.ThrowIfNull(agentCmd);
        return ["bash", "-c", LaunchScript, "bash", .. agentCmd];
    }

    /// <summary>
    /// The <c>settings.json</c> content: <c>{"apiKeyHelper": "echo '&lt;api_key&gt;'"}</c>. Claude Code runs the helper
    /// through a shell, so the key is single-quoted (Python writes it bare; the two files are identical for keys
    /// without shell metacharacters only in what they echo, not byte for byte).
    /// </summary>
    public static string SettingsJson(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        return "{\"apiKeyHelper\": " + JsonSerializer.Serialize("echo " + ShellQuote(apiKey), RelaxedJson) + "}";
    }

    /// <summary>
    /// Port of <c>_seed_claude_config</c>: Claude Code 2.1.37 ignores <c>ANTHROPIC_AUTH_TOKEN</c> and silently
    /// enters OAuth (rc 0, no output); an <c>apiKeyHelper</c> in <c>$HOME/.claude/settings.json</c> works.
    /// Python concatenates the key into single quotes unescaped; this quotes it safely for the same file content.
    /// </summary>
    public static string SettingsCommand(string apiKey) =>
        "mkdir -p \"$HOME/.claude\" && echo " + ShellQuote(SettingsJson(apiKey)) + " > \"$HOME/.claude/settings.json\"";

    /// <summary>Single-quotes a value for bash (the <c>shlex.quote</c> idiom: <c>'</c> becomes <c>'\''</c>).</summary>
    public static string ShellQuote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
