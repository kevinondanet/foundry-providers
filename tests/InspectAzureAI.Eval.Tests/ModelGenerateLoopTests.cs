using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port-level behaviour of <c>Model.generate_loop</c> (<c>model/_model.py</c>): generate + tool execution until
/// the model stops calling tools, returning the new messages and the final output. (The solver-level loop of
/// <c>task_generate</c> is covered by <c>GenerateLoopTests</c>.)
/// </summary>
public sealed class ModelGenerateLoopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inspect-model-generate-loop-tests", Guid.NewGuid().ToString("N"));

    private readonly EnvVarScope _env;

    public ModelGenerateLoopTests()
    {
        _env = new EnvVarScope().Set(CacheOps.CacheDirVar, _root);
    }

    public void Dispose()
    {
        _env.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static readonly ToolDef Add = new(
        "add",
        "Adds two numbers.",
        new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["a"] = ToolParam.Of("integer"), ["b"] = ToolParam.Of("integer") },
            Required = ["a", "b"],
        },
        (args, _) => Task.FromResult<ToolResult>((args["a"]!.GetValue<int>() + args["b"]!.GetValue<int>()).ToString()));

    /// <summary>A tool source that counts how often it is resolved.</summary>
    private sealed class CountingSource(IReadOnlyList<ToolDef> tools) : IToolSource
    {
        public int Calls;

        public Task<IReadOnlyList<ToolDef>> ToolsAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(tools);
        }
    }

    [Fact]
    public async Task loops_until_the_model_stops_calling_tools_and_returns_only_the_new_messages()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("add", new { a = 1, b = 2 }, id: "c1"), ScriptedTurn.Text("3"));
        var model = new Model(api);

        var (messages, output) = await model.GenerateLoopAsync("What is 1 + 2?", [Add]);

        Assert.Equal(["assistant", "tool", "assistant"], messages.Select(m => m.Role));
        var toolMessage = Assert.Single(messages.OfType<ChatMessageTool>());
        Assert.Equal("3", toolMessage.Text);
        Assert.Equal("c1", toolMessage.ToolCallId);
        Assert.Equal("3", output.Completion);

        Assert.Equal(2, api.Requests.Count);
        var firstInput = Assert.Single(api.Requests[0].Input);
        Assert.Equal("What is 1 + 2?", Assert.IsType<ChatMessageUser>(firstInput).Text);
        Assert.Equal("add", Assert.Single(api.Requests[0].Tools).Name);
        Assert.Equal(["user", "assistant", "tool"], api.Requests[1].Input.Select(m => m.Role));
    }

    [Fact]
    public async Task returns_the_single_assistant_message_when_there_are_no_tool_calls_and_leaves_the_input_untouched()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("hello"));
        var model = new Model(api);
        var input = new List<ChatMessage> { new ChatMessageSystem("Be brief."), new ChatMessageUser("hi") };

        var (messages, output) = await model.GenerateLoopAsync(input);

        var assistant = Assert.IsType<ChatMessageAssistant>(Assert.Single(messages));
        Assert.Equal("hello", assistant.Text);
        Assert.Equal("hello", output.Completion);
        Assert.Equal(2, input.Count);
        Assert.Empty(Assert.Single(api.Requests).Tools);
    }

    [Fact]
    public async Task tool_sources_are_resolved_on_every_turn()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("add", new { a = 2, b = 2 }), ScriptedTurn.Text("4"));
        var model = new Model(api);
        var source = new CountingSource([Add]);

        var (messages, output) = await model.GenerateLoopAsync("2 + 2?", [source]);

        Assert.Equal(2, source.Calls);
        Assert.Equal("4", Assert.Single(messages.OfType<ChatMessageTool>()).Text);
        Assert.Equal("4", output.Completion);
    }

    [Fact]
    public async Task a_plain_tool_list_converts_to_the_tools_parameter()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("ok"));
        var model = new Model(api);
        var tools = new List<ToolDef> { Add };

        await model.GenerateLoopAsync("hi", tools);

        Assert.Equal("add", Assert.Single(Assert.Single(api.Requests).Tools).Name);
    }

    [Fact]
    public async Task max_tool_output_comes_from_the_call_config_then_the_model_config()
    {
        var original = new string('x', 20000) + "0123456789";
        var large = new ToolDef("large", "Returns text.", new ToolParams(), (_, _) => Task.FromResult<ToolResult>(original));

        var callApi = new ScriptedModelApi(ScriptedTurn.ToolCall("large", new { }), ScriptedTurn.Text("done"));
        var (callMessages, _) = await new Model(callApi, new GenerateConfig { MaxToolOutput = 100 }).GenerateLoopAsync("go", [large], config: new GenerateConfig { MaxToolOutput = 10 });
        var callText = Assert.Single(callMessages.OfType<ChatMessageTool>()).Text;
        Assert.Contains("<START_TOOL_OUTPUT>\n" + original[^10..] + "\n<END_TOOL_OUTPUT>", callText);
        Assert.DoesNotContain(original[^11..], callText);

        var modelApi = new ScriptedModelApi(ScriptedTurn.ToolCall("large", new { }), ScriptedTurn.Text("done"));
        var (modelMessages, _) = await new Model(modelApi, new GenerateConfig { MaxToolOutput = 100 }).GenerateLoopAsync("go", [large]);
        var modelText = Assert.Single(modelMessages.OfType<ChatMessageTool>()).Text;
        Assert.Contains("<START_TOOL_OUTPUT>\n" + original[^100..] + "\n<END_TOOL_OUTPUT>", modelText);
        Assert.DoesNotContain(original[^101..], modelText);
    }

    [Fact]
    public async Task the_config_is_passed_to_every_generate()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("add", new { a = 1, b = 1 }), ScriptedTurn.Text("2"));
        var model = new Model(api);

        await model.GenerateLoopAsync("1 + 1?", [Add], config: new GenerateConfig { Temperature = 0.25 });

        Assert.Equal(2, api.Requests.Count);
        Assert.All(api.Requests, r => Assert.Equal(0.25, r.Config.Temperature));
    }

    [Fact]
    public async Task on_stream_is_forwarded_to_every_generate()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("add", new { a = 1, b = 1 }, text: "Adding."), ScriptedTurn.Text("2"));
        var model = new Model(api);
        var streamed = new List<string>();

        await model.GenerateLoopAsync("1 + 1?", [Add], onStream: e =>
        {
            streamed.Add(Assert.IsType<StreamTextEvent>(e).Text);
            return Task.CompletedTask;
        });

        Assert.Equal(["Adding.", "2"], streamed);
    }

    [Fact]
    public async Task the_cache_applies_to_every_generate_of_the_loop()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("add", new { a = 1, b = 2 }), ScriptedTurn.Text("3")) { ThrowWhenExhausted = true };
        var model = new Model(api);

        var (first, _) = await model.GenerateLoopAsync("What is 1 + 2?", [Add], cache: true);
        Assert.Equal(2, api.Requests.Count);

        var (second, output) = await model.GenerateLoopAsync("What is 1 + 2?", [Add], cache: true);

        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(first.Select(m => m.Role), second.Select(m => m.Role));
        Assert.Equal("3", output.Completion);
    }
}
