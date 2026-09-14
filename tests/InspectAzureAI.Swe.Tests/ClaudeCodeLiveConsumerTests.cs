using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Swe.Tests;

/// <summary>Port of inspect_swe <c>_claude_code/_events/live_consumer.py</c> behaviour against a real transcript and synthetic model events.</summary>
public class ClaudeCodeLiveConsumerTests
{
    private const string Prompt = "Explore the parser module and report its public API.";

    private static ToolCall Call(string id, string function, string args) => new(id, function, JsonNode.Parse(args)!.AsObject());

    private static ToolCall AgentCall(string id, string prompt, string? subagentType = "Explore", string? description = "look") =>
        new(id, "Agent", new JsonObject { ["subagent_type"] = subagentType, ["description"] = description, ["prompt"] = prompt });

    private static ModelEvent Event(IReadOnlyList<ChatMessage> input, params ToolCall[] calls) => new()
    {
        Model = "m",
        Input = input,
        ToolChoice = ToolChoice.Auto,
        Config = new GenerateConfig(),
        Output = calls.Length == 0
            ? ModelOutput.FromContent("m", "done")
            : new ModelOutput { Model = "m", Choices = [new ChatCompletionChoice(new ChatMessageAssistant("", toolCalls: calls), StopReason.ToolCalls)] },
    };

    /// <summary>What <c>Model.Record</c> does with an ambient sink.</summary>
    private static ModelEvent Record(ClaudeCodeLiveConsumer consumer, Transcript transcript, ModelEvent e)
    {
        var final = consumer.OnRecording(e);
        transcript.Add(final);
        consumer.OnModelEvent(final);
        return final;
    }

    private static IReadOnlyList<ChatMessage> UserInput(string text) => [new ChatMessageSystem("You are a sub-agent."), new ChatMessageUser(text)];

