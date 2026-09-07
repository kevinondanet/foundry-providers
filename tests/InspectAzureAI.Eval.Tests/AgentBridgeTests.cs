using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Port of <c>tests/agent/test_bridge_track_state.py</c> (main-thread tracking) plus the model resolution, config precedence and refusal-retry behaviour of <c>AgentBridge.GenerateAsync</c>.</summary>
public class AgentBridgeTests
{
    private const string TaskPrompt = "In the year 2022, what castle did the Doctor spend 4.5 billion years in?";

    private static readonly ChatMessageSystem TaskSystem = new("You are opencode, an agent that ...");

    private sealed class CollectingSink : IModelEventSink
    {
        public List<ModelEvent> Events { get; } = [];

        public void OnModelEvent(ModelEvent e) => Events.Add(e);
    }

    private static AgentBridge TaskBridge(params ChatMessage[] initial) =>
        new(new AgentState(initial.Length == 0 ? [new ChatMessageUser(TaskPrompt)] : initial), new Model(new ScriptedModelApi()));

    private static ModelOutput Track(AgentBridge bridge, IReadOnlyList<ChatMessage> input, string completion)
    {
        var output = ModelOutput.FromContent("mockllm/model", completion);
        bridge.TrackState(input, output);
        return output;
    }

    private static List<ChatMessage> Extend(IReadOnlyList<ChatMessage> prefix, params ChatMessage[] more) => [.. prefix, .. more];

    private static string Condensed(string text) => "attachment://" + Mm3Hash.Hash(text);

    private static List<ChatMessage> TitleGenerationInput(string task = TaskPrompt) =>
    [
        new ChatMessageSystem("You are a title generator ..."),
        new ChatMessageUser("Generate a title for this conversation:\n"),
        new ChatMessageUser(task),
    ];

    [Fact]
    public void mm3_hash_matches_the_python_reference_vectors()
    {
        Assert.Equal("e271865701f545617eaf87e42bba7d87", Mm3Hash.Hash("foo"));
        Assert.Equal("00000000000000000000000000000000", Mm3Hash.Hash(""));
        Assert.Equal("e34bbc7bbc071b6c7a433ca9c49a9347", Mm3Hash.Hash("The quick brown fox jumps over the lazy dog"));
        Assert.Equal("2103cee83e28dd17b0660f3747957265", Mm3Hash.Hash(TaskPrompt));
    }

    [Fact]
    public void first_call_is_adopted()
    {
        var bridge = TaskBridge();
        Track(bridge, TitleGenerationInput(), "Doctor Who Series 9 setting");

        Assert.Equal("Doctor Who Series 9 setting", bridge.State.Output.Completion);
        Assert.Equal(4, bridge.State.Messages.Count);
    }

    [Fact]
    public void side_call_arriving_first_does_not_displace_task_thread()
    {
        var bridge = TaskBridge();
        Track(bridge, TitleGenerationInput(), "Doctor Who Series 9 setting");
        Track(bridge, [TaskSystem, new ChatMessageUser(TaskPrompt)], "Castle");
        Track(bridge, [TaskSystem, new ChatMessageUser(TaskPrompt)], "Castle TARDIS Console Room");

        Assert.Equal("Castle", bridge.State.Output.Completion);
        Assert.Equal([TaskSystem.Text, TaskPrompt, "Castle"], bridge.State.Messages.Select(m => m.Text).ToArray());
    }

    [Fact]
    public void side_call_after_main_loop_does_not_displace_task_thread()
    {
        var bridge = TaskBridge();
        List<ChatMessage> turn1 = [TaskSystem, new ChatMessageUser(TaskPrompt)];
        var out1 = Track(bridge, turn1, "let me look into that");
        var turn2 = Extend(turn1, out1.Message, new ChatMessageTool("tool result"));
        Track(bridge, turn2, "Castle");

        Track(bridge,
        [
            new ChatMessageSystem("You are a title generator ..."),
            new ChatMessageUser("Generate a title for this conversation:\n"),
            new ChatMessageUser(TaskPrompt),
            new ChatMessageUser("Respond with the title only."),
            new ChatMessageUser("Do not use quotes."),
        ], "Doctor Who Series 9 setting");

        Assert.Equal("Castle", bridge.State.Output.Completion);
        Assert.Equal(turn2.Count + 1, bridge.State.Messages.Count);
    }

