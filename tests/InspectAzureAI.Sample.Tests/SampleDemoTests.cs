using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Model.Cost;

namespace InspectAzureAI.Sample.Tests;

/// <summary>
/// The Sample app's eval-engine demos driven in-process against the canned <c>--fake</c> transport: the prompt cache
/// (write, then read from disk), cost accounting from a price file (and the unpriced case) and structured output
/// (the schema on the wire, the reply parsed into the record).
/// </summary>
public sealed class SampleDemoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inspect-sample-tests", Guid.NewGuid().ToString("N"));

    public SampleDemoTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task cache_demo_writes_on_the_first_call_and_reads_on_the_second()
    {
        var cacheDir = Path.Combine(_dir, "cache");
        using var env = new EnvVar(CacheOps.CacheDirVar, cacheDir);
        var api = Cli.CreateApi("gpt-5.4-mini", null, fake: true);

        var (exit, output) = await CaptureAsync(() => Cli.CacheDemo(api, "hello there", "1D"));

        Assert.Equal(0, exit);
        Assert.Contains("cache dir : " + Path.Combine(cacheDir, "generate", "gpt-5.4-mini"), output);
        Assert.Contains("policy    : expiry 1D (86400s), per_epoch True", output);
        Assert.Contains("call 1    : cache=write (provider called, output stored)", output);
        Assert.Contains("call 2    : cache=read (served from the cache, no provider call)", output);
        Assert.Contains("I am a canned fake Azure AI endpoint", output);
        Assert.Contains("entries   : 1 for gpt-5.4-mini", output);
        Assert.Single(Directory.GetFiles(Path.Combine(cacheDir, "generate", "gpt-5.4-mini")));
    }

    [Fact]
    public async Task cache_demo_rejects_a_bad_expiry()
    {
        var api = Cli.CreateApi("gpt-5.4-mini", null, fake: true);
        var ex = await Assert.ThrowsAsync<UsageError>(() => Cli.CacheDemo(api, "hello", "soon"));
        Assert.Contains("--cache-expiry", ex.Message);
    }

    [Fact]
    public async Task cost_demo_prices_the_call_from_a_config_file()
    {
        var config = Path.Combine(_dir, "prices.json");
        await File.WriteAllTextAsync(config, """{ "priced-fake-deployment": { "input": 1.0, "output": 2.0, "input_cache_write": 0, "input_cache_read": 0 } }""");
        var api = Cli.CreateApi("priced-fake-deployment", null, fake: true);

        var (exit, output) = await CaptureAsync(() => Cli.CostDemo(api, "hello", config));

        Assert.Equal(0, exit);
        Assert.Contains("prices    : input $1/M, output $2/M, cache write $0/M, cache read $0/M", output);
        Assert.Contains("usage     : input=30 output=15 total=45", output);
        // 30 input tokens at $1/M plus 15 output tokens at $2/M
        Assert.Contains("cost      : $0.000060 = input 30 × $1/M + output 15 × $2/M", output);
    }

    [Fact]
    public async Task cost_demo_reports_an_unpriced_model_as_such()
    {
        ModelInfoLookup.ClearModelInfoCache();
        var api = Cli.CreateApi("unknown-deployment-xyz", null, fake: true);

        var (exit, output) = await CaptureAsync(() => Cli.CostDemo(api, "hello", null));

        Assert.Equal(0, exit);
        Assert.Contains("model info: (not in the model database)", output);
        Assert.Contains("prices    : none for unknown-deployment-xyz", output);
        Assert.Contains(ModelCostConfig.EnvironmentVariable, output);
        Assert.Contains("cost      : unpriced", output);
    }

    [Fact]
    public async Task cost_demo_rejects_a_bad_config_file()
    {
        var config = Path.Combine(_dir, "bad.json");
        await File.WriteAllTextAsync(config, "{ \"m\": { \"input\": 1 } }");
        var api = Cli.CreateApi("gpt-5.4-mini", null, fake: true);

        var ex = await Assert.ThrowsAsync<UsageError>(() => Cli.CostDemo(api, "hello", config));
        Assert.Contains("--model-cost-config", ex.Message);
        Assert.Contains("--model-cost-config", (await Assert.ThrowsAsync<UsageError>(() => Cli.CostDemo(api, "hello", Path.Combine(_dir, "missing.json")))).Message);
    }

    [Fact]
    public async Task structured_demo_sends_the_schema_and_parses_the_reply()
    {
        var api = Cli.CreateApi("gpt-5.4-mini", null, fake: true);

        var (exit, output) = await CaptureAsync(() => Cli.Structured(api, ""));

        Assert.Equal(0, exit);
        Assert.Contains("-- response schema on the wire --", output);
        Assert.Contains("\"type\": \"json_schema\"", output);
        Assert.Contains("\"name\": \"city_facts\"", output);
        Assert.Contains("\"population_millions\"", output);
        Assert.Contains("\"description\": \"The city's name\"", output);
        Assert.Contains("\"strict\": true", output);
        Assert.Contains("city      : Paris", output);
        Assert.Contains("country   : France", output);
        Assert.Contains("population: 2.1 million", output);
        Assert.Contains("landmarks : Eiffel Tower, Louvre, Notre-Dame", output);
    }

    private static async Task<(int Exit, string Output)> CaptureAsync(Func<Task<int>> run)
    {
        var stdout = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(stdout);
        try
        {
            var exit = await run();
            return (exit, stdout.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    private sealed class EnvVar : IDisposable
    {
        private readonly string _name;

        private readonly string? _previous;

        public EnvVar(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
