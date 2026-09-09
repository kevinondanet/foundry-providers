using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Cache;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Cache;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the <c>cache</c> example (<c>examples/cache</c>): the five tasks' shape, the custom solver's cache
/// argument, the cache behaviour each task demonstrates (a second run served from the cache, scopes keying
/// separately, <c>per_epoch=False</c> sharing one provider call across epochs, the stored expiry) and an offline run
/// through the runner. Every test gets its own <c>INSPECT_CACHE_DIR</c>.
/// </summary>
public sealed class CacheTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, Func<EvalTask>> PythonTasks = new Dictionary<string, Func<EvalTask>>(StringComparer.Ordinal)
    {
        ["cache_example"] = CacheTasks.CacheExample,
        ["cache_example_with_expiry"] = CacheTasks.CacheExampleWithExpiry,
        ["cache_example_never_expires"] = CacheTasks.CacheExampleNeverExpires,
        ["cache_example_scoped"] = CacheTasks.CacheExampleScoped,
        ["cache_example_ignore_epochs"] = CacheTasks.CacheExampleIgnoreEpochs,
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "cache-" + Guid.NewGuid().ToString("N"));
    private readonly string _logDir;
    private readonly string _cacheDir;
    private readonly string? _previousCacheDir;

    public CacheTests()
    {
        _logDir = Path.Combine(_root, "logs");
        _cacheDir = Path.Combine(_root, "cache");
        _previousCacheDir = Environment.GetEnvironmentVariable(CacheOps.CacheDirVar);
        Environment.SetEnvironmentVariable(CacheOps.CacheDirVar, _cacheDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CacheOps.CacheDirVar, _previousCacheDir);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // task shape
    // ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("cache_example")]
    [InlineData("cache_example_with_expiry")]
    [InlineData("cache_example_never_expires")]
    [InlineData("cache_example_scoped")]
    [InlineData("cache_example_ignore_epochs")]
    public void every_task_has_the_one_sample_the_match_scorer_and_no_sandbox(string name)
    {
        var task = PythonTasks[name]();

        Assert.Equal(name, task.Name);
        var sample = Assert.Single(task.Dataset);
        Assert.Equal("What is the capital of France?", sample.Input.Text);
        Assert.Equal("Paris", sample.Target.Text);
        Assert.Equal("match", Assert.Single(task.Scorers).Name);
        Assert.Null(task.Sandbox);
        Assert.Null(task.Epochs);
    }

    [Fact]
    public void the_example_declares_the_five_python_tasks_in_order()
    {
        var example = new CacheExample();

        Assert.Equal("cache", example.Name);
        Assert.Equal(PythonTasks.Keys, example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.NotEmpty(example.Deviations);
        Assert.Null(example.FakeSandbox(Context(fake: true)));
    }

    [Fact]
    public async Task solver_with_cache_passes_its_policy_to_generate()
    {
        var policies = new List<CachePolicy?>();
        Generate generate = (state, _, _, cache, _) =>
        {
            policies.Add(cache);
            return Task.FromResult(state);
        };
        var scoped = new CachePolicy { Scopes = new Dictionary<string, string> { ["role"] = "attacker" } };

        await CacheTasks.SolverWithCache(cache: true)(State(), generate, CancellationToken.None);
        await CacheTasks.SolverWithCache(scoped)(State(), generate, CancellationToken.None);
        await CacheTasks.SolverWithCache(null)(State(), generate, CancellationToken.None);

        // Python's cache=True is the default policy; a policy goes through untouched; False (null) disables caching
        Assert.Equal([CachePolicy.Default, scoped, null], policies);
    }

    [Fact]
    public void the_report_describes_each_cache_mode()
    {
        Assert.Equal("no model call", CacheReport.Describe(null));
        Assert.StartsWith("read", CacheReport.Describe(Event(CacheMode.Read)), StringComparison.Ordinal);
        Assert.StartsWith("write", CacheReport.Describe(Event(CacheMode.Write)), StringComparison.Ordinal);
        Assert.StartsWith("none", CacheReport.Describe(Event(null)), StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the cache behaviour each task demonstrates
    // ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("cache_example")]
    [InlineData("cache_example_with_expiry")]
    [InlineData("cache_example_never_expires")]
    [InlineData("cache_example_scoped")]
    [InlineData("cache_example_ignore_epochs")]
    public async Task a_second_run_of_a_task_is_served_from_the_cache(string name)
    {
        var first = new ScriptedModelApi(ScriptedTurn.Text("Paris"));
        var second = new ScriptedModelApi(ScriptedTurn.Text("Paris"));

        var firstLog = await Run(PythonTasks[name](), first);
        var secondLog = await Run(PythonTasks[name](), second);

        Assert.Equal(EvalStatus.Success, firstLog.Status);
        Assert.Equal(EvalStatus.Success, secondLog.Status);
        Assert.Equal([CacheMode.Write], CacheModes(firstLog));
        Assert.Equal([CacheMode.Read], CacheModes(secondLog));
        Assert.Single(first.Requests);
        Assert.Empty(second.Requests);
        Assert.Equal("Paris", secondLog.Samples![0].Output.Completion);
        Assert.Equal("C", secondLog.Samples[0].Scores!["match"].Text);
        Assert.Equal(1.0, secondLog.Results!.Scores[0].Metrics["accuracy"].Value);
    }

    [Fact]
    public async Task scopes_key_the_cache_separately_from_the_default_policy()
    {
        await Run(CacheTasks.CacheExample(), new ScriptedModelApi(ScriptedTurn.Text("Paris")));
        var scopedApi = new ScriptedModelApi(ScriptedTurn.Text("Paris"));

        var scoped = await Run(CacheTasks.CacheExampleScoped(), scopedApi);
        var scopedAgain = await Run(CacheTasks.CacheExampleScoped(), new ScriptedModelApi(ScriptedTurn.Text("Paris")));

        // the same prompt under {role: attacker, team: red} is a miss, then a hit of its own entry
        Assert.Equal([CacheMode.Write], CacheModes(scoped));
        Assert.Single(scopedApi.Requests);
        Assert.Equal([CacheMode.Read], CacheModes(scopedAgain));
    }

    [Fact]
    public async Task ignore_epochs_serves_every_epoch_after_the_first_from_one_provider_call()
    {
        var api = new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.Text("Paris"), 5));
        var perEpochApi = new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.Text("Paris"), 5));
        var ctx = Context(fake: true);

        var log = await Run(CacheExample.Build(ctx, CacheTasks.CacheExampleIgnoreEpochs()), api, epochs: 3);
        var perEpoch = await Run(CacheExample.Build(ctx, CacheTasks.CacheExample()), perEpochApi, epochs: 3);

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(3, log.Results!.CompletedSamples);
        Assert.Single(api.Requests);
        Assert.Equal([CacheMode.Write, CacheMode.Read, CacheMode.Read], log.Samples!.OrderBy(sample => sample.Epoch).SelectMany(CacheModes));
        // the default policy keys on the epoch, so every epoch calls the provider
        Assert.Equal(3, perEpochApi.Requests.Count);
        Assert.All(perEpoch.Samples!, sample => Assert.Equal([CacheMode.Write], CacheModes(sample)));
    }

    [Fact]
    public async Task the_fake_build_runs_one_sample_at_a_time_and_appends_the_report()
    {
        var output = new StringWriter();
        var ctx = Context(fake: true, output);

        var fake = CacheExample.Build(ctx, CacheTasks.CacheExample());
        var live = CacheExample.Build(Context(fake: false), CacheTasks.CacheExample());
        await Run(fake, new ScriptedModelApi(ScriptedTurn.Text("Paris")));

        Assert.Equal(1, fake.Config.MaxConnections);
        Assert.Null(live.Config.MaxConnections);
        Assert.Contains(CacheReport.DirectoryPrefix, output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"{CacheReport.SamplePrefix}sample 1 epoch 1: write", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task the_expiry_of_the_policy_is_stored_with_the_entry()
    {
        var before = DateTimeOffset.UtcNow;
        await Run(CacheTasks.CacheExampleWithExpiry(), new ScriptedModelApi(ScriptedTurn.Text("Paris")));
        var entry = Assert.Single(CacheFiles());
        var expiry = DateTimeOffset.Parse(JsonNode.Parse(File.ReadAllText(entry))!["expiry"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);

        // "12h": the entry expires twelve hours after it was stored
        Assert.InRange(expiry, before.AddHours(12).AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(12).AddMinutes(5));

        await Run(CacheTasks.CacheExampleNeverExpires(), new ScriptedModelApi(ScriptedTurn.Text("Paris")));
        var files = CacheFiles();
        Assert.Equal(2, files.Count);
        var forever = files.Single(file => file != entry);
        Assert.Null(JsonNode.Parse(File.ReadAllText(forever))!["expiry"]);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the runner
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_the_second_run_reads_the_cache()
    {
        var example = new CacheExample();
        var registry = ExampleRegistry.Of(example);

        var first = new StringWriter();
        Assert.Equal(0, await ExampleRunner.MainAsync(["cache", "--fake", "--log-dir", _logDir], registry, first, first));
        Assert.Single(example.FakeApi!.Requests);

        var second = new StringWriter();
        Assert.Equal(0, await ExampleRunner.MainAsync(["cache", "--fake", "--log-dir", _logDir], registry, second, second));

        Assert.Contains("status    : success (1/1 samples completed)", first.ToString(), StringComparison.Ordinal);
        Assert.Contains("sample 1 epoch 1: write", first.ToString(), StringComparison.Ordinal);
        Assert.Contains(CacheReport.DirectoryPrefix + CacheOps.CachePath("scripted"), first.ToString(), StringComparison.Ordinal);
        Assert.Contains("sample 1 epoch 1: read", second.ToString(), StringComparison.Ordinal);
        Assert.Matches(@"match\s+accuracy\s+1\.000", second.ToString());
        Assert.Empty(example.FakeApi!.Requests);
        Assert.Equal(2, Directory.GetFiles(_logDir, "*.eval").Length);
    }

    [Fact]
    public async Task the_runner_shows_ignore_epochs_reusing_the_first_epoch_across_epochs()
    {
        var example = new CacheExample();
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["cache", "--fake", "--task", "cache_example_ignore_epochs", "--epochs", "3", "--log-dir", _logDir], ExampleRegistry.Of(example), output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("sample 1 epoch 1: write", text, StringComparison.Ordinal);
        Assert.Contains("sample 1 epoch 2: read", text, StringComparison.Ordinal);
        Assert.Contains("sample 1 epoch 3: read", text, StringComparison.Ordinal);
        Assert.Contains("status    : success (3/3 samples completed)", text, StringComparison.Ordinal);
        Assert.Single(example.FakeApi!.Requests);
    }

    [Fact]
    public async Task help_lists_the_tasks_and_deviations()
    {
        var output = new StringWriter();

        Assert.Equal(0, await ExampleRunner.MainAsync(["cache", "--help"], ExampleRegistry.Of(new CacheExample()), output, output));

        foreach (var name in PythonTasks.Keys)
        {
            Assert.Contains(name, output.ToString(), StringComparison.Ordinal);
        }

        Assert.Contains("deviations from Python:", output.ToString(), StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------------------------

    private Task<EvalLog> Run(EvalTask task, ScriptedModelApi api, int? epochs = null) =>
        Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = _logDir, Epochs = epochs });

    private IReadOnlyList<string> CacheFiles() =>
        Directory.Exists(_cacheDir)
            ? Directory.GetFiles(_cacheDir, "*", SearchOption.AllDirectories).Where(file => !file.EndsWith(".tmp", StringComparison.Ordinal)).ToList()
            : [];

    private static IEnumerable<CacheMode?> CacheModes(EvalLog log) => log.Samples!.SelectMany(CacheModes);

    private static IEnumerable<CacheMode?> CacheModes(EvalSample sample) => sample.Events.OfType<ModelEvent>().Select(e => e.Cache);

    private static ExampleContext Context(bool fake, TextWriter? output = null) =>
        new(Path.Combine(AppContext.BaseDirectory, "cache"), null, fake, new Dictionary<string, string>(), null, null, output ?? TextWriter.Null);

    private static TaskState State() => new("scripted", 1, 1, "What is the capital of France?", [new ChatMessageUser("What is the capital of France?")]);

    private static ModelEvent Event(CacheMode? cache) => new()
    {
        Model = "scripted",
        Input = [],
        ToolChoice = ToolChoice.Auto,
        Config = new GenerateConfig(),
        Output = new ModelOutput(),
        Cache = cache,
    };
}
