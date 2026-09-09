using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Model.Cache;

namespace InspectAzureAI.Swe.CopilotCli;

/// <summary>Which bring-your-own-key wire the CLI speaks to the bridge (<c>COPILOT_PROVIDER_TYPE</c>) and so which bridge route it hits.</summary>
public enum CopilotCliProvider
{
    /// <summary><c>openai</c>: <c>POST &lt;base&gt;/chat/completions</c> with a bearer token (the bridge's chat completions route).</summary>
    OpenAI,

    /// <summary><c>anthropic</c>: <c>POST &lt;base&gt;/v1/messages</c> with <c>x-api-key</c> (the bridge's Messages route).</summary>
    Anthropic,
}

/// <summary>How the CLI's own tool permissions are granted inside the sandbox (Inspect approval happens on the host, at the bridge).</summary>
public enum CopilotCliPermission
{
    /// <summary><c>--yolo</c> (= <c>--allow-all-tools --allow-all-paths --allow-all-urls</c>) plus <c>COPILOT_ALLOW_ALL=true</c>.</summary>
    Yolo,

    /// <summary><c>--allow-all-paths</c> with one <c>--allow-tool=&lt;tool&gt;</c> per <see cref="CopilotCliOptions.AllowedTools"/> entry.</summary>
    AllowList,
}

/// <summary>
/// The parameters of the GitHub Copilot CLI agent, the sibling of <see cref="InspectAzureAI.Swe.ClaudeCode.ClaudeCodeOptions"/>
/// (inspect_swe <c>claude_code()</c>): what Copilot CLI 1.0.83 accepts in non-interactive BYOK mode (its
/// <c>copilot help</c> flags and <c>COPILOT_PROVIDER_*</c> variables) mapped onto the same agent surface. Not
/// supported: MCP servers and bridged tools, the marketplace, remote sessions.
/// </summary>
public sealed record CopilotCliOptions
{
    /// <summary>The release installed when <see cref="Version"/> is "auto" and the sandbox has no <c>copilot</c>: the version the wire probe was run against.</summary>
    public const string DefaultVersion = "1.0.83";

    /// <summary>The GitHub release asset base: <c>{ReleaseBaseUrl}/v{version}/copilot-linux-{arch}.tar.gz</c> and <c>SHA256SUMS.txt</c>.</summary>
    public const string DefaultReleaseBaseUrl = "https://github.com/github/copilot-cli/releases/download";

    /// <summary>The name the CLI presents to itself and sends as <c>model</c>; the bridge aliases it to the served model.</summary>
    public const string DefaultModel = "inspect";

    /// <summary>The choices of <c>--effort</c> in <c>copilot help</c> (the same set as Inspect's <c>reasoning_effort</c>).</summary>
    public static readonly IReadOnlyList<string> EffortLevels = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    public string Name { get; init; } = "copilot_cli";

    public string Description { get; init; } = "GitHub Copilot CLI agent";

    /// <summary>
    /// Text prepended (with the state's system messages) to the first prompt of a session: the CLI has no
    /// system-prompt flag, and a resumed session already carries it.
    /// </summary>
    public string? SystemPrompt { get; init; }

    public AgentAttempts Attempts { get; init; } = new();

    /// <summary>The model name presented to the CLI (<c>--model</c>, <c>COPILOT_MODEL</c>), aliased by the bridge to the sample's served model.</summary>
    public string Model { get; init; } = DefaultModel;

    /// <summary>
    /// Overrides the cosmetic identity the CLI is told it is running (a name it knows, say, so it picks that
    /// model's token budget instead of the 128k default); <see cref="Model"/> stays registered as an alias too.
    /// </summary>
    public string? ModelConfig { get; init; }

    /// <summary>One of <see cref="EffortLevels"/>: passed as <c>--effort</c> and merged host-side into the served model's config.</summary>
    public string? Effort { get; init; }

    public CopilotCliProvider Provider { get; init; } = CopilotCliProvider.OpenAI;

    public CopilotCliPermission Permission { get; init; } = CopilotCliPermission.Yolo;

    /// <summary><c>--allow-tool=&lt;tool&gt;</c> entries used with <see cref="CopilotCliPermission.AllowList"/> (e.g. <c>shell</c>, <c>write</c>).</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary><c>--deny-tool=&lt;tool&gt;</c> entries.</summary>
    public IReadOnlyList<string> DeniedTools { get; init; } = [];

    /// <summary>A custom agent name (<c>--agent</c>) from a plugin or the workspace's <c>.github/agents</c>.</summary>
    public string? CustomAgent { get; init; }