    [Fact]
    public void main_loop_accumulation_is_tracked()
    {
        var bridge = TaskBridge();
        List<ChatMessage> turn1 = [TaskSystem, new ChatMessageUser(TaskPrompt)];
        var out1 = Track(bridge, turn1, "checking");
        Assert.Equal("checking", bridge.State.Output.Completion);

        var turn2 = Extend(turn1, out1.Message, new ChatMessageTool("tool result"));
        var out2 = Track(bridge, turn2, "still checking");
        Assert.Equal("still checking", bridge.State.Output.Completion);

        var turn3 = Extend(turn2, out2.Message, new ChatMessageTool("tool result 2"));
        Track(bridge, turn3, "Castle");
        Assert.Equal("Castle", bridge.State.Output.Completion);
        Assert.Equal(turn3.Count + 1, bridge.State.Messages.Count);
    }

    [Fact]
    public void shorter_side_call_is_parked_and_the_main_loop_keeps_tracking()
    {
        var bridge = TaskBridge();
        List<ChatMessage> turn1 = [TaskSystem, new ChatMessageUser(TaskPrompt)];
        var out1 = Track(bridge, turn1, "working");
        var turn2 = Extend(turn1, out1.Message, new ChatMessageTool("tool result"));
        var out2 = Track(bridge, turn2, "more work");

        Track(bridge, [new ChatMessageUser("Detect the paths in this bash command: ls /tmp")], "/tmp");
        Assert.Equal("more work", bridge.State.Output.Completion);

        var turn3 = Extend(turn2, out2.Message, new ChatMessageTool("tool result 2"));
        Track(bridge, turn3, "Castle");
        Assert.Equal("Castle", bridge.State.Output.Completion);
    }

    [Fact]
    public void scaffold_compaction_recovery_promotes_the_extended_candidate()
    {
        var bridge = TaskBridge();
        List<ChatMessage> turn1 = [TaskSystem, new ChatMessageUser(TaskPrompt)];
        var out1 = Track(bridge, turn1, "working");
        var turn2 = Extend(turn1, out1.Message, new ChatMessageTool("tool result"));
        var out2 = Track(bridge, turn2, "more work");
        var turn3 = Extend(turn2, out2.Message, new ChatMessageTool("tool result 2"));
        Track(bridge, turn3, "even more work");

        List<ChatMessage> compact1 = [TaskSystem, new ChatMessageUser("Summary of the conversation so far: ...")];
        var cout1 = Track(bridge, compact1, "compacted work");
        Assert.Equal("even more work", bridge.State.Output.Completion);

        var compact2 = Extend(compact1, cout1.Message, new ChatMessageTool("tool result 3"));
        Track(bridge, compact2, "Castle");

        Assert.Equal("Castle", bridge.State.Output.Completion);
        Assert.Equal(compact2.Count + 1, bridge.State.Messages.Count);
    }

    [Fact]
    public void length_heuristic_fallback_without_initial_input()
    {
        var bridge = new AgentBridge(new AgentState([]), new Model(new ScriptedModelApi()));
        List<ChatMessage> turn1 = [TaskSystem, new ChatMessageUser(TaskPrompt)];
        var out1 = Track(bridge, turn1, "working");
        var turn2 = Extend(turn1, out1.Message, new ChatMessageTool("tool result"));
        Track(bridge, turn2, "Castle");
        Assert.Equal("Castle", bridge.State.Output.Completion);

        Track(bridge, [new ChatMessageUser("side call")], "side answer");
        Assert.Equal("Castle", bridge.State.Output.Completion);
    }

    [Fact]
    public void repeated_same_length_call_keeps_first_answer()
    {
        var bridge = TaskBridge();
        List<ChatMessage> input = [TaskSystem, new ChatMessageUser(TaskPrompt)];
        Track(bridge, input, "Castle");
        Track(bridge, input, "Castle TARDIS Console Room");
        Assert.Equal("Castle", bridge.State.Output.Completion);
    }

