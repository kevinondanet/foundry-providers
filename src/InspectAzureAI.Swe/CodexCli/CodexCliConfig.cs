using System.Globalization;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.CodexCli;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of inspect_swe <c>_codex_cli/config.py</c> and the <c>config.toml</c> assembly of <c>codex_cli.py:311-343</c>:
/// web search and auto-review resolution, the <c>-c</c> overrides, the guardian alias, the version gate and the TOML.
/// </summary>
public static class CodexCliConfig
{
    /// <summary>The model slug Codex uses for guardian (auto_review) requests.</summary>
    public const string GuardianModelSlug = "codex-auto-review";

    /// <summary>The first Codex CLI release where <c>codex exec</c> preserves auto_review approvals.</summary>
    public const string AutoReviewMinVersion = "0.137.0";

    /// <summary>The custom model provider id (a built-in provider cannot take <c>stream_idle_timeout_ms</c>).</summary>
    public const string ProviderId = "openai-proxy";

    /// <summary>How long Codex waits on an idle stream: the bridge answers only once the whole generation is done.</summary>
    public const long StreamIdleTimeoutMs = 3_600_000;

    /// <summary>The variable holding the bridge token, read by Codex as the provider key and as bridged MCP servers' bearer token.</summary>
    internal const string ApiKeyEnvVar = "OPENAI_API_KEY";

    private const string InvalidWebSearchMessage = "web_search must be one of 'live', 'cached', or 'disabled'.";

    /// <summary>The Codex spelling of a mode: <c>live</c>, <c>cached</c> or <c>disabled</c>; an undefined value throws Python's <c>ValueError</c> text.</summary>
    public static string WebSearchValue(CodexWebSearch mode) => mode switch
    {
        CodexWebSearch.Live => "live",
        CodexWebSearch.Cached => "cached",
        CodexWebSearch.Disabled => "disabled",
        _ => throw new ArgumentException(InvalidWebSearchMessage, nameof(mode)),
    };

    /// <summary>Port of <c>resolve_codex_web_search</c>: the deprecated <c>disallowed_tools=["web_search"]</c> forces <see cref="CodexWebSearch.Disabled"/>.</summary>
    public static CodexWebSearch ResolveWebSearch(CodexWebSearch mode, IReadOnlyList<string>? deprecatedDisallowedTools)
    {
        WebSearchValue(mode);
        return deprecatedDisallowedTools is not null && deprecatedDisallowedTools.Contains("web_search", StringComparer.Ordinal)
            ? CodexWebSearch.Disabled
            : mode;
    }

    /// <summary>
    /// Port of <c>codex_cli_config_overrides</c>: the <c>-c</c> values for the explicit arguments, TOML-encoded
    /// (<c>web_search="live"</c>, <c>features.goals=true</c>) and, with auto_review, <c>approval_policy</c>,
    /// <c>sandbox_mode</c>, <c>approvals_reviewer</c> and <c>features.guardian_approval</c>. The policy text is written
    /// only to <c>config.toml</c>, because a multi-line value does not survive <c>-c</c>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> CliOverrides(CodexWebSearch mode, bool goals, CodexAutoReview? autoReview)
    {
        var overrides = new List<KeyValuePair<string, string>>
        {
            KeyValuePair.Create("web_search", $"\"{WebSearchValue(mode)}\""),
            KeyValuePair.Create("features.goals", goals ? "true" : "false"),
        };
        if (autoReview is not null)
        {
            overrides.Add(KeyValuePair.Create("approval_policy", "\"on-request\""));
            overrides.Add(KeyValuePair.Create("sandbox_mode", "\"workspace-write\""));
            overrides.Add(KeyValuePair.Create("approvals_reviewer", "\"auto_review\""));
            overrides.Add(KeyValuePair.Create("features.guardian_approval", "true"));
        }

        return overrides;
    }

    /// <summary>
    /// Port of <c>codex_config_options</c>: <c>web_search</c>, <c>features.goals</c> and, with auto_review, the four
    /// approval keys plus an <c>auto_review = {policy}</c> table when a policy is set.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, object?>> ConfigOptions(CodexWebSearch mode, bool goals, CodexAutoReview? autoReview)
    {
        var options = new List<KeyValuePair<string, object?>>
        {
            Pair("web_search", WebSearchValue(mode)),
            Pair("features.goals", goals),
        };
        if (autoReview is not null)
        {
            options.Add(Pair("approval_policy", "on-request"));
            options.Add(Pair("sandbox_mode", "workspace-write"));
            options.Add(Pair("approvals_reviewer", "auto_review"));
            options.Add(Pair("features.guardian_approval", true));
            if (autoReview.Policy is not null)
            {
                options.Add(Pair("auto_review", new List<KeyValuePair<string, object?>> { Pair("policy", autoReview.Policy) }));
            }
        }

        return options;
    }

