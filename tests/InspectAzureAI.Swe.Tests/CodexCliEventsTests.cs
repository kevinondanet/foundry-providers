using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.CodexCli;

namespace InspectAzureAI.Swe.Tests;

/// <summary>
/// Port of <c>tests/test_codex_subagent_spans.py</c> (fixture shapes from a codex 0.147.0 / gpt-5.6-sol run) plus
/// the V1 attribution, V2 binding, close, reset, compaction and view cases of spec 3.6.3.
/// </summary>
public class CodexCliEventsTests
{
    private const string EncryptedPrompt = "gAAAAABqdjXI1bVTsv-encrypted-payload";

    private static ToolCall V2Spawn(string callId, string taskName) =>
        new(callId, "spawn_agent", new JsonObject { ["task_name"] = taskName, ["fork_turns"] = "all", ["message"] = EncryptedPrompt });

    private static ToolCall V1Spawn(string callId, string prompt) =>
        new(callId, "spawn_agent", new JsonObject { ["agent_type"] = "explore", ["message"] = prompt });

    private static ChatMessageUser User(string text) => new(text);

    /// <summary>A user message as the Responses bridge builds it from an <c>agent_message</c> item.</summary>
    private static ChatMessageUser AgentMessage(string author, string recipient, string messageType = "MESSAGE", string payload = "")
    {
        var envelope = $"Message Type: {messageType}\nTask name: {recipient}\nSender: {author}\nPayload:\n{payload}";
        var raw = new JsonObject
        {
            ["type"] = "agent_message",
            ["author"] = author,
            ["recipient"] = recipient,
            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = envelope }),
        };
        return new ChatMessageUser(new Content[] { new ContentText($"Agent message from {author}:\n{envelope}") })
        {
            Metadata = new Dictionary<string, object?> { ["agent_message"] = raw },
        };
    }

    private static ChatMessageTool ToolResult(string callId, string function, string text) => new(text, callId, function);

    private static ModelEvent Event(IReadOnlyList<ChatMessage> input, params ToolCall[] toolCalls) => new()
    {
        Model = "openai/gpt-5.6-sol",
        Input = input,
        ToolChoice = ToolChoice.Auto,
        Config = new GenerateConfig(),
        Output = new ModelOutput
        {
            Model = "gpt-5.6-sol",
            Choices = [new ChatCompletionChoice(new ChatMessageAssistant("ok", toolCalls.Length == 0 ? null : toolCalls), toolCalls.Length == 0 ? StopReason.Stop : StopReason.ToolCalls)],
        },
    };

    /// <summary>What <see cref="Eval.Model.Model"/> does with a completed event: the sink rewrites it, it lands, the sink sees the final copy.</summary>
    private static ModelEvent Record(CodexCliConsumer consumer, Transcript transcript, ModelEvent e)
    {
        var recorded = consumer.OnRecording(e);
        transcript.Add(recorded);
        consumer.OnModelEvent(recorded);
        return recorded;
    }

    private static List<SpanBeginEvent> AgentBegins(Transcript transcript) => transcript.Events.OfType<SpanBeginEvent>().Where(e => e.Type == "agent").ToList();

    private static List<string> Ends(Transcript transcript) => transcript.Events.OfType<SpanEndEvent>().Select(e => e.Id).ToList();

    // --- detection: V2 spawn calls and results ---

    [Fact]
    public void find_spawned_agents_uses_v2_task_name()
    {
        var spawned = Assert.Single(CodexCliDetection.FindSpawnedAgents([V2Spawn("call_1", "write_fizzbuzz")]));

        Assert.Equal(("write_fizzbuzz", "agent", "write_fizzbuzz", null), (spawned.Name, spawned.AgentType, spawned.TaskName, spawned.ReasoningEffort));
    }

    [Fact]
    public void find_spawned_agents_keeps_v1_agent_type_and_skips_calls_without_a_message()
    {
        var spawned = CodexCliDetection.FindSpawnedAgents(
        [
            V1Spawn("call_1", "write a fizzbuzz program to /tmp/fizzbuzz.py"),
            new ToolCall("call_2", "spawn_agent", new JsonObject { ["agent_type"] = "explore", ["message"] = "" }),
            new ToolCall("call_3", "spawn_agent", new JsonObject { ["message"] = "look", ["reasoning_effort"] = "high" }),
            new ToolCall("call_4", "exec_command", new JsonObject { ["message"] = "not a spawn" }),
        ]);

        Assert.Equal(["call_1", "call_3"], spawned.Select(s => s.CallId));
        Assert.Equal("explore", spawned[0].Name);
        Assert.Equal(("agent", "high"), (spawned[1].Name, spawned[1].ReasoningEffort));
    }

    [Fact]
    public void spawn_result_parses_v2_task_name()
    {
        var result = CodexCliDetection.SpawnResult(ToolResult("call_1", "spawn_agent", """{"task_name":"/root/write_fizzbuzz"}"""))!;

        Assert.Equal(("/root/write_fizzbuzz", null), (result.AgentId, result.Nickname));
    }

    [Fact]
    public void spawn_result_still_parses_v1_agent_id()
    {
        var result = CodexCliDetection.SpawnResult(ToolResult("call_1", "spawn_agent", """{"agent_id":"thread_abc","nickname":"Explorer"}"""))!;

        Assert.Equal(("thread_abc", "Explorer"), (result.AgentId, result.Nickname));
        Assert.Equal("/root/x", CodexCliDetection.SpawnResult(ToolResult("c", "spawn_agent", """{"agent_id":"","task_name":"/root/x","nickname":""}"""))!.AgentId);
        Assert.Null(CodexCliDetection.SpawnResult(ToolResult("c", "wait_agent", """{"agent_id":"thread_abc"}""")));
        Assert.Null(CodexCliDetection.SpawnResult(ToolResult("c", "spawn_agent", "not json")));
    }

    // --- detection: agent_message identity and completion signals ---

    [Fact]
    public void agent_message_recipients_identifies_requester()
    {
        IReadOnlyList<ChatMessage> input =
        [
            AgentMessage("/root", "/root/write_fizzbuzz"),
            AgentMessage("/root/write_fizzbuzz/implement_file", "/root/write_fizzbuzz"),
        ];

        Assert.Equal(["/root/write_fizzbuzz"], CodexCliDetection.AgentMessageRecipients(input));
    }

    [Fact]
    public void agent_message_recipients_empty_without_agent_messages()
    {
        Assert.Empty(CodexCliDetection.AgentMessageRecipients([User("plain task")]));
    }

    [Fact]
    public void final_answer_authors_detects_completion()
    {
        IReadOnlyList<ChatMessage> input =
        [
            AgentMessage("/root", "/root/write_primes"),
            AgentMessage("/root/write_primes", "/root", "FINAL_ANSWER", "primes written"),
        ];

        Assert.Equal(["/root/write_primes"], CodexCliDetection.FinalAnswerAuthors(input));
    }

    [Fact]
    public void final_answer_authors_ignores_plain_messages()
    {
        Assert.Empty(CodexCliDetection.FinalAnswerAuthors([AgentMessage("/root/write_primes", "/root", "MESSAGE")]));
    }

    [Fact]
    public void completed_thread_ids_come_from_wait_and_close_results_and_notifications()
    {
        IReadOnlyList<ChatMessage> input =
        [
            ToolResult("w", "wait_agent", """{"status":{"thread_a":{"completed":"done"},"thread_b":{"running":{}}}}"""),
            ToolResult("c", "close_agent", """{"status":{"thread_c":{"completed":null}}}"""),
            ToolResult("x", "exec_command", """{"status":{"thread_x":{"completed":"done"}}}"""),
            User("""<subagent_notification>{"agent_path":"thread_d","status":{"completed":"ok"}}</subagent_notification>"""),
            User("""<subagent_notification>{"agent_path":"thread_e","status":{"errored":"x"}}</subagent_notification>"""),
        ];

        Assert.Equal(["thread_a", "thread_c", "thread_d"], CodexCliDetection.CompletedThreadIds(input));
    }

    // --- consumer: V2 names, recipient attribution, nesting, FINAL_ANSWER ---

    [Fact]
    public void consumer_names_v2_spans_after_task_name()
    {
        var transcript = new Transcript();
        var consumer = new CodexCliConsumer(transcript);

        consumer.OnModelEvent(Event([User("spawn two agents")], V2Spawn("call_fb", "write_fizzbuzz"), V2Spawn("call_pr", "write_primes")));

        var begins = AgentBegins(transcript);
        Assert.Equal(["write_fizzbuzz", "write_primes"], begins.Select(e => e.Name));
        Assert.Equal(["agent-call_fb", "agent-call_pr"], begins.Select(e => e.Id));
        Assert.Equal<object?>("agent", begins[0].Metadata!["agent_type"]);
        Assert.Equal<object?>("write_fizzbuzz", begins[0].Metadata!["task_name"]);
        Assert.False(begins[0].Metadata!.ContainsKey("reasoning_effort"));
    }

    [Fact]
    public void consumer_attributes_subagent_call_by_recipient()
    {
        var transcript = new Transcript();
        using var outer = transcript.Span("outer");
        var outerId = transcript.CurrentSpanId;
        var consumer = new CodexCliConsumer(transcript);
        consumer.OnModelEvent(Event([User("spawn two agents")], V2Spawn("call_fb", "write_fizzbuzz"), V2Spawn("call_pr", "write_primes")));
        var begins = AgentBegins(transcript);
        Assert.All(begins, begin => Assert.Equal(outerId, begin.ParentId));

        Assert.Equal(begins[0].Id, consumer.OnRecording(Event([AgentMessage("/root", "/root/write_fizzbuzz")])).SpanId);
        Assert.Equal(begins[1].Id, consumer.OnRecording(Event([AgentMessage("/root", "/root/write_primes")])).SpanId);
        Assert.Equal(outerId, consumer.OnRecording(Event([User("spawn two agents"), AgentMessage("/root/write_primes", "/root")])).SpanId);
    }

    [Fact]
    public void consumer_nests_grandchild_span_under_child()
    {
        var transcript = new Transcript();
        var consumer = new CodexCliConsumer(transcript);
        consumer.OnModelEvent(Event([User("go")], V2Spawn("call_fb", "write_fizzbuzz")));
        var fizzbuzz = AgentBegins(transcript)[0].Id;

        Record(consumer, transcript, Event([AgentMessage("/root", "/root/write_fizzbuzz")], V2Spawn("call_impl", "implement_file")));

        var implement = AgentBegins(transcript)[1];
        Assert.Equal(("implement_file", fizzbuzz), (implement.Name, implement.ParentId));
    }

    [Fact]
    public void consumer_closes_span_on_final_answer()
    {
        var transcript = new Transcript();
        var consumer = new CodexCliConsumer(transcript);
        consumer.OnModelEvent(Event([User("go")], V2Spawn("call_pr", "write_primes")));
        var primes = AgentBegins(transcript)[0].Id;

        Assert.Equal(primes, consumer.OnRecording(Event([AgentMessage("/root", "/root/write_primes")])).SpanId);
        consumer.OnRecording(Event([User("go"), AgentMessage("/root/write_primes", "/root", "FINAL_ANSWER", "all primes written")]));

        Assert.Equal([primes], Ends(transcript));
    }

    [Fact]
    public void consumer_v1_prompt_attribution_unchanged()
    {
        var transcript = new Transcript();
        var consumer = new CodexCliConsumer(transcript);
        const string Prompt = "write a fizzbuzz program and save it to /tmp/fizzbuzz.py";
        consumer.OnModelEvent(Event([User("go")], V1Spawn("call_1", Prompt)));

        Assert.Equal(AgentBegins(transcript)[0].Id, consumer.OnRecording(Event([User(Prompt)])).SpanId);
    }

    // --- extras of spec 3.6.3 ---

    [Fact]
    public void v1_attribution_finds_the_spawn_prompt_after_agents_md_and_environment_context_messages()
    {
        var transcript = new Transcript();
        var consumer = new CodexCliConsumer(transcript);
        const string Prompt = "Survey the repository layout and report the build system.";
        consumer.OnModelEvent(Event([User("go")], V1Spawn("call_1", Prompt)));

        var subAgentCall = Event(
        [
            new ChatMessageSystem("You are Codex."),
            User("# AGENTS.md instructions for /workspace\n\n<INSTRUCTIONS>\nKeep changes minimal.\n</INSTRUCTIONS>"),
            User("<environment_context>\n  <cwd>/workspace</cwd>\n</environment_context>"),
            User(Prompt),
        ]);

        Assert.Equal(AgentBegins(transcript)[0].Id, consumer.OnRecording(subAgentCall).SpanId);
    }

    [Fact]
    public void a_subagent_notification_quoting_a_spawn_prompt_does_not_attribute_the_parents_call()
    {
        var transcript = new Transcript();
        using var outer = transcript.Span("outer");
        var consumer = new CodexCliConsumer(transcript);
        const string Prompt = "Survey the repository layout and report the build system.";
        consumer.OnModelEvent(Event([User("go")], V1Spawn("call_1", Prompt)));

        var parentCall = Event(
        [
            User("go"),
            User($$$"""<subagent_notification>{"agent_path":"thread_1","status":{"running":"{{{Prompt}}}"}}</subagent_notification>"""),
        ]);

        Assert.Equal(transcript.CurrentSpanId, consumer.OnRecording(parentCall).SpanId);
        Assert.Empty(Ends(transcript));
    }

    [Fact]
    public void a_v2_first_call_before_the_spawn_result_binds_and_the_later_result_does_not_rebind()
    {
        var transcript = new Transcript();
        var consumer = new CodexCliConsumer(transcript);
        consumer.OnModelEvent(Event([User("go")], V2Spawn("call_fb", "write_fizzbuzz")));
        var span = AgentBegins(transcript)[0].Id;

        Assert.Equal(span, consumer.OnRecording(Event([AgentMessage("/root", "/root/write_fizzbuzz")])).SpanId);
        Assert.Equal(span, consumer.OnRecording(Event([AgentMessage("/root", "/root/write_fizzbuzz"), User("keep going")])).SpanId);

        consumer.OnRecording(Event(
        [
            User("go"),
            ToolResult("call_fb", "spawn_agent", """{"task_name":"/root/renamed"}"""),
            AgentMessage("/root/renamed", "/root", "FINAL_ANSWER", "not the bound thread"),
        ]));
        Assert.Empty(Ends(transcript));

        consumer.OnRecording(Event([User("go"), AgentMessage("/root/write_fizzbuzz", "/root", "FINAL_ANSWER", "fizzbuzz written")]));
        Assert.Equal([span], Ends(transcript));
    }

    [Fact]
    public void nicknames_are_recorded_from_a_spawn_result_whose_call_is_not_open()
    {
        var consumer = new CodexCliConsumer(new Transcript());

        var recorded = consumer.OnRecording(Event(
            [User("go"), ToolResult("call_elsewhere", "spawn_agent", """{"agent_id":"thread_9","nickname":"Scout"}""")],
            new ToolCall("call_wait", "wait_agent", new JsonObject { ["targets"] = new JsonArray("thread_9"), ["timeout_ms"] = 5000 })));

        Assert.Equal("- Scout — `thread_9`\n\n_timeout: 5s_", recorded.Output.Message.ToolCalls![0].View!.Content);
    }

    [Fact]
    public void a_completed_status_and_close_agent_close_bound_threads()
    {
        var transcript = new Transcript();
        var consumer = new CodexCliConsumer(transcript);
        consumer.OnModelEvent(Event([User("go")], V1Spawn("call_a", "First sub-agent prompt, long enough."), V1Spawn("call_b", "Second sub-agent prompt, long enough.")));
        var (a, b) = (AgentBegins(transcript)[0].Id, AgentBegins(transcript)[1].Id);
        consumer.OnRecording(Event([User("go"), ToolResult("call_a", "spawn_agent", """{"agent_id":"thread_a"}"""), ToolResult("call_b", "spawn_agent", """{"agent_id":"thread_b"}""")]));

        consumer.OnRecording(Event([User("go"), ToolResult("call_wait", "wait_agent", """{"status":{"thread_a":{"completed":"done"},"thread_b":{"running":{}}}}""")]));
        Assert.Equal([a], Ends(transcript));

        consumer.OnModelEvent(Event([User("go")], new ToolCall("call_close", "close_agent", new JsonObject { ["target"] = "thread_b" })));
        Assert.Equal([a, b], Ends(transcript));
    }

    [Fact]
    public void reset_closes_orphans_innermost_first_and_clears_the_bindings()
    {
        var transcript = new Transcript();
        var consumer = new CodexCliConsumer(transcript);
        consumer.OnModelEvent(Event([User("go")], V2Spawn("call_1", "one"), V2Spawn("call_2", "two")));
        consumer.OnRecording(Event([AgentMessage("/root", "/root/one")]));

        consumer.Reset();

        Assert.Equal(["agent-call_2", "agent-call_1"], Ends(transcript));
        Assert.Null(consumer.OnRecording(Event([AgentMessage("/root", "/root/one")])).SpanId);
        consumer.OnRecording(Event([AgentMessage("/root/one", "/root", "FINAL_ANSWER", "done")]));
        consumer.Reset();
        Assert.Equal(2, Ends(transcript).Count);
    }

    [Fact]
    public void a_compaction_marker_with_leading_whitespace_in_the_last_of_several_user_messages_records_a_compaction()
    {
        var transcript = new Transcript();
        using var outer = transcript.Span("outer");
        var consumer = new CodexCliConsumer(transcript);

        var recorded = Record(consumer, transcript, Event(
        [
            User("Fix the bug."),
            new ChatMessageAssistant("Fixed."),
            User("Also update the docs."),
            User("  \n" + CodexCliDetection.CompactionMarker + "\nCreate a handoff summary."),
        ]));

        var compaction = Assert.Single(transcript.Events.OfType<CompactionEvent>());
        Assert.Equal(("codex_cli", "summary", transcript.CurrentSpanId), (compaction.Source, compaction.Type, compaction.SpanId));
        Assert.Equal<object?>("auto", compaction.Metadata!["trigger"]);
        var events = transcript.Events.ToList();
        Assert.True(events.IndexOf(compaction) < events.FindIndex(e => e is ModelEvent), "the compaction marker precedes its model event");
        Assert.Equal(transcript.CurrentSpanId, recorded.SpanId);

        consumer.OnRecording(Event([User("Please note: " + CodexCliDetection.CompactionMarker)]));
        Assert.Single(transcript.Events.OfType<CompactionEvent>());
    }

    [Fact]
    public void tool_views_are_set_on_the_recorded_copy_only_for_calls_without_one()
    {
        var consumer = new CodexCliConsumer(new Transcript());
        var custom = new ToolCallContent("text", "mine") { Title = "custom" };
        var original = Event(
            [User("go")],
            new ToolCall("c1", "exec_command", new JsonObject { ["cmd"] = "ls" }),
            new ToolCall("c2", "exec_command", new JsonObject { ["cmd"] = "pwd" }) { View = custom },
            new ToolCall("c3", "close_agent", new JsonObject { ["target"] = "t" }));

        var calls = consumer.OnRecording(original).Output.Message.ToolCalls!;

        Assert.Equal("exec_command", calls[0].View!.Title);
        Assert.Same(custom, calls[1].View);
        Assert.Null(calls[2].View);
        Assert.Null(original.Output.Message.ToolCalls![0].View);
        var plain = Event([User("hi")]);
        Assert.Same(plain.Output, consumer.OnRecording(plain).Output);
    }

    [Fact]
    public void an_event_offered_twice_is_processed_once()
    {
        var transcript = new Transcript();
        var consumer = new CodexCliConsumer(transcript);
        var e = Event([User(CodexCliDetection.CompactionMarker)]);

        var first = consumer.OnRecording(e);
        var second = consumer.OnRecording(first);

        Assert.Same(first, second);
        Assert.Single(transcript.Events.OfType<CompactionEvent>());
    }
}