    [Fact]
    public void sub_agent_loop_recovers_to_main_thread()
    {
        var bridge = TaskBridge();
        List<ChatMessage> turn1 = [TaskSystem, new ChatMessageUser(TaskPrompt)];
        var out1 = Track(bridge, turn1, "delegating");
        var turn2 = Extend(turn1, out1.Message, new ChatMessageTool("tool result"));
        var out2 = Track(bridge, turn2, "spawning subtask");

        List<ChatMessage> sub1 = [new ChatMessageSystem("You are a subtask agent ..."), new ChatMessageUser("Research Doctor Who series 9 filming locations.")];
        var sout1 = Track(bridge, sub1, "researching");
        var sub2 = Extend(sub1, sout1.Message, new ChatMessageTool("search results"));
        Track(bridge, sub2, "Cardiff Castle");

        var turn3 = Extend(turn2, out2.Message, new ChatMessageTool("subtask: Cardiff Castle"));
        var tout3 = Track(bridge, turn3, "almost there");
        var turn4 = Extend(turn3, tout3.Message, new ChatMessageTool("tool result 2"));
        Track(bridge, turn4, "Castle");

        Assert.Equal("Castle", bridge.State.Output.Completion);
        Assert.Equal(turn4.Count + 1, bridge.State.Messages.Count);
    }

    [Fact]
    public void single_final_call_reclaims_main_thread_after_sub_agent_loop()
    {
        var bridge = TaskBridge();
        List<ChatMessage> turn1 = [TaskSystem, new ChatMessageUser(TaskPrompt)];
        var out1 = Track(bridge, turn1, "delegating");
        var turn2 = Extend(turn1, out1.Message, new ChatMessageTool("tool result"));
        var out2 = Track(bridge, turn2, "spawning subtask");
        var turn3 = Extend(turn2, out2.Message, new ChatMessageTool("tool result 2"));
        var out3 = Track(bridge, turn3, "waiting on subtask");

        List<ChatMessage> sub1 = [new ChatMessageSystem("You are a subtask agent ..."), new ChatMessageUser("Research Doctor Who series 9 filming locations.")];
        var sout1 = Track(bridge, sub1, "researching");
        var sub2 = Extend(sub1, sout1.Message, new ChatMessageTool("search results"));
        Track(bridge, sub2, "Cardiff Castle");
        Assert.Equal("Cardiff Castle", bridge.State.Output.Completion);

        var turn4 = Extend(turn3, out3.Message, new ChatMessageTool("subtask: Cardiff Castle"));
        Track(bridge, turn4, "Castle");

        Assert.Equal("Castle", bridge.State.Output.Completion);
        Assert.Equal(turn4.Count + 1, bridge.State.Messages.Count);
    }

    [Fact]
    public void extension_recognized_despite_new_ids_and_metadata()
    {
        var bridge = TaskBridge();
        List<ChatMessage> turn1 =
        [
            new ChatMessageSystem("You are opencode, an agent that ...") { Id = "sys-1" },
            new ChatMessageUser(TaskPrompt) { Id = "user-1", Metadata = new Dictionary<string, object?> { ["turn"] = 1 } },
        ];
        var out1 = Track(bridge, turn1, "working");

        List<ChatMessage> turn2 =
        [
            new ChatMessageSystem("You are opencode, an agent that ...") { Id = "sys-2" },
            new ChatMessageUser(TaskPrompt) { Id = "user-2", Metadata = new Dictionary<string, object?> { ["turn"] = 2 } },
            new ChatMessageAssistant(out1.Message.Text) { Metadata = new Dictionary<string, object?> { ["replayed"] = true } },
            new ChatMessageTool("tool result"),
        ];
        Track(bridge, turn2, "Castle");

        Assert.Equal("Castle", bridge.State.Output.Completion);
        Assert.Equal(turn2.Count + 1, bridge.State.Messages.Count);
    }

    [Fact]
    public void condensed_user_turn_anchors_descent()
    {
        var bridge = TaskBridge();
        Track(bridge, TitleGenerationInput(Condensed(TaskPrompt)), "Doctor Who Series 9 setting");
        Track(bridge, [TaskSystem, new ChatMessageUser(Condensed(TaskPrompt))], "Castle");

        Assert.Equal("Castle", bridge.State.Output.Completion);
        Assert.Equal([TaskSystem.Text, Condensed(TaskPrompt), "Castle"], bridge.State.Messages.Select(m => m.Text).ToArray());
    }

    [Fact]
    public void unrelated_attachment_reference_does_not_anchor_descent()
    {
        var bridge = TaskBridge();
        Track(bridge, [TaskSystem, new ChatMessageUser(TaskPrompt)], "Castle");
        Track(bridge, [TaskSystem, new ChatMessageUser(Condensed("something else entirely")), new ChatMessageUser("Respond with the title only.")], "Title");

        Assert.Equal("Castle", bridge.State.Output.Completion);
    }

