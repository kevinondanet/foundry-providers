using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Eval.Tools.Skills;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.CodexCli;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Port of the <c>CodexWebSearch</c> literal of inspect_swe <c>_codex_cli/config.py</c>: Codex's <c>web_search</c> mode.</summary>
public enum CodexWebSearch
{
    /// <summary><c>live</c>: live web search.</summary>
    Live,

    /// <summary><c>cached</c>: cached web search.</summary>
    Cached,

    /// <summary><c>disabled</c>: no web search (the bridge's web-search grant is withheld too).</summary>
    Disabled,
}

/// <summary>
/// Port of <c>CodexAutoReview</c> (<c>_codex_cli/config.py:10-30</c>): options for Codex automated approval review.
/// When enabled, Codex runs with its own sandbox active (<c>workspace-write</c>) and <c>on-request</c> approvals, and
/// escalation requests are adjudicated by a guardian model rather than a human.
/// </summary>
public sealed record CodexAutoReview
{
    /// <summary>Additional policy instructions inserted into the guardian review prompt (written to <c>[auto_review]</c> in <c>config.toml</c> only).</summary>
    public string? Policy { get; init; }

    /// <summary>
    /// The model serving guardian review requests (Codex slug <see cref="CodexCliConfig.GuardianModelSlug"/>); null serves
    /// them with the agent's model. Python also accepts a model name or role string; this port takes a <see cref="Eval.Model.Model"/>
    /// instance only (deviation D-X3).
    /// </summary>
    public Model? Model { get; init; }
}

/// <summary>
/// The parameters of inspect_swe <c>codex_cli()</c> (<c>_codex_cli/codex_cli.py:68-96</c>), plus the port-only download,
/// bridge and cache settings the other CLI agents have.
/// </summary>
public sealed record CodexCliOptions
{
    public const string DefaultName = "codex_cli";

    public const string DefaultDescription = "Autonomous coding agent capable of writing, testing, debugging,\nand iterating on code across multiple languages.";

    /// <summary>The Codex release this port was verified against; the showcase pins it (the agent default stays <c>auto</c>, deviation D-X12).</summary>
    public const string TestedVersion = "0.154.0";

    public string Name { get; init; } = DefaultName;

    public string Description { get; init; } = DefaultDescription;

    /// <summary>Appended to the state's system messages and written to <c>AGENTS.md</c>.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>An explicit Codex <c>--model</c> slug; null derives it from the served model (<see cref="CodexCliModelAlignment"/>).</summary>
    public string? ModelConfig { get; init; }

    /// <summary>Skills read when the agent is created and installed to <c>CODEX_HOME/skills</c>.</summary>
    public IReadOnlyList<SkillSource>? Skills { get; init; }

    /// <summary>MCP servers written to <c>config.toml</c> as <c>[mcp_servers.&lt;name&gt;]</c> (rendered for Codex's schema, deviation D-S3).</summary>
    public IReadOnlyList<McpServerConfig>? McpServers { get; init; }

    /// <summary>Host tools served to Codex as MCP servers at <c>{bridge}/mcp/{name}</c>.</summary>
    public IReadOnlyList<BridgedToolsSpec>? BridgedTools { get; init; }

    public CodexWebSearch WebSearch { get; init; } = CodexWebSearch.Live;

    /// <summary>Enable Codex goal tools (<c>features.goals</c>).</summary>
    public bool Goals { get; init; } = true;

    /// <summary>Codex automated approval review (guardian); null is Python's <c>False</c> and <c>new()</c> its <c>True</c>. Requires Codex 0.137.0 or later.</summary>
    public CodexAutoReview? AutoReview { get; init; }

    /// <summary>Centaur mode (Codex offered to a human through the human CLI agent); null is Python's <c>False</c> and <c>new()</c> its <c>True</c>.</summary>
    public CentaurOptions? Centaur { get; init; }

    public AgentAttempts Attempts { get; init; } = new();

    /// <summary>The model served through the bridge; null serves the sample's active model.</summary>
    public Model? Model { get; init; }

    /// <summary>Request model names served by other models (<see cref="Eval.Model.Model"/> instances only, deviation D-X3).</summary>
    public IReadOnlyDictionary<string, Model>? ModelAliases { get; init; }

