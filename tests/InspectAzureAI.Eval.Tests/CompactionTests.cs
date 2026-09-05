using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model.Compaction;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>
/// Port-level behaviour of <c>model/_trim.py</c> and <c>model/_compaction/*</c>: trimming, the strategies, the
/// orchestrator's threshold triggering and event, and the BasicAgent hook.
/// </summary>
public class CompactionTests
{
    private static ChatMessageSystem System(string text, string? id = null) => new(text) { Id = id ?? ShortUuid.Generate() };

    private static ChatMessageUser User(string text, string? id = null, string? source = null) => new(text) { Id = id ?? ShortUuid.Generate(), Source = source };

    private static ChatMessageAssistant Assistant(string text, string? id = null, IReadOnlyList<ToolCall>? toolCalls = null) => new(text, toolCalls) { Id = id ?? ShortUuid.Generate() };

    private static ChatMessageTool Tool(string content, string toolCallId, string function, string? id = null) => new(content, toolCallId, function) { Id = id ?? ShortUuid.Generate() };

    private static ToolCall Call(string id, string function, object? args = null) =>
        new(id, function, args is null ? new JsonObject() : JsonSerializer.SerializeToNode(args)!.AsObject());

    private static Model ScriptedModel(params ScriptedTurn[] turns) => new(new ScriptedModelApi(turns));

    private static List<ChatMessage> Pairs(int count, ChatMessage? first = null)
    {
        var messages = new List<ChatMessage>();
        if (first is not null)
        {
            messages.Add(first);
        }

        for (var i = 0; i < count; i++)
        {
            messages.Add(User($"User message {i}"));
            messages.Add(Assistant($"Assistant message {i}"));
        }

        return messages;
    }

    /// <summary>A scripted api that also answers the compaction hooks (token counts, context window, native compaction).</summary>
    private sealed class CountingApi(ScriptedModelApi inner) : IModelApi, ICompactionModelApi
    {
        public ScriptedModelApi Inner => inner;

        public Func<IReadOnlyList<ChatMessage>, int>? Count { get; init; }

        public Func<IReadOnlyList<ChatMessage>, Task<int>>? CountAsync { get; init; }

        public int? Window { get; init; }

        public bool CanCompactReasoning { get; init; } = true;

        public bool RedactedReasoningHidden { get; init; }

        public Func<IReadOnlyList<ChatMessage>, NativeCompactionResult>? Native { get; init; }

        public string ModelName => inner.ModelName;

        public int? MaxTokens() => inner.MaxTokens();

        public Task<GenerateResult> GenerateAsync(
            IReadOnlyList<ChatMessage> input,
            IReadOnlyList<ToolInfo> tools,
            ToolChoice toolChoice,
            GenerateConfig config,
            StreamHandler? onStream,
            CancellationToken cancellationToken = default) =>
            inner.GenerateAsync(input, tools, toolChoice, config, onStream, cancellationToken);

        int? ICompactionModelApi.ContextWindow => Window;

        bool ICompactionModelApi.CompactReasoningHistory => CanCompactReasoning;

        bool ICompactionModelApi.ApplyRedactedReasoningTokensToInput => RedactedReasoningHidden;

        Task<int> ICompactionModelApi.CountTokensAsync(IReadOnlyList<ChatMessage> input, CancellationToken cancellationToken) =>
            CountAsync?.Invoke(input) ?? Task.FromResult(Count?.Invoke(input) ?? TokenEstimator.CountTokens(input));

        Task<NativeCompactionResult> ICompactionModelApi.CompactAsync(
            IReadOnlyList<ChatMessage> input,
            IReadOnlyList<ToolInfo> tools,
            GenerateConfig config,
            string? instructions,
            CancellationToken cancellationToken) =>
            Native is null ? throw new NotSupportedException("CountingApi does not support native compaction.") : Task.FromResult(Native(input));
    }

    /// <summary>An api that is neither scripted nor compaction-aware: the Python "unknown context window" path.</summary>
    private sealed class PlainApi(ScriptedModelApi inner) : IModelApi
    {
        public string ModelName => "plain";

        public int? MaxTokens() => null;

        public Task<GenerateResult> GenerateAsync(
            IReadOnlyList<ChatMessage> input,
            IReadOnlyList<ToolInfo> tools,
            ToolChoice toolChoice,
            GenerateConfig config,
            StreamHandler? onStream,
            CancellationToken cancellationToken = default) =>
            inner.GenerateAsync(input, tools, toolChoice, config, onStream, cancellationToken);
    }

    /// <summary>Port of the test-only <c>ConsecutiveUserCompaction</c>: returns an input message followed by a summary.</summary>
    private sealed class ConsecutiveUserCompaction() : CompactionStrategy("summary", CompactionThreshold.FromTokens(1_000_000), true)
    {
        public override Task<CompactionResult> CompactAsync(Model model, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools, CancellationToken cancellationToken = default)
        {
            var summary = new ChatMessageUser("summary") { Id = "summary", Metadata = new Dictionary<string, object?> { ["summary"] = true } };
            return Task.FromResult(new CompactionResult([User("input", "input", source: "input"), summary], summary));
        }
    }

    // ------------------------------------------------------------------ TrimMessages

    [Fact]
    public void trim_of_an_empty_list_is_empty() => Assert.Empty(TrimMessages.Trim([]));

    [Fact]
    public void trim_keeps_everything_but_a_trailing_assistant_message()
    {
        List<ChatMessage> messages = [System("You are a helpful assistant."), User("Hello!"), Assistant("How can I help you today?")];

        Assert.Equal(messages.Take(2), TrimMessages.Trim(messages));
        Assert.Equal(messages.Skip(1).Take(1), TrimMessages.Trim(messages.Skip(1).ToList()));
    }

    [Fact]
    public void trim_preserves_a_ratio_of_the_conversation_after_system_and_input()
    {
        var system = System("You are a helpful assistant.");
        var messages = Pairs(10, system);

        var trimmed = TrimMessages.Trim(messages, preserve: 0.5);

        Assert.Equal(2 + 10 - 1, trimmed.Count);
        Assert.Same(system, trimmed[0]);
        Assert.Equal("User message 0", trimmed[1].Text);
        Assert.Equal("User message 5", trimmed[2].Text);
    }

    [Fact]
    public void trim_drops_a_tool_message_without_its_assistant_call()
    {
        var tool = Tool("{\"result\": \"orphaned\"}", "orphaned", "orphaned_function");
        var user = User("User message");

        var trimmed = TrimMessages.Trim([user, tool]);

        Assert.DoesNotContain(tool, trimmed);
        Assert.Contains(user, trimmed);
    }

    [Fact]
    public void trim_keeps_tool_messages_whose_assistant_call_survives()
    {
        var assistant = Assistant("Let me check the weather for you.", toolCalls: [Call("tool1", "get_weather", new { location = "London" })]);
        var response = Tool("{\"temperature\": 22}", "tool1", "get_weather");
        List<ChatMessage> messages =
        [
            System("You are a helpful assistant."),
            User("Hello!"),
            Assistant("Hi there"),
            User("How can you help me today?"),
            assistant,
            response,
            User("Thanks for the weather info!"),
        ];

        Assert.Equal(messages, TrimMessages.Trim(messages, preserve: 1));
        var trimmed = TrimMessages.Trim(messages, preserve: 0.5);
        Assert.Contains(assistant, trimmed);
        Assert.Contains(response, trimmed);
    }

