using InspectAzureAI.Eval.Agents;

namespace InspectAzureAI.Swe.ClaudeCode;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of the parameters of inspect_swe <c>_claude_code/claude_code.py</c> <c>claude_code()</c> (the subset the
/// C# agent supports: no skills, MCP servers, bridged tools, centaur mode or checkpointing). Validation mirrors
/// the Python <c>ValueError</c>s; the CLI-related fields keep Python's names in their summaries.
/// </summary>
public sealed record ClaudeCodeOptions
{
    /// <summary>The default agent description (<c>claude_code.py:117-120</c>, dedented).</summary>
    public const string DefaultDescription = "Autonomous coding agent capable of writing, testing, debugging,\nand iterating on code across multiple languages.";

    /// <summary>Python's <c>ClaudeCodePermissionMode</c> literal.</summary>
    public static readonly IReadOnlyList<string> PermissionModes = ["acceptEdits", "auto", "bypassPermissions", "default", "dontAsk", "plan"];

    /// <summary>Python's <c>ClaudeCodeEffort</c> literal.</summary>
    public static readonly IReadOnlyList<string> EffortLevels = ["low", "medium", "high", "xhigh", "max"];

    public string Name { get; init; } = "Claude Code";

    public string Description { get; init; } = DefaultDescription;

    /// <summary>Text appended to Claude Code's built-in system prompt (<c>--append-system-prompt</c>).</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Text replacing Claude Code's built-in system prompt (<c>--system-prompt</c>).</summary>
    public string? ReplaceSystemPrompt { get; init; }

    /// <summary>Tool names passed to <c>--disallowed-tools</c>.</summary>
    public IReadOnlyList<string>? DisallowedTools { get; init; }

    public AgentAttempts Attempts { get; init; } = new();

    /// <summary>The model served through the bridge; null means the sample's active model.</summary>
    public Model? Model { get; init; }

    /// <summary>Overrides the presented (cosmetic) model identity (<c>model_config</c>).</summary>
    public string? ModelConfig { get; init; }

    /// <summary>Reasoning effort applied host-side to the served model (<c>effort</c>).</summary>
    public string? Effort { get; init; }

    /// <summary>Extra bridge aliases; they win over the derived names (<c>model_aliases</c>).</summary>
    public IReadOnlyDictionary<string, Model>? ModelAliases { get; init; }

    /// <summary>One of <see cref="PermissionModes"/>; null means <c>--dangerously-skip-permissions</c>.</summary>
    public string? PermissionMode { get; init; }

    public int? RetryRefusals { get; init; } = 3;

    public int? RetryUncaughtErrors { get; init; } = 3;

    public string? Cwd { get; init; }

    /// <summary>Caller environment, applied last over every default of <see cref="ClaudeCodeEnv"/>.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public string? User { get; init; }

    public string? Sandbox { get; init; }

    /// <summary>"auto", "sandbox", "stable", "latest" or a semver version (see <see cref="ClaudeCodeBinary"/>).</summary>
    public string Version { get; init; } = "auto";

    public bool Debug { get; init; }

    /// <summary>Host cache directory for downloaded binaries; null means <see cref="ClaudeCodeBinary.DefaultCacheDir"/>.</summary>
    public string? CacheDir { get; init; }

    /// <summary>Skips the install-script discovery of the download base URL when set.</summary>
    public string? DownloadBaseUrl { get; init; }

    /// <summary>Handler for the download client (tests serve a fake CDN through it).</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>Bridge port; 0 picks a free port.</summary>
    public int Port { get; init; }

    /// <summary>Port of the validation in <c>claude_code()</c> (<c>claude_code.py:107-111, 267-270</c>).</summary>
    public void Validate()
    {
        if (SystemPrompt is not null && ReplaceSystemPrompt is not null)
        {
            throw new ArgumentException("system_prompt and replace_system_prompt cannot both be specified");
        }

        if (PermissionMode is not null && !PermissionModes.Contains(PermissionMode, StringComparer.Ordinal))
        {
            throw new ArgumentException("permission_mode must be one of 'acceptEdits', 'auto', 'bypassPermissions', 'default', 'dontAsk', or 'plan'.");
        }

        if (Effort is not null && !EffortLevels.Contains(Effort, StringComparer.Ordinal))
        {
            throw new ArgumentException("effort must be one of 'low', 'medium', 'high', 'xhigh', or 'max'.");
        }

        ArgumentNullException.ThrowIfNull(Attempts, nameof(Attempts));
        ArgumentNullException.ThrowIfNull(Version, nameof(Version));
    }
}
