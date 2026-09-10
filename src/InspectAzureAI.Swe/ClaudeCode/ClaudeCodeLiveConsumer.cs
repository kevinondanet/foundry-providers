using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Swe.ClaudeCode;

/// <summary>
/// Port of inspect_swe <c>_claude_code/_events/live_consumer.py</c> <c>LiveConsumer</c>: the bridge's model event sink
/// and the consumer of Claude Code's stream-json output, which together attribute the transcript to sub-agents.
/// </summary>
/// <remarks>
/// <para>
/// Claude Code's sub-agents are opaque in its JSONL (no parent tool-use id on their model calls), so when a completed
/// generation calls <c>Task</c> or <c>Agent</c> (<see cref="OnModelEvent"/>, which runs before the bridge answers the
/// CLI) the consumer opens an <c>agent-{tool_use_id}</c> span and remembers the prompt. A later bridged call whose
/// first user message contains exactly one remembered prompt (of at least <see cref="MinPromptLength"/> characters) is
/// recorded in that sub-agent's span; any other call goes to the outer span, the transcript's current span at the time.
/// </para>
/// <para>
/// This port records a model event once, at completion (deviation D-C1), so attribution and the tool views of
/// <see cref="ClaudeCodeToolView"/> are applied in <see cref="OnRecording"/>, just before the event lands. A sub-agent span
/// closes when the parent's next request carries its <c>tool_result</c> (deviation D-C2, recorded before that request's
/// event), when the JSONL shows the <c>tool_result</c> (idempotent), or at <see cref="Reset"/>. JSONL is only
/// available after the CLI exits (deviation D-C4), so <c>compact_boundary</c> compaction events are recorded then,
/// still attributed to the right span.
/// </para>
/// <para>Bridged generations run concurrently, so all state is guarded by one lock.</para>
/// </remarks>
public sealed class ClaudeCodeLiveConsumer : IModelEventSink
{
    /// <summary>Port of <c>_MIN_PROMPT_LENGTH</c>: shorter prompts could match unrelated text, so they never attribute.</summary>
    public const int MinPromptLength = 16;

    private readonly Transcript _transcript;

    private readonly Lock _sync = new();

    /// <summary>Open sub-agent spans: tool use id to span id, in opening order.</summary>
    private readonly OrderedDictionary<string, string> _openAgents = new(StringComparer.Ordinal);

    /// <summary>Running sub-agents: tool use id to their <c>Task</c>/<c>Agent</c> prompt.</summary>
    private readonly OrderedDictionary<string, string> _pending = new(StringComparer.Ordinal);

    /// <summary>Every span opened this attempt, closed or not, so compaction can be attributed after an early close.</summary>
    private readonly Dictionary<string, string> _agentSpans = new(StringComparer.Ordinal);

    private StopReason? _lastStopReason;

    /// <summary>Creates a consumer recording span and compaction events on <paramref name="transcript"/>.</summary>
    public ClaudeCodeLiveConsumer(Transcript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        _transcript = transcript;
    }

    /// <summary>Port of <c>last_stop_reason</c>: the stop reason of the latest completed generation this attempt.</summary>
    public StopReason? LastStopReason
    {
        get
        {
            lock (_sync)
            {
                return _lastStopReason;
            }
        }
    }

    /// <summary>Port of <c>outer_span_id</c>: resolved at call time, never captured.</summary>
    private string? OuterSpanId => _transcript.CurrentSpanId;

    /// <summary>
    /// The <c>on_pending</c> half, run just before the event is recorded: adds tool views to the output's calls that
    /// lack one, records a span end for each open sub-agent whose <c>tool_result</c> is in the input, and attributes the
    /// event to a span (<see cref="TranscriptEvent.SpanId"/> is kept when already set).
    /// </summary>
    public ModelEvent OnRecording(ModelEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var output = WithViews(e.Output);
        lock (_sync)
        {
            foreach (var message in e.Input)
            {
                if (message is ChatMessageTool { ToolCallId: { } toolCallId } && _openAgents.Remove(toolCallId, out var spanId))
                {
                    _pending.Remove(toolCallId);
                    _transcript.Add(new SpanEndEvent(spanId));
                }
            }

            return e with { SpanId = e.SpanId ?? Attribute(e.Input), Output = output };
        }
    }