    /// <summary>Plugin directories inside the sandbox (<c>--plugin-dir</c>, one flag each).</summary>
    public IReadOnlyList<string> PluginDirs { get; init; } = [];

    /// <summary>Extra directories the CLI may touch (<c>--add-dir</c>, one flag each).</summary>
    public IReadOnlyList<string> AddDirs { get; init; } = [];

    /// <summary><c>--no-custom-instructions</c>: ignore the workspace's instruction files.</summary>
    public bool NoCustomInstructions { get; init; }

    /// <summary><c>--disable-builtin-mcps</c>: the GitHub MCP server needs network and a token the sandbox does not have.</summary>
    public bool DisableBuiltinMcps { get; init; } = true;

    /// <summary><c>--no-ask-user</c>: the CLI must never wait for a person (stdin is closed anyway).</summary>
    public bool NoAskUser { get; init; } = true;

    /// <summary>"auto" (a sandbox <c>copilot</c>, else <see cref="DefaultVersion"/>), "sandbox" (must be installed) or a version like "1.0.83".</summary>
    public string Version { get; init; } = "auto";

    public string? Cwd { get; init; }

    /// <summary>Caller environment, applied last over every default of <see cref="CopilotCliEnv"/>; a <c>COPILOT_HOME</c> here also moves <c>--log-dir</c> and the OTEL file under it.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public string? User { get; init; }

    public string? Sandbox { get; init; }

    public bool Debug { get; init; }

    /// <summary>Host cache directory for downloaded release tarballs; null means <see cref="CopilotCliBinary.DefaultCacheDir"/>.</summary>
    public string? CacheDir { get; init; }

    /// <summary>Release asset base URL; null means <see cref="DefaultReleaseBaseUrl"/>.</summary>
    public string? ReleaseBaseUrl { get; init; }

    /// <summary>Handler for the download client (tests serve a fake release through it).</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>Bridge port; 0 picks a free port.</summary>
    public int Port { get; init; }

    /// <summary>Prompt cache policy for the generations the CLI makes through the bridge; null disables the cache.</summary>
    public CachePolicy? Cache { get; init; }

    public int RetryRefusals { get; init; } = 3;

    public int RetryUncaughtErrors { get; init; } = 3;

    /// <summary>Turn on the CLI's OTEL file exporter (<c>COPILOT_OTEL_*</c>) writing <c>&lt;home&gt;/otel.jsonl</c>.</summary>
    public bool Otel { get; init; }

    /// <summary><c>--usage-output-file</c>: where the CLI writes its usage JSON (a path inside the sandbox).</summary>
    public string? UsageOutputFile { get; init; }

    /// <summary>
    /// Record the CLI's streaming-only <c>ephemeral</c> lines (per-token <c>assistant.message_delta</c>,
    /// <c>tool.execution_partial_result</c>, <c>session.background_tasks_changed</c>, ...) as transcript info events
    /// too (see <see cref="CopilotCliEvents.IsStreamingLine"/>). Off by default: they add 150-250 events per sample
    /// and nothing the non-ephemeral lines do not carry. The debug store keeps every line regardless.
    /// </summary>
    public bool RecordStreamingLines { get; init; }

    /// <summary>The validation of the option combinations the CLI would otherwise reject at launch.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Attempts, nameof(Attempts));
        ArgumentNullException.ThrowIfNull(Version, nameof(Version));
        ArgumentNullException.ThrowIfNull(AllowedTools, nameof(AllowedTools));
        ArgumentNullException.ThrowIfNull(DeniedTools, nameof(DeniedTools));
        ArgumentNullException.ThrowIfNull(PluginDirs, nameof(PluginDirs));
        ArgumentNullException.ThrowIfNull(AddDirs, nameof(AddDirs));
        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new ArgumentException("model must be a non-empty name (the CLI sends it as the request model).");
        }

        if (Effort is not null && !EffortLevels.Contains(Effort, StringComparer.Ordinal))
        {
            throw new ArgumentException("effort must be one of 'none', 'minimal', 'low', 'medium', 'high', 'xhigh', or 'max'.");
        }

        if (!Enum.IsDefined(Permission))
        {
            throw new ArgumentException("permission must be Yolo or AllowList.");
        }

        if (Permission != CopilotCliPermission.Yolo && AllowedTools.Count == 0)
        {
            throw new ArgumentException("permission AllowList requires at least one allowed tool (or use Yolo).");
        }

        if (!Enum.IsDefined(Provider))
        {
            throw new ArgumentException("provider must be OpenAI or Anthropic.");
        }
    }
}
