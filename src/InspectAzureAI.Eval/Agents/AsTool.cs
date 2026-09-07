using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Agents;

/// <summary>Port of <c>agent/_as_tool.py</c>: <c>as_tool</c>, <c>agent_tool_name</c> and <c>sanitize_tool_name</c>.</summary>
public static partial class Agents
{
    /// <summary>
    /// Port of <c>as_tool(agent, description, limits, max_output)</c>: a tool taking an <c>input</c> string
    /// that runs the agent on a fresh conversation (inside an "agent" span, under <paramref name="limits"/>)
    /// and returns the content of its output message (else of its last assistant message, else ""). A limit
    /// exceeded inside the agent surfaces as the tool's error. <paramref name="maxOutput"/> defaults to 0 (no
    /// truncation) because the agent's report is the payload the caller asked for; null defers to the caller.
    /// Python also exposes the agent's own parameters as tool arguments; this port's agents take none.
    /// </summary>
    public static ToolDef AsTool(AgentDef agent, string? description = null, AgentLimits? limits = null, int? maxOutput = 0)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var name = AgentToolName(agent);
        var resolvedDescription = ResolveAgentDescription(agent, name, description);
        var parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["input"] = ToolParam.Of("string", "Input message.") },
            Required = ["input"],
        };

        async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken)
        {
            if (arguments["input"] is not JsonValue value || !value.TryGetValue<string>(out var input))
            {
                throw new ToolParsingError("Parameter 'input' must be a string.");
            }

            var state = new AgentState([new ChatMessageUser(input) { Source = "input" }]);
            using var scope = AgentLimitScope.Apply(limits, cancellationToken);
            using var span = SampleContext.Current?.Transcript.Span(agent.Name, "agent");
            try
            {
                state = await agent.Execute(state, scope.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (scope.TimedOut)
            {
                throw scope.TimeLimitExceeded();
            }

            if (!state.Output.Empty)
            {
                return ToolResultFromContent(state.Output.Message.Content);
            }

            return state.Messages.LastOrDefault() is ChatMessageAssistant last ? ToolResultFromContent(last.Content) : "";
        }

        return new ToolDef(name, resolvedDescription, parameters, ExecuteAsync) { MaxOutput = maxOutput };
    }

    /// <summary>
    /// Port of <c>agent_tool_name</c>: the agent's name sanitized to a tool name, or "agent" when nothing usable
    /// remains (Python falls back to the registry name first, which this port's agents do not have).
    /// </summary>
    public static string AgentToolName(AgentDef agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var sanitized = SanitizeToolName(agent.Name);
        return sanitized.Length > 0 ? sanitized : "agent";
    }

    /// <summary>Port of <c>sanitize_tool_name</c>: trimmed, lower-cased, whitespace runs to <c>_</c>, anything but <c>[a-z0-9_-]</c> dropped.</summary>
    public static string SanitizeToolName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var lowered = name.Trim().ToLowerInvariant();
        var underscored = Regex.Replace(lowered, @"\s+", "_");
        return Regex.Replace(underscored, "[^a-z0-9_-]", "");
    }

    /// <summary>Port of the <c>tool_result_content</c> conversion: string content stays text, content lists pass through untouched.</summary>
    internal static ToolResult ToolResultFromContent(MessageContent content) =>
        content.IsString ? content.Text! : ToolResult.FromContents(content.Items!);

    /// <summary>Port of the description resolution of <c>agent_tool_info</c>: the argument, then the agent's own description, else an error.</summary>
    internal static string ResolveAgentDescription(AgentDef agent, string toolName, string? description)
    {
        var resolved = string.IsNullOrEmpty(description) ? agent.Description : description;
        if (string.IsNullOrEmpty(resolved))
        {
            throw new ArgumentException(
                $"Description not provided for agent function '{toolName}'. Provide a description either via the AgentDef, "
                + "or the description argument to AsTool() or Handoff().",
                nameof(agent));
        }

        return resolved;
    }
}