    [Fact]
    public void quote_wrapped_prompt_anchors_descent()
    {
        var bridge = TaskBridge();
        var quoted = $"\"{TaskPrompt}\"";
        Track(bridge, [TaskSystem, new ChatMessageUser(quoted)], "Castle");
        Track(bridge, TitleGenerationInput(quoted), "Doctor Who Series 9 setting");

        Assert.Equal("Castle", bridge.State.Output.Completion);
        Assert.Equal([TaskSystem.Text, quoted, "Castle"], bridge.State.Messages.Select(m => m.Text).ToArray());
    }

    [Fact]
    public void quote_wrapped_prompt_title_call_first()
    {
        var bridge = TaskBridge();
        var quoted = $"\"{TaskPrompt}\"";
        Track(bridge, TitleGenerationInput(quoted), "Doctor Who Series 9 setting");
        Track(bridge, [TaskSystem, new ChatMessageUser(quoted)], "Castle");

        Assert.Equal("Castle", bridge.State.Output.Completion);
        Assert.Equal("Castle", bridge.State.Messages[^1].Text);
    }

    [Fact]
    public void verbatim_side_call_does_not_displace_quote_wrapped_main()
    {
        var bridge = TaskBridge();
        var quoted = $"\"{TaskPrompt}\"";
        Track(bridge, [TaskSystem, new ChatMessageUser(quoted)], "Castle");
        Track(bridge, [new ChatMessageSystem("You are a topic detector ..."), new ChatMessageUser(TaskPrompt)], "Doctor Who");

        Assert.Equal("Castle", bridge.State.Output.Completion);
    }

    [Fact]
    public void decorated_prompt_anchors_descent_by_containment()
    {
        var bridge = TaskBridge();
        Track(bridge, TitleGenerationInput(), "Doctor Who Series 9 setting");
        Track(bridge, [TaskSystem, new ChatMessageUser($"<task>\n{TaskPrompt}\n</task>")], "Castle");

        Assert.Equal("Castle", bridge.State.Output.Completion);
    }

    [Fact]
    public void short_initial_input_does_not_anchor_by_containment()
    {
        const string shortTask = "ls /tmp";
        var bridge = TaskBridge(new ChatMessageUser(shortTask));
        Track(bridge, [TaskSystem, new ChatMessageUser(shortTask)], "Castle");
        Track(bridge,
        [
            new ChatMessageSystem("You are a title generator ..."),
            new ChatMessageUser($"Generate a title for: {shortTask}"),
            new ChatMessageUser("Respond with the title only."),
        ], "Listing temporary files");

        Assert.Equal("Castle", bridge.State.Output.Completion);
    }

