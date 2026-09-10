using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Swe.CodexCli;

/// <summary>
/// Port of inspect_swe <c>_codex_cli/_events/consumer.py</c> <c>CodexConsumer</c>: the bridge model-event sink that
/// rebuilds Codex sub-agent spans from bridged model events alone (no Codex <c>--json</c> output is read).
/// </summary>
/// <remarks>
/// <para>
/// A <c>spawn_agent</c> call in a parent's output opens an agent span keyed by the call id (<see cref="OnModelEvent"/>,
/// before the bridge answers, so the span is open before the sub-agent's first call). Each later call is attributed
/// (<see cref="OnRecording"/>): Multi-Agent V2 by its single <c>agent_message</c> recipient, V1 by finding exactly one
/// open spawn prompt in its user text, otherwise the outer span. The spawn result binds the sub-agent's thread id;
/// a completed status, a <c>FINAL_ANSWER</c> message or a <c>close_agent</c> call closes the span, and
/// <see cref="Reset"/> closes what is left. A local compaction request also records a <see cref="CompactionEvent"/>.
/// </para>
/// <para>
/// This port records a model event once, at completion (deviation D-E1), so Python's <c>on_pending</c> and the view
/// half of <c>on_complete</c> both run in <see cref="OnRecording"/>, and tool views land on the transcript event only
/// (deviation D-E2). The outer span is the transcript's current span at call time. Bridge handlers run concurrently,
/// so all state is guarded by one lock.
/// </para>
/// </remarks>
public sealed class CodexCliConsumer : IModelEventSink
{
    /// <summary>The <see cref="CompactionEvent.Source"/> of Codex compaction markers.</summary>
    public const string CompactionSource = "codex_cli";

    /// <summary>The shortest spawn prompt matched as a substring (guards against short prompts matching unrelated text).</summary>
    public const int MinPromptLength = 16;

    private readonly Lock _gate = new();

    private readonly Transcript _transcript;

    private readonly OrderedDictionary<string, OpenAgent> _agents = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _threadIndex = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _nicknames = new(StringComparer.Ordinal);

    private readonly HashSet<string> _recording = new(StringComparer.Ordinal);

    public CodexCliConsumer(Transcript transcript)
    {
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
    }

    /// <summary>
    /// Python's <c>on_pending</c> plus the tool views of <c>on_complete</c>: binds thread ids from spawn results, closes
    /// completed threads, attributes the call to a span, records a compaction marker, and returns the event with its
    /// span id and tool-call views set. An event already seen (a sink registered both on the model and ambiently) is
    /// returned unchanged.
    /// </summary>
    public ModelEvent OnRecording(ModelEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        lock (_gate)
        {
            if (e.Uuid is { } uuid && !_recording.Add(uuid))
            {
                return e;
            }

            HarvestBindings(e.Input);
            foreach (var threadId in CodexCliDetection.CompletedThreadIds(e.Input))
            {
                CloseThread(threadId);
            }

            foreach (var author in CodexCliDetection.FinalAnswerAuthors(e.Input))
            {
                CloseThread(author);
            }

            var spanId = e.SpanId ?? Attribute(e.Input);
            if (CodexCliDetection.IsCompactionRequest(e.Input))
            {
                _transcript.Add(new CompactionEvent
                {
                    Source = CompactionSource,
                    SpanId = spanId,
                    Metadata = new Dictionary<string, object?> { ["trigger"] = "auto" },
                });
            }

            return e with { SpanId = spanId, Output = WithViews(e.Output) };
        }
    }

    /// <summary>Python's <c>on_complete</c> span half: opens a span for each new <c>spawn_agent</c> call, then closes each <c>close_agent</c> target.</summary>
    public void OnModelEvent(ModelEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        lock (_gate)
        {
            if (e.Uuid is { } uuid)
            {
                _recording.Remove(uuid);
            }

            if (e.Output.Choices.Count == 0 || e.Output.Message.ToolCalls is not { Count: > 0 } calls)
            {
                return;
            }

            var parentSpanId = e.SpanId ?? _transcript.CurrentSpanId;
            foreach (var spawned in CodexCliDetection.FindSpawnedAgents(calls))
            {
                if (_agents.ContainsKey(spawned.CallId))
                {
                    continue;
                }

                var spanId = $"agent-{spawned.CallId}";
                _agents.Add(spawned.CallId, new OpenAgent(spawned.CallId, spanId, spawned.Message, spawned.Name));
                var metadata = new Dictionary<string, object?> { ["agent_type"] = spawned.AgentType };
                if (spawned.TaskName is not null)
                {
                    metadata["task_name"] = spawned.TaskName;
                }

                if (spawned.ReasoningEffort is not null)
                {
                    metadata["reasoning_effort"] = spawned.ReasoningEffort;
                }

                _transcript.Add(new SpanBeginEvent(spanId, spawned.Name, "agent", parentSpanId) { Metadata = metadata });
            }

            foreach (var target in CodexCliDetection.FindCloseTargets(calls))
            {
                CloseThread(target);
            }
        }
    }

