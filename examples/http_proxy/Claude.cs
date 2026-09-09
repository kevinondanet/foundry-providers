using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Examples.HttpProxy;

using SweClaudeCode = InspectAzureAI.Swe.ClaudeCode.ClaudeCode;

/// <summary>
/// Port of <c>examples/http_proxy/claude.py</c> <c>claude_code</c>: the agent that runs the Claude Code CLI installed in
/// the sandbox image (<c>claude --print --dangerously-skip-permissions --model &lt;model&gt; [--append-system-prompt
/// &lt;system messages&gt;] &lt;user messages&gt;</c>) against a sandbox agent bridge serving the sample's model, with the
/// fake <c>FUTUREMODEL_API_KEY</c> in its environment for the agent to discover, and returns the bridge's
/// reconstructed conversation as the agent state. Deviation: the exec, the environment and the bridge come from
/// <see cref="SweClaudeCode.Agent"/> (<c>src/InspectAzureAI.Swe</c>) rather than a hand-rolled <c>sandbox().exec</c>,
/// so the CLI also gets <c>--session-id</c>, <c>--output-format stream-json --verbose</c> and a seeded
/// <c>~/.claude/settings.json</c>, its JSONL output is recorded on the transcript, and authentication is the bridge's
/// per-instance token in <c>ANTHROPIC_AUTH_TOKEN</c> (the C# bridge listens on the host and answers 401 without it)
/// instead of Python's placeholder <c>ANTHROPIC_API_KEY</c>.
/// </summary>
public static class Claude
{
    /// <summary>Python's <c>@agent def claude_code</c> registry name.</summary>
    public const string AgentName = "claude_code";

    /// <summary>The fake API key of <c>claude.py</c>, for the agent to discover and use.</summary>
    public const string FutureModelApiKey = "fm-fake-key-for-demo";

    /// <summary>The description the wrapped agent is registered with.</summary>
    public const string Description = "Runs the Claude Code CLI in the sandbox through the sandbox agent bridge (port of examples/http_proxy/claude.py).";

    /// <summary>Port of <c>claude_code()</c>.</summary>
    public static AgentDef ClaudeCode()
    {
        var inner = SweClaudeCode.Agent(Options());
        return new AgentDef(AgentName, Description, inner.Execute);
    }

    /// <summary>
    /// The options behind <see cref="ClaudeCode"/>: the image's own <c>claude</c> (<c>Version = "sandbox"</c>, so nothing
    /// is downloaded, as Python simply execs <c>claude</c>), no permission mode (<c>--dangerously-skip-permissions</c>),
    /// and the extra environment variable of <c>claude.py</c>.
    /// </summary>
    public static ClaudeCodeOptions Options() => new()
    {
        Version = "sandbox",
        PermissionMode = null,
        Env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Fake API key for the agent to discover and use
            ["FUTUREMODEL_API_KEY"] = FutureModelApiKey,
        },
    };
}
