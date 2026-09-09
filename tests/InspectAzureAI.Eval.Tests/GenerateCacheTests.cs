using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>
/// Port-level behaviour of the <c>cache</c> argument of <c>task_generate</c> (<c>_eval/task/generate.py</c>), the
/// <c>Generate</c> protocol and the <c>generate()</c> solver: the argument reaches <c>Model.generate</c> on every
/// call of the tool loop. Every test gets its own <c>INSPECT_CACHE_DIR</c>.
/// </summary>
public sealed class GenerateCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inspect-generate-cache-tests", Guid.NewGuid().ToString("N"));

    private readonly EnvVarScope _env;

    public GenerateCacheTests()
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

    private sealed class CollectingSink : IModelEventSink
    {
        public List<ModelEvent> Events { get; } = [];

        public void OnModelEvent(ModelEvent e) => Events.Add(e);
    }

    private static TaskState State(params ToolDef[] tools)
    {
        var state = new TaskState("scripted", 1, 1, "sum", [new ChatMessageUser("sum")]);
        state.Tools.AddRange(tools);
        return state;
    }

    [Fact]
    public async Task generate_with_cache_serves_the_second_identical_call_from_the_cache()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("answer")) { ThrowWhenExhausted = true };
        var sink = new CollectingSink();
        var generate = GenerateLoop.Create(new Model(api) { EventSink = sink });

        var first = await generate(State(), cache: CachePolicy.Default);
        // Python's cache=True: the bool converts to the default policy
        var second = await generate(State(), cache: true);

        Assert.Single(api.Requests);
        Assert.Equal("answer", first.Output.Completion);
        Assert.Equal("answer", second.Output.Completion);
        Assert.Equal([CacheMode.Write, CacheMode.Read], sink.Events.Select(e => e.Cache));
    }

    [Fact]
    public async Task generate_without_cache_calls_the_model_every_time()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("one"), ScriptedTurn.Text("two"));
        var sink = new CollectingSink();
        var generate = GenerateLoop.Create(new Model(api) { EventSink = sink });

        var first = await generate(State());
        var second = await generate(State(), cache: false);

        Assert.Equal(2, api.Requests.Count);
        Assert.Equal("one", first.Output.Completion);
        Assert.Equal("two", second.Output.Completion);
        Assert.All(sink.Events, e => Assert.Null(e.Cache));
    }

    [Fact]
    public async Task a_missing_cache_argument_falls_back_to_the_config_cache()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("answer")) { ThrowWhenExhausted = true };
        var generate = GenerateLoop.Create(new Model(api));
        var config = new GenerateConfig { Cache = true };

        await generate(State(), config: config);
        var second = await generate(State(), config: config);

        Assert.Single(api.Requests);
        Assert.Equal("answer", second.Output.Completion);
    }

    [Fact]
    public async Task the_cache_applies_to_every_generate_of_the_tool_loop()
    {
        var api = new ScriptedModelApi(ScriptedTurn.ToolCall("add", new { a = 1, b = 2 }, id: "c1"), ScriptedTurn.Text("3")) { ThrowWhenExhausted = true };
        var generate = GenerateLoop.Create(new Model(api));

        var first = await generate(State(Add), cache: true);
        Assert.Equal(2, api.Requests.Count);

        var second = await generate(State(Add), cache: true);

        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(["user", "assistant", "tool", "assistant"], first.Messages.Select(m => m.Role));
        Assert.Equal(first.Messages.Select(m => m.Role), second.Messages.Select(m => m.Role));
        Assert.Equal("3", Assert.Single(second.Messages.OfType<ChatMessageTool>()).Text);
        Assert.Equal("3", second.Output.Completion);
    }

    [Fact]
    public async Task the_generate_solver_forwards_cache_to_the_delegate()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("answer")) { ThrowWhenExhausted = true };
        var generate = GenerateLoop.Create(new Model(api));
        var solver = Solvers.Generate(cache: true);

        await solver(State(), generate, CancellationToken.None);
        var second = await solver(State(), generate, CancellationToken.None);

        Assert.Single(api.Requests);
        Assert.Equal("answer", second.Output.Completion);
    }

    [Fact]
    public async Task the_generate_solver_without_cache_calls_the_model()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("one"), ScriptedTurn.Text("two"));
        var generate = GenerateLoop.Create(new Model(api));
        var solver = Solvers.Generate();

        await solver(State(), generate, CancellationToken.None);
        var second = await solver(State(), generate, CancellationToken.None);

        Assert.Equal(2, api.Requests.Count);
        Assert.Equal("two", second.Output.Completion);
    }
}