    /// <summary>
    /// Port of <c>on_complete</c>: remembers the stop reason, then opens a span for each new <c>Task</c> or <c>Agent</c>
    /// call with a non-empty string <c>prompt</c>. It runs before the bridge answers the CLI, so the span and prompt
    /// are registered before the sub-agent's first request can arrive.
    /// </summary>
    public void OnModelEvent(ModelEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Output.Choices.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            // A failed attempt carries a placeholder output (Python: empty choices), which must not count as completed.
            if (e.Error is null)
            {
                _lastStopReason = e.Output.StopReason;
            }

            if (e.Output.Message.ToolCalls is not { Count: > 0 } calls)
            {
                return;
            }

            var parentSpanId = e.SpanId ?? OuterSpanId;
            foreach (var call in calls)
            {
                if (call.Function is not ("Task" or "Agent")
                    || !TryGetString(call.Arguments["prompt"], out var prompt)
                    || prompt.Length == 0
                    || _openAgents.ContainsKey(call.Id))
                {
                    continue;
                }

                var spanId = $"agent-{call.Id}";
                _openAgents[call.Id] = spanId;
                _pending[call.Id] = prompt;
                _agentSpans[call.Id] = spanId;

                // args.get("subagent_type") or args.get("name") or "agent"
                var nameNode = ClaudeCodeToolView.Truthy(call.Arguments["subagent_type"]) ? call.Arguments["subagent_type"]
                    : ClaudeCodeToolView.Truthy(call.Arguments["name"]) ? call.Arguments["name"]
                    : null;
                var name = nameNode is null ? "agent" : ClaudeCodeToolView.Str(nameNode);
                var description = call.Arguments["description"];
                _transcript.Add(new SpanBeginEvent(spanId, name, "agent", parentSpanId)
                {
                    Metadata = ClaudeCodeToolView.Truthy(description)
                        ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["description"] = MetadataValue(description) }
                        : null,
                });
            }
        }
    }

    /// <summary>
    /// Port of <c>process_jsonl_line</c>. A <c>user</c> line's <c>tool_result</c> blocks close their sub-agent spans.
    /// A <c>system</c>/<c>compact_boundary</c> line records a <see cref="CompactionEvent"/>, reading the metadata from
    /// <c>compactMetadata.preTokens</c> or <c>compact_metadata.pre_tokens</c> (deviation D-C3). Other lines are ignored.
    /// </summary>
    public void ProcessJsonlLine(JsonNode raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw is not JsonObject line || !TryGetString(line["type"], out var type))
        {
            return;
        }

        if (type == "user")
        {
            HandleUser(line);
        }
        else if (type == "system" && TryGetString(line["subtype"], out var subtype) && subtype == "compact_boundary")
        {
            HandleCompactBoundary(line);
        }
    }

    /// <summary>Port of <c>reset</c>: ends every open sub-agent span (newest first) and clears the attempt's state.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            var open = _openAgents.Values.ToArray();
            for (var i = open.Length - 1; i >= 0; i--)
            {
                _transcript.Add(new SpanEndEvent(open[i]));
            }

            _openAgents.Clear();
            _pending.Clear();
            _agentSpans.Clear();
            _lastStopReason = null;
        }
    }

    /// <summary>Port of <c>_attribute</c> (called under the lock).</summary>
    private string? Attribute(IReadOnlyList<ChatMessage> input)
    {
        if (_pending.Count == 0)
        {
            return OuterSpanId;
        }

        var userText = FirstUserText(input);
        if (string.IsNullOrEmpty(userText))
        {
            return OuterSpanId;
        }

        string? match = null;
        var matches = 0;
        foreach (var (toolUseId, prompt) in _pending)
        {
            // len() counts code points, not UTF-16 units
            if (prompt.EnumerateRunes().Count() >= MinPromptLength && userText.Contains(prompt, StringComparison.Ordinal))
            {
                matches++;
                match = toolUseId;
            }
        }

        return matches == 1 && _openAgents.TryGetValue(match!, out var spanId) ? spanId : OuterSpanId;
    }

    /// <summary>Port of <c>_first_user_text</c>: the text of the first message after leading system messages, when it is a user message.</summary>
    private static string? FirstUserText(IReadOnlyList<ChatMessage> input)
    {
        foreach (var message in input)
        {
            if (message is ChatMessageSystem)
            {
                continue;
            }

            return message is ChatMessageUser user ? user.Text : null;
        }

        return null;
    }

    /// <summary>Port of <c>_handle_user</c>.</summary>
    private void HandleUser(JsonObject line)
    {
        if (line["message"] is not JsonObject message || message["content"] is not JsonArray content)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var node in content)
            {
                if (node is not JsonObject block
                    || !TryGetString(block["type"], out var blockType)
                    || blockType != "tool_result"
                    || !TryGetString(block["tool_use_id"], out var toolUseId)
                    || toolUseId.Length == 0)
                {
                    continue;
                }

                _pending.Remove(toolUseId);
                if (_openAgents.Remove(toolUseId, out var spanId))
                {
                    _transcript.Add(new SpanEndEvent(spanId));
                }
            }
        }
    }

    /// <summary>Port of <c>_handle_compact_boundary</c>; the parent span is looked up among every span opened this attempt.</summary>
    private void HandleCompactBoundary(JsonObject line)
    {
        lock (_sync)
        {
            var spanId = TryGetString(line["parent_tool_use_id"], out var parent) && parent.Length > 0 && _agentSpans.TryGetValue(parent, out var agentSpan)
                ? agentSpan
                : OuterSpanId;
            var meta = line["compactMetadata"] as JsonObject ?? line["compact_metadata"] as JsonObject ?? new JsonObject();
            var trigger = meta["trigger"] is { } triggerNode ? MetadataValue(triggerNode) : "auto";
            var content = TryGetString(line["content"], out var text) && text.Length > 0 ? text : "Conversation compacted";
            _transcript.Add(new CompactionEvent
            {
                Source = "claude_code",
                TokensBefore = IntOf(meta["preTokens"]) ?? IntOf(meta["pre_tokens"]),
                SpanId = spanId,
                Metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { ["trigger"] = trigger, ["content"] = content },
            });
        }
    }

    /// <summary>Tool views for the first choice's calls that have none (Python fills <c>tc.view</c> in place).</summary>
    private static ModelOutput WithViews(ModelOutput output)
    {
        if (output.Choices.Count == 0 || output.Choices[0].Message.ToolCalls is not { Count: > 0 } calls)
        {
            return output;
        }

        var changed = false;
        var updated = new List<ToolCall>(calls.Count);
        foreach (var call in calls)
        {
            if (call.View is null && ClaudeCodeToolView.For(call.Function, call.Arguments) is { } view)
            {
                updated.Add(call with { View = view });
                changed = true;
            }
            else
            {
                updated.Add(call);
            }
        }

        if (!changed)
        {
            return output;
        }

        var first = output.Choices[0];
        return output with { Choices = [first with { Message = first.Message with { ToolCalls = updated } }, .. output.Choices.Skip(1)] };
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.String)
        {
            value = jsonValue.GetValue<string>();
            return true;
        }

        value = "";
        return false;
    }

    /// <summary>A metadata value: strings as-is, anything else as a detached JSON copy.</summary>
    /// <remarks>The <c>(object)</c> cast matters: string converts implicitly to <see cref="JsonNode"/>, which would otherwise type the conditional.</remarks>
    private static object? MetadataValue(JsonNode? node) =>
        node is null ? null : TryGetString(node, out var text) ? (object)text : node.DeepClone();

    private static int? IntOf(JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        var real = value.GetValue<double>();
        return real is >= int.MinValue and <= int.MaxValue && Math.Floor(real) == real ? (int)real : null;
    }
}
