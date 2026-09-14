using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Swe.CodexCli;

/// <summary>A <c>spawn_agent</c> call in a parent's output (port of <c>SpawnedAgent</c>).</summary>
/// <param name="CallId">The spawn tool call id.</param>
/// <param name="AgentType">The V1 <c>agent_type</c> (<c>agent</c> when absent).</param>
/// <param name="Message">The spawn prompt (encrypted under Multi-Agent V2).</param>
/// <param name="ReasoningEffort">The requested reasoning effort, when given.</param>
/// <param name="TaskName">The Multi-Agent V2 task name (for example <c>write_fizzbuzz</c>); null under V1.</param>
public sealed record CodexSpawnedAgent(string CallId, string AgentType, string Message, string? ReasoningEffort, string? TaskName = null)
{
    /// <summary>The span name: the V2 task name, else the V1 agent type.</summary>
    public string Name => TaskName ?? AgentType;
}

/// <summary>The thread id and nickname a <c>spawn_agent</c> result carries (port of <c>SpawnResult</c>).</summary>
/// <param name="AgentId">The V1 <c>agent_id</c>, or the V2 absolute task path (<c>/root/&lt;name&gt;</c>).</param>
/// <param name="Nickname">Codex's friendly per-agent name, when given.</param>
public sealed record CodexSpawnResult(string AgentId, string? Nickname);

/// <summary>
/// Line-by-line port of inspect_swe <c>_codex_cli/_events/detection.py</c>: pure readers of the chat messages and tool
/// calls a bridged model event carries, from which the consumer rebuilds sub-agent spans and spots compaction. Python
/// sets are returned as lists of distinct values in first-seen order. The raw <c>agent_message</c> item Python keeps
/// in content <c>internal</c> is read from <see cref="ChatMessage.Metadata"/> (deviation D-R8).
/// </summary>
public static class CodexCliDetection
{
    public const string SpawnAgent = "spawn_agent";

    public const string CloseAgent = "close_agent";

    public const string WaitAgent = "wait_agent";

    /// <summary>The prompt Codex appends as a user message for a local compaction request (<c>codex-rs compact.rs:70-82</c>).</summary>
    public const string CompactionMarker = "You are performing a CONTEXT CHECKPOINT COMPACTION.";

    /// <summary>The tag wrapping a sub-agent's completion notice in a parent's input.</summary>
    public const string SubagentNotificationTag = "<subagent_notification>";

    /// <summary>The metadata key the Responses bridge stores a raw <c>agent_message</c> item under.</summary>
    public const string AgentMessageMetadataKey = "agent_message";

    private const string SubagentNotificationCloseTag = "</subagent_notification>";

    /// <summary>Port of <c>agent_message_recipients</c>: the non-empty <c>recipient</c> of every agent_message item in the input.</summary>
    public static IReadOnlyList<string> AgentMessageRecipients(IReadOnlyList<ChatMessage> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var recipients = new Distinct();
        foreach (var item in AgentMessageItems(input))
        {
            if (StringOf(item["recipient"]) is { Length: > 0 } recipient)
            {
                recipients.Add(recipient);
            }
        }

        return recipients.Values;
    }

    /// <summary>Port of <c>final_answer_authors</c>: authors of agent_message items with an <c>input_text</c> part starting (after whitespace) with <c>Message Type: FINAL_ANSWER</c>.</summary>
    public static IReadOnlyList<string> FinalAnswerAuthors(IReadOnlyList<ChatMessage> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var authors = new Distinct();
        foreach (var item in AgentMessageItems(input))
        {
            if (StringOf(item["author"]) is not { Length: > 0 } author || item["content"] is not JsonArray parts)
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (part is JsonObject obj
                    && StringOf(obj["type"]) == "input_text"
                    && StringOf(obj["text"]) is { } text
                    && text.TrimStart().StartsWith("Message Type: FINAL_ANSWER", StringComparison.Ordinal))
                {
                    authors.Add(author);
                    break;
                }
            }
        }