    /// <summary>
    /// Port of <c>resolve_codex_auto_review_model_aliases</c>: with an auto-review model, the caller's aliases plus
    /// <see cref="GuardianModelSlug"/> bound to it (the guardian entry wins); otherwise the caller's aliases unchanged.
    /// </summary>
    public static IReadOnlyDictionary<string, Model>? AutoReviewAliases(CodexAutoReview? autoReview, IReadOnlyDictionary<string, Model>? modelAliases)
    {
        if (autoReview?.Model is not { } guardian)
        {
            return modelAliases;
        }

        var aliases = new Dictionary<string, Model>(StringComparer.Ordinal);
        foreach (var (name, model) in modelAliases ?? new Dictionary<string, Model>())
        {
            aliases[name] = model;
        }

        aliases[GuardianModelSlug] = guardian;
        return aliases;
    }

    /// <summary>
    /// Port of <c>check_codex_auto_review_version</c>: an unknown version passes; one below
    /// <see cref="AutoReviewMinVersion"/> throws, comparing the first three numeric parts as Python compares tuples.
    /// </summary>
    public static void CheckAutoReviewVersion(string? version)
    {
        if (version is null)
        {
            return;
        }

        var installed = VersionParts(version);
        var required = VersionParts(AutoReviewMinVersion);
        for (var i = 0; i < Math.Min(installed.Length, required.Length); i++)
        {
            if (installed[i] != required[i])
            {
                if (installed[i] > required[i])
                {
                    return;
                }

                throw AutoReviewTooOld(version);
            }
        }

        if (installed.Length < required.Length)
        {
            throw AutoReviewTooOld(version);
        }
    }

    /// <summary>
    /// The <c>config.toml</c> document in Python's insertion order: <c>analytics = {enabled = false}</c>, the
    /// <see cref="ConfigOptions"/>, <c>mcp_servers.&lt;name&gt;</c> for the static servers then the bridged ones (a later
    /// server with the same name replaces the earlier in place; bridged servers read their token from
    /// <c>OPENAI_API_KEY</c>), <c>preferred_auth_method</c>, <c>model_provider</c> and the
    /// <c>model_providers.openai-proxy</c> table pointing at <c>{bridgeBaseUrl}/v1</c>. <see cref="Toml.Write"/> puts the
    /// plain keys before the tables.
    /// </summary>
    public static string BuildToml(
        CodexWebSearch mode,
        bool goals,
        CodexAutoReview? autoReview,
        IReadOnlyList<McpServerConfig> staticServers,
        IReadOnlyList<McpServerConfigHttp> bridgedServers,
        string bridgeBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(staticServers);
        ArgumentNullException.ThrowIfNull(bridgedServers);
        ArgumentNullException.ThrowIfNull(bridgeBaseUrl);
        var document = new List<KeyValuePair<string, object?>>
        {
            Pair("analytics", new List<KeyValuePair<string, object?>> { Pair("enabled", false) }),
        };
        document.AddRange(ConfigOptions(mode, goals, autoReview));
        foreach (var server in staticServers)
        {
            document.Add(Pair(Toml.TablePath("mcp_servers", server.Name), AgentMcp.CodexServerTable(server)));
        }

        foreach (var server in bridgedServers)
        {
            document.Add(Pair(Toml.TablePath("mcp_servers", server.Name), AgentMcp.CodexServerTable(server, ApiKeyEnvVar)));
        }

        document.Add(Pair("preferred_auth_method", "apikey"));
        document.Add(Pair("model_provider", ProviderId));
        document.Add(Pair(Toml.TablePath("model_providers", ProviderId), new List<KeyValuePair<string, object?>>
        {
            Pair("name", "OpenAI Proxy"),
            Pair("base_url", $"{bridgeBaseUrl}/v1"),
            Pair("env_key", ApiKeyEnvVar),
            Pair("wire_api", "responses"),
            Pair("stream_idle_timeout_ms", StreamIdleTimeoutMs),
        }));
        return Toml.Write(document);
    }

    private static KeyValuePair<string, object?> Pair(string key, object? value) => KeyValuePair.Create(key, value);

    private static long[] VersionParts(string version) =>
        version.Split('.').Take(3).Select(part => long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new ArgumentException($"Invalid Codex CLI version '{version}'.", nameof(version))).ToArray();

    private static InvalidOperationException AutoReviewTooOld(string version) => new(
        $"auto_review requires Codex CLI >= {AutoReviewMinVersion} (found {version}). Pass version='latest' (or an explicit newer version) to codex_cli().");
}