    /// <summary>Closes the open spans innermost-first and clears the per-launch state (Python runs it after every launch).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            var open = _agents.Values.ToList();
            for (var i = open.Count - 1; i >= 0; i--)
            {
                _transcript.Add(new SpanEndEvent(open[i].SpanId));
            }

            _agents.Clear();
            _threadIndex.Clear();
            _nicknames.Clear();
            _recording.Clear();
        }
    }

    /// <summary>Port of <c>_harvest_bindings</c>: nicknames from every spawn result; the first result for an open spawn binds its thread id.</summary>
    private void HarvestBindings(IReadOnlyList<ChatMessage> input)
    {
        foreach (var message in input)
        {
            if (message is not ChatMessageTool tool || CodexCliDetection.SpawnResult(tool) is not { } result || tool.ToolCallId is not { } callId)
            {
                continue;
            }

            if (result.Nickname is not null)
            {
                _nicknames[result.AgentId] = result.Nickname;
            }

            if (_agents.TryGetValue(callId, out var agent) && agent.ThreadId is null)
            {
                agent.ThreadId = result.AgentId;
                _threadIndex[result.AgentId] = callId;
            }
        }
    }

    private void CloseThread(string threadId)
    {
        if (!_threadIndex.Remove(threadId, out var callId) || !_agents.TryGetValue(callId, out var agent))
        {
            return;
        }

        _agents.Remove(callId);
        _transcript.Add(new SpanEndEvent(agent.SpanId));
    }

    /// <summary>Port of <c>_attribute</c>: V2 recipient first, then V1 prompt substring; otherwise the outer span.</summary>
    private string? Attribute(IReadOnlyList<ChatMessage> input)
    {
        var outer = _transcript.CurrentSpanId;
        if (_agents.Count == 0)
        {
            return outer;
        }

        if (AttributeByRecipient(input) is { } recipientSpan)
        {
            return recipientSpan;
        }

        var userText = UserText(input);
        if (userText.Length == 0)
        {
            return outer;
        }

        var matches = _agents.Values
            .Where(agent => CodexCliText.CodePoints(agent.Prompt) >= MinPromptLength && userText.Contains(agent.Prompt, StringComparison.Ordinal))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0].SpanId : outer;
    }

    /// <summary>
    /// Port of <c>_attribute_by_recipient</c>: a single recipient bound to an open agent resolves to its span; otherwise
    /// a unique unbound agent whose name is the recipient's last path segment is bound to it. Null falls through.
    /// </summary>
    private string? AttributeByRecipient(IReadOnlyList<ChatMessage> input)
    {
        var recipients = CodexCliDetection.AgentMessageRecipients(input);
        if (recipients.Count != 1)
        {
            return null;
        }

        var recipient = recipients[0];
        if (_threadIndex.TryGetValue(recipient, out var boundCallId) && _agents.TryGetValue(boundCallId, out var bound))
        {
            return bound.SpanId;
        }

        var basename = recipient[(recipient.LastIndexOf('/') + 1)..];
        var candidates = _agents.Values.Where(agent => agent.ThreadId is null && agent.Name == basename).Take(2).ToList();
        if (candidates.Count != 1)
        {
            return null;
        }

        var candidate = candidates[0];
        candidate.ThreadId = recipient;
        _threadIndex[recipient] = candidate.CallId;
        return candidate.SpanId;
    }

    /// <summary>Port of <c>_user_text</c>: every non-empty user text without a sub-agent notification, joined by newlines.</summary>
    private static string UserText(IReadOnlyList<ChatMessage> input) =>
        string.Join("\n", input
            .OfType<ChatMessageUser>()
            .Select(message => message.Text)
            .Where(text => text.Length > 0 && !text.Contains(CodexCliDetection.SubagentNotificationTag, StringComparison.Ordinal)));

    /// <summary>The first choice with <see cref="CodexCliToolViews"/> views on its tool calls that have none.</summary>
    private ModelOutput WithViews(ModelOutput output)
    {
        if (output.Choices.Count == 0 || output.Message.ToolCalls is not { Count: > 0 } calls)
        {
            return output;
        }

        var changed = false;
        var viewed = new List<ToolCall>(calls.Count);
        foreach (var call in calls)
        {
            if (call.View is null && CodexCliToolViews.For(call.Function, call.Arguments, _nicknames) is { } view)
            {
                viewed.Add(call with { View = view });
                changed = true;
            }
            else
            {
                viewed.Add(call);
            }
        }

        if (!changed)
        {
            return output;
        }

        var choices = output.Choices.ToList();
        choices[0] = choices[0] with { Message = choices[0].Message with { ToolCalls = viewed } };
        return output with { Choices = choices };
    }

    private sealed class OpenAgent(string callId, string spanId, string prompt, string name)
    {
        public string CallId { get; } = callId;

        public string SpanId { get; } = spanId;

        public string Prompt { get; } = prompt;

        public string Name { get; } = name;

        public string? ThreadId { get; set; }
    }
}