    [Fact]
    public async Task generate_serves_aliases_and_routes_everything_else_to_the_default_model()
    {
        var defaultApi = new ScriptedModelApi([ScriptedTurn.Text("default"), ScriptedTurn.Text("default"), ScriptedTurn.Text("default")], "served");
        var aliasApi = new ScriptedModelApi([ScriptedTurn.Text("alias")], "haiku-deployment");
        var bridge = new AgentBridge(
            new AgentState([new ChatMessageUser(TaskPrompt)]),
            new Model(defaultApi),
            new Dictionary<string, Model> { ["claude-haiku"] = new Model(aliasApi) });

        Assert.Equal("alias", (await bridge.GenerateAsync("claude-haiku", [new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig())).Completion);
        Assert.Equal("default", (await bridge.GenerateAsync("inspect", [new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig())).Completion);
        Assert.Equal("default", (await bridge.GenerateAsync("inspect/other", [new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig())).Completion);
        Assert.Equal("default", (await bridge.GenerateAsync("claude-opus-4", [new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig())).Completion);
        Assert.Single(aliasApi.Requests);
        Assert.Equal(3, defaultApi.Requests.Count);
    }

    [Fact]
    public async Task generate_tracks_state_and_keeps_message_ids_stable_across_calls()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("first"), ScriptedTurn.Text("second"));
        var bridge = new AgentBridge(new AgentState([new ChatMessageUser(TaskPrompt)]), new Model(api));

        var out1 = await bridge.GenerateAsync("inspect", [TaskSystem, new ChatMessageUser(TaskPrompt)], [], ToolChoice.Auto, new GenerateConfig());
        Assert.Equal("first", bridge.State.Output.Completion);
        Assert.Equal(3, bridge.State.Messages.Count);

        await bridge.GenerateAsync("inspect", [TaskSystem, new ChatMessageUser(TaskPrompt), new ChatMessageAssistant(out1.Message.Text), new ChatMessageTool("result")], [], ToolChoice.Auto, new GenerateConfig());
        Assert.Equal("second", bridge.State.Output.Completion);
        Assert.Equal(5, bridge.State.Messages.Count);
        Assert.Equal(api.Requests[0].Input[1].Id, api.Requests[1].Input[1].Id);
        Assert.NotEqual(api.Requests[1].Input[1].Id, api.Requests[1].Input[0].Id);
    }

    [Fact]
    public async Task generate_retries_refusals_up_to_the_limit()
    {
        var refusal = ModelOutput.FromContent("m", "I cannot help with that.", StopReason.ContentFilter);
        var api = new ScriptedModelApi(ScriptedTurn.From(refusal), ScriptedTurn.From(refusal), ScriptedTurn.Text("Castle"));
        var bridge = new AgentBridge(new AgentState([new ChatMessageUser(TaskPrompt)]), new Model(api), retryRefusals: 2);

        var output = await bridge.GenerateAsync("inspect", [new ChatMessageUser(TaskPrompt)], [], ToolChoice.Auto, new GenerateConfig());

        Assert.Equal("Castle", output.Completion);
        Assert.Equal(3, api.Requests.Count);
    }

    [Fact]
    public async Task generate_returns_the_refusal_once_the_retry_budget_is_spent()
    {
        var refusal = ModelOutput.FromContent("m", "I cannot help with that.", StopReason.ContentFilter);
        var api = new ScriptedModelApi(ScriptedTurn.From(refusal), ScriptedTurn.From(refusal), ScriptedTurn.Text("Castle"));
        var bridge = new AgentBridge(new AgentState([new ChatMessageUser(TaskPrompt)]), new Model(api), retryRefusals: 1);

        var output = await bridge.GenerateAsync("inspect", [new ChatMessageUser(TaskPrompt)], [], ToolChoice.Auto, new GenerateConfig());

        Assert.Equal(StopReason.ContentFilter, output.StopReason);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task model_config_wins_over_the_request_and_generation_params_are_dropped_unless_forwarded()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("a"), ScriptedTurn.Text("b"));
        var model = new Model(api, new GenerateConfig { Temperature = 0.2 });
        var request = new GenerateConfig { Temperature = 0.9, TopP = 0.5, MaxTokens = 10, StopSeqs = ["END"] };

        await new AgentBridge(new AgentState([]), model, forwardGenerationConfig: true)
            .GenerateAsync("inspect", [new ChatMessageUser("hi")], [], ToolChoice.Auto, request);
        await new AgentBridge(new AgentState([]), model)
            .GenerateAsync("inspect", [new ChatMessageUser("hi")], [], ToolChoice.Auto, request);

        var forwarded = api.Requests[0].Config;
        Assert.Equal(0.2, forwarded.Temperature);
        Assert.Equal(0.5, forwarded.TopP);
        Assert.Equal(10, forwarded.MaxTokens);
        Assert.Equal(["END"], forwarded.StopSeqs);

        var cleared = api.Requests[1].Config;
        Assert.Equal(0.2, cleared.Temperature);
        Assert.Null(cleared.TopP);
        Assert.Equal(2048, cleared.MaxTokens);
        Assert.Equal(["END"], cleared.StopSeqs);
    }

    [Fact]
    public async Task the_model_event_sink_is_installed_around_bridged_generations()
    {
        var sink = new CollectingSink();
        var api = new ScriptedModelApi(ScriptedTurn.Text("a"));
        var bridge = new AgentBridge(new AgentState([]), new Model(api), modelEventSink: sink);

        await bridge.GenerateAsync("inspect", [new ChatMessageUser("hi")], [], ToolChoice.Auto, new GenerateConfig());

        var e = Assert.Single(sink.Events);
        Assert.Equal("a", e.Output.Completion);
        Assert.Null(ModelEventSinks.Current);
    }
}
