using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Examples.EvalsInEval;

using SweClaudeCode = InspectAzureAI.Swe.ClaudeCode.ClaudeCode;

/// <summary>
/// Port of <c>examples/evals_in_eval/claude.py</c> <c>claude_code</c>: the agent that runs the Claude Code CLI installed
/// in the sandbox image (<c>claude --print --dangerously-skip-permissions --model &lt;model&gt;
/// [--append-system-prompt &lt;system messages&gt;] &lt;user messages&gt;</c>) against a sandbox agent bridge serving the
/// sample's model, and returns the bridge's reconstructed conversation as the agent state. Deviation: the exec, the
/// environment and the bridge come from <see cref="SweClaudeCode.Agent"/> (<c>src/InspectAzureAI.Swe</c>) rather than
/// a hand-rolled <c>sandbox().exec</c>, so the CLI also gets <c>--session-id</c>, <c>--output-format stream-json
/// --verbose</c> and a seeded <c>~/.claude/settings.json</c>, its JSONL output is recorded on the transcript, and
/// authentication is the bridge's per-instance token in <c>ANTHROPIC_AUTH_TOKEN</c> (the C# bridge listens on the
/// host and answers 401 without it) instead of Python's placeholder <c>ANTHROPIC_API_KEY</c>. <c>INSPECT_EVAL_MODEL</c>
/// (Python: <c>str(get_model())</c>) defaults to <see cref="DefaultInspectEvalModel"/> because the inner
/// <c>inspect</c> CLI is the Python one: an Anthropic provider spec makes its SDK follow <c>ANTHROPIC_BASE_URL</c>
/// and <c>ANTHROPIC_AUTH_TOKEN</c> to the same bridge, which maps any model name to the sample's model.
/// </summary>
public static class Claude
{
    /// <summary>Python's <c>@agent def claude_code</c> registry name.</summary>
    public const string AgentName = "claude_code";

    /// <summary>The <c>INSPECT_EVAL_MODEL</c> handed to the inner Python <c>inspect</c> runs (see the class summary).</summary>
    public const string DefaultInspectEvalModel = "anthropic/inspect";

    /// <summary>The description the wrapped agent is registered with.</summary>
    public const string Description = "Runs the Claude Code CLI in the sandbox through the sandbox agent bridge (port of examples/evals_in_eval/claude.py).";

    /// <summary>
    /// Port of <c>claude_code()</c>. <paramref name="inspectEvalModel"/> is the <c>INSPECT_EVAL_MODEL</c> exported to
    /// the CLI (and so to the <c>inspect eval</c> subprocesses it starts).
    /// </summary>
    public static AgentDef ClaudeCode(string inspectEvalModel = DefaultInspectEvalModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inspectEvalModel);
        var inner = SweClaudeCode.Agent(Options(inspectEvalModel));
        return new AgentDef(AgentName, Description, inner.Execute);
    }

    /// <summary>
    /// The options behind <see cref="ClaudeCode"/>: the image's own <c>claude</c> (<c>Version = "sandbox"</c>, so nothing
    /// is downloaded, as Python simply execs <c>claude</c>), no permission mode (<c>--dangerously-skip-permissions</c>),
    /// and the extra environment variable of <c>claude.py</c>.
    /// </summary>
    public static ClaudeCodeOptions Options(string inspectEvalModel = DefaultInspectEvalModel) => new()
    {
        Version = "sandbox",
        PermissionMode = null,
        Env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // set the environment variable so Claude Code doesn't need to pass the --model argument
            ["INSPECT_EVAL_MODEL"] = inspectEvalModel,
        },
    };
}