    [Fact]
    public void trim_user_message_resets_the_active_tool_ids()
    {
        var assistant = Assistant("First tool call", toolCalls: [Call("tool1", "func1")]);
        var user = User("User interruption");
        var tool = Tool("{\"result\": \"result1\"}", "tool1", "func1");

        var trimmed = TrimMessages.Trim([assistant, user, tool]);

        Assert.Contains(assistant, trimmed);
        Assert.Contains(user, trimmed);
        Assert.DoesNotContain(tool, trimmed);
    }

    [Fact]
    public void trim_always_keeps_input_messages_and_the_first_user_when_none_are_marked()
    {
        var system = System("S");
        var inputUser = User("User input", source: "input");
        var inputAssistant = Assistant("Assistant input") with { Source = "input" };
        var conversationUser = User("User conversation");
        var conversationAssistant = Assistant("Assistant conversation");

        var trimmed = TrimMessages.Trim([system, inputUser, inputAssistant, conversationUser, conversationAssistant], preserve: 0);
        Assert.Equal([system, inputUser, inputAssistant], trimmed);

        var user1 = User("First user");
        var unmarked = TrimMessages.Trim([system, user1, Assistant("First assistant"), User("Second user"), Assistant("Second assistant")], preserve: 0);
        Assert.Equal([system, user1], unmarked);
    }