    /// <summary>Intercepts bridged model requests (<see cref="AgentBridge.Filter"/>).</summary>
    public GenerateFilter? Filter { get; init; }

    /// <summary>How many refusals the bridge regenerates (null: none, as in Python).</summary>
    public int? RetryRefusals { get; init; }

    /// <summary><c>CODEX_HOME</c> (expanded in the sandbox with <c>eval echo</c>); <c>AGENTS.md</c>, skills and <c>config.toml</c> go there. Null uses <c>{cwd}/.codex</c>.</summary>
    public string? HomeDir { get; init; }

    public string? Cwd { get; init; }

    /// <summary>Environment variables for the CLI, applied over the agent's own.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public string? User { get; init; }

    public string? Sandbox { get; init; }

    /// <summary><c>auto</c> (a sandbox <c>codex</c>, else the latest release), <c>sandbox</c>, <c>stable</c>, <c>latest</c> or a version such as <c>0.154.0</c>.</summary>
    public string Version { get; init; } = "auto";

    /// <summary>Extra <c>-c key=value</c> overrides, in order, placed before the agent's own so the agent's win.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? ConfigOverrides { get; init; }

    /// <summary>Log each launch's stdout and stderr (and keep them in the <see cref="CodexCliDebug"/> store record).</summary>
    public bool Debug { get; init; }

    /// <summary>
    /// Port of the deprecated <c>disallowed_tools</c> keyword: only <c>"web_search"</c> is accepted, and it forces
    /// <see cref="CodexWebSearch.Disabled"/>.
    /// </summary>
    [Obsolete("Use WebSearch = CodexWebSearch.Disabled.")]
    public IReadOnlyList<string>? DisallowedTools
    {
        get => DeprecatedDisallowedTools;
        init => DeprecatedDisallowedTools = value;
    }

    /// <summary>What <see cref="Validate"/> and the agent read instead of the obsolete <c>DisallowedTools</c> property.</summary>
    internal IReadOnlyList<string>? DeprecatedDisallowedTools { get; init; }

    /// <summary>Host cache directory for release archives and catalogs; null means <see cref="CodexCliBinary.DefaultCacheDir"/>.</summary>
    public string? CacheDir { get; init; }

    /// <summary>The GitHub repository API base; null means <see cref="CodexCliBinary.DefaultReleaseApiBaseUrl"/>.</summary>
    public string? ReleaseApiBaseUrl { get; init; }

    /// <summary>The raw-content base of the model catalog; null means <see cref="CodexCliBinary.DefaultCatalogBaseUrl"/>.</summary>
    public string? CatalogBaseUrl { get; init; }

    /// <summary>Handler for release, download and catalog requests (tests serve a fake GitHub through it).</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>Bridge port; 0 picks a free port (deviation D-X1: Python counts ports up from 3000 in the store).</summary>
    public int Port { get; init; }

    /// <summary>Prompt cache policy for the generations the CLI makes through the bridge; null disables the cache.</summary>
    public CachePolicy? Cache { get; init; }

    /// <summary>
    /// The checks Python makes when <c>codex_cli()</c> is called: a known <see cref="WebSearch"/> mode, only
    /// <c>"web_search"</c> among the deprecated disallowed tools, a version keyword or valid release version, and a
    /// non-negative port.
    /// </summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Name, nameof(Name));
        ArgumentNullException.ThrowIfNull(Description, nameof(Description));
        ArgumentNullException.ThrowIfNull(Attempts, nameof(Attempts));
        ArgumentNullException.ThrowIfNull(Version, nameof(Version));
        if (DeprecatedDisallowedTools is { } disallowed)
        {
            var unsupported = disallowed
                .Where(tool => tool != "web_search")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (unsupported.Length > 0)
            {
                throw new ArgumentException($"Unsupported Codex disallowed_tools value(s): {string.Join(", ", unsupported)}");
            }
        }

        CodexCliConfig.WebSearchValue(WebSearch);
        if (Version is not ("auto" or "sandbox" or "stable" or "latest"))
        {
            CodexCliBinary.ValidateVersion(Version);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(Port, nameof(Port));
    }
}
