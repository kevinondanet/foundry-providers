using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// <c>claude_code()</c> end to end, offline: the real agent and <see cref="Eval.Agents.Bridge.SandboxAgentBridge"/> on
/// loopback, a scripted model, and <see cref="FakeClaudeCodeCli"/> playing Claude Code (5.4 scenarios 1-3).
/// </summary>
[Collection("ClaudeCode")]
public class ClaudeCodeEndToEndTests
{
    private const string SubagentPrompt = "Explore the repository layout and list every test directory.";

    private sealed class Run : IDisposable
    {
        private readonly IDisposable _scope;

        private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));

        public Run(params ScriptedTurn[] turns)
        {
            Api = new ScriptedModelApi(turns);
            Cli = new FakeClaudeCodeCli(Sandbox);
            Context = new SampleContext { ActiveModel = new Model(Api), Sandboxes = SandboxEnvironments.Single(Sandbox) };
            _scope = SampleContext.Begin(Context);
        }

        public CliSandbox Sandbox { get; } = new();

        public FakeClaudeCodeCli Cli { get; }

        public ScriptedModelApi Api { get; }

        public SampleContext Context { get; }

        public StrictHttpHandler Http { get; } = new();

        public ClaudeCodeAgent Agent(ClaudeCodeOptions? options = null) =>
            new((options ?? new ClaudeCodeOptions()) with { HttpHandler = Http, CacheDir = _cacheDir });

        /// <summary>Runs the agent inside an outer agent span and returns the state and that span's id.</summary>
        public async Task<(AgentState State, string OuterSpanId)> ExecuteAsync(ClaudeCodeOptions? options = null, string prompt = "Explore the repo and write app.py")
        {
            using (Context.Transcript.Span("claude-code", "agent"))
            {
                var outer = Context.Transcript.CurrentSpanId!;
                var state = await Agent(options).ExecuteAsync(new AgentState([new ChatMessageUser(prompt)]));
                return (state, outer);
            }
        }

        public void Dispose()
        {
            _scope.Dispose();
            Assert.Empty(Http.Requests);
        }
    }

    private static ScriptedTurn Calls(params ToolCall[] calls) => ScriptedTurn.From(new ModelOutput
    {
        Model = ScriptedModelApi.DefaultModelName,
        Choices = [new ChatCompletionChoice(new ChatMessageAssistant("", toolCalls: calls, model: ScriptedModelApi.DefaultModelName), StopReason.ToolCalls)],
    });

    private static ToolCall Agent(string id, string prompt) =>
        new(id, "Agent", new JsonObject { ["subagent_type"] = "Explore", ["description"] = "look", ["prompt"] = prompt });

    /// <summary>The model, span and compaction events, without the outer span's own begin and end.</summary>
    private static List<TranscriptEvent> Flow(Transcript transcript, string outer) =>
        transcript.Events.Where(e => e is ModelEvent or CompactionEvent or SpanBeginEvent or SpanEndEvent)
            .Where(e => e is not SpanBeginEvent begin || begin.Id != outer)
            .Where(e => e is not SpanEndEvent end || end.Id != outer)
            .ToList();

    private static bool IsSubagentCall(ModelEvent e) => e.Input.OfType<ChatMessageSystem>().Any(m => m.Text == FakeClaudeCodeCli.SubagentSystemPrompt);

    [Fact]
    public async Task a_sub_agent_turn_is_spanned_attributed_closed_and_followed_by_compaction()
    {
        using var run = new Run(
            Calls(Agent("toolu_1", SubagentPrompt), new ToolCall("toolu_2", "Write", new JsonObject { ["file_path"] = "/workspace/app.py", ["content"] = "print('hi')\n" })),
            ScriptedTurn.Text("Tests live in tests/ and src/tests/."),
            ScriptedTurn.Text("Explored the repo and wrote app.py."));
        run.Cli.TrailingLines.Add(JsonNode.Parse("""{"type": "system", "subtype": "compact_boundary", "compact_metadata": {"trigger": "auto", "pre_tokens": 1234}}""")!.AsObject());

        var (state, outer) = await run.ExecuteAsync();

        var flow = Flow(run.Context.Transcript, outer);
        Assert.Equal(6, flow.Count);
        var main = Assert.IsType<ModelEvent>(flow[0]);
        Assert.Equal(outer, main.SpanId);
        var begin = Assert.IsType<SpanBeginEvent>(flow[1]);
        Assert.Equal(("agent-toolu_1", "Explore", "agent", outer), (begin.Id, begin.Name, begin.Type, begin.ParentId));
        Assert.Equal("look", begin.Metadata!["description"]);
        var sub = Assert.IsType<ModelEvent>(flow[2]);
        Assert.Equal("agent-toolu_1", sub.SpanId);
        Assert.True(IsSubagentCall(sub));
        Assert.Equal("agent-toolu_1", Assert.IsType<SpanEndEvent>(flow[3]).Id);
        var parent = Assert.IsType<ModelEvent>(flow[4]);
        Assert.Equal(outer, parent.SpanId);
        Assert.Contains(parent.Input, m => m is ChatMessageTool { ToolCallId: "toolu_1" });
        var compaction = Assert.IsType<CompactionEvent>(flow[5]);
        Assert.Equal((1234, "claude_code", outer), (compaction.TokensBefore, compaction.Source, compaction.SpanId));
        Assert.Equal("auto", compaction.Metadata!["trigger"]);

        var calls = main.Output.Message.ToolCalls!;
        Assert.Equal("Agent: Explore", calls[0].View!.Title);
        Assert.Equal("Write", calls[1].View!.Title);
        Assert.Equal("`file_path: /workspace/app.py`\n\n``````python\n{{content}}``````\n", main.Output.Message.ToolCalls![1].View!.Content);

        // the state is the main conversation; the views live on the transcript only (D-E2)
        Assert.Equal("Explored the repo and wrote app.py.", state.Messages[^1].Text);
        Assert.All(state.Messages.OfType<ChatMessageAssistant>().SelectMany(m => m.ToolCalls ?? []), c => Assert.Null(c.View));
        Assert.Equal(2, state.Messages.OfType<ChatMessageTool>().Count());
        Assert.Equal("print('hi')\n", Encoding.UTF8.GetString(run.Sandbox.Files["/workspace/app.py"]));

        var requests = run.Cli.Requests.Snapshot();
        Assert.Equal(3, requests.Count);
        Assert.All(requests, r => Assert.Equal(("POST", "/v1/messages", 200), (r.Method, r.Path, r.Status)));
        Assert.Single(run.Cli.Launches);
        var token = run.Sandbox.Calls.Single(FakeClaudeCodeCli.IsLaunch).Env!["ANTHROPIC_AUTH_TOKEN"];
        Assert.All(requests, r => Assert.Equal($"Bearer {token}", r.Authorization));
        Assert.Equal(0, run.Api.Remaining);
    }

    [Fact]
    public async Task parallel_sub_agents_with_overlapping_prompts_are_attributed_to_the_outer_span()
    {
        using var run = new Run(
            Calls(Agent("toolu_a", SubagentPrompt), Agent("toolu_b", SubagentPrompt)),
            ScriptedTurn.Text("sub-agent report"),
            ScriptedTurn.Text("sub-agent report"),
            ScriptedTurn.Text("Both explorations finished."));

        var (state, outer) = await run.ExecuteAsync();

        var flow = Flow(run.Context.Transcript, outer);
        Assert.Equal(["agent-toolu_a", "agent-toolu_b"], flow.OfType<SpanBeginEvent>().Select(b => b.Id));
        var subs = flow.OfType<ModelEvent>().Where(IsSubagentCall).ToArray();
        Assert.Equal(2, subs.Length);
        Assert.All(subs, e => Assert.Equal(outer, e.SpanId));

        // both spans end before the parent's next model event, in the order of its tool results
        var last = flow.FindLastIndex(e => e is ModelEvent);
        Assert.Equal(["agent-toolu_a", "agent-toolu_b"], flow.Take(last).OfType<SpanEndEvent>().Select(e => e.Id));
        Assert.Equal(2, flow.OfType<SpanEndEvent>().Count());
        Assert.Equal("Both explorations finished.", state.Messages[^1].Text);
    }

    [Fact]
    public async Task a_refusal_exit_counts_as_success()
    {
        using var run = new Run(ScriptedTurn.From(ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "I can't help with that.", StopReason.ContentFilter)));

        var (state, _) = await run.ExecuteAsync(new ClaudeCodeOptions { RetryRefusals = null }, prompt: "Do something disallowed");

        Assert.Single(run.Cli.Launches);
        Assert.Equal("I can't help with that.", state.Messages[^1].Text);
        var result = run.Sandbox.Calls.Single(FakeClaudeCodeCli.IsLaunch);
        Assert.Contains(run.Context.Transcript.Events.OfType<InfoEvent>(), e => e.Data?["type"]?.GetValue<string>() == "result" && e.Data?["is_error"]?.GetValue<bool>() == true);
        Assert.NotNull(result.Env);
    }
}
