using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Swe.CodexCli;

/// <summary>
/// Byte-exact port of inspect_swe <c>_codex_cli/_events/toolview.py</c>: custom <see cref="ToolCallContent"/> views for
/// Codex's built-in tools, whose <c>{{arg}}</c> placeholders the log viewer fills from the call's arguments. Only
/// tools whose payload renders poorly as a function call get one; <c>close_agent</c> and <c>resume_agent</c> keep the
/// default rendering.
/// </summary>
public static class CodexCliToolViews
{
    /// <summary>
    /// The view for <paramref name="function"/>, or null for other tools (or when the key argument is missing).
    /// <c>wait_agent</c> renders each target thread id with its nickname from <paramref name="nicknames"/>.
    /// </summary>
    public static ToolCallContent? For(string function, JsonObject arguments, IReadOnlyDictionary<string, string> nicknames)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(nicknames);
        return function switch
        {
            CodexCliDetection.WaitAgent => WaitAgentView(arguments, nicknames),
            "exec_command" => arguments.ContainsKey("cmd") ? Markdown("exec_command", "``````bash\n{{cmd}}\n``````\n") : null,
            CodexCliDetection.SpawnAgent => SpawnAgentView(arguments),
            "apply_patch" => ApplyPatchView(arguments),
            "web_search" => arguments.ContainsKey("query") ? Markdown("web_search", "{{query}}") : null,
            "send_input" => CodexCliDetection.Truthy(arguments["message"]) ? Markdown("send_input", "{{message}}") : null,
            _ => null,
        };
    }

    private static ToolCallContent Markdown(string title, string content) => new("markdown", content) { Title = title };

    private static ToolCallContent? SpawnAgentView(JsonObject arguments)
    {
        if (!arguments.ContainsKey("message"))
        {
            return null;
        }

        var agentType = CodexCliDetection.Truthy(arguments["agent_type"]) ? CodexCliDetection.PyStr(arguments["agent_type"]) : "";
        return Markdown(agentType.Length > 0 ? $"spawn_agent: {agentType}" : "spawn_agent", "{{message}}");
    }

    private static ToolCallContent? ApplyPatchView(JsonObject arguments)
    {
        var key = arguments.ContainsKey("input") ? "input" : arguments.ContainsKey("patch") ? "patch" : null;
        return key is null ? null : Markdown("apply_patch", "``````diff\n{{" + key + "}}\n``````\n");
    }

    private static ToolCallContent? WaitAgentView(JsonObject arguments, IReadOnlyDictionary<string, string> nicknames)
    {
        if (arguments["targets"] is not JsonArray { Count: > 0 } targets)
        {
            return null;
        }

        var lines = string.Join("\n", targets.Select(target => "- " + TargetLabel(target, nicknames)));
        var suffix = TimeoutMilliseconds(arguments["timeout_ms"]) is { } timeout
            ? $"\n\n_timeout: {CodexCliText.Invariant(CodexCliText.FloorDiv(timeout, 1000))}s_"
            : "";
        return Markdown("wait_agent", lines + suffix);
    }

    private static string TargetLabel(JsonNode? target, IReadOnlyDictionary<string, string> nicknames)
    {
        if (CodexCliDetection.StringOf(target) is not { } id)
        {
            return $"`{CodexCliDetection.PyStr(target)}`";
        }

        return nicknames.TryGetValue(id, out var nickname) && nickname.Length > 0 ? $"{nickname} — `{id}`" : $"`{id}`";
    }

    /// <summary>Python <c>int(timeout_ms)</c> when <c>isinstance(timeout_ms, (int, float))</c> (a bool counts as an int).</summary>
    private static long? TimeoutMilliseconds(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.Number when long.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var whole) => whole,
            JsonValueKind.Number when CodexCliDetection.NumberOf(value) is { } number => (long)Math.Truncate(number),
            _ => null,
        };
    }
}