    [Fact]
    public void trim_rejects_a_preserve_ratio_outside_zero_to_one()
    {
        var messages = Pairs(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => TrimMessages.Trim(messages, preserve: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrimMessages.Trim(messages, preserve: -0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrimMessages.Trim(messages, preserve: double.NaN));
    }

    [Fact]
    public void trim_keeps_whole_assistant_tool_chains()
    {
        var assistant1 = Assistant("First tool call", toolCalls: [Call("tool1", "func1")]);
        var tool1 = Tool("r1", "tool1", "func1");
        var assistant2 = Assistant("Second tool call", toolCalls: [Call("tool2", "func2")]);
        var tool2 = Tool("r2", "tool2", "func2");

        var trimmed = TrimMessages.Trim([User("Can you help?"), assistant1, tool1, assistant2, tool2], preserve: 0.5);

        Assert.DoesNotContain(assistant1, trimmed);
        Assert.DoesNotContain(tool1, trimmed);
        Assert.Contains(assistant2, trimmed);
        Assert.Contains(tool2, trimmed);

        var chains = new List<ChatMessage>();
        for (var i = 0; i < 5; i++)
        {
            chains.Add(User($"User message {i}"));
            chains.Add(Assistant($"Assistant message {i}", toolCalls: [Call($"tool{i}", $"func{i}")]));
            chains.Add(Tool($"result{i}", $"tool{i}", $"func{i}"));
        }

        var kept = TrimMessages.Trim(chains, preserve: 0.4);
        Assert.Equal(2, kept.OfType<ChatMessageAssistant>().Count());
        Assert.Equal(2, kept.OfType<ChatMessageTool>().Count());
        var callIds = kept.OfType<ChatMessageAssistant>().SelectMany(m => m.ToolCalls ?? []).Select(tc => tc.Id).ToHashSet();
        Assert.All(kept.OfType<ChatMessageTool>(), t => Assert.Contains(t.ToolCallId!, callIds));
    }

    [Fact]
    public void trim_removes_orphan_tool_calls_and_nulls_them_when_all_are_orphaned()
    {
        var assistant = Assistant("Making multiple tool calls", toolCalls: [Call("tool1", "func1"), Call("tool2", "func2"), Call("tool3", "func3")]);
        var trimmed = TrimMessages.Trim([User("User message"), assistant, Tool("r1", "tool1", "func1")], preserve: 1.0);

        var kept = Assert.Single(trimmed.OfType<ChatMessageAssistant>());
        Assert.NotSame(assistant, kept);
        Assert.NotEqual(assistant.Id, kept.Id);
        Assert.Equal(["tool1"], kept.ToolCalls!.Select(tc => tc.Id));

        var allOrphaned = Assistant("Making tool calls", toolCalls: [Call("tool1", "func1"), Call("tool2", "func2")]);
        var result = TrimMessages.Trim([User("User message"), allOrphaned, User("Follow up")], preserve: 1.0);
        Assert.Null(Assert.Single(result.OfType<ChatMessageAssistant>()).ToolCalls);

        // orphans created by the trim itself are cleaned up too
        List<ChatMessage> messages =
        [
            User("First user", source: "input"),
            Assistant("Early assistant", toolCalls: [Call("early1", "func1")]),
            Tool("early", "early1", "func1"),
            User("Middle user"),
            Assistant("Later assistant", toolCalls: [Call("later1", "func1"), Call("later2", "func2")]),
            Tool("later1", "later1", "func1"),
            Tool("later2", "later2", "func2"),
        ];
        var half = TrimMessages.Trim(messages, preserve: 0.5);
        var resultIds = half.OfType<ChatMessageTool>().Select(t => t.ToolCallId).ToHashSet();
        foreach (var tc in half.OfType<ChatMessageAssistant>().SelectMany(m => m.ToolCalls ?? []))
        {
            Assert.Contains(tc.Id, resultIds);
        }
    }

    [Fact]
    public void partition_separates_system_input_and_conversation()
    {
        var system = System("S");
        var input = User("User input", source: "input");
        var user = User("User conversation");
        var assistant = Assistant("Assistant conversation");

        var partitioned = TrimMessages.Partition([system, input, user, assistant]);
        Assert.Equal([system], partitioned.System);
        Assert.Equal([input], partitioned.Input);
        Assert.Equal([user, assistant], partitioned.Conversation);

        var user1 = User("First user");
        var assistant1 = Assistant("First assistant");
        var user2 = User("Second user");
        var unmarked = TrimMessages.Partition([system, user1, assistant1, user2]);
        Assert.Equal([user1], unmarked.Input);
        Assert.Equal([assistant1, user2], unmarked.Conversation);

        var empty = TrimMessages.Partition([]);
        Assert.Empty(empty.System);
        Assert.Empty(empty.Input);
        Assert.Empty(empty.Conversation);

        var onlyAssistant = TrimMessages.Partition([assistant]);
        Assert.Equal([assistant], onlyAssistant.Input);
        Assert.Empty(onlyAssistant.Conversation);

        var summary = User("summary") with { Metadata = new Dictionary<string, object?> { ["summary"] = true } };
        var withSummary = TrimMessages.Partition([summary, user]);
        Assert.Empty(withSummary.Input);
        Assert.Equal([summary, user], withSummary.Conversation);
    }

    // ------------------------------------------------------------------ CompactionMemory

    [Fact]
    public void clear_memory_content_replaces_content_arguments_and_keeps_metadata_arguments()
    {
        var memory = Assistant("Saving", toolCalls: [Call("mem1", "memory", new { command = "create", path = "/memories/notes.txt", file_text = "Some saved content" })]);
        var other = Assistant("Other", toolCalls: [Call("t1", "bash", new { command = "ls" })]);

        var cleared = CompactionMemory.ClearMemoryContent([memory, other, User("u")]);

        var edited = Assert.IsType<ChatMessageAssistant>(cleared[0]);
        Assert.NotEqual(memory.Id, edited.Id);
        var arguments = edited.ToolCalls![0].Arguments;
        Assert.Equal("create", arguments["command"]!.GetValue<string>());
        Assert.Equal("/memories/notes.txt", arguments["path"]!.GetValue<string>());
        Assert.Equal(CompactionMemory.ContentSavedPlaceholder, arguments["file_text"]!.GetValue<string>());
        Assert.Equal("Some saved content", memory.ToolCalls![0].Arguments["file_text"]!.GetValue<string>());
        Assert.Same(other, cleared[1]);
        Assert.True(CompactionMemory.HasMemoryCalls([memory]));
        Assert.False(CompactionMemory.HasMemoryCalls([other]));
        Assert.StartsWith("Context compaction approaching.", CompactionMemory.MemoryWarningMessage().Text);
    }

    // ------------------------------------------------------------------ CompactionEdit

    [Fact]
    public async Task edit_clears_thinking_from_all_but_the_most_recent_turns()
    {
        var strategy = new CompactionEdit(keepThinkingTurns: 1, keepToolUses: 10);
        List<ChatMessage> messages =
        [
            System("System prompt"),
            User("Question 1"),
            Assistant("") with { Content = new Content[] { new ContentReasoning("Thinking about question 1"), new ContentText("Answer 1") } },
            User("Question 2"),
            Assistant("") with { Content = new Content[] { new ContentReasoning("Thinking about question 2"), new ContentText("Answer 2") } },
            User("Follow up"),
        ];

        var (compacted, summary) = await strategy.CompactAsync(ScriptedModel(), messages, []);

        Assert.Null(summary);
        Assert.Equal(6, compacted.Count);
        var first = Assert.IsType<ChatMessageAssistant>(compacted[2]);
        Assert.Equal("Answer 1", Assert.IsType<ContentText>(Assert.Single(first.Content.Items!)).Text);
        Assert.NotEqual(messages[2].Id, first.Id);
        var last = Assert.IsType<ChatMessageAssistant>(compacted[4]);
        Assert.Equal(2, last.Content.Items!.Count);
        Assert.IsType<ContentReasoning>(last.Content.Items![0]);

        var (all, _) = await new CompactionEdit(keepThinkingTurns: null, keepToolUses: 10).CompactAsync(ScriptedModel(), messages, []);
        Assert.All(all.OfType<ChatMessageAssistant>(), m => Assert.Contains(m.Content.Items!, c => c is ContentReasoning));

        var optedOut = new Model(new CountingApi(new ScriptedModelApi()) { CanCompactReasoning = false });
        var (preserved, _) = await strategy.CompactAsync(optedOut, messages, []);
        Assert.All(preserved.OfType<ChatMessageAssistant>(), m => Assert.Contains(m.Content.Items!, c => c is ContentReasoning));

        var onlyReasoning = Assistant("") with { Content = new Content[] { new ContentReasoning("only") } };
        var (emptied, _) = await new CompactionEdit(keepThinkingTurns: 0).CompactAsync(ScriptedModel(), [User("q"), onlyReasoning, User("u")], []);
        Assert.Equal("", Assert.IsType<ChatMessageAssistant>(emptied[1]).Content.Text);
    }

    [Fact]
    public async Task edit_clears_the_oldest_tool_results_and_keeps_the_calls()
    {
        var strategy = new CompactionEdit(keepThinkingTurns: null, keepToolUses: 2);
        List<ChatMessage> messages =
        [
            User("Start"),
            Assistant("Using tool 1", toolCalls: [Call("tool1", "get_weather")]),
            Tool("Weather: Sunny", "tool1", "get_weather"),
            Assistant("Using tool 2", toolCalls: [Call("tool2", "get_time")]),
            Tool("Time: 12:00", "tool2", "get_time"),
            Assistant("Using tool 3", toolCalls: [Call("tool3", "search")]),
            Tool("Search results", "tool3", "search"),
            Assistant("Using tool 4", toolCalls: [Call("tool4", "read_file")]),
            Tool("File contents", "tool4", "read_file"),
        ];

        var (compacted, _) = await strategy.CompactAsync(ScriptedModel(), messages, []);

        Assert.Equal(9, compacted.Count);
        Assert.Equal(CompactionEdit.ToolResultRemoved, compacted[2].Text);
        Assert.Equal(CompactionEdit.ToolResultRemoved, compacted[4].Text);
        Assert.NotEqual(messages[2].Id, compacted[2].Id);
        Assert.Equal("Search results", compacted[6].Text);
        Assert.Equal("File contents", compacted[8].Text);
        Assert.Single(Assert.IsType<ChatMessageAssistant>(compacted[1]).ToolCalls!);
        Assert.Equal("Weather: Sunny", messages[2].Text);

        var (allCleared, _) = await new CompactionEdit(keepThinkingTurns: null, keepToolUses: 0).CompactAsync(ScriptedModel(), messages, []);
        Assert.All(allCleared.OfType<ChatMessageTool>(), t => Assert.Equal(CompactionEdit.ToolResultRemoved, t.Text));

        var (excluded, _) = await new CompactionEdit(keepThinkingTurns: null, keepToolUses: 1, excludeTools: ["get_weather"]).CompactAsync(ScriptedModel(), messages, []);
        Assert.Equal("Weather: Sunny", excluded[2].Text);
        Assert.Equal(CompactionEdit.ToolResultRemoved, excluded[4].Text);
        Assert.Equal(CompactionEdit.ToolResultRemoved, excluded[6].Text);
        Assert.Equal("File contents", excluded[8].Text);
    }

    [Fact]
    public async Task edit_removes_calls_and_results_when_tool_inputs_are_not_kept()
    {
        var strategy = new CompactionEdit(keepThinkingTurns: null, keepToolUses: 1, keepToolInputs: false);
        List<ChatMessage> messages =
        [
            User("Start"),
            Assistant("Using multiple tools", toolCalls: [Call("tool1", "get_weather"), Call("tool2", "get_time")]),
            Tool("Weather: Sunny", "tool1", "get_weather"),
            Tool("Time: 12:00", "tool2", "get_time"),
            Assistant("Using tool 3", toolCalls: [Call("tool3", "search")]),
            Tool("Search results", "tool3", "search"),
        ];

        var (compacted, _) = await strategy.CompactAsync(ScriptedModel(), messages, []);

        Assert.Equal(4, compacted.Count);
        var first = Assert.IsType<ChatMessageAssistant>(compacted[1]);
        Assert.Null(first.ToolCalls);
        var placeholders = first.Content.Items!.OfType<ContentText>().Select(c => c.Text).ToList();
        Assert.Equal("Using multiple tools", placeholders[0]);
        Assert.Contains(placeholders, t => t.Contains("get_weather") && t.Contains("removed from history"));
        Assert.Contains(placeholders, t => t.Contains("get_time"));
        Assert.Equal("Search results", compacted[3].Text);
        Assert.Equal(6, messages.Count);
    }

    [Fact]
    public async Task edit_strips_trailing_assistant_messages_and_clears_memory_content()
    {
        var memory = Assistant("Saving", toolCalls: [Call("mem1", "memory", new { command = "create", path = "/m", file_text = "content" })]);
        var trailing = Assistant("done");
        List<ChatMessage> messages = [User("q"), memory, Tool("saved", "mem1", "memory"), User("next"), trailing];

        var (compacted, _) = await new CompactionEdit(keepToolUses: 10).CompactAsync(ScriptedModel(), messages, []);
        Assert.Equal(4, compacted.Count);
        Assert.Equal(CompactionMemory.ContentSavedPlaceholder, Assert.IsType<ChatMessageAssistant>(compacted[1]).ToolCalls![0].Arguments["file_text"]!.GetValue<string>());

        var (preserved, _) = await new CompactionEdit(memory: false, keepToolUses: 10).CompactAsync(ScriptedModel(), messages, []);
        Assert.Same(memory, preserved[1]);

        var (trimmed, _) = await new CompactionTrim(preserve: 1.0).CompactAsync(ScriptedModel(), messages, []);
        Assert.Equal(CompactionMemory.ContentSavedPlaceholder, Assert.IsType<ChatMessageAssistant>(trimmed[1]).ToolCalls![0].Arguments["file_text"]!.GetValue<string>());
        var (untouched, _) = await new CompactionTrim(memory: false, preserve: 1.0).CompactAsync(ScriptedModel(), messages, []);
        Assert.Same(memory, untouched[1]);
    }

    [Fact]
    public void edit_rejects_negative_budgets_and_the_base_rejects_unknown_types()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompactionEdit(keepThinkingTurns: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompactionEdit(keepToolUses: -1));
        Assert.Equal("all", new CompactionEdit(keepThinkingTurns: null).ReprParams()["keep_thinking_turns"]);
        Assert.Equal(0.9, new CompactionEdit().Threshold.Fraction);
        Assert.Equal(5000, ((CompactionThreshold)5000.0).Tokens);
        Assert.Equal(0.5, ((CompactionThreshold)0.5).Fraction);
        Assert.Equal(450, CompactionThreshold.FromFraction(0.9).Resolve(500));
        Assert.Equal(0.9, default(CompactionThreshold).Fraction);
    }

    // ------------------------------------------------------------------ CompactionSummary

    [Fact]
    public async Task summary_replaces_the_conversation_with_a_tagged_summary_message()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("THE SUMMARY"));
        var model = new Model(api);
        var system = System("You are a helpful assistant.");
        var input = User("What is 2+2?", source: "input");
        List<ChatMessage> messages = [system, input, Assistant("Let me think."), User("Please answer."), Assistant("The answer is 4.")];

