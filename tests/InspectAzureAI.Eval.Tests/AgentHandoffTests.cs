using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Agents = InspectAzureAI.Eval.Agents.Agents;

/// <summary>
/// Port of <c>tests/agent/test_agent_handoff.py</c>, <c>test_agent_execute.py</c> (handoff, as_tool, run, limits),
/// <c>test_agent_content_only.py</c> and <c>test_agent_name.py</c> (tool naming).
/// </summary>
public class AgentHandoffTests
{
    private static readonly ToolDef Addition = new(
        "addition",
        "Add two numbers.",
        new ToolParams { Properties = new Dictionary<string, ToolParam> { ["x"] = ToolParam.Of("integer"), ["y"] = ToolParam.Of("integer") }, Required = ["x", "y"] },
        (args, _) => Task.FromResult<ToolResult>((args["x"]!.GetValue<int>() + args["y"]!.GetValue<int>()).ToString()));

    /// <summary>Port of <c>searcher()</c>: appends a user message and returns.</summary>
    private static readonly AgentDef Searcher = new("searcher", "Searcher that computes max searches.", (state, _) =>
    {
        state.Messages.Add(new ChatMessageUser("The maximum searches is 5"));
        return Task.FromResult(state);
    });

    /// <summary>Port of <c>searcher3()</c>: generates one assistant message with the active model.</summary>
    private static readonly AgentDef Searcher3 = new("searcher3", "Searcher that generates.", async (state, ct) =>
    {
        var output = await SampleContext.Require().ActiveModel.GenerateAsync(state.Messages.ToArray(), cancellationToken: ct);
        state.Messages.Add(output.Message);
        state.Output = output;
        return state;
    });

    /// <summary>Port of <c>tool_checker()</c>: reports how many tool messages / tool-calling assistant messages it was shown.</summary>
    private static readonly AgentDef ToolChecker = new("tool_checker", "Tool checker that checks if tool messages are present.", (state, _) =>
    {
        var count = state.Messages.Count(m => m is ChatMessageTool || m is ChatMessageAssistant { ToolCalls: not null });
        state.Messages.Add(new ChatMessageAssistant(count.ToString()));
        return Task.FromResult(state);
    });

    /// <summary>Port of <c>oracle()</c>: two generations with a user turn in between.</summary>
    private static readonly AgentDef Oracle = new("oracle", "Oracle that answers questions.", async (state, ct) =>
    {
        var model = SampleContext.Require().ActiveModel;
        state.Output = await model.GenerateAsync(state.Messages.ToArray(), cancellationToken: ct);
        state.Messages.Add(state.Output.Message);
        state.Messages.Add(new ChatMessageUser("That's great, can you give me another answer?"));
        state.Output = await model.GenerateAsync(state.Messages.ToArray(), cancellationToken: ct);
        state.Messages.Add(state.Output.Message);
        return state;
    });

    /// <summary>Port of <c>looping_agent()</c>: generates and appends forever (only a limit stops it).</summary>
    private static readonly AgentDef LoopingAgent = new("looping_agent", "An agent which forever calls generate and appends messages.", async (state, ct) =>
    {
        var model = SampleContext.Require().ActiveModel;
        while (true)
        {
            var output = await model.GenerateAsync(state.Messages.ToArray(), cancellationToken: ct);
            state.Messages.Add(output.Message);
        }
    });

