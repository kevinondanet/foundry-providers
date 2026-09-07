using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

/// <summary>The canned <see cref="ScriptedModelApi"/> used by every behavioural test and the showcase's fake mode.</summary>
public class ScriptedModelApiTests
{
    private static readonly ToolInfo Bash = new("bash", "run bash");

    [Fact]
    public async Task text_turns_replay_in_order_and_requests_are_recorded()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("one"), ScriptedTurn.Text("two", new ModelUsage(1, 2, 3)));

        var first = await api.GenerateAsync([new ChatMessageUser("a")], [Bash], ToolChoice.Auto, new GenerateConfig { MaxTokens = 9 });
        var second = await api.GenerateAsync([new ChatMessageUser("b")], [], ToolChoice.None, new GenerateConfig());

        Assert.Equal("one", first.OutputOrThrow().Completion);
        Assert.Equal(StopReason.Stop, first.Output!.StopReason);
        Assert.Equal("two", second.OutputOrThrow().Completion);
        Assert.Equal(new ModelUsage(1, 2, 3), second.Output!.Usage);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal("a", api.Requests[0].Input[0].Text);
        Assert.Equal("bash", Assert.Single(api.Requests[0].Tools).Name);
        Assert.Equal(9, api.Requests[0].Config.MaxTokens);
        Assert.Same(ToolChoice.None, api.Requests[1].ToolChoice);
        Assert.Equal(0, api.Remaining);
        Assert.Equal("scripted", first.Call.Request["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task tool_call_turns_build_an_assistant_message_with_the_call()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("bash", new { cmd = "ls -la" }, id: "call_1", text: "listing"));

        var output = (await api.GenerateAsync([new ChatMessageUser("a")], [Bash], ToolChoice.Auto, new GenerateConfig())).OutputOrThrow();

        Assert.Equal(StopReason.ToolCalls, output.StopReason);
        Assert.Equal("listing", output.Message.Text);
        var call = Assert.Single(output.Message.ToolCalls!);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("bash", call.Function);
        Assert.Equal("ls -la", call.Arguments["cmd"]!.GetValue<string>());
        Assert.NotEmpty(Assert.Single(ScriptedTurn.ToolCall("bash", new Dictionary<string, object?> { ["cmd"] = "x" }).Output!.Message.ToolCalls!).Id);
    }

    [Fact]
    public async Task exhausted_scripts_answer_a_final_text_unless_told_to_throw()
    {
        var quiet = new ScriptedModelApi();
        var strict = new ScriptedModelApi([]) { ThrowWhenExhausted = true };

        var output = (await quiet.GenerateAsync([new ChatMessageUser("a")], [], ToolChoice.Auto, new GenerateConfig())).OutputOrThrow();

        Assert.Equal(ScriptedModelApi.ExhaustedText, output.Completion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => strict.GenerateAsync([new ChatMessageUser("a")], [], ToolChoice.Auto, new GenerateConfig()));
    }

    [Fact]
    public async Task throw_error_and_factory_turns()
    {
        var api = new ScriptedModelApi(
            ScriptedTurn.Throw(new TimeoutException("slow")),
            ScriptedTurn.Error(new InvalidOperationException("terminal")),
            ScriptedTurn.From((input, tools) => ModelOutput.FromContent("scripted", $"{input.Count} messages, {tools.Count} tools")));

        await Assert.ThrowsAsync<TimeoutException>(() => api.GenerateAsync([new ChatMessageUser("a")], [], ToolChoice.Auto, new GenerateConfig()));
        var terminal = await api.GenerateAsync([new ChatMessageUser("a")], [], ToolChoice.Auto, new GenerateConfig());
        var built = await api.GenerateAsync([new ChatMessageUser("a"), new ChatMessageUser("b")], [Bash], ToolChoice.Auto, new GenerateConfig());

        Assert.Null(terminal.Output);
        Assert.IsType<InvalidOperationException>(terminal.Error);
        Assert.True(terminal.Call.Error);
        Assert.Equal("2 messages, 1 tools", built.OutputOrThrow().Completion);
    }

    [Fact]
    public async Task model_name_is_configurable_and_streaming_receives_the_text()
    {
        var api = new ScriptedModelApi([ScriptedTurn.Text("hello")], modelName: "fake-gpt");
        var events = new List<StreamEvent>();

        await api.GenerateAsync([new ChatMessageUser("a")], [], ToolChoice.Auto, new GenerateConfig(), e => { events.Add(e); return Task.CompletedTask; });

        Assert.Equal("fake-gpt", api.ModelName);
        Assert.Equal(2048, api.MaxTokens());
        Assert.Equal("hello", Assert.IsType<StreamTextEvent>(Assert.Single(events)).Text);
    }
}
