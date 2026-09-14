namespace InspectAzureAI.Swe.ClaudeCode;

/// <summary>
/// Port of the <c>agent_env</c> of inspect_swe 0.2.70 <c>_claude_code/claude_code.py:354-367</c>: the environment of
/// the Claude Code subprocess. The bridge address and token come from the host-side <c>SandboxAgentBridge</c> rather
/// than Python's in-sandbox <c>localhost</c> proxy and literal dummy token. The blocking-MCP and auto-memory variables
/// go beyond 0.2.70 (deviation D-C8). Everything else is verbatim, and caller values win.
/// </summary>
public static class ClaudeCodeEnv
{
    /// <summary>Fallback api key when a caller removes <c>ANTHROPIC_AUTH_TOKEN</c> (<c>claude_code.py:374</c>).</summary>
    public const string DefaultApiKey = "dummy-key-for-bridge";

    /// <summary>
    /// Tokens Claude Code treats as an explicit "false". <c>MCP_CONNECTION_NONBLOCKING</c> has inverted polarity —
    /// it feeds <c>nonBlocking = !isFalsy(value)</c> — so only one of these makes MCP connection block.
    /// </summary>
    internal static readonly IReadOnlySet<string> FalsyValues = new HashSet<string>(["0", "false", "no", "off"], StringComparer.Ordinal);

    /// <summary>Tokens Claude Code treats as an explicit "true" (<c>CLAUDE_CODE_DISABLE_AUTO_MEMORY</c> is a truthy check).</summary>
    internal static readonly IReadOnlySet<string> TruthyValues = new HashSet<string>(["1", "true", "yes", "on"], StringComparer.Ordinal);

    /// <summary>Make MCP connection blocking with budgets that cover a slow sandbox (upstream <c>BLOCKING_MCP_ENV</c>, newer than 0.2.70).</summary>
    public static readonly IReadOnlyDictionary<string, string> BlockingMcpEnv = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MCP_CONNECTION_NONBLOCKING"] = "false",
        ["MCP_TIMEOUT"] = "300000",
        ["MCP_CONNECT_TIMEOUT_MS"] = "300000",
    };

    /// <summary>Turn off auto-memory: a sample's sandbox has no next session for it to pay off in (<c>DISABLE_AUTO_MEMORY_ENV</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> DisableAutoMemoryEnv = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1",
    };

    /// <summary>Port of <c>claude_code_agent_env</c>; the result keeps Python's source order.</summary>
    public static IReadOnlyDictionary<string, string> Build(string bridgeBaseUrl, string authToken, ClaudeCodeModels models, IReadOnlyDictionary<string, string>? env = null)
    {
        ArgumentNullException.ThrowIfNull(bridgeBaseUrl);
        ArgumentNullException.ThrowIfNull(authToken);
        ArgumentNullException.ThrowIfNull(models);
        var result = new OrderedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["ANTHROPIC_BASE_URL"] = bridgeBaseUrl,
            ["ANTHROPIC_AUTH_TOKEN"] = authToken,
            ["ANTHROPIC_MODEL"] = models.Presented,
            ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = models.Opus,
            ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = models.Sonnet,
            ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = models.Haiku,
            ["CLAUDE_CODE_SUBAGENT_MODEL"] = models.Subagent,
            ["ANTHROPIC_SMALL_FAST_MODEL"] = models.Haiku,
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
            ["CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS"] = "1",
            ["IS_SANDBOX"] = "1",
            ["MCP_CONNECTION_NONBLOCKING"] = BlockingMcpEnv["MCP_CONNECTION_NONBLOCKING"],
            ["MCP_TIMEOUT"] = BlockingMcpEnv["MCP_TIMEOUT"],
            ["MCP_CONNECT_TIMEOUT_MS"] = BlockingMcpEnv["MCP_CONNECT_TIMEOUT_MS"],
            ["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = DisableAutoMemoryEnv["CLAUDE_CODE_DISABLE_AUTO_MEMORY"],
        };
        if (env is not null)
        {
            foreach (var (name, value) in env)
            {
                result[name] = value;
            }
        }

        return result;
    }
}