    /// <summary>Port of <c>web_surfer()</c>: sets the completion and returns.</summary>
    private static readonly AgentDef WebSurfer = new("web_surfer", "Web surfer for conducting web research into a topic.", (state, _) =>
    {
        state.Output = ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "22");
        return Task.FromResult(state);
    });

    private static ScriptedModelApi Hello(int turns = 100) =>
        new(Enumerable.Range(0, turns).Select(_ => ScriptedTurn.Text("hello", new ModelUsage(TotalTokens: 1))));

    private static ChatMessageAssistant Transfer(string tool, string id = "1", params ToolCall[] siblings) =>
        new("Call tool", toolCalls: [.. siblings, new ToolCall(id, tool, new JsonObject())]);

    private static async Task<ExecuteToolsResult> HandoffAsync(ToolDef handoff, IEnumerable<ChatMessage> conversation, params ToolDef[] otherTools) =>
        await ToolExecutor.ExecuteToolsAsync([.. conversation], [.. otherTools, handoff]);

    // ---- filters -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task content_only_removes_system_messages()
    {
        var filtered = await MessageFilters.ContentOnly([new ChatMessageSystem("System prompt"), new ChatMessageUser("User message"), new ChatMessageAssistant("Assistant response")], default);

        Assert.Equal(["user", "assistant"], filtered.Select(m => m.Role));
    }

    [Fact]
    public async Task content_only_passes_user_messages_through_unchanged()
    {
        var user = new ChatMessageUser("Test user message") { Id = "user-123" };

        var filtered = await MessageFilters.ContentOnly([user], default);

        Assert.Same(user, Assert.Single(filtered));
    }

    [Fact]
    public async Task content_only_converts_tool_messages_to_user_messages_keeping_their_ids()
    {
        var tool = new ChatMessageTool("Tool result", "call-789", "my_function") { Id = "tool-456" };

        var filtered = await MessageFilters.ContentOnly([tool], default);

        var user = Assert.IsType<ChatMessageUser>(Assert.Single(filtered));
        Assert.Equal("tool-456", user.Id);
        Assert.Equal("Tool result", user.Content.Text);
    }

    [Fact]
    public async Task content_only_removes_reasoning()
    {
        var assistant = new ChatMessageAssistant(new Content[]
        {
            new ContentReasoning("Internal thinking process"),
            new ContentText("Visible response"),
            new ContentReasoning("More internal thoughts"),
            new ContentText("More visible content"),
        });

        var filtered = await MessageFilters.ContentOnly([assistant], default);

        var items = Assert.IsType<ChatMessageAssistant>(Assert.Single(filtered)).Content.Items!;
        Assert.Equal(["Visible response", "More visible content"], items.Select(c => Assert.IsType<ContentText>(c).Text));
    }

    [Fact]
    public async Task content_only_renders_tool_calls_as_text()
    {
        var call = new ToolCall("call-001", "calculate", new JsonObject { ["x"] = 5, ["y"] = 10 });
        var assistant = new ChatMessageAssistant("Here's the calculation:", toolCalls: [call]);

        var filtered = await MessageFilters.ContentOnly([assistant], default);

        var result = Assert.IsType<ChatMessageAssistant>(Assert.Single(filtered));
        Assert.Null(result.ToolCalls);
        Assert.Equal(2, result.Content.Items!.Count);
        Assert.Equal("Here's the calculation:", Assert.IsType<ContentText>(result.Content.Items[0]).Text);
        Assert.Equal("calculate(x=5, y=10)", Assert.IsType<ContentText>(result.Content.Items[1]).Text);
        Assert.Equal(assistant.Id, result.Id);
    }

    [Fact]
    public async Task content_only_renders_multiple_tool_calls_on_separate_lines()
    {
        var assistant = new ChatMessageAssistant("Multiple calls:", toolCalls:
        [
            new ToolCall("call-001", "func1", new JsonObject { ["a"] = 1 }),
            new ToolCall("call-002", "func2", new JsonObject { ["b"] = 2 }),
            new ToolCall("call-003", "func3", new JsonObject { ["c"] = 3 }),
        ]);

        var filtered = await MessageFilters.ContentOnly([assistant], default);

        var items = Assert.IsType<ChatMessageAssistant>(Assert.Single(filtered)).Content.Items!;
        Assert.Equal(2, items.Count);
        Assert.Equal("func1(a=1)\nfunc2(b=2)\nfunc3(c=3)", Assert.IsType<ContentText>(items[1]).Text);
    }

    [Fact]
    public async Task content_only_turns_string_content_into_a_text_item_and_ignores_empty_tool_calls()
    {
        var filtered = await MessageFilters.ContentOnly([new ChatMessageAssistant("Simple string content"), new ChatMessageAssistant("No tool calls", toolCalls: [])], default);

        foreach (var (message, text) in filtered.Zip(new[] { "Simple string content", "No tool calls" }))
        {
            var assistant = Assert.IsType<ChatMessageAssistant>(message);
            Assert.Null(assistant.ToolCalls);
            Assert.Equal(text, Assert.IsType<ContentText>(Assert.Single(assistant.Content.Items!)).Text);
        }
    }

    [Fact]
    public async Task content_only_over_a_conversation_preserves_ids()
    {
        var messages = new ChatMessage[]
        {
            new ChatMessageSystem("System instructions"),
            new ChatMessageUser("What's the weather?") { Id = "user-001" },
            new ChatMessageAssistant(
                new Content[] { new ContentReasoning("I need to check the weather"), new ContentText("Let me check the weather for you.") },
                toolCalls: [new ToolCall("weather-001", "get_weather", new JsonObject { ["location"] = "New York" })]) { Id = "assistant-002" },
            new ChatMessageTool("Temperature: 72°F, Sunny", "weather-001", "get_weather") { Id = "tool-003" },
            new ChatMessageAssistant("The weather in New York is currently 72°F and sunny.") { Id = "assistant-004" },
        };

        var filtered = await MessageFilters.ContentOnly(messages, default);

        Assert.Equal(["user-001", "assistant-002", "tool-003", "assistant-004"], filtered.Select(m => m.Id));
        Assert.Equal(["user", "assistant", "user", "assistant"], filtered.Select(m => m.Role));
        var checking = Assert.IsType<ChatMessageAssistant>(filtered[1]);
        Assert.Null(checking.ToolCalls);
        Assert.Equal(["Let me check the weather for you.", "get_weather(location='New York')"], checking.Content.Items!.Select(c => Assert.IsType<ContentText>(c).Text));
        Assert.Equal("Temperature: 72°F, Sunny", filtered[2].Text);
    }

    [Fact]
    public async Task remove_tools_drops_tool_messages_and_tool_calls()
    {
        var assistant = new ChatMessageAssistant("calling", toolCalls: [new ToolCall("1", "addition", new JsonObject())]);

        var filtered = await MessageFilters.RemoveTools([new ChatMessageUser("hi"), assistant, new ChatMessageTool("2", "1", "addition"), new ChatMessageAssistant("done")], default);

        Assert.Equal(["user", "assistant", "assistant"], filtered.Select(m => m.Role));
        var stripped = Assert.IsType<ChatMessageAssistant>(filtered[1]);
        Assert.Null(stripped.ToolCalls);
        Assert.Equal(assistant.Id, stripped.Id);
        Assert.Equal("calling", stripped.Content.Text);
    }

    [Fact]
    public async Task last_message_keeps_only_the_last_message()
    {
        var last = new ChatMessageAssistant("last");

        Assert.Same(last, Assert.Single(await MessageFilters.LastMessage([new ChatMessageUser("first"), last], default)));
        Assert.Empty(await MessageFilters.LastMessage([], default));
        Assert.Equal(2, (await MessageFilters.Identity([new ChatMessageUser("first"), last], default)).Count);
    }

    [Fact]
    public void trim_messages_keeps_system_and_input_and_the_last_share_of_the_conversation()
    {
        var conversation = Enumerable.Range(0, 10).Select(i => i % 2 == 0 ? (ChatMessage)new ChatMessageAssistant($"a{i}") : new ChatMessageUser($"u{i}")).ToList();
        var messages = new List<ChatMessage> { new ChatMessageSystem("sys"), new ChatMessageUser("input") { Source = "input" } };
        messages.AddRange(conversation);

        var trimmed = MessageFilters.TrimMessagesTo(messages);

        // int(10 * 0.3) = 3 conversation messages are dropped from the front.
        Assert.Equal(9, trimmed.Count);
        Assert.Equal("sys", trimmed[0].Text);
        Assert.Equal("input", trimmed[1].Text);
        Assert.Same(conversation[3], trimmed[2]);
        Assert.Same(conversation[9], trimmed[^1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => MessageFilters.TrimMessagesTo(messages, 1.5));
    }

    [Fact]
    public void trim_messages_drops_orphan_tool_messages_and_orphan_tool_calls_and_never_ends_on_an_assistant()
    {
        var c1 = new ToolCall("c1", "addition", new JsonObject());
        var c2 = new ToolCall("c2", "addition", new JsonObject());
        var c3 = new ToolCall("c3", "addition", new JsonObject());
        var pair = new ChatMessageAssistant("two calls", toolCalls: [c2, c3]);
        var messages = new ChatMessage[]
        {
            new ChatMessageUser("input") { Source = "input" },
            new ChatMessageAssistant("one call", toolCalls: [c1]),
            new ChatMessageTool("2", "c1", "addition"),
            pair,
            new ChatMessageTool("4", "c2", "addition"),
            new ChatMessageUser("ok"),
        };

        // int(5 * 0.3) = 1: the cut lands on tool(c1), whose assistant is gone, so it is dropped too.
        var trimmed = MessageFilters.TrimMessagesTo(messages);

        Assert.Equal(["user", "assistant", "tool", "user"], trimmed.Select(m => m.Role));
        var repaired = Assert.IsType<ChatMessageAssistant>(trimmed[1]);
        Assert.Equal(["c2"], repaired.ToolCalls!.Select(c => c.Id));
        Assert.NotEqual(pair.Id, repaired.Id);

        Assert.Equal(["user"], MessageFilters.TrimMessagesTo([new ChatMessageUser("input") { Source = "input" }, new ChatMessageAssistant("dangling")], 1.0).Select(m => m.Role));
    }

    [Fact]
    public void partition_messages_falls_back_to_the_first_user_message_when_nothing_is_marked_input()
    {
        var partitioned = MessageFilters.PartitionMessages([new ChatMessageSystem("s"), new ChatMessageAssistant("preface"), new ChatMessageUser("q"), new ChatMessageAssistant("a")]);

        Assert.Equal(["s"], partitioned.System.Select(m => m.Text));
        Assert.Equal(["preface", "q"], partitioned.Input.Select(m => m.Text));
        Assert.Equal(["a"], partitioned.Conversation.Select(m => m.Text));

        var marked = MessageFilters.PartitionMessages([new ChatMessageUser("q") { Source = "input" }, new ChatMessageUser("summary") { Metadata = new Dictionary<string, object?> { ["summary"] = true } }, new ChatMessageAssistant("a")]);
        Assert.Equal(["q"], marked.Input.Select(m => m.Text));
        Assert.Equal(["summary", "a"], marked.Conversation.Select(m => m.Text));
    }

    // ---- naming ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Claude Code", "claude_code")]
    [InlineData("  My  Agent  ", "my_agent")]
    [InlineData("Agent#1!", "agent1")]
    [InlineData("already_ok-name", "already_ok-name")]
    [InlineData("🤖", "")]
    public void sanitize_tool_name_matches_python(string name, string expected) => Assert.Equal(expected, Agents.SanitizeToolName(name));

    [Fact]
    public void agent_tool_name_falls_back_when_the_display_name_is_unusable()
    {
        Assert.Equal("agent", Agents.AgentToolName(new AgentDef("🤖", "An agent", (s, _) => Task.FromResult(s))));
        Assert.Equal("transfer_to_agent", Agents.Handoff(new AgentDef("🤖", "An agent", (s, _) => Task.FromResult(s))).Name);
        Assert.Equal("transfer_to_display_name", Agents.Handoff(Agents.AgentWith(Searcher, name: "Display Name")).Name);
    }

    // ---- handoff tool ------------------------------------------------------------------------------------------

    [Fact]
    public void handoff_creates_a_serial_transfer_tool_described_like_the_agent()
    {
        var tool = Agents.Handoff(Searcher);

        Assert.Equal("transfer_to_searcher", tool.Name);
        Assert.Equal("Searcher that computes max searches.", tool.Description);
        Assert.False(tool.Parallel);
        Assert.Empty(tool.Parameters.Properties);
        Assert.NotNull(tool.Handoff);
        Assert.Same(Searcher, tool.Handoff!.Agent);
        Assert.Same(MessageFilters.ContentOnly, tool.Handoff.OutputFilter);
        Assert.Null(tool.Handoff.InputFilter);
        Assert.True(Agents.HasHandoff([Addition, tool]));
        Assert.False(Agents.HasHandoff([Addition]));
        Assert.False(Agents.HasHandoff(null));

        Assert.Equal("Searcher3", Agents.Handoff(Searcher, description: "Searcher3").Description);
        Assert.Equal("search", Agents.Handoff(Searcher, toolName: "search").Name);
    }

    [Fact]
    public async Task handoff_and_as_tool_require_a_description()
    {
        var undocumented = new AgentDef("web_surfer_no_docs", "", (s, _) => Task.FromResult(s));

        Assert.Contains("Description not provided", Assert.Throws<ArgumentException>(() => Agents.Handoff(undocumented)).Message);
        Assert.Contains("Description not provided", Assert.Throws<ArgumentException>(() => Agents.AsTool(undocumented)).Message);
        Assert.Equal("Described", Agents.AsTool(undocumented, description: "Described").Description);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Agents.Handoff(Searcher).Execute(new JsonObject(), CancellationToken.None));
        Assert.Contains("should not be called directly", ex.Message);
    }

    [Fact]
    public async Task a_handoff_runs_the_agent_over_the_rewritten_conversation()
    {
        using var scope = new SampleContextScope();
        IReadOnlyList<ChatMessage>? seen = null;
        var recorder = new AgentDef("searcher", "Records what it sees.", (state, _) =>
        {
            seen = state.Messages.ToList();
            state.Messages.Add(new ChatMessageUser("The maximum searches is 5"));
            return Task.FromResult(state);
        });
        var conversation = new ChatMessage[] { new ChatMessageSystem("parent instructions"), new ChatMessageUser("Please use the searcher."), Transfer("transfer_to_searcher") };

        var result = await HandoffAsync(Agents.Handoff(recorder), conversation);

        Assert.Equal(["user", "assistant", "tool"], seen!.Select(m => m.Role));
        var boundary = Assert.IsType<ChatMessageTool>(seen![^1]);
        Assert.Equal("Successfully transferred to searcher.", boundary.Text);
        Assert.Equal("1", boundary.ToolCallId);
        Assert.Equal("transfer_to_searcher", boundary.Function);

        Assert.Equal(["tool", "user"], result.Messages.Select(m => m.Role));
        Assert.Equal("Successfully transferred to searcher.", result.Messages[0].Text);
        Assert.Equal("The maximum searches is 5", result.Messages[1].Text);
        // Like Python, the output is synthesized from the last assistant message the agent saw (the transfer call).
        Assert.Equal("Call tool", result.Output!.Completion);
        Assert.Equal("transfer_to_searcher", Assert.Single(result.Output.Message.ToolCalls!).Function);

        var toolEvent = Assert.Single(scope.Transcript.Events.OfType<ToolEvent>());
        Assert.Equal("searcher", toolEvent.Agent);
        Assert.Null(toolEvent.Error);
        var spans = scope.Transcript.Events.OfType<SpanBeginEvent>().ToList();
        Assert.Equal([("searcher", "handoff"), ("transfer_to_searcher", "tool"), ("searcher", "agent")], spans.Select(s => (s.Name, s.Type)));
        Assert.Equal(spans[0].Id, spans[1].ParentId);
        Assert.Equal(spans[1].Id, spans[2].ParentId);
    }

    [Fact]
    public async Task a_handoff_hides_sibling_tool_calls_from_the_agent()
    {
        using var scope = new SampleContextScope();
        IReadOnlyList<ChatMessage>? seen = null;
        var recorder = new AgentDef("searcher", "Records what it sees.", (state, _) =>
        {
            seen = state.Messages.ToList();
            return Task.FromResult(state);
        });
        var sibling = new ToolCall("0", "addition", new JsonObject { ["x"] = 1, ["y"] = 1 });
        var conversation = new ChatMessage[] { new ChatMessageUser("Add and search."), Transfer("transfer_to_searcher", "1", sibling) };

        var result = await HandoffAsync(Agents.Handoff(recorder), conversation, Addition);

        var assistant = Assert.IsType<ChatMessageAssistant>(seen![^2]);
        Assert.Equal(["1"], assistant.ToolCalls!.Select(c => c.Id));
        Assert.Equal(["tool", "tool", "user"], result.Messages.Select(m => m.Role));
        Assert.Equal("2", result.Messages[0].Text);
        Assert.Equal("The searcher agent has completed its work.", result.Messages[^1].Text);
    }

    [Fact]
    public async Task a_handoff_prefixes_assistant_messages_with_the_agent_name_and_closes_with_a_user_message()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("max_searches is 5")));
        var conversation = new ChatMessage[] { new ChatMessageUser("Please use the searcher3."), Transfer("transfer_to_searcher3") };

        var result = await HandoffAsync(Agents.Handoff(Searcher3), conversation);

        Assert.Equal(["tool", "assistant", "user"], result.Messages.Select(m => m.Role));
        var assistant = Assert.IsType<ChatMessageAssistant>(result.Messages[1]);
        Assert.Equal("[searcher3] max_searches is 5", assistant.Text);
        Assert.False(assistant.Content.IsString);
        Assert.Equal("The searcher3 agent has completed its work.", result.Messages[2].Text);
        Assert.Equal("max_searches is 5", result.Output!.Completion);
        Assert.Equal(["user", "assistant", "tool"], scope.Api.Requests[0].Input.Select(m => m.Role));
    }

    [Fact]
    public void prepend_agent_name_prefixes_the_first_text_item_only()
    {
        Assert.Equal("[a] hello", Agents.PrependAgentName(new ChatMessageAssistant("hello"), "a").Text);
        var items = Agents.PrependAgentName(new ChatMessageAssistant(new Content[] { new ContentReasoning("r"), new ContentText(""), new ContentText("second") }), "a").Content.Items!;
        Assert.Equal("", Assert.IsType<ContentText>(items[1]).Text);
        Assert.Equal("second", Assert.IsType<ContentText>(items[2]).Text);
        var prefixed = Agents.PrependAgentName(new ChatMessageAssistant(new Content[] { new ContentText("first"), new ContentText("second") }), "a").Content.Items!;
        Assert.Equal(["[a] first", "second"], prefixed.Select(c => Assert.IsType<ContentText>(c).Text));
    }

    [Fact]
    public async Task the_remove_tools_input_filter_hides_prior_tool_use_from_the_agent()
    {
        var conversation = new ChatMessage[]
        {
            new ChatMessageUser("Please use the addition tool to add 1+1. Then, handoff to the tool_checker."),
            new ChatMessageAssistant("adding", toolCalls: [new ToolCall("0", "addition", new JsonObject { ["x"] = 1, ["y"] = 1 })]),
            new ChatMessageTool("2", "0", "addition"),
            Transfer("transfer_to_tool_checker"),
        };

        using (var scope = new SampleContextScope())
        {
            var result = await HandoffAsync(Agents.Handoff(ToolChecker), conversation, Addition);
            Assert.Contains(result.Messages, m => m.Text == "[tool_checker] 4");
        }

        using (var scope = new SampleContextScope())
        {
            IReadOnlyList<ChatMessage>? seen = null;
            var checker = new AgentDef("tool_checker", "Tool checker.", (state, ct) =>
            {
                seen = state.Messages.ToList();
                return ToolChecker.Execute(state, ct);
            });
            var result = await HandoffAsync(Agents.Handoff(checker, inputFilter: MessageFilters.RemoveTools), conversation, Addition);
            Assert.Contains(result.Messages, m => m.Text == "[tool_checker] 0");
            // The tool boundary was filtered away, so a user boundary took its place.
            Assert.Equal("Successfully transferred to tool_checker.", Assert.IsType<ChatMessageUser>(seen![^1]).Text);
            Assert.Equal(["tool", "assistant", "user"], result.Messages.Select(m => m.Role));
        }
    }

    [Fact]
    public async Task the_output_filter_shapes_what_the_parent_sees()
    {
        var conversation = new ChatMessage[] { new ChatMessageUser("Please ask the oracle a question?"), Transfer("transfer_to_oracle") };

        using (var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("first answer"), ScriptedTurn.Text("second answer"))))
        {
            var result = await HandoffAsync(Agents.Handoff(Oracle, outputFilter: MessageFilters.Identity), conversation);
            Assert.Equal(["tool", "assistant", "user", "assistant", "user"], result.Messages.Select(m => m.Role));
            Assert.Equal("[oracle] first answer", result.Messages[1].Text);
            Assert.True(result.Messages[1].Content.IsString);
            Assert.Equal("The oracle agent has completed its work.", result.Messages[^1].Text);
        }

        using (var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("first answer"), ScriptedTurn.Text("second answer"))))
        {
            var result = await HandoffAsync(Agents.Handoff(Oracle, outputFilter: MessageFilters.LastMessage), conversation);
            Assert.Equal(["tool", "assistant", "user"], result.Messages.Select(m => m.Role));
            Assert.Equal("[oracle] second answer", result.Messages[1].Text);
        }

        using (var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("first answer"), ScriptedTurn.Text("second answer"))))
        {
            var result = await HandoffAsync(Agents.Handoff(Oracle), conversation);
            Assert.Equal(["tool", "assistant", "user", "assistant", "user"], result.Messages.Select(m => m.Role));
            Assert.False(result.Messages[1].Content.IsString);
        }
    }

    [Fact]
    public async Task a_handoff_limit_stops_the_agent_and_tells_the_parent()
    {
        using var scope = new SampleContextScope(Hello());
        var handoff = Agents.Handoff(LoopingAgent, limits: new AgentLimits(MessageLimit: 10));
        var conversation = new List<ChatMessage> { new ChatMessageUser("input"), Transfer("transfer_to_looping_agent") };

        var first = await HandoffAsync(handoff, conversation);

        Assert.Equal("The looping_agent exceeded its message limit of 10.", first.Messages[^1].Text);
        // The agent starts from 3 messages (input, transfer call, transfer result) and generates until it holds 10.
        Assert.Equal(7, scope.Api.Requests.Count);
        Assert.Null(Assert.Single(scope.Transcript.Events.OfType<ToolEvent>()).Error);

        // Limits are scoped per handoff, so a second transfer gets a fresh allowance.
        conversation.AddRange(first.Messages);
        conversation.Add(Transfer("transfer_to_looping_agent", "2"));
        var second = await HandoffAsync(handoff, conversation);
        Assert.Equal("The looping_agent exceeded its message limit of 10.", second.Messages[^1].Text);
    }

    [Fact]
    public async Task a_sample_limit_inside_a_handoff_is_reported_the_same_way()
    {
        using var scope = new SampleContextScope(Hello(), limits: new Limits { MessageLimit = 10 });
        var conversation = new ChatMessage[] { new ChatMessageUser("input"), Transfer("transfer_to_looping_agent") };

        var result = await HandoffAsync(Agents.Handoff(LoopingAgent), conversation);

        Assert.Equal("The looping_agent exceeded its message limit of 10.", result.Messages[^1].Text);
    }

    [Fact]
    public async Task a_handoff_token_limit_counts_the_agents_usage()
    {
        using var scope = new SampleContextScope(Hello());
        var conversation = new ChatMessage[] { new ChatMessageUser("input"), Transfer("transfer_to_looping_agent") };

        var result = await HandoffAsync(Agents.Handoff(LoopingAgent, limits: new AgentLimits(TokenLimit: 5)), conversation);

        Assert.Equal("The looping_agent exceeded its token limit of 5.", result.Messages[^1].Text);
        Assert.Equal(6, scope.Api.Requests.Count);
    }

    // ---- as_tool -----------------------------------------------------------------------------------------------

    [Fact]
    public void as_tool_exposes_an_input_parameter_and_is_not_truncated_by_default()
    {
        var tool = Agents.AsTool(WebSurfer);

        Assert.Equal("web_surfer", tool.Name);
        Assert.Equal("Web surfer for conducting web research into a topic.", tool.Description);
        Assert.Equal(["input"], tool.Parameters.Properties.Keys);
        Assert.Equal("Input message.", tool.Parameters.Properties["input"].Description);
        Assert.Equal(["input"], tool.Parameters.Required);
        Assert.Equal(0, tool.MaxOutput);
        Assert.True(tool.Parallel);
        Assert.Null(tool.Handoff);
        Assert.Equal(2048, Agents.AsTool(WebSurfer, maxOutput: 2048).MaxOutput);
        Assert.Null(Agents.AsTool(WebSurfer, maxOutput: null).MaxOutput);
    }

    [Fact]
    public async Task as_tool_returns_the_agents_output_content()
    {
        using var scope = new SampleContextScope();
        IReadOnlyList<ChatMessage>? seen = null;
        var recorder = new AgentDef("web_surfer", "Surfs.", (state, _) =>
        {
            seen = state.Messages.ToList();
            state.Output = new ModelOutput { Completion = "22" };
            return Task.FromResult(state);
        });

        Assert.Equal("22", (await Agents.AsTool(WebSurfer).Execute(new JsonObject { ["input"] = "This is the input" }, CancellationToken.None)).AsText());
        await Agents.AsTool(recorder).Execute(new JsonObject { ["input"] = "This is the input" }, CancellationToken.None);
        var input = Assert.IsType<ChatMessageUser>(Assert.Single(seen!));
        Assert.Equal("This is the input", input.Text);
        Assert.Equal("input", input.Source);

        var lastAssistant = new AgentDef("a", "Appends.", (state, _) =>
        {
            state.Messages.Add(new ChatMessageAssistant(new Content[] { new ContentText("report"), new ContentImage("data:image/png;base64,AAAA") }));
            return Task.FromResult(state);
        });
        var contents = await Agents.AsTool(lastAssistant).Execute(new JsonObject { ["input"] = "go" }, CancellationToken.None);
        Assert.Equal(2, contents.Contents!.Count);
        Assert.Equal("report", contents.AsText());

        var silent = new AgentDef("s", "Says nothing.", (state, _) => Task.FromResult(state));
        Assert.Equal("", (await Agents.AsTool(silent).Execute(new JsonObject { ["input"] = "go" }, CancellationToken.None)).AsText());

        await Assert.ThrowsAsync<ToolParsingError>(() => Agents.AsTool(silent).Execute(new JsonObject { ["input"] = 3 }, CancellationToken.None));
        Assert.Contains(scope.Transcript.Events, e => e is SpanBeginEvent { Name: "web_surfer", Type: "agent" });
    }

    [Fact]
    public async Task as_tool_limits_surface_as_a_tool_error()
    {
        using var scope = new SampleContextScope(Hello());
        var tool = Agents.AsTool(LoopingAgent, limits: new AgentLimits(MessageLimit: 10));
        var call = new ChatMessageAssistant("Call tool", toolCalls: [new ToolCall("1", "looping_agent", new JsonObject { ["input"] = "input" })]);

        var result = await ToolExecutor.ExecuteToolsAsync([call], [tool]);

        var message = Assert.IsType<ChatMessageTool>(Assert.Single(result.Messages));
        Assert.Equal("limit", message.Error!.Type);
        Assert.Equal("The tool exceeded its message limit of 10.", message.Error.Message);
        // The agent starts from the single input message and generates until it holds 10.
        Assert.Equal(9, scope.Api.Requests.Count);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task as_tool_respects_the_sample_limits()
    {
        using var scope = new SampleContextScope(Hello(), limits: new Limits { MessageLimit = 10 });
        var call = new ChatMessageAssistant("Call tool", toolCalls: [new ToolCall("1", "looping_agent", new JsonObject { ["input"] = "input" })]);

        var result = await ToolExecutor.ExecuteToolsAsync([call], [Agents.AsTool(LoopingAgent)]);

        Assert.Equal("The tool exceeded its message limit of 10.", Assert.IsType<ChatMessageTool>(result.Messages[0]).Error!.Message);
    }

    // ---- run ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task run_returns_the_agents_state_inside_an_agent_span()
    {
        using var scope = new SampleContextScope();

        var result = await Agents.RunAsync(WebSurfer, "This is the input");

        Assert.Equal("22", result.State.Output.Completion);
        Assert.Null(result.LimitError);
        var input = Assert.IsType<ChatMessageUser>(Assert.Single(result.State.Messages));
        Assert.Equal("input", input.Source);
        Assert.Contains(scope.Transcript.Events, e => e is SpanBeginEvent { Name: "web_surfer", Type: "agent" });

        await Agents.RunAsync(WebSurfer, "This is the input", name: "my-agent");
        Assert.Contains(scope.Transcript.Events, e => e is SpanBeginEvent { Name: "my-agent", Type: "agent" });
    }

    [Fact]
    public async Task run_copies_the_input_messages()
    {
        var original = new ChatMessageUser("hello");
        var input = new List<ChatMessage> { original };

        var result = await Agents.RunAsync(Searcher, input);

        Assert.Single(input);
        Assert.Null(original.Source);
        Assert.Equal(2, result.State.Messages.Count);
        Assert.Equal("input", result.State.Messages[0].Source);
        Assert.Equal(original.Id, result.State.Messages[0].Id);
    }

    [Fact]
    public async Task run_reports_its_own_limit_and_the_state_the_agent_reached()
    {
        using var scope = new SampleContextScope(Hello());

        var result = await Agents.RunAsync(LoopingAgent, "This is the input", new AgentLimits(MessageLimit: 10));

        Assert.NotNull(result.LimitError);
        Assert.Equal("message", result.LimitError!.Type);
        Assert.Equal(10, result.State.Messages.Count);
        Assert.Equal(10, result.LimitError.Value);

        var fine = await Agents.RunAsync(WebSurfer, "This is the input", new AgentLimits(TokenLimit: 100));
        Assert.Null(fine.LimitError);
        Assert.Equal("22", fine.State.Output.Completion);
    }

    [Fact]
    public async Task run_does_not_catch_a_parent_scopes_limit()
    {
        using var scope = new SampleContextScope(Hello());
        using var outer = AgentLimitScope.Apply(new AgentLimits(TokenLimit: 10));

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => Agents.RunAsync(LoopingAgent, "This is the input", new AgentLimits(TokenLimit: 100)));

        Assert.Equal("token", ex.Type);
        Assert.Equal(11, ex.Value);
        Assert.Equal("10", ex.LimitStr);
        Assert.True(outer.Owns(ex));
        Assert.Equal(11, outer.Usage.TotalTokens);
    }

    [Fact]
    public async Task run_does_not_catch_a_scoped_sample_limit_it_nests_under()
    {
        using var scope = new SampleContextScope(Hello());
        // the sample-level scoped limit of the runner: the agent's own scope nests under it on the same tree
        using var sample = Limit.Apply(new TokenLimit(10));

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => Agents.RunAsync(LoopingAgent, "This is the input", new AgentLimits(TokenLimit: 100)));

        Assert.Equal("token", ex.Type);
        Assert.Same(sample.Limits[0], ex.SourceLimit);
        Assert.Equal(11, ((TokenLimit)sample.Limits[0]).Usage);
        Assert.Contains(scope.Transcript.Events.OfType<SampleLimitEvent>(), e => e.Type == "token" && e.Limit == 10);
    }

    [Fact]
    public async Task run_does_not_catch_the_sample_limit()
    {
        using var scope = new SampleContextScope(Hello(), limits: new Limits { MessageLimit = 5 });

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => Agents.RunAsync(LoopingAgent, "This is the input", new AgentLimits(MessageLimit: 100)));

        Assert.Equal("message", ex.Type);
        Assert.Null(ex.SourceLimit);
    }

    [Fact]
    public async Task run_reports_its_time_limit()
    {
        var slow = new AgentDef("slow", "Waits.", async (state, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return state;
        });

        var result = await Agents.RunAsync(slow, "go", new AgentLimits(TimeLimit: TimeSpan.FromMilliseconds(50)));

        Assert.Equal("time", result.LimitError!.Type);
        Assert.Contains("Time limit exceeded", result.LimitError.Message);

        // The caller's own cancellation is not a limit error.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Agents.RunAsync(slow, "go", new AgentLimits(TimeLimit: TimeSpan.FromSeconds(30)), cancellationToken: cts.Token));
    }

    [Fact]
    public void agent_with_replaces_name_and_description_and_is_agent_recognises_agent_defs()
    {
        var renamed = Agents.AgentWith(Searcher, name: "Renamed Agent");
        Assert.Equal("Renamed Agent", renamed.Name);
        Assert.Equal(Searcher.Description, renamed.Description);
        Assert.Same(Searcher.Execute, renamed.Execute);
        Assert.Equal("searcher", Searcher.Name);
        Assert.Equal("Other", Agents.AgentWith(Searcher, description: "Other").Description);
        Assert.True(Agents.IsAgent(Searcher));
        Assert.False(Agents.IsAgent(Addition));
        Assert.False(Agents.IsAgent(null));
    }

    [Fact]
    public void limit_scopes_validate_and_nest()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentLimitScope.Apply(new AgentLimits(MessageLimit: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentLimitScope.Apply(new AgentLimits(TokenLimit: -1)));
        Assert.Null(AgentLimitScope.Current);

        using (var outer = AgentLimitScope.Apply(new AgentLimits(TokenLimit: 3)))
        {
            Assert.Same(outer, AgentLimitScope.Current);
            using (var inner = AgentLimitScope.Apply(new AgentLimits(TokenLimit: 2, MessageLimit: 4)))
            {
                Assert.Same(inner, AgentLimitScope.Current);
                AgentLimitScope.RecordUsage(new ModelUsage(TotalTokens: 2));
                AgentLimitScope.CheckMessageLimit(3);
                Assert.Equal(2, outer.Usage.TotalTokens);
                // Both limits are exceeded; the outermost wins.
                var ex = Assert.Throws<LimitExceededException>(() => AgentLimitScope.RecordUsage(new ModelUsage(TotalTokens: 2)));
                Assert.True(outer.Owns(ex));
                Assert.Equal("3", ex.LimitStr);
                var messages = Assert.Throws<LimitExceededException>(() => AgentLimitScope.CheckMessageLimit(4));
                Assert.True(inner.Owns(messages));
                Assert.Contains("reached", messages.Message);
            }

            Assert.Same(outer, AgentLimitScope.Current);
            AgentLimitScope.CheckMessageLimit(1000);
        }

        Assert.Null(AgentLimitScope.Current);
    }

    [Fact]
    public void a_tool_events_agent_round_trips_through_the_log_json()
    {
        var e = new ToolEvent("1", "transfer_to_searcher", new JsonObject(), "Successfully transferred to searcher.") { Agent = "searcher" };

        var json = JsonSerializer.Serialize<TranscriptEvent>(e, EvalLogWriter.Options);
        Assert.Equal("searcher", JsonNode.Parse(json)!["agent"]!.GetValue<string>());
        var read = Assert.IsType<ToolEvent>(JsonSerializer.Deserialize<TranscriptEvent>(json, EvalLogWriter.Options));
        Assert.Equal("searcher", read.Agent);

        var plain = JsonSerializer.Serialize<TranscriptEvent>(new ToolEvent("1", "addition", new JsonObject(), "2"), EvalLogWriter.Options);
        Assert.Null(JsonNode.Parse(plain)!["agent"]);
    }
}