        var (compacted, summary) = await new CompactionSummary().CompactAsync(model, messages, []);

        Assert.NotNull(summary);
        Assert.True((bool)summary.Metadata!["summary"]!);
        Assert.Contains("[CONTEXT COMPACTION SUMMARY]", summary.Text);
        Assert.Contains("<summary>\nTHE SUMMARY\n</summary>", summary.Text);
        Assert.Equal([system, input, summary], compacted);
        Assert.Same(summary, compacted[^1]);

        var request = Assert.Single(api.Requests);
        Assert.Equal(6, request.Input.Count);
        var prompt = Assert.IsType<ChatMessageUser>(request.Input[^1]).Text;
        Assert.StartsWith("\nYou have been working on the task described above", prompt);
        Assert.DoesNotContain("{addendums}", prompt);
        Assert.DoesNotContain("- Memory Files", prompt);
        Assert.Contains("\nAny promises made to the user\n\n\nBe concise but complete", prompt);
        Assert.Empty(request.Tools);
    }

    [Fact]
    public async Task summary_starts_from_the_latest_summary_and_inserts_instructions_and_the_memory_addendum()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("S"));
        var summarizer = new Model(api);
        var old = User("old summary") with { Metadata = new Dictionary<string, object?> { ["summary"] = true } };
        var memoryCall = Assistant("saving", toolCalls: [Call("m1", "memory", new { command = "create", path = "/m", file_text = "x" })]);
        List<ChatMessage> messages = [System("S"), User("task", source: "input"), Assistant("ancient"), old, memoryCall, Tool("ok", "m1", "memory"), User("more")];

        var strategy = new CompactionSummary(model: summarizer, instructions: "Focus on code.");
        var (compacted, summary) = await strategy.CompactAsync(ScriptedModel(), messages, []);

        Assert.NotNull(summary);
        var request = Assert.Single(api.Requests);
        Assert.DoesNotContain(request.Input, m => m.Text == "ancient");
        Assert.Contains(request.Input, m => ReferenceEquals(m, old));
        var prompt = request.Input[^1].Text;
        Assert.Contains("Focus on code.\n\n\n- Memory Files", prompt);
        Assert.Equal(3, compacted.Count);
        Assert.Equal("scripted", strategy.ReprParams()["model"]);

        var noMemory = new ScriptedModelApi(ScriptedTurn.Text("S"));
        await new CompactionSummary(memory: false, model: new Model(noMemory)).CompactAsync(ScriptedModel(), messages, []);
        Assert.DoesNotContain("- Memory Files", noMemory.Requests[0].Input[^1].Text);

        var custom = new ScriptedModelApi(ScriptedTurn.Text("S"));
        await new CompactionSummary(model: new Model(custom), prompt: "Summarize now. {addendums}", instructions: "Keep paths.").CompactAsync(ScriptedModel(), messages, []);
        Assert.Equal("Summarize now. Keep paths.\n\n" + CompactionSummary.MemorySummaryAddendum, custom.Requests[0].Input[^1].Text);
    }

    [Fact]
    public async Task summary_overflow_is_an_error_rather_than_a_summary()
    {
        var api = new ScriptedModelApi(ScriptedTurn.From(ModelOutput.FromContent("scripted", "context length exceeded", StopReason.ModelLength)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new CompactionSummary().CompactAsync(new Model(api), [User("q", source: "input"), Assistant("a"), User("u")], []));

        Assert.Contains("Compaction summary generation exceeded the model's context window", ex.Message);
    }

    [Fact]
    public async Task summary_truncates_oversized_tool_output_to_fit_the_window()
    {
        var inner = new ScriptedModelApi(ScriptedTurn.Text("S"));
        var api = new CountingApi(inner) { Window = 2000 };
        var model = new Model(api);
        var big = Tool(new string('X', 8000), "t1", "bash");
        var image = Tool("", "t2", "shot") with { Content = new Content[] { new ContentText("look:"), new ContentImage("data:image/png;base64,AAAA") } };
        List<ChatMessage> messages = [User("task", source: "input"), Assistant("run", toolCalls: [Call("t1", "bash"), Call("t2", "shot")]), big, image, User("next")];

        var (_, summary) = await new CompactionSummary().CompactAsync(model, messages, []);

        Assert.NotNull(summary);
        var sent = Assert.Single(inner.Requests).Input;
        var sentTool = Assert.IsType<ChatMessageTool>(sent[2]);
        Assert.Contains(CompactionSummary.TruncationMarker, sentTool.Text);
        Assert.True(sentTool.Text.Length < 8000);
        Assert.Equal(8000, big.Text.Length);
        var sentImage = Assert.IsType<ChatMessageTool>(sent[3]);
        Assert.Equal("look:", Assert.IsType<ContentText>(sentImage.Content.Items![0]).Text);
        Assert.Equal("[image elided for summarization]", Assert.IsType<ContentText>(sentImage.Content.Items![1]).Text);
        Assert.IsType<ContentImage>(image.Content.Items![1]);
        Assert.True(await model.CountTokensAsync(sent.ToList()) <= 2000 - Math.Min(2048, 1000));

        Assert.Equal("abcdefghijklm" + CompactionSummary.TruncationMarker + "nopqrstuvwxyz", CompactionSummary.TruncateMiddle("abcdefghijklm" + new string('-', 100) + "nopqrstuvwxyz", 26 + CompactionSummary.TruncationMarker.Length));
        Assert.Equal("short", CompactionSummary.TruncateMiddle("short", 100));
        Assert.Equal("nobudget", CompactionSummary.TruncateMiddle("nobudget", 10));
    }

    // ------------------------------------------------------------------ CompactionNative / CompactionAuto

    [Fact]
    public async Task native_is_not_supported_on_the_azure_providers_and_reports_the_token_count()
    {
        var credential = new FakeTokenCredential("token");
        IModelApi[] apis =
        [
            new AzureAIModelApi("gpt-x", "https://example.invalid", settings: new AzureAIClientSettings { TokenCredential = credential }),
            new AnthropicFoundryModelApi("claude-x", "https://example.invalid", settings: new AzureAIClientSettings { TokenCredential = credential }),
            new ScriptedModelApi(),
        ];
        List<ChatMessage> messages = [System("S"), User("Hello"), Assistant("Hi"), User("more")];

        foreach (var api in apis)
        {
            var ex = await Assert.ThrowsAsync<NotSupportedException>(() => new CompactionNative().CompactAsync(new Model(api), messages, []));
            Assert.Contains($"{api.GetType().Name} does not support native compaction.", ex.Message);
            Assert.Matches(@"Messages input had \d+ tokens\.", ex.Message);
            Assert.Contains("switch to CompactionAuto", ex.Message);
        }

        ProviderLogger.Reset();
        var failingCount = new Model(new CountingApi(new ScriptedModelApi()) { CountAsync = _ => throw new InvalidOperationException("count_tokens failed") });
        var noCount = await Assert.ThrowsAsync<NotSupportedException>(() => new CompactionNative().CompactAsync(failingCount, messages, []));
        Assert.DoesNotContain("tokens", noCount.Message);
        Assert.Contains("does not support native compaction", noCount.Message);
        Assert.Contains(ProviderLogger.Warnings, w => w.Contains("Error attempting to count tokens"));
    }

    [Fact]
    public async Task native_delegates_to_a_provider_that_supports_it_and_records_usage()
    {
        var compacted = new ChatMessageUser("<compacted>");
        var api = new CountingApi(new ScriptedModelApi()) { Native = _ => new NativeCompactionResult([compacted], new ModelUsage(10, 5, 15)) };
        using var scope = new SampleContextScope();
        var model = new Model(api);

        var result = await new CompactionNative(instructions: "keep paths").CompactAsync(model, [User("a"), Assistant("b"), User("c")], []);

        Assert.Equal([compacted], result.Input);
        Assert.Null(result.Message);
        Assert.False(new CompactionNative().PreservePrefix);
        Assert.Equal(15, scope.Context.Limits.TotalUsage.TotalTokens);
    }

    [Fact]
    public async Task auto_falls_back_to_summary_when_native_is_unsupported_and_warns_on_other_failures()
    {
        var scripted = new ScriptedModelApi(ScriptedTurn.Text("SUMMARY"));
        List<ChatMessage> messages = [System("S"), User("Hello", source: "input"), Assistant("Hi"), User("How are you?"), Assistant("Well.")];

        var (result, summary) = await new CompactionAuto().CompactAsync(new Model(scripted), messages, []);
        Assert.NotNull(summary);
        Assert.Equal(3, result.Count);
        Assert.Contains("SUMMARY", summary.Text);

        ProviderLogger.Reset();
        var failing = new CountingApi(new ScriptedModelApi(ScriptedTurn.Text("SUMMARY2"))) { Native = _ => throw new InvalidOperationException("API rate limit exceeded") };
        var (fallback, summary2) = await new CompactionAuto().CompactAsync(new Model(failing), messages, []);
        Assert.NotNull(summary2);
        Assert.Contains("SUMMARY2", fallback[^1].Text);
        var warning = Assert.Single(ProviderLogger.Warnings);
        Assert.Contains("Native compaction failed: API rate limit exceeded", warning);
        Assert.Contains("Falling back to summary compaction", warning);

        var working = new CountingApi(new ScriptedModelApi()) { Native = _ => new NativeCompactionResult([new ChatMessageUser("native")], null) };
        var (native, none) = await new CompactionAuto().CompactAsync(new Model(working), messages, []);
        Assert.Null(none);
        Assert.Equal("native", Assert.Single(native).Text);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelling = new CountingApi(new ScriptedModelApi()) { Native = _ => throw new OperationCanceledException(cts.Token) };
        await Assert.ThrowsAsync<OperationCanceledException>(() => new CompactionAuto().CompactAsync(new Model(cancelling), messages, [], cts.Token));
    }

    [Fact]
    public void auto_memory_setting_defaults_to_auto_and_forwards_parameters()
    {
        var auto = new CompactionAuto(threshold: 0.5, instructions: "Focus");
        Assert.True(auto.Memory);
        Assert.False(auto.Native.Memory);
        Assert.True(auto.Summary.Memory);
        Assert.Equal("auto", auto.ReprParams()["memory"]);
        Assert.Equal("Focus", auto.Native.Instructions);
        Assert.Equal("Focus", auto.Summary.Instructions);
        Assert.Equal(0.5, auto.Native.Threshold.Fraction);
        Assert.Equal("summary", auto.Type);

        var explicitTrue = new CompactionAuto(memory: true);
        Assert.True(explicitTrue.Native.Memory);
        Assert.True(explicitTrue.Summary.Memory);
        var explicitFalse = new CompactionAuto(memory: false);
        Assert.False(explicitFalse.Memory);
        Assert.False(explicitFalse.Summary.Memory);
    }

    // ------------------------------------------------------------------ Compaction orchestrator

    [Fact]
    public async Task no_compaction_under_an_absolute_threshold()
    {
        var model = ScriptedModel();
        var system = System("System", "sys1");
        var compact = Compaction.Create(new CompactionEdit(threshold: 500), [system], null, model);

        var (result, summary) = await compact.CompactInputAsync([system, User("Short message", "msg1"), Assistant("Short response", "msg2")]);

        Assert.Null(summary);
        Assert.Equal(3, result.Count);

        var (again, _) = await compact.CompactInputAsync([system, User("Short message", "msg1"), Assistant("Short response", "msg2"), User("Q2", "msg3"), Assistant("A2", "msg4")]);
        Assert.Equal(5, again.Count);
        Assert.Equal(["sys1", "msg1", "msg2", "msg3", "msg4"], again.Select(m => m.Id));

        var (empty, _) = await Compaction.Create(new CompactionEdit(threshold: 500), [], [], model).CompactInputAsync([User("Hello", "only")]);
        Assert.Single(empty);
    }

    [Fact]
    public void fractional_thresholds_resolve_against_the_context_window()
    {
        Assert.Equal(115_200, Compaction.ResolveThreshold(ScriptedModel(), CompactionThreshold.Default));
        Assert.Equal(500, Compaction.ResolveThreshold(new Model(new CountingApi(new ScriptedModelApi()) { Window = 1000 }), 0.5));
        Assert.Equal(700, Compaction.ResolveThreshold(new Model(new CountingApi(new ScriptedModelApi()) { Window = 1000 }), 700));

        CompactionModelInfo.SetContextWindow("scripted", 4000);
        try
        {
            Assert.Equal(3600, Compaction.ResolveThreshold(ScriptedModel(), 0.9));
        }
        finally
        {
            Assert.True(CompactionModelInfo.RemoveContextWindow("scripted"));
        }

        ProviderLogger.Reset();
        var plain = new Model(new PlainApi(new ScriptedModelApi()));
        Assert.Null(plain.ContextWindow());
        Assert.Equal((int)(0.9 * ModelCompactionExtensions.DefaultContextWindow), Compaction.ResolveThreshold(plain, 0.9));
        Assert.Contains(ProviderLogger.Warnings, w => w.Contains("Unable to determine context window for plain"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompactionModelInfo.SetContextWindow("x", 0));
    }

    [Fact]
    public async Task memory_warning_is_issued_once_between_ninety_percent_and_the_threshold()
    {
        var api = new CountingApi(new ScriptedModelApi()) { Count = m => m.Count == 1 && m[0].Text.Contains("\"type\": \"function\"") ? 5 : 90 };
        var model = new Model(api);
        var memoryTool = new ToolInfo(CompactionMemory.MemoryTool, "Save content to memory");
        var system = System("S", "sys1");
        List<ChatMessage> messages = [system, User("Q", "msg1"), Assistant("A", "msg2")];

        var compact = Compaction.Create(new CompactionEdit(threshold: 100, memory: true), [system], [memoryTool], model);
        var (result, summary) = await compact.CompactInputAsync(messages);
        Assert.Null(summary);
        Assert.Equal(4, result.Count);
        Assert.Equal(CompactionMemory.MemoryWarningText, result[^1].Text);

        var (second, _) = await compact.CompactInputAsync(messages);
        Assert.Equal(4, second.Count);

        var disabled = Compaction.Create(new CompactionEdit(threshold: 100, memory: false), [system], [memoryTool], model);
        Assert.DoesNotContain((await disabled.CompactInputAsync(messages)).Input, m => m.Text == CompactionMemory.MemoryWarningText);

        var noTool = Compaction.Create(new CompactionEdit(threshold: 100, memory: true), [system], [new ToolInfo("bash", "Run")], model);
        Assert.DoesNotContain((await noTool.CompactInputAsync(messages)).Input, m => m.Text == CompactionMemory.MemoryWarningText);
    }

    [Fact]
    public async Task compaction_over_the_threshold_records_an_event_with_token_counts()
    {
        using var scope = new SampleContextScope();
        var system = System("S", "sys1");
        var compact = Compaction.Create(new CompactionEdit(threshold: 300, keepToolUses: 0), [system], null, scope.Model);
        List<ChatMessage> messages =
        [
            system,
            User("Question", "msg1"),
            Assistant("Using tool", "msg2", [Call("t1", "bash", new { command = new string('A', 200) })]),
            Tool(new string('B', 1200), "t1", "bash", "msg3"),
            User("Follow up", "msg4"),
            Assistant("Done", "msg5"),
        ];
        var before = await scope.Model.CountTokensAsync(messages) + await scope.Model.CountToolTokensAsync([]);
        Assert.True(before > 300);

        var (result, summary) = await compact.CompactInputAsync(messages);

        Assert.Null(summary);
        Assert.Equal(5, result.Count);
        Assert.Same(system, result[0]);
        Assert.Equal(CompactionEdit.ToolResultRemoved, result[3].Text);
        var e = Assert.Single(scope.Transcript.Events.OfType<CompactionEvent>());
        Assert.Equal("edit", e.Type);
        Assert.Equal("inspect", e.Source);
        Assert.Null(e.Role);
        Assert.Equal(before, e.TokensBefore);
        Assert.Equal(await scope.Model.CountTokensAsync(result), e.TokensAfter);
        Assert.True(e.TokensAfter < e.TokensBefore);
        Assert.Equal("CompactionEdit", e.Metadata!["strategy"]);
        Assert.Equal(6, e.Metadata["messages_before"]);
        Assert.Equal(5, e.Metadata["messages_after"]);
        Assert.Equal("threshold", e.Metadata["trigger"]);
    }

    [Fact]
    public async Task compaction_iterates_and_reports_an_insufficient_result_with_a_breakdown()
    {
        var model = ScriptedModel();
        var system = System("S", "sys1");
        var iterative = Compaction.Create(new CompactionTrim(threshold: 60, preserve: 0.5), [system], null, model);
        var messages = new List<ChatMessage> { system };
        for (var i = 0; i < 10; i++)
        {
            messages.Add(User(string.Concat(Enumerable.Repeat($"Q{i}", 10)), $"u{i}"));
            messages.Add(Assistant(string.Concat(Enumerable.Repeat($"A{i}", 10)), $"a{i}"));
        }

        var initial = await model.CountTokensAsync(messages);
        Assert.True(initial > 60);
        var (result, _) = await iterative.CompactInputAsync(messages);
        Assert.True(result.Count < messages.Count);
        Assert.True(await model.CountTokensAsync(result) + await model.CountToolTokensAsync([]) <= 60);
        Assert.Same(system, result[0]);

        var prefix = System(string.Concat(Enumerable.Repeat("Prefix ", 10)), "sys2");
        var stuck = Compaction.Create(new CompactionEdit(threshold: 50, keepToolUses: 100), [prefix], [new ToolInfo("bash", "Run commands")], model);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stuck.CompactInputAsync([prefix, User(new string('Q', 400), "msg1")]));
        Assert.Contains("Compaction insufficient", ex.Message);
        Assert.Contains("tools:", ex.Message);
        Assert.Contains("prefix:", ex.Message);
        Assert.Contains("messages:", ex.Message);
        Assert.Contains("hidden_reasoning:", ex.Message);
    }

    [Fact]
    public async Task forced_compaction_skips_the_threshold_gate()
    {
        using var scope = new SampleContextScope();
        var messages = Enumerable.Range(0, 10).Select(i => (ChatMessage)User($"msg{i}", $"u{i}")).ToList();

        var predictive = Compaction.Create(new CompactionTrim(threshold: 1_000_000, preserve: 0.5), [], null, scope.Model);
        Assert.Equal(10, (await predictive.CompactInputAsync(messages)).Input.Count);

        var forced = Compaction.Create(new CompactionTrim(threshold: 1_000_000, preserve: 0.5), [], null, scope.Model);
        var (result, _) = await forced.CompactInputAsync(messages, force: true);
        Assert.True(result.Count < 10);
        Assert.Equal("forced", Assert.Single(scope.Transcript.Events.OfType<CompactionEvent>()).Metadata!["trigger"]);
    }

    [Fact]
    public async Task record_output_calibrates_the_total_against_the_generate_usage()
    {
        using var scope = new SampleContextScope();
        var api = new CountingApi(new ScriptedModelApi()) { Count = m => m.Count * 10 };
        var model = new Model(api);
        var system = System("S", "sys");
        var compact = Compaction.Create(new CompactionTrim(threshold: 100, preserve: 0.5), [system], null, model);
        List<ChatMessage> messages = [system, User("q1", "u1"), Assistant("a1", "a1")];

        var (first, _) = await compact.CompactInputAsync(messages);
        Assert.Equal(3, first.Count);

        // generate said the input was 75 tokens (plus 5 cached): that becomes the baseline for these ids
        await compact.RecordOutputAsync(first, new ModelOutput { Usage = new ModelUsage(75, 1, 76) { InputTokensCacheRead = 5 } });
        messages.Add(User("q2", "u2"));
        var (second, _) = await compact.CompactInputAsync(messages);
        Assert.Equal(4, second.Count);
        Assert.Empty(scope.Transcript.Events.OfType<CompactionEvent>());

        messages.Add(Assistant("a2", "a2"));
        messages.Add(User("q3", "u3"));
        var (third, _) = await compact.CompactInputAsync(messages);
        var e = Assert.Single(scope.Transcript.Events.OfType<CompactionEvent>());
        Assert.Equal(80 + 30, e.TokensBefore);
        Assert.Equal(["sys", "u1", "a2", "u3"], third.Select(m => m.Id));
        Assert.Equal(40, e.TokensAfter);

        // the baseline is invalidated by compaction: with no usage recorded the next call counts per message
        var (fourth, _) = await compact.CompactInputAsync([.. third, User("q4", "u4")]);
        Assert.Equal(5, fourth.Count);
        await compact.RecordOutputAsync(fourth, new ModelOutput { Usage = null });
        Assert.Single(scope.Transcript.Events.OfType<CompactionEvent>());
    }

    [Fact]
    public async Task prefix_is_restored_and_a_summary_is_recognized_on_the_next_cycle()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("SUMMARY1"), ScriptedTurn.Text("SUMMARY2"));
        var model = new Model(api);
        var system = System("System prompt", "sys1");
        var input = User("Initial input", "input1", source: "input");
        var compact = Compaction.Create(new CompactionSummary(threshold: 100), [system, input], null, model);
        List<ChatMessage> messages = [system, input, Assistant(new string('A', 500), "msg1"), User(new string('Q', 500), "msg2")];

        var (result, summary) = await compact.CompactInputAsync(messages);

        Assert.NotNull(summary);
        Assert.True((bool)summary.Metadata!["summary"]!);
        Assert.Equal([system, input, summary], result);
        Assert.Same(summary, result[^1]);

        messages.Add(summary);
        messages.Add(Assistant("Continuing", "msg3"));
        var (second, summary2) = await compact.CompactInputAsync(messages);
        Assert.Null(summary2);
        Assert.Equal([system, input, summary, messages[^1]], second);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task consecutive_user_messages_are_collapsed_for_apis_that_require_it()
    {
        var model = new Model(new ScriptedModelApi { CollapseUserMessages = true });
        var compact = Compaction.Create(new ConsecutiveUserCompaction(), [], null, model);

        var (result, summary) = await compact.CompactInputAsync([User("original", "original")], force: true);

        Assert.Null(summary);
        var only = Assert.IsType<ChatMessageUser>(Assert.Single(result));
        Assert.Equal("input\nsummary", only.Text);
        Assert.Equal("input", only.Source);
        Assert.True((bool)only.Metadata!["summary"]!);
        var partitioned = TrimMessages.Partition(result);
        Assert.Empty(partitioned.Input);
        Assert.Equal(result, partitioned.Conversation);

        var summaries = new Model(new ScriptedModelApi([ScriptedTurn.Text("SUMMARY1"), ScriptedTurn.Text("SUMMARY2")]) { CollapseUserMessages = true });
        var compactSummaries = Compaction.Create(new CompactionSummary(threshold: 1_000_000), [], null, summaries);
        var (first, none) = await compactSummaries.CompactInputAsync([User("TASK", "task", source: "input")], force: true);
        Assert.Null(none);
        Assert.Contains("SUMMARY1", Assert.Single(first).Text);
        var (again, second) = await compactSummaries.CompactInputAsync([.. first, Assistant("continuing", "assistant")], force: true);
        Assert.Same(second, Assert.Single(again));
        Assert.DoesNotContain("SUMMARY1", again[0].Text);
        Assert.Contains("SUMMARY2", again[0].Text);
    }

    [Fact]
    public async Task concurrent_callers_do_not_duplicate_the_compacted_input()
    {
        var api = new CountingApi(new ScriptedModelApi())
        {
            CountAsync = async _ =>
            {
                await Task.Yield();
                return 1;
            },
        };
        var compact = Compaction.Create(new CompactionEdit(threshold: 10_000_000), [], null, new Model(api));
        List<ChatMessage> messages = [User("hello", "msg1"), Assistant("hi", "msg2")];

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => compact.CompactInputAsync(messages)));

        var (final, _) = await compact.CompactInputAsync(messages);
        Assert.Equal(["msg1", "msg2"], final.Select(m => m.Id));
    }

    [Fact]
    public async Task messages_without_ids_are_rejected_and_the_active_model_is_the_default()
    {
        var compact = Compaction.Create(new CompactionEdit(), [], null, ScriptedModel());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => compact.CompactInputAsync([User("x") with { Id = null }]));
        Assert.Equal("Message must have an ID", ex.Message);

        Assert.Throws<InvalidOperationException>(() => Compaction.Create(new CompactionEdit(), []));
        using var scope = new SampleContextScope();
        Assert.NotNull(Compaction.Create(new CompactionEdit(), []));
    }

    [Fact]
    public async Task native_strategies_prepend_only_system_prefix_messages()
    {
        var compacted = new ChatMessageUser("<compacted>");
        var api = new CountingApi(new ScriptedModelApi()) { Native = _ => new NativeCompactionResult([compacted], null) };
        var system = System("S", "sys");
        var input = User("task", "input", source: "input");
        var compact = Compaction.Create(new CompactionNative(), [system, input], null, new Model(api));

        var (result, summary) = await compact.CompactInputAsync([system, input, Assistant("a", "a1")], force: true);

        Assert.Null(summary);
        Assert.Equal([system, compacted], result);
    }

    [Fact]
    public async Task redacted_reasoning_tokens_count_only_when_the_provider_declares_the_blind_spot()
    {
        using var scope = new SampleContextScope();
        ChatMessageAssistant Redacted(string id) => new(new Content[] { new ContentReasoning("ENCRYPTED", Redacted: true), new ContentText("visible") })
        {
            Id = id,
            Metadata = new Dictionary<string, object?> { [Compaction.RedactedReasoningTokensMetadataKey] = 1000 },
        };

        List<ChatMessage> messages = [User("q", "u1"), Redacted("a1"), User("u", "u2")];
        var hidden = new Model(new CountingApi(new ScriptedModelApi()) { Count = _ => 1, RedactedReasoningHidden = true });
        Assert.Equal(1000, Compaction.RedactedReasoningTokensTotal(messages, hidden));
        Assert.Equal(0, Compaction.RedactedReasoningTokensTotal(messages, new Model(new CountingApi(new ScriptedModelApi()) { Count = _ => 1 })));

        var compact = Compaction.Create(new CompactionEdit(threshold: 100, keepThinkingTurns: 0), [], null, hidden);
        var (result, _) = await compact.CompactInputAsync(messages);
        var e = Assert.Single(scope.Transcript.Events.OfType<CompactionEvent>());
        Assert.Equal(1000 + 1 + 1, e.TokensBefore);
        Assert.Equal(1, e.TokensAfter);
        Assert.DoesNotContain(result.OfType<ChatMessageAssistant>(), m => !m.Content.IsString && m.Content.Items!.Any(c => c is ContentReasoning));

        var visible = Compaction.Create(new CompactionEdit(threshold: 100, keepThinkingTurns: 0), [], null, new Model(new CountingApi(new ScriptedModelApi()) { Count = _ => 1 }));
        Assert.Equal(3, (await visible.CompactInputAsync(messages)).Input.Count);
        Assert.Single(scope.Transcript.Events.OfType<CompactionEvent>());
    }

    // ------------------------------------------------------------------ CompactionEvent JSON

    [Fact]
    public void compaction_event_round_trips_through_the_log_json_with_python_field_names()
    {
        var e = new CompactionEvent
        {
            Type = "trim",
            Source = "inspect",
            TokensBefore = 100,
            TokensAfter = 50,
            Metadata = new Dictionary<string, object?> { ["strategy"] = "CompactionTrim", ["messages_before"] = 5, ["messages_after"] = 3, ["trigger"] = "threshold" },
        };

        var json = JsonSerializer.Serialize<TranscriptEvent>(e, EvalLogWriter.Options);
        var node = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("compaction", node["event"]!.GetValue<string>());
        Assert.Equal("trim", node["type"]!.GetValue<string>());
        Assert.Equal(100, node["tokens_before"]!.GetValue<int>());
        Assert.Equal(50, node["tokens_after"]!.GetValue<int>());
        Assert.Equal("inspect", node["source"]!.GetValue<string>());
        Assert.Equal("CompactionTrim", node["metadata"]!["strategy"]!.GetValue<string>());
        Assert.Equal(5, node["metadata"]!["messages_before"]!.GetValue<int>());
        Assert.False(node.ContainsKey("role"));
        Assert.True(node.ContainsKey("timestamp"));

        var read = Assert.IsType<CompactionEvent>(JsonSerializer.Deserialize<TranscriptEvent>(json, EvalLogWriter.Options));
        Assert.Equal("trim", read.Type);
        Assert.Equal(100, read.TokensBefore);
        Assert.Equal(50, read.TokensAfter);
        Assert.Equal("inspect", read.Source);
        Assert.Null(read.Role);
        Assert.Equal("threshold", read.Metadata!["trigger"]);
        Assert.Equal(3, read.Metadata["messages_after"]);
        Assert.Equal(e.Timestamp, read.Timestamp);

        var typed = Assert.IsType<CompactionEvent>(JsonSerializer.Deserialize<CompactionEvent>("{\"event\":\"compaction\",\"role\":\"grader\"}", EvalLogWriter.Options));
        Assert.Equal("summary", typed.Type);
        Assert.Equal("grader", typed.Role);
        Assert.Null(typed.Metadata);
    }

    // ------------------------------------------------------------------ BasicAgent hook

    private static readonly ToolDef Calc = new(
        "calc",
        "Evaluates a sum.",
        new ToolParams { Properties = new Dictionary<string, ToolParam> { ["expr"] = ToolParam.Of("string") }, Required = ["expr"] },
        (args, _) => Task.FromResult<ToolResult>(args["expr"]!.GetValue<string>() == "6*7" ? "42" : "?"));

    [Fact]
    public async Task basic_agent_compacts_its_input_through_the_hook()
    {
        var api = new ScriptedModelApi(
            ScriptedTurn.ToolCall("calc", new { expr = "6*7" }, usage: new ModelUsage(5000, 10, 5010)),
            ScriptedTurn.ToolCall("submit", new { answer = "42" }));
        using var scope = new SampleContextScope(api);
        var state = new TaskState("scripted", 1, 1, "What is 6 times 7?", [new ChatMessageUser("What is 6 times 7?")]);

        var solver = Solvers.BasicAgent(tools: [Calc], compaction: Compaction.Hook(new CompactionEdit(threshold: 1000, keepToolUses: 0)));
        var result = await solver(state, GenerateLoop.Create(scope.Model), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal("42", result.Output.Completion);
        Assert.Equal(2, api.Requests.Count);
        var second = api.Requests[1].Input;
        Assert.Equal(CompactionEdit.ToolResultRemoved, Assert.Single(second.OfType<ChatMessageTool>()).Text);
        Assert.Equal("42", Assert.Single(result.Messages.OfType<ChatMessageTool>(), t => t.Function == "calc").Text);
        var e = Assert.Single(scope.Transcript.Events.OfType<CompactionEvent>());
        Assert.Equal("threshold", e.Metadata!["trigger"]);
        Assert.True(e.TokensBefore > 5000);
    }

    [Fact]
    public async Task basic_agent_recovers_from_a_context_overflow_by_forced_compaction()
    {
        var api = new ScriptedModelApi(
            ScriptedTurn.From(ModelOutput.FromContent("scripted", "", StopReason.ModelLength)),
            ScriptedTurn.ToolCall("submit", new { answer = "42" }));
        using var scope = new SampleContextScope(api);
        var state = new TaskState("scripted", 1, 1, "What is 6 times 7?", [new ChatMessageUser("What is 6 times 7?")]);

        var solver = Solvers.BasicAgent(tools: [Calc], compaction: Compaction.Hook(new CompactionTrim(threshold: 1_000_000, preserve: 0.5)));
        var result = await solver(state, GenerateLoop.Create(scope.Model), CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal("42", result.Output.Completion);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal("forced", Assert.Single(scope.Transcript.Events.OfType<CompactionEvent>()).Metadata!["trigger"]);
        Assert.DoesNotContain(scope.Transcript.Events.OfType<InfoEvent>(), i => i.Data?.ToString().Contains("context window exceeded") == true);
        Assert.DoesNotContain(result.Messages, m => m is ChatMessageAssistant { Content.IsString: true } a && a.Text == "" && a.ToolCalls is null);
    }
}
