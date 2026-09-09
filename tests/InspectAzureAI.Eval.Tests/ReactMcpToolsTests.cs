using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Agents = InspectAzureAI.Eval.Agents.Agents;

/// <summary>
/// Port-level behaviour of the tool source handling of <c>agent/_react.py</c>: <c>mcp_connection(tools)</c>
/// around the loop and the per-turn tool resolution of <c>_agent_generate</c>, with a fake MCP server.
/// </summary>
public class ReactMcpToolsTests
{
    private static ToolDef Tool(string name, string result, List<string>? log = null) =>
        new(name, $"The {name} tool.", new ToolParams(), (_, _) =>
        {
            log?.Add($"tool:{name}");
            return Task.FromResult<ToolResult>(result);
        });

    /// <summary>A fake MCP server (Python's <c>_FakeToolServer</c>): counts connections and tool resolutions.</summary>
    private sealed class FakeServer(IReadOnlyList<ToolDef> tools, List<string>? log = null) : McpServer
    {
        public int Calls;
        public int Entered;
        public int Exited;

        public override Task<IReadOnlyList<ToolDef>> ToolsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(tools);
        }

        public override Task EnterAsync(CancellationToken cancellationToken = default)
        {
            Entered++;
            log?.Add("enter");
            return Task.CompletedTask;
        }

        public override Task ExitAsync()
        {
            Exited++;
            log?.Add("exit");
            return Task.CompletedTask;
        }
    }

    private static ScriptedTurn Submit(string answer) => ScriptedTurn.ToolCall("submit", new { answer });

    private static Task<AgentState> RunAsync(AgentDef agent) =>
        agent.Execute(new AgentState([new ChatMessageUser("What is the answer?") { Source = "input" }]), CancellationToken.None);

    [Fact]
    public async Task tools_from_an_mcp_server_are_offered_to_the_model_and_callable()
    {
        var server = new FakeServer([Tool("tool_a", "42")]);
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.ToolCall("tool_a", new { }), Submit("42")));

        var state = await RunAsync(Agents.React(tools: [server]));

        Assert.Equal(["tool_a", "submit"], scope.Api.Requests[0].Tools.Select(t => t.Name));
        Assert.Equal("42", Assert.Single(state.Messages.OfType<ChatMessageTool>()).Text);
        Assert.Equal("42", state.Output.Completion);
        Assert.Equal(1, server.Entered);
        Assert.Equal(1, server.Exited);
        // Python resolves the tool sources afresh on every turn
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task the_server_stays_connected_across_the_whole_loop()
    {
        var log = new List<string>();
        var server = new FakeServer([Tool("tool_a", "42", log)], log);
        var call = ScriptedTurn.ToolCall("tool_a", new { }).Output!;
        var submit = Submit("42").Output!;
        using var scope = new SampleContextScope(new ScriptedModelApi(
            ScriptedTurn.From((_, _) =>
            {
                log.Add("generate");
                return call;
            }),
            ScriptedTurn.From((_, _) =>
            {
                log.Add("generate");
                return submit;
            })));

        await RunAsync(Agents.React(tools: [server]));

        Assert.Equal(["enter", "generate", "tool:tool_a", "generate", "exit"], log);
    }

    [Fact]
    public async Task the_connection_is_released_when_the_loop_fails()
    {
        var server = new FakeServer([Tool("tool_a", "42")]);
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Error(new InvalidOperationException("boom"))));

        await Assert.ThrowsAsync<ModelGenerateException>(() => RunAsync(Agents.React(tools: [server])));

        Assert.Equal(1, server.Entered);
        Assert.Equal(1, server.Exited);
    }

    [Fact]
    public async Task static_tools_sources_and_the_submit_tool_keep_their_order()
    {
        var server = new FakeServer([Tool("tool_a", "a"), Tool("tool_b", "b")]);
        using var scope = new SampleContextScope(new ScriptedModelApi(Submit("done")));

        await RunAsync(Agents.React(tools: [Tool("first", "1"), server, Tool("last", "9")]));

        Assert.Equal(["first", "tool_a", "tool_b", "last", "submit"], scope.Api.Requests[0].Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task mcp_tools_selects_a_subset_and_still_connects_the_server()
    {
        var server = new FakeServer([Tool("tool_a", "a"), Tool("tool_b", "b")]);
        var source = new McpToolSourceLocal(server, ["tool_b"]);
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.ToolCall("tool_b", new { }), Submit("b")));

        var state = await RunAsync(Agents.React(tools: [source]));

        Assert.Equal(["tool_b", "submit"], scope.Api.Requests[0].Tools.Select(t => t.Name));
        Assert.Equal("b", Assert.Single(state.Messages.OfType<ChatMessageTool>()).Text);
        Assert.Equal(1, server.Entered);
        Assert.Equal(1, server.Exited);
    }

    [Fact]
    public async Task a_model_agent_receives_the_resolved_tools()
    {
        var server = new FakeServer([Tool("tool_a", "a")]);
        IReadOnlyList<string>? seen = null;
        AgentModel agent = (state, tools, _) =>
        {
            seen = tools.Select(t => t.Name).ToList();
            var output = ModelOutput.FromContent("agent", "done");
            state.Messages.Add(output.Message);
            state.Output = output;
            return Task.FromResult(state);
        };

        var state = await RunAsync(Agents.React(modelAgent: agent, tools: [Tool("first", "1"), server], submit: AgentSubmit.Disabled));

        Assert.Equal(["first", "tool_a"], seen);
        Assert.Equal("done", state.Output.Completion);
        Assert.Equal(1, server.Entered);
        Assert.Equal(1, server.Exited);
    }

    [Fact]
    public async Task a_plain_tool_list_converts_to_the_tools_parameter()
    {
        var tools = new List<ToolDef> { Tool("tool_a", "42") };
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.ToolCall("tool_a", new { }), Submit("42")));

        var state = await RunAsync(Agents.React(tools: tools));

        Assert.Equal(["tool_a", "submit"], scope.Api.Requests[0].Tools.Select(t => t.Name));
        Assert.Equal("42", state.Output.Completion);
    }

    [Fact]
    public async Task resolve_async_flattens_tools_and_sources_in_order()
    {
        var server = new FakeServer([Tool("tool_a", "a"), Tool("tool_b", "b")]);
        var first = Tool("first", "1");

        var resolved = await ToolSources.ResolveAsync([first, server, Tool("last", "9")]);

        Assert.Equal(["first", "tool_a", "tool_b", "last"], resolved.Select(t => t.Name));
        Assert.Same(first, resolved[0]);
        Assert.Equal(["tool_a", "tool_b"], (await ToolSources.ResolveAsync(server)).Select(t => t.Name));
        Assert.Equal(["first"], (await ToolSources.ResolveAsync(first)).Select(t => t.Name));
    }
}