    private static JsonNode Line(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void task_and_agent_calls_open_agent_spans_under_the_parent_span()
    {
        var transcript = new Transcript();
        using var outer = transcript.Span("outer", "agent");
        var outerId = transcript.CurrentSpanId;
        var consumer = new ClaudeCodeLiveConsumer(transcript);

        Record(consumer, transcript, Event(
            [new ChatMessageUser("go")],
            AgentCall("toolu_1", Prompt),
            Call("toolu_2", "Task", """{"name": "reviewer", "prompt": "Review the change set thoroughly."}"""),
            Call("toolu_3", "Task", """{"prompt": ""}"""),
            Call("toolu_4", "Agent", """{"prompt": 42}"""),
            Call("toolu_5", "Bash", """{"prompt": "not a sub-agent at all"}"""),
            Call("toolu_6", "Agent", """{"subagent_type": "", "name": "", "prompt": "An anonymous helper task."}""")));

        var begins = transcript.Events.OfType<SpanBeginEvent>().Where(b => b.Type == "agent" && b.Id != outerId).ToArray();
        Assert.Equal(["agent-toolu_1", "agent-toolu_2", "agent-toolu_6"], begins.Select(b => b.Id));
        Assert.Equal(["Explore", "reviewer", "agent"], begins.Select(b => b.Name));
        Assert.All(begins, b => Assert.Equal(outerId, b.ParentId));
        Assert.Equal("look", begins[0].Metadata!["description"]);
        Assert.Null(begins[1].Metadata);

        // re-delivery of the same call is idempotent
        consumer.OnModelEvent(Event([new ChatMessageUser("go")], AgentCall("toolu_1", Prompt)));
        Assert.Equal(4, transcript.Events.OfType<SpanBeginEvent>().Count());
    }

    [Fact]
    public void a_sub_agent_call_is_attributed_by_its_prompt_and_everything_else_to_the_outer_span()
    {
        var transcript = new Transcript();
        using var outer = transcript.Span("outer", "agent");
        var outerId = transcript.CurrentSpanId;
        var consumer = new ClaudeCodeLiveConsumer(transcript);

        var first = Record(consumer, transcript, Event([new ChatMessageUser("go")], AgentCall("toolu_1", Prompt)));
        var sub = Record(consumer, transcript, Event(UserInput($"<context/>\n{Prompt}")));
        var main = Record(consumer, transcript, Event([new ChatMessageUser("unrelated"), new ChatMessageAssistant("x")]));
        var assistantFirst = Record(consumer, transcript, Event([new ChatMessageAssistant(Prompt), new ChatMessageUser(Prompt)]));
        var noUserText = Record(consumer, transcript, Event([new ChatMessageUser("")]));

        Assert.Equal(outerId, first.SpanId);
        Assert.Equal("agent-toolu_1", sub.SpanId);
        Assert.Equal(outerId, main.SpanId);
        Assert.Equal(outerId, assistantFirst.SpanId);
        Assert.Equal(outerId, noUserText.SpanId);
    }

    [Fact]
    public void ambiguous_or_short_prompts_fall_back_to_the_outer_span()
    {
        var transcript = new Transcript();
        var consumer = new ClaudeCodeLiveConsumer(transcript);
        const string shorter = "Explore the parser";
        const string tiny = "fix it";

        Record(consumer, transcript, Event([new ChatMessageUser("go")], AgentCall("a", Prompt), AgentCall("b", shorter), AgentCall("c", tiny)));

        // Prompt contains the shorter prompt too: two matches.
        Assert.Null(Record(consumer, transcript, Event(UserInput(Prompt))).SpanId);

        // only the shorter prompt matches
        Assert.Equal("agent-b", Record(consumer, transcript, Event(UserInput(shorter))).SpanId);

        // a prompt under MinPromptLength never attributes
        Assert.Null(Record(consumer, transcript, Event(UserInput(tiny))).SpanId);
        Assert.Equal(16, ClaudeCodeLiveConsumer.MinPromptLength);
    }

    [Fact]
    public void an_existing_span_id_is_kept()
    {
        var transcript = new Transcript();
        var consumer = new ClaudeCodeLiveConsumer(transcript);
        Record(consumer, transcript, Event([new ChatMessageUser("go")], AgentCall("toolu_1", Prompt)));

        var recorded = consumer.OnRecording(Event(UserInput(Prompt)) with { SpanId = "explicit" });

        Assert.Equal("explicit", recorded.SpanId);
    }

    [Fact]
    public void the_parents_next_request_closes_the_span_before_its_model_event()
    {
        var transcript = new Transcript();
        using var outer = transcript.Span("outer", "agent");
        var outerId = transcript.CurrentSpanId;
        var consumer = new ClaudeCodeLiveConsumer(transcript);
        Record(consumer, transcript, Event([new ChatMessageUser("go")], AgentCall("toolu_1", Prompt)));

        var parent = Record(consumer, transcript, Event([new ChatMessageUser(Prompt), new ChatMessageTool("report", toolCallId: "toolu_1", function: "Agent")]));

        var events = transcript.Events;
        Assert.Equal("agent-toolu_1", Assert.Single(events.OfType<SpanEndEvent>()).Id);
        Assert.IsType<SpanEndEvent>(events[^2]);
        Assert.Equal(parent.Uuid, Assert.IsType<ModelEvent>(events[^1]).Uuid);

        // the prompt is no longer pending, so the parent (whose first user text contains it) goes to the outer span
        Assert.Equal(outerId, parent.SpanId);

        // the JSONL tool_result arrives later and is a no-op
        consumer.ProcessJsonlLine(Line("""{"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "toolu_1"}]}}"""));
        Assert.Single(transcript.Events.OfType<SpanEndEvent>());
    }

    [Fact]
    public void a_jsonl_tool_result_closes_the_span_and_other_lines_are_ignored()
    {
        var transcript = new Transcript();
        var consumer = new ClaudeCodeLiveConsumer(transcript);
        Record(consumer, transcript, Event([new ChatMessageUser("go")], AgentCall("toolu_1", Prompt), AgentCall("toolu_2", Prompt + " Then summarise.")));

        consumer.ProcessJsonlLine(Line("""{"type": "user", "message": {"content": "plain text"}}"""));
        consumer.ProcessJsonlLine(Line("""{"type": "user", "message": {"content": [{"type": "text", "text": "x"}, {"type": "tool_result", "tool_use_id": "toolu_9"}, {"type": "tool_result"}, "junk"]}}"""));
        consumer.ProcessJsonlLine(Line("""{"type": "assistant", "message": {"content": [{"type": "tool_result", "tool_use_id": "toolu_1"}]}}"""));
        consumer.ProcessJsonlLine(Line("""[1, 2]"""));
        consumer.ProcessJsonlLine(Line("""{"type": "system", "subtype": "init"}"""));
        Assert.Empty(transcript.Events.OfType<SpanEndEvent>());

        consumer.ProcessJsonlLine(Line("""{"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "toolu_2", "content": "done"}]}}"""));

        Assert.Equal("agent-toolu_2", Assert.Single(transcript.Events.OfType<SpanEndEvent>()).Id);

        // toolu_2 is no longer pending: its prompt now attributes to toolu_1 only
        Assert.Equal("agent-toolu_1", Record(consumer, transcript, Event(UserInput(Prompt + " Then summarise."))).SpanId);
    }

    [Fact]
    public void compact_boundary_records_a_compaction_event_in_camel_or_snake_case()
    {
        var transcript = new Transcript();
        using var outer = transcript.Span("outer", "agent");
        var outerId = transcript.CurrentSpanId;
        var consumer = new ClaudeCodeLiveConsumer(transcript);

        consumer.ProcessJsonlLine(Line("""{"type": "system", "subtype": "compact_boundary", "content": "Compacted!", "compactMetadata": {"trigger": "manual", "preTokens": 1234}}"""));
        consumer.ProcessJsonlLine(Line("""{"type": "system", "subtype": "compact_boundary", "compact_metadata": {"trigger": "auto", "pre_tokens": 98765}}"""));
        consumer.ProcessJsonlLine(Line("""{"type": "system", "subtype": "compact_boundary"}"""));

        var compactions = transcript.Events.OfType<CompactionEvent>().ToArray();
        Assert.Equal(3, compactions.Length);
        Assert.All(compactions, c => Assert.Equal("claude_code", c.Source));
        Assert.All(compactions, c => Assert.Equal(outerId, c.SpanId));
        Assert.Equal(new int?[] { 1234, 98765, null }, compactions.Select(c => c.TokensBefore));
        Assert.Equal(["manual", "auto", "auto"], compactions.Select(c => c.Metadata!["trigger"]));
        Assert.Equal(["Compacted!", "Conversation compacted", "Conversation compacted"], compactions.Select(c => c.Metadata!["content"]));
    }

    [Fact]
    public void compaction_inside_a_sub_agent_uses_its_span_even_after_an_early_close()
    {
        var transcript = new Transcript();
        using var outer = transcript.Span("outer", "agent");
        var outerId = transcript.CurrentSpanId;
        var consumer = new ClaudeCodeLiveConsumer(transcript);
        Record(consumer, transcript, Event([new ChatMessageUser("go")], AgentCall("toolu_1", Prompt)));
        Record(consumer, transcript, Event([new ChatMessageUser("go"), new ChatMessageTool("r", toolCallId: "toolu_1")]));

        consumer.ProcessJsonlLine(Line("""{"type": "system", "subtype": "compact_boundary", "parent_tool_use_id": "toolu_1", "compact_metadata": {"pre_tokens": 7}}"""));
        consumer.ProcessJsonlLine(Line("""{"type": "system", "subtype": "compact_boundary", "parent_tool_use_id": "toolu_unknown"}"""));

        var compactions = transcript.Events.OfType<CompactionEvent>().ToArray();
        Assert.Equal("agent-toolu_1", compactions[0].SpanId);
        Assert.Equal(outerId, compactions[1].SpanId);
    }

    [Fact]
    public void reset_ends_open_spans_newest_first_and_clears_the_attempt()
    {
        var transcript = new Transcript();
        var consumer = new ClaudeCodeLiveConsumer(transcript);
        Record(consumer, transcript, Event([new ChatMessageUser("go")], AgentCall("a", Prompt), AgentCall("b", "Write the release notes for version two.")));
        Assert.Equal(StopReason.ToolCalls, consumer.LastStopReason);

        consumer.Reset();

        Assert.Equal(["agent-b", "agent-a"], transcript.Events.OfType<SpanEndEvent>().Select(e => e.Id));
        Assert.Null(consumer.LastStopReason);
        Assert.Null(Record(consumer, transcript, Event(UserInput(Prompt))).SpanId);
        consumer.Reset();
        Assert.Equal(2, transcript.Events.OfType<SpanEndEvent>().Count());
    }

    [Fact]
    public void recorded_output_carries_tool_views_and_keeps_existing_ones()
    {
        var transcript = new Transcript();
        var consumer = new ClaudeCodeLiveConsumer(transcript);
        var custom = new ToolCallContent("text", "mine") { Title = "Custom" };
        var write = Call("toolu_w", "Write", """{"file_path": "a.py", "content": "x\n"}""");
        var kept = Call("toolu_k", "ExitPlanMode", """{"plan": "p"}""") with { View = custom };
        var bash = Call("toolu_b", "Bash", """{"command": "ls"}""");

        var recorded = Record(consumer, transcript, Event([new ChatMessageUser("go")], write, kept, bash, AgentCall("toolu_a", Prompt)));

        var calls = recorded.Output.Message.ToolCalls!;
        Assert.Equal("Write", calls[0].View!.Title);
        Assert.Same(custom, calls[1].View);
        Assert.Null(calls[2].View);
        Assert.Equal("Agent: Explore", calls[3].View!.Title);
        Assert.Equal("Write", transcript.Events.OfType<ModelEvent>().Single().Output.Message.ToolCalls![0].View!.Title);

        // an output without calls is returned as-is
        var plain = Event([new ChatMessageUser("go")]);
        Assert.Same(plain.Output, consumer.OnRecording(plain).Output);
    }

    [Fact]
    public void a_failed_generation_does_not_set_the_stop_reason()
    {
        var transcript = new Transcript();
        var consumer = new ClaudeCodeLiveConsumer(transcript);

        consumer.OnModelEvent(Event([new ChatMessageUser("go")]) with { Error = "boom", Output = ModelOutput.FromContent("m", "", StopReason.ContentFilter) });
        Assert.Null(consumer.LastStopReason);

        consumer.OnModelEvent(Event([new ChatMessageUser("go")]) with { Output = ModelOutput.FromContent("m", "no", StopReason.ContentFilter) });
        Assert.Equal(StopReason.ContentFilter, consumer.LastStopReason);

        consumer.OnModelEvent(Event([new ChatMessageUser("go")]) with { Output = new ModelOutput { Model = "m" } });
        Assert.Equal(StopReason.ContentFilter, consumer.LastStopReason);
    }

    [Fact]
    public async Task concurrent_sub_agent_calls_are_attributed_and_closed_exactly_once()
    {
        var transcript = new Transcript();
        using var outer = transcript.Span("outer", "agent");
        var consumer = new ClaudeCodeLiveConsumer(transcript);
        const int count = 24;
        var prompts = Enumerable.Range(0, count).Select(i => $"Sub-agent task number {i:D3} with a unique marker <{i:D3}>").ToArray();
        Record(consumer, transcript, Event([new ChatMessageUser("go")], prompts.Select((p, i) => AgentCall($"t{i}", p)).ToArray()));

        var attributed = await Task.WhenAll(prompts.Select((p, i) => Task.Run(() => Record(consumer, transcript, Event(UserInput(p))).SpanId)));
        await Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(() =>
            Record(consumer, transcript, Event([new ChatMessageUser("go"), new ChatMessageTool("r", toolCallId: $"t{i}")])))));

        Assert.Equal(Enumerable.Range(0, count).Select(i => $"agent-t{i}"), attributed);
        var ends = transcript.Events.OfType<SpanEndEvent>().Select(e => e.Id).ToArray();
        Assert.Equal(count, ends.Length);
        Assert.Equal(count, ends.Distinct().Count());
        consumer.Reset();
        Assert.Equal(count, transcript.Events.OfType<SpanEndEvent>().Count());
    }
}