        return authors.Values;
    }

    /// <summary>Port of <c>find_spawned_agents</c>: the <c>spawn_agent</c> calls with a non-empty string <c>message</c>.</summary>
    public static IReadOnlyList<CodexSpawnedAgent> FindSpawnedAgents(IReadOnlyList<ToolCall>? toolCalls)
    {
        var spawned = new List<CodexSpawnedAgent>();
        foreach (var call in toolCalls ?? [])
        {
            if (call.Function != SpawnAgent)
            {
                continue;
            }

            var args = call.Arguments ?? new JsonObject();
            if (StringOf(args["message"]) is not { Length: > 0 } message)
            {
                continue;
            }

            spawned.Add(new CodexSpawnedAgent(
                call.Id,
                Truthy(args["agent_type"]) ? PyStr(args["agent_type"]) : "agent",
                message,
                Truthy(args["reasoning_effort"]) ? PyStr(args["reasoning_effort"]) : null,
                Truthy(args["task_name"]) ? PyStr(args["task_name"]) : null));
        }

        return spawned;
    }

    /// <summary>Port of <c>find_close_targets</c>: the non-empty string <c>target</c> of each <c>close_agent</c> call.</summary>
    public static IReadOnlyList<string> FindCloseTargets(IReadOnlyList<ToolCall>? toolCalls)
    {
        var targets = new List<string>();
        foreach (var call in toolCalls ?? [])
        {
            if (call.Function == CloseAgent && StringOf(call.Arguments?["target"]) is { Length: > 0 } target)
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    /// <summary>
    /// Port of <c>spawn_result</c>: a <c>spawn_agent</c> tool result whose JSON text holds <c>agent_id</c> (V1) or, when
    /// that is falsy, <c>task_name</c> (V2) as a non-empty string, with an optional non-empty <c>nickname</c>.
    /// </summary>
    public static CodexSpawnResult? SpawnResult(ChatMessageTool message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Function != SpawnAgent || Loads(message.Text) is not JsonObject data)
        {
            return null;
        }

        var agentId = Truthy(data["agent_id"]) ? data["agent_id"] : data["task_name"];
        if (StringOf(agentId) is not { Length: > 0 } id)
        {
            return null;
        }

        return new CodexSpawnResult(id, StringOf(data["nickname"]) is { Length: > 0 } nickname ? nickname : null);
    }

    /// <summary>
    /// Port of <c>completed_thread_ids</c>: thread ids reported completed by <c>wait_agent</c>/<c>close_agent</c> results
    /// (<c>{"status": {"&lt;tid&gt;": {"completed": …}}}</c>) and by <c>&lt;subagent_notification&gt;</c> user messages
    /// (<c>{"agent_path": "&lt;tid&gt;", "status": {"completed": …}}</c>).
    /// </summary>
    public static IReadOnlyList<string> CompletedThreadIds(IReadOnlyList<ChatMessage> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var completed = new Distinct();
        foreach (var message in input)
        {
            switch (message)
            {
                case ChatMessageTool { Function: WaitAgent or CloseAgent } tool:
                    CollectStatusCompleted(Loads(tool.Text), completed);
                    break;
                case ChatMessageUser user when user.Text.Contains(SubagentNotificationTag, StringComparison.Ordinal):
                    CollectNotificationCompleted(user.Text, completed);
                    break;
            }
        }

        return completed.Values;
    }

    /// <summary>Port of <c>is_compaction_request</c>: whether any user message starts, after leading whitespace, with <see cref="CompactionMarker"/>.</summary>
    public static bool IsCompactionRequest(IReadOnlyList<ChatMessage> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.OfType<ChatMessageUser>().Any(message => message.Text.TrimStart().StartsWith(CompactionMarker, StringComparison.Ordinal));
    }

    /// <summary>Python truthiness of a JSON value (null, false, 0, <c>""</c>, <c>[]</c> and <c>{}</c> are false).</summary>
    internal static bool Truthy(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Count > 0,
        JsonArray array => array.Count > 0,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => NumberOf(value) is { } number && number != 0,
            _ => false,
        },
        _ => false,
    };

    /// <summary>
    /// The value of a JSON number, read from its JSON text so that values built from any CLR numeric type (which
    /// <c>TryGetValue&lt;double&gt;</c> refuses unless the type matches) and parsed values read alike.
    /// </summary>
    internal static double? NumberOf(JsonValue value) =>
        value.GetValueKind() == JsonValueKind.Number && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>Python <c>str()</c> of a JSON value: a string as is, <c>True</c>/<c>False</c>, <c>None</c>, a number's JSON text, and JSON text for containers.</summary>
    internal static string PyStr(JsonNode? node) => node switch
    {
        null => "None",
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.True => "True",
        JsonValue value when value.GetValueKind() == JsonValueKind.False => "False",
        _ => PythonJson.Dumps(node),
    };

    /// <summary>Port of <c>_loads</c>: the parsed JSON, or null when the text does not parse.</summary>
    internal static JsonNode? Loads(string? text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>Port of <c>_agent_message_items</c>: the raw agent_message items the bridge stored on user messages.</summary>
    private static IEnumerable<JsonObject> AgentMessageItems(IReadOnlyList<ChatMessage> input)
    {
        foreach (var message in input)
        {
            if (message is not ChatMessageUser { Metadata: { } metadata } || !metadata.TryGetValue(AgentMessageMetadataKey, out var carried))
            {
                continue;
            }

            switch (carried)
            {
                case JsonObject item:
                    yield return item;
                    break;
                case JsonElement { ValueKind: JsonValueKind.Object } element:
                    yield return JsonNode.Parse(element.GetRawText())!.AsObject();
                    break;
            }
        }
    }

    private static void CollectStatusCompleted(JsonNode? data, Distinct completed)
    {
        if (data is not JsonObject obj || obj["status"] is not JsonObject status)
        {
            return;
        }

        foreach (var (threadId, value) in status)
        {
            if (value is JsonObject entry && entry.ContainsKey("completed"))
            {
                completed.Add(threadId);
            }
        }
    }

    private static void CollectNotificationCompleted(string text, Distinct completed)
    {
        var payload = text
            .Replace(SubagentNotificationTag, "", StringComparison.Ordinal)
            .Replace(SubagentNotificationCloseTag, "", StringComparison.Ordinal)
            .Trim();
        if (Loads(payload) is JsonObject data
            && StringOf(data["agent_path"]) is { } threadId
            && data["status"] is JsonObject status
            && status.ContainsKey("completed"))
        {
            completed.Add(threadId);
        }
    }

    /// <summary>An insertion-ordered set of strings.</summary>
    private sealed class Distinct
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        private readonly List<string> _values = [];

        public IReadOnlyList<string> Values => _values;

        public void Add(string value)
        {
            if (_seen.Add(value))
            {
                _values.Add(value);
            }
        }
    }
}

/// <summary>Formatting helpers shared by the Codex event readers.</summary>
internal static class CodexCliText
{
    /// <summary>Python <c>len()</c> of a string: its Unicode scalar count.</summary>
    public static int CodePoints(string text) => text.EnumerateRunes().Count();

    /// <summary>Python <c>a // b</c> for integers (floor division).</summary>
    public static long FloorDiv(long a, long b)
    {
        var quotient = a / b;
        return (a % b != 0) && ((a < 0) != (b < 0)) ? quotient - 1 : quotient;
    }

    public static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
