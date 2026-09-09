using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Agents = InspectAzureAI.Eval.Agents.Agents;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>Port-level behaviour of <c>chain</c>, the prompt solvers (<c>solver/_prompt.py</c>), <c>use_tools</c>, <c>format_template</c> and <c>as_solver</c>.</summary>
public class SolverTests
{
    private static TaskState State(params ChatMessage[] messages) =>
        new("scripted", 1, 1, "question", messages.Length == 0 ? [new ChatMessageUser("question")] : messages);

    private static readonly Generate NoGenerate = (state, _, _, _, _) => Task.FromResult(state);

    private static Solver Append(string text) => (state, _, _) =>
    {
        state.Messages.Add(new ChatMessageUser(text));
        return Task.FromResult(state);
    };

    private static ToolDef Tool(string name) => new(name, $"{name} tool", new ToolParams(), (_, _) => Task.FromResult<ToolResult>(name));

    [Fact]
    public async Task chain_runs_solvers_in_order_and_unrolls_nested_chains()
    {
        var chain = Solvers.Chain(Append("a"), Solvers.Chain(Append("b"), Append("c")), Append("d"));

        var state = await chain(State(), NoGenerate, CancellationToken.None);

        Assert.Equal(["question", "a", "b", "c", "d"], state.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task chain_stops_once_a_solver_completes_the_state()
    {
        Solver complete = (state, _, _) =>
        {
            state.Completed = true;
            return Task.FromResult(state);
        };

        var state = await Solvers.Chain(Append("a"), complete, Append("never"))(State(), NoGenerate, CancellationToken.None);

        Assert.Equal(["question", "a"], state.Messages.Select(m => m.Text));
        Assert.True(state.Completed);
    }

    [Fact]
    public async Task system_message_is_inserted_after_existing_system_messages()
    {
        var state = State(new ChatMessageSystem("sys1"), new ChatMessageUser("question"), new ChatMessageSystem("sys2"), new ChatMessageAssistant("reply"));

        await Solvers.SystemMessage("added")(state, NoGenerate, CancellationToken.None);

        Assert.Equal(["sys1", "question", "sys2", "added", "reply"], state.Messages.Select(m => m.Text));
        Assert.IsType<ChatMessageSystem>(state.Messages[3]);
    }

    [Fact]
    public async Task system_message_goes_first_when_there_is_no_system_message()
    {
        var state = State();

        await Solvers.SystemMessage("added")(state, NoGenerate, CancellationToken.None);

        Assert.Equal(["added", "question"], state.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task templates_substitute_parameters_metadata_and_store_with_parameters_winning()
    {
        var state = State();
        state.Metadata["topic"] = "cats";
        state.Metadata["shadowed"] = "metadata";
        state.Store.Set("count", 3);
        state.Store.Set("shadowed", "store");

        await Solvers.SystemMessage("{topic}/{count}/{shadowed}/{unknown}", new Dictionary<string, object?> { ["shadowed"] = "param" })(state, NoGenerate, CancellationToken.None);

        Assert.Equal("cats/3/param/{unknown}", state.Messages[0].Text);
    }

    [Fact]
    public async Task prompt_template_rewrites_the_user_prompt_with_the_prompt_placeholder()
    {
        var state = State(new ChatMessageSystem("sys"), new ChatMessageUser("What is 2+2?"));
        state.Metadata["style"] = "briefly";

        await Solvers.PromptTemplate("Answer {style}: {prompt}\n")(state, NoGenerate, CancellationToken.None);

        Assert.Equal("Answer briefly: What is 2+2?\n", state.UserPrompt.Text);
        Assert.Equal("sys", state.Messages[0].Text);
        Assert.Equal(2, state.Messages.Count);
    }

    [Fact]
    public async Task prompt_template_keeps_non_text_content_of_the_prompt()
    {
        var prompt = new ChatMessageUser(new Content[] { new ContentImage("data:image/png;base64,AAAA"), new ContentText("look") });
        var state = State(prompt);

        await Solvers.PromptTemplate("Please {prompt}")(state, NoGenerate, CancellationToken.None);

        var rewritten = Assert.IsType<ChatMessageUser>(Assert.Single(state.Messages));
        Assert.Equal(prompt.Id, rewritten.Id);
        Assert.Equal("Please look", rewritten.Text);
        Assert.IsType<ContentImage>(rewritten.ContentList[0]);
        Assert.Equal(2, rewritten.ContentList.Count);
    }

    [Fact]
    public async Task user_message_appends_a_formatted_user_message()
    {
        var state = State();
        state.Store.Set("name", "Ada");

        await Solvers.UserMessage("Hello {name}")(state, NoGenerate, CancellationToken.None);

        var added = Assert.IsType<ChatMessageUser>(state.Messages[^1]);
        Assert.Equal("Hello Ada", added.Text);
    }

    [Theory]
    [InlineData("{{literal}} {name}", "{literal} Ada")]
    [InlineData("{ratio:F2}!", "0.50!")]
    [InlineData("{name:>5}!", "{name:>5}!")]
    [InlineData("{missing} and {name.first} and {} and {0}", "{missing} and {name.first} and {} and {0}")]
    [InlineData("{empty} stays", "{empty} stays")]
    [InlineData("lone { brace", "lone { brace")]
    [InlineData("{flag} {ratio}", "True 0.5")]
    public void template_formatter_matches_python_format_template(string template, string expected)
    {
        var variables = new Dictionary<string, object?> { ["name"] = "Ada", ["empty"] = null, ["flag"] = true, ["ratio"] = 0.5 };

        Assert.Equal(expected, TemplateFormatter.Format(template, variables));
    }

    [Fact]
    public async Task use_tools_replaces_or_appends_tools_and_sets_tool_choice_only_when_given()
    {
        var state = State();
        state.Tools.Add(Tool("existing"));

        await Solvers.UseTools(Tool("a"), Tool("b"))(state, NoGenerate, CancellationToken.None);
        Assert.Equal(["a", "b"], state.Tools.Select(t => t.Name));
        Assert.Null(state.ToolChoice);

        await Solvers.UseTools([Tool("c")], new ToolFunction("c"), append: true)(state, NoGenerate, CancellationToken.None);
        Assert.Equal(["a", "b", "c"], state.Tools.Select(t => t.Name));
        Assert.Equal(new ToolFunction("c"), state.ToolChoice);

        await Solvers.UseTools([], ToolChoice.Any)(state, NoGenerate, CancellationToken.None);
        Assert.Equal(["a", "b", "c"], state.Tools.Select(t => t.Name));
        Assert.Same(ToolChoice.Any, state.ToolChoice);
    }

    [Fact]
    public async Task generate_solver_forwards_mode_and_config_to_the_runner_generate()
    {
        ToolCallsMode? seenMode = null;
        GenerateConfig? seenConfig = null;
        Generate generate = (state, mode, config, _, _) =>
        {
            seenMode = mode;
            seenConfig = config;
            return Task.FromResult(state);
        };
        var config = new GenerateConfig { Temperature = 0.1 };

        await Solvers.Generate(ToolCallsMode.Single, config)(State(), generate, CancellationToken.None);

        Assert.Equal(ToolCallsMode.Single, seenMode);
        Assert.Same(config, seenConfig);
    }

    [Fact]
    public async Task as_solver_copies_messages_and_output_back_inside_an_agent_span()
    {
        using var scope = new SampleContextScope();
        var agent = new AgentDef("echo-agent", "echoes", (agentState, _) =>
        {
            agentState.Messages.Add(new ChatMessageAssistant("done", model: "m"));
            agentState.Output = ModelOutput.FromContent("m", "final answer");
            return Task.FromResult(agentState);
        });
        var state = State();

        await Agents.AsSolver(agent)(state, NoGenerate, CancellationToken.None);

        Assert.Equal(["question", "done"], state.Messages.Select(m => m.Text));
        Assert.Equal("final answer", state.Output.Completion);
        var begin = Assert.Single(scope.Transcript.Events.OfType<SpanBeginEvent>());
        Assert.Equal("echo-agent", begin.Name);
        Assert.Equal("agent", begin.Type);
        Assert.Single(scope.Transcript.Events.OfType<SpanEndEvent>());
    }

    [Fact]
    public async Task as_solver_still_copies_the_partial_conversation_when_the_agent_fails()
    {
        var agent = new AgentDef("failing", "fails", (agentState, _) =>
        {
            agentState.Messages.Add(new ChatMessageAssistant("partial", model: "m"));
            throw new InvalidOperationException("boom");
        });
        var state = State();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Agents.AsSolver(agent)(state, NoGenerate, CancellationToken.None));

        Assert.Equal(["question", "partial"], state.Messages.Select(m => m.Text));
        Assert.Equal("partial", state.Output.Completion);
    }

    [Fact]
    public async Task system_and_user_messages_substitute_a_prompt_key_that_only_prompt_template_reserves()
    {
        var state = State(new ChatMessageUser("What is 2+2?"));
        state.Metadata["prompt"] = "Answer tersely";

        await Solvers.SystemMessage("Style: {prompt}")(state, NoGenerate, CancellationToken.None);
        await Solvers.PromptTemplate("Q: {prompt}")(state, NoGenerate, CancellationToken.None);
        await Solvers.UserMessage("Again: {prompt}")(state, NoGenerate, CancellationToken.None);

        Assert.Equal("Style: Answer tersely", state.Messages[0].Text);
        Assert.Equal("Q: What is 2+2?", state.Messages[1].Text);
        Assert.Equal("Again: Answer tersely", state.Messages[2].Text);
    }
}
