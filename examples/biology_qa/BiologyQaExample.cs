using System.Globalization;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.BiologyQa;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/biology_qa.py</c> as an <see cref="IExample"/>: the <c>biology_qa</c> task (the 20 biology
/// trivia questions of the bundled <c>biology_qa</c> example dataset, answered with the <c>web_search()</c> tool
/// configured for the grok, openai, anthropic, tavily and gemini providers at once, and graded by
/// <c>model_graded_qa()</c>). Deviation: on Foundry only two of the five providers execute — a <c>claude-*</c>
/// deployment on the anthropic route uses Claude's own web search, every other deployment searches through Tavily
/// (<c>TAVILY_API_KEY</c>); the grok, openai and gemini options are carried in the tool's options exactly as Python
/// writes them but never run, because the Azure chat completions route has no server-side search. Under
/// <c>--fake</c> the tool is the same but its Tavily HTTP transport is the scripted <see cref="FakeTavilyHandler"/>.
/// </summary>
public sealed class BiologyQaExample : IExample
{
    /// <summary>The task name (<c>@task def biology_qa</c>).</summary>
    public const string TaskName = "biology_qa";

    public string Name => TaskName;

    public string Description => "Biology QA: 20 trivia questions answered with the web_search tool (five providers configured; tavily or Claude's own search run on Foundry), graded by model_graded_qa";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, Build, "use_tools(web_search(providers={grok, openai, anthropic, tavily, gemini})) + generate, model-graded QA"),
    ];

    public ExampleDefaults Defaults { get; } = new(ModelHint: "a tool-calling deployment; TAVILY_API_KEY for the tavily provider (a claude-* deployment on the anthropic route uses Claude's own search instead)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "Only the anthropic and tavily providers execute on Foundry: a claude-* deployment on the anthropic route uses Claude's server-side web search, every other deployment searches through Tavily (TAVILY_API_KEY, outbound HTTPS to api.tavily.com). The grok, openai (search_context_size, user_location) and gemini (time_range_filter) options are recorded in the tool's options exactly as Python writes them but are inert, because the Azure chat completions route has no server-side search.",
        "gemini_options.time_range_filter holds ISO-8601 strings (Python's datetime.now(timezone.utc).replace(microsecond=0) isoformat: 2026-01-01T00:00:00+00:00) instead of datetime objects, and is computed when the task is built rather than when the module is imported.",
        "The --fake scripted model (one web_search call per question, then the answer the search returned) and the scripted Tavily transport (a canned https://api.tavily.com/search response whose answer is the dataset's target, with one citation) are additions for running the example offline; --fake sets TAVILY_API_KEY to a placeholder for the process when it is not set, because the Tavily provider validates the variable before its first call.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => FakeBiologyQaModel.Create();

    /// <summary>No sandbox: web_search runs in-process.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>Port of <c>openai_options</c>, verbatim.</summary>
    public static JsonObject OpenaiOptions() => new()
    {
        ["search_context_size"] = "high",
        ["user_location"] = new JsonObject
        {
            ["type"] = "approximate",
            ["country"] = "US",
            ["city"] = "Boston",
        },
    };

    /// <summary>Port of <c>tavily_options</c>, verbatim.</summary>
    public static JsonObject TavilyOptions() => new() { ["max_results"] = 5, ["max_connections"] = 8 };

    /// <summary>
    /// Port of <c>gemini_options</c>: a <c>time_range_filter</c> of the last 365 days ending now (UTC, whole
    /// seconds). Deviation: the two instants are ISO-8601 strings in Python's <c>isoformat()</c> shape
    /// (<c>2026-01-01T00:00:00+00:00</c>), the JSON form the tool options carry, rather than datetime objects.
    /// </summary>
    public static JsonObject GeminiOptions(DateTimeOffset? now = null)
    {
        var end = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        end = new DateTimeOffset(end.Year, end.Month, end.Day, end.Hour, end.Minute, end.Second, TimeSpan.Zero);
        var start = end.AddDays(-365);
        return new JsonObject
        {
            ["time_range_filter"] = new JsonObject
            {
                ["start_time"] = IsoFormat(start),
                ["end_time"] = IsoFormat(end),
            },
        };
    }

    /// <summary>Port of the <c>providers={...}</c> dictionary of the task, verbatim (<c>gemini</c> computed now).</summary>
    public static WebSearchProviders Providers() => new()
    {
        ["grok"] = true,
        ["openai"] = OpenaiOptions(),
        ["anthropic"] = true,
        ["tavily"] = TavilyOptions(),
        ["gemini"] = GeminiOptions(),
    };

    /// <summary>The task's <c>web_search(providers={...})</c> tool; <paramref name="handler"/> replaces the Tavily HTTP transport (the fake run).</summary>
    public static ToolDef WebSearchTool(HttpMessageHandler? handler = null) => BuiltinTools.WebSearch(new WebSearchProviderSpec[] { Providers() }, handler);

    /// <summary>
    /// Port of <c>@task def biology_qa() -> Task</c>, discoverable by the <c>inspectai</c> CLI
    /// (<c>eval biology_qa --assembly ...</c>): <c>example_dataset("biology_qa", FieldSpec(input="question",
    /// target="answer"))</c>, <c>use_tools(web_search(providers=...))</c> + <c>generate()</c>, <c>model_graded_qa()</c>.
    /// </summary>
    [Task(TaskName)]
    public static EvalTask BiologyQa() => Build(handler: null);

    /// <summary>The task over a web_search tool whose Tavily transport is <paramref name="handler"/> (null: the real service).</summary>
    public static EvalTask Build(HttpMessageHandler? handler) => new()
    {
        Name = TaskName,
        Dataset = Datasets.Example(name: "biology_qa", fields: new FieldSpec(Input: "question", Target: "answer")),
        Solver = Solvers.Chain(
            Solvers.UseTools(WebSearchTool(handler)),
            Solvers.Generate()),
        Scorers = [Scorers.ModelGradedQa()],
    };

    private static EvalTask Build(ExampleContext ctx)
    {
        if (!ctx.Fake)
        {
            return Build(handler: null);
        }

        FakeTavilyHandler.EnsureApiKey();
        return Build(new FakeTavilyHandler());
    }

    /// <summary>Python's <c>datetime.isoformat()</c> of a whole-second UTC instant.</summary>
    private static string IsoFormat(DateTimeOffset instant) => instant.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+00:00";
}
