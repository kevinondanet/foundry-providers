using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Eval.Tools.Skills;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.ClaudeCode;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of the parameters of inspect_swe 0.2.70 <c>_claude_code/claude_code.py</c> <c>claude_code()</c>
/// (<c>claude_code.py:104-137</c>). Not ported: the deprecated <c>auto_mode</c> argument (use
/// <see cref="PermissionMode"/> <c>"auto"</c>, deviation D-C6), checkpointing (D-C7), and string model specs, since
/// models are <see cref="Model"/> instances (D-C10). Validation mirrors the Python <c>ValueError</c>s. The CLI-related
/// fields keep Python's names in their summaries.
/// </summary>
public sealed record ClaudeCodeOptions
{
    /// <summary>The default agent description (<c>claude_code.py:106-109</c>, dedented and trimmed).</summary>
    public const string DefaultDescription = "Autonomous coding agent capable of writing, testing, debugging,\nand iterating on code across multiple languages.";

    /// <summary>Python's <c>ClaudeCodePermissionMode</c> literal.</summary>
    public static readonly IReadOnlyList<string> PermissionModes = ["acceptEdits", "auto", "bypassPermissions", "default", "dontAsk", "plan"];

    /// <summary>The accepted <see cref="Effort"/> levels (beyond 0.2.70, deviation D-C8).</summary>
    public static readonly IReadOnlyList<string> EffortLevels = ["low", "medium", "high", "xhigh", "max"];

    public string Name { get; init; } = "Claude Code";

    public string Description { get; init; } = DefaultDescription;

    /// <summary>Text appended to Claude Code's built-in system prompt (<c>--append-system-prompt</c>).</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Text replacing Claude Code's built-in system prompt (<c>--system-prompt</c>).</summary>
    public string? ReplaceSystemPrompt { get; init; }

    /// <summary>
    /// Port of <c>skills</c>: extra skills, read when the agent is constructed and installed into
    /// <c>&lt;cwd&gt;/.claude/skills</c> (the project skills directory) before each run.
    /// </summary>
    public IReadOnlyList<SkillSource>? Skills { get; init; }

    /// <summary>Port of <c>mcp_servers</c>: MCP servers made available to Claude Code (listed before bridged servers in the configuration).</summary>
    public IReadOnlyList<McpServerConfig>? McpServers { get; init; }

    /// <summary>
    /// Port of <c>bridged_tools</c>: host-side tools exposed to Claude Code as MCP servers on the bridge. Their tools
    /// are always added to <c>--allowed-tools</c>, whatever <see cref="AllowlistMcpTools"/> says.
    /// </summary>
    public IReadOnlyList<BridgedToolsSpec>? BridgedTools { get; init; }

    /// <summary>
    /// Tool names passed to <c>--disallowed-tools</c>. Disallowing <c>WebSearch</c> (bare or as <c>WebSearch(...)</c>)
    /// also withholds the web-search grant from the bridge.
    /// </summary>
    public IReadOnlyList<string>? DisallowedTools { get; init; }

    /// <summary>
    /// Port of <c>centaur</c>: when set, Claude Code is made available to a human through the human CLI agent instead
    /// of running unattended. Null is Python's <c>False</c>; <c>new CentaurOptions()</c> is <c>True</c>.
    /// </summary>
    public CentaurOptions? Centaur { get; init; }

    public AgentAttempts Attempts { get; init; } = new();

    /// <summary>The model served through the bridge; null means the sample's active model.</summary>
    public Model? Model { get; init; }

    /// <summary>Overrides the presented (cosmetic) model identity (<c>model_config</c>).</summary>
    public string? ModelConfig { get; init; }

    /// <summary>Reasoning effort applied host-side to the served model only (beyond 0.2.70).</summary>
    public string? Effort { get; init; }

    /// <summary>Extra bridge aliases; they win over the derived names (<c>model_aliases</c>).</summary>
    public IReadOnlyDictionary<string, Model>? ModelAliases { get; init; }

    /// <summary>Port of <c>opus_model</c>: the model for <c>opus</c> (or <c>opusplan</c> in plan mode); null inherits the presented model.</summary>
    public Model? OpusModel { get; init; }

    /// <summary>Port of <c>sonnet_model</c>: the model for <c>sonnet</c> (or <c>opusplan</c> outside plan mode); null inherits the presented model.</summary>
    public Model? SonnetModel { get; init; }

    /// <summary>Port of <c>haiku_model</c>: the model for <c>haiku</c> and background work; null inherits the presented model.</summary>
    public Model? HaikuModel { get; init; }

    /// <summary>Port of <c>subagent_model</c>: the model for sub-agents; null inherits the presented model.</summary>
    public Model? SubagentModel { get; init; }

    /// <summary>Port of <c>filter</c>: intercepts each bridged generation (see <see cref="AgentBridge.Filter"/>).</summary>
    public GenerateFilter? Filter { get; init; }

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

    /// <summary>
    /// Port of <c>allowlist_mcp_tools</c>: whether the tools of <see cref="McpServers"/> go into <c>--allowed-tools</c>.
    /// Set false with <see cref="PermissionMode"/> <c>"auto"</c> to let Claude Code's classifier adjudicate them.
    /// </summary>
    public bool AllowlistMcpTools { get; init; } = true;

    /// <summary>Host cache directory for downloaded binaries; null means <see cref="ClaudeCodeBinary.DefaultCacheDir"/>.</summary>
    public string? CacheDir { get; init; }

    /// <summary>Skips the install-script discovery of the download base URL when set.</summary>
    public string? DownloadBaseUrl { get; init; }

    /// <summary>Handler for the download client (tests serve a fake CDN through it).</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>Bridge port; 0 picks a free port.</summary>
    public int Port { get; init; }

    /// <summary>
    /// Prompt cache policy for the generations the CLI makes through the bridge (port-only; Python's bridge takes
    /// no cache). Null disables the cache.
    /// </summary>
    public CachePolicy? Cache { get; init; }

    /// <summary>Port of the validation in <c>claude_code()</c> (<c>claude_code.py:214-217</c>, <c>81-100</c>).</summary>
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
