using System.Text.Encodings.Web;
using System.Text.Json;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.ClaudeCode;

/// <summary>
/// The Claude Code MCP configuration (port of <c>resolve_mcp_servers</c> and <c>resolve_allowed_mcp_tools</c>,
/// <c>claude_code.py:318-331, 600-650</c>). Python passes the <c>{"mcpServers": {…}}</c> JSON inline to
/// <c>--mcp-config</c>. Bridged servers' configs carry the bridge token, so this port writes the same JSON to a
/// 0600 file in the sandbox and passes its path (deviation D-C5).
/// </summary>
public static class ClaudeCodeMcp
{
    /// <summary>The sandbox directory holding per-session MCP configuration files.</summary>
    public const string ConfigDirectory = "/tmp/.inspect-claude-code";

    /// <summary>
    /// The script, run as root with <see cref="ConfigDirectory"/> as <c>$1</c>, that prepares the directory: created when
    /// missing, then made a sticky drop directory (mode 1733) in which any user can create a file but no user can list
    /// the directory or replace or remove another user's file. It refuses a symlink and a directory root does not own,
    /// since another sandbox user could swap either for a link after the check, and it never touches a file inside.
    /// </summary>
    public const string PrepareDirectoryScript =
        "mkdir -p \"$1\" && [ ! -L \"$1\" ] && [ \"$(stat -c %u \"$1\")\" = 0 ] && chmod 1733 \"$1\" "
        + "|| { echo \"refusing to write the MCP configuration: $1 is not a directory owned by root\" >&2; exit 1; }";

    /// <summary>
    /// The script, run as the agent's user with the configuration on stdin and the file as <c>$1</c>, that writes it: into
    /// a temporary file that <c>mktemp</c> creates exclusively and private (<c>umask 077</c>, mode 0600), then renamed
    /// onto <c>$1</c>. The token is never readable by another user, and whatever already sits at <c>$1</c>, a planted
    /// symlink included, is replaced by the rename (or the rename fails) rather than opened and written through.
    /// </summary>
    public const string WriteConfigScript =
        "umask 077 && tmp=\"$(mktemp \"$1.XXXXXX\")\" && { cat > \"$tmp\" && mv -f \"$tmp\" \"$1\" || { rm -f \"$tmp\"; exit 1; }; }";

    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The configuration file for a Claude Code session: <c>/tmp/.inspect-claude-code/mcp-{sessionId}.json</c>.</summary>
    public static string ConfigPath(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        if (sessionId.Contains('/', StringComparison.Ordinal) || sessionId.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("The session id must be a single path segment.", nameof(sessionId));
        }

        return $"{ConfigDirectory}/mcp-{sessionId}.json";
    }

    /// <summary>
    /// The compact <c>{"mcpServers": {…}}</c> JSON (<see cref="AgentMcp.ClaudeMcpConfig"/>): static servers first, then
    /// bridged ones, with a later server replacing an earlier one of the same name.
    /// </summary>
    public static string ConfigJson(IReadOnlyList<McpServerConfig> staticServers, IReadOnlyList<McpServerConfig> bridgedServers)
    {
        ArgumentNullException.ThrowIfNull(staticServers);
        ArgumentNullException.ThrowIfNull(bridgedServers);
        return AgentMcp.ClaudeMcpConfig(staticServers.Concat(bridgedServers)).ToJsonString(CompactJson);
    }

    /// <summary>The CLI arguments that load the configuration file: <c>["--mcp-config", path]</c>.</summary>
    public static IReadOnlyList<string> ConfigArgs(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return ["--mcp-config", path];
    }

    /// <summary>
    /// Port of <c>resolve_allowed_mcp_tools</c>: the static servers' allow rules only when
    /// <paramref name="allowlistMcpTools"/> is set, then the bridged servers' rules, always.
    /// </summary>
    public static IReadOnlyList<string> AllowedTools(IReadOnlyList<McpServerConfig> staticServers, IReadOnlyList<McpServerConfig> bridgedServers, bool allowlistMcpTools) =>
        AgentMcp.AllowedMcpTools(staticServers, bridgedServers, allowlistMcpTools);
}
