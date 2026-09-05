using System.Globalization;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cost;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Foundry;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Model pricing data, cost computation and the cost limit (port of the cost parts of model/_model.py, _model_info.py and util/_limit.py).</summary>
public sealed class CostTests : IDisposable
{
    // $1000 per million input and output tokens: 3 + 4 tokens cost $0.007, the figures of Python's test_sample_limits.py
    private static readonly ModelCost ScriptedCost = new(1000.0, 1000.0, 0.0, 0.0);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inspect-cost-tests", Guid.NewGuid().ToString("N"));

    public CostTests()
    {
        ModelInfoLookup.ClearModelInfoCache();
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        ModelInfoLookup.ClearModelInfoCache();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private EvalOptions Options(ScriptedModelApi api) => new() { Model = new Model(api), LogDir = Path.Combine(_tempDir, "logs"), MaxSamples = 1 };

    private string WriteConfig(string json, string name = "pricing.json")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, json);
        return path;
    }

    // fixtures are not copied to the output directory, so walk up from the test assembly to the project's fixtures folder
    private static readonly Lazy<string> FixtureRoot = new(() =>
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException("No 'fixtures' directory above " + AppContext.BaseDirectory);
    });

    // ---- compute_model_cost ------------------------------------------------------------------------------

    /// <summary>Expected values are the output of the venv's <c>compute_model_cost</c> for the same prices, usage and TTL.</summary>
    public static TheoryData<ModelCost, ModelUsage, string?, double> PythonCostCases => new()
    {
        { new ModelCost(2.5, 10.0, 0.0, 1.25), new ModelUsage(400, 100, 1100) { InputTokensCacheRead = 600 }, null, 0.00275 },
        { new ModelCost(3.0, 15.0, 3.75, 0.3), new ModelUsage(1000, 500, 1500) { InputTokensCacheWrite = 2000, InputTokensCacheRead = 3000 }, null, 0.0189 },
        { new ModelCost(3.0, 15.0, 3.75, 0.3), new ModelUsage(1000, 500, 1500) { InputTokensCacheWrite = 2000, InputTokensCacheRead = 3000 }, "1h", 0.0234 },
        { new ModelCost(3.0, 15.0, 3.75, 0.3), new ModelUsage(1000, 500, 1500) { InputTokensCacheWrite = 2000, InputTokensCacheRead = 3000 }, "5m", 0.0189 },
        { new ModelCost(1000.0, 2000.0, 1500.0, 100.0), new ModelUsage(10, 5, 15) { InputTokensCacheWrite = 20, InputTokensCacheRead = 30 }, null, 0.053000000000000005 },
        { new ModelCost(0.15, 0.6, 0.0, 0.075), new ModelUsage(123456, 7890, 131346) { InputTokensCacheRead = 65432, ReasoningTokens = 1000 }, null, 0.0281598 },
        { new ModelCost(1000.0, 1000.0, 0.0, 0.0), new ModelUsage(3, 4, 7), null, 0.007 },
        { new ModelCost(3.0, 15.0, 3.75, 0.3), new ModelUsage(), null, 0.0 },
        { new ModelCost(3.0, 15.0, 3.75, 0.3), new ModelUsage(0, 0, 1_000_000) { InputTokensCacheWrite = 1_000_000 }, "1h", 6.0 },
    };

    [Theory]
    [MemberData(nameof(PythonCostCases))]
    public void compute_model_cost_matches_python_to_the_last_bit(ModelCost cost, ModelUsage usage, string? cacheTtl, double expected)
    {
        Assert.Equal(expected, ModelCosts.ComputeModelCost(cost, usage, cacheTtl));
    }

    [Fact]
    public void cost_for_a_model_is_null_when_unknown_or_unpriced_and_a_value_when_priced()
    {
        var usage = new ModelUsage(3, 4, 7);

        Assert.Null(ModelCosts.ComputeCostForModel("unknown-deployment-xyz", usage));
        Assert.Null(ModelCosts.ComputeCostForModel("openai/gpt-4o", usage));

        ModelInfoLookup.SetModelCost("openai/gpt-4o", ScriptedCost);

        Assert.Equal(0.007, ModelCosts.ComputeCostForModel("openai/gpt-4o", usage));
        Assert.Equal(0.021, ModelCosts.SampleTotalCost(new Dictionary<string, ModelUsage>
        {
            ["a"] = usage with { TotalCost = 0.007 },
            ["b"] = usage with { TotalCost = 0.014 },
            ["c"] = usage,
        }), 10);
    }

    // ---- the embedded database ---------------------------------------------------------------------------

    [Fact]
    public void embedded_database_matches_the_python_reference_dump()
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureRoot.Value, "model-info", "python-model-db.json")))!.AsObject();
        var database = ModelInfoLookup.Database;

        Assert.Equal(781, expected.Count);
        Assert.Equal(expected.Count, database.Count);
        foreach (var (key, node) in expected)
        {
            var e = node!.AsObject();
            var info = Assert.Contains(key, database);
            Assert.Equal((string?)e["organization"], info.Organization);
            Assert.Equal((string?)e["model"], info.Model);
            Assert.Equal((string?)e["snapshot"], info.Snapshot);
            Assert.Equal((string?)e["release_date"], info.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Assert.Equal((string?)e["knowledge_cutoff_date"], info.KnowledgeCutoffDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Assert.Equal((int?)e["context_length"], info.ContextLength);
            Assert.Equal((int?)e["output_tokens"], info.OutputTokens);
            Assert.Equal((int?)e["input_tokens"], info.InputTokens);
            Assert.Equal((bool?)e["reasoning"], info.Reasoning);
            Assert.Equal((string?)e["reasoning_effort_default"], info.ReasoningEffortDefault);
            Assert.Equal((string?)e["family"], info.Family);
            Assert.Null(info.Cost);
        }
    }

    // ---- lookup -----------------------------------------------------------------------------------------

    /// <summary>Expected fields are what the venv's <c>_get_model_info_direct</c> returns for the same name.</summary>
    [Theory]
    [InlineData("openai/gpt-4o", "OpenAI", "GPT-4o", null, 128000)]
    [InlineData("azureai/gpt-4o", "OpenAI", "GPT-4o", null, 128000)]
    [InlineData("openai/GPT_4o", "OpenAI", "GPT-4o", null, 128000)]
    [InlineData("openai/azure/gpt-4o", "OpenAI", "GPT-4o", null, 128000)]
    [InlineData("azureai/claude-sonnet-4-6", "Anthropic", "Claude Sonnet 4.6", "20250217", 1000000)]
    [InlineData("anthropic/claude-3-5-sonnet-latest", "Anthropic", "Claude Sonnet 3.5", "latest", 200000)]
    [InlineData("azureai/DeepSeek-V4-Flash-0731", "DeepSeek", "V4 Flash", "0731", 1048576)]
    [InlineData("azureai/Kimi-K2.7-Code", "moonshotai", "Kimi K2.7 Code", null, 262144)]
    [InlineData("azureai/gpt-5.4-mini", "OpenAI", "GPT-5.4 Mini", null, 400000)]
    [InlineData("azureai/gpt-5.6-sol", "OpenAI", "GPT-5.6 Sol", null, 1050000)]
    [InlineData("azureai/o3-mini", "OpenAI", "o3-mini", null, 200000)]
    [InlineData("gpt-4o", "OpenAI", "GPT-4o", null, 128000)]
    [InlineData("DeepSeek-V4-Flash-0731", "DeepSeek", "V4 Flash", "0731", 1048576)]
    [InlineData("claude-sonnet-4-6", "Anthropic", "Claude Sonnet 4.6", "20250217", 1000000)]
    public void lookup_resolves_inspect_names_and_bare_deployment_names_like_python(string name, string organization, string model, string? snapshot, int contextLength)
    {
        var info = ModelInfoLookup.GetModelInfo(name);

        Assert.NotNull(info);
        Assert.Equal(organization, info.Organization);
        Assert.Equal(model, info.Model);
        Assert.Equal(snapshot, info.Snapshot);
        Assert.Equal(contextLength, info.ContextLength);
    }

    [Theory]
    [InlineData("unknown-provider/unknown-model-xyz")]
    [InlineData("azureai/Mistral-Large-3")]
    [InlineData("together/meta-llama/Llama-3.1-8B-Instruct")]
    [InlineData("bedrock/anthropic.claude-sonnet-4-5-20250929-v1:0")]
    [InlineData("azureai/model-router")]
    [InlineData("azureai/MAI-Thinking-1")]
    [InlineData("azureai/Cohere-command-a-plus-05-2026")]
    [InlineData("unknown-deployment-xyz")]
    [InlineData("model-router")]
    public void lookup_misses_like_python(string name)
    {
        Assert.Null(ModelInfoLookup.GetModelInfo(name));
    }

    [Theory]
    [InlineData("gpt-5.4-mini", null, "openai/gpt-5.4-mini")]
    [InlineData("grok-4.6", null, "grok/grok-4.6")]
    [InlineData("DeepSeek-V4-Flash", null, "deepseek/DeepSeek-V4-Flash")]
    [InlineData("Kimi-K2.7-Code", null, "moonshotai/Kimi-K2.7-Code")]
    [InlineData("claude-sonnet-4-6", null, "anthropic/claude-sonnet-4-6")]
    [InlineData("Mistral-Large-3", null, "mistral/Mistral-Large-3")]
    [InlineData("my-deployment", "OpenAI", "openai/my-deployment")]
    [InlineData("my-deployment", "Anthropic", "anthropic/my-deployment")]
    [InlineData("my-deployment", "xAI", "grok/my-deployment")]
    [InlineData("MAI-Thinking-1", null, null)]
    [InlineData("model-router", null, null)]
    [InlineData("Cohere-command-a-plus-05-2026", null, null)]
    [InlineData("my-deployment", null, null)]
    public void foundry_overlay_maps_deployment_names_to_base_model_keys(string name, string? format, string? expected)
    {
        Assert.Equal(expected, FoundryModelOverlay.BaseModelKey(name, format));
    }

    [Fact]
    public void grok_deployments_resolve_through_the_overlay_as_python_does_with_provider_resolution()
    {
        // Python's direct lookup misses grok (no org detection); its provider-resolving lookup finds xAI / Grok 4.6
        foreach (var name in new[] { "grok-4.6", "azureai/grok-4.6" })
        {
            var info = ModelInfoLookup.GetModelInfo(name);
            Assert.NotNull(info);
            Assert.Equal("xAI", info.Organization);
            Assert.Equal("Grok 4.6", info.Model);
            Assert.Equal(500000, info.ContextLength);
        }
    }

    [Fact]
    public void foundry_deployments_register_as_aliases_of_their_base_model_without_clobbering_overrides()
    {
        ModelInfoLookup.SetModelInfo("priced-gpt", new ModelInfo { Cost = ScriptedCost });
        var capabilities = new Dictionary<string, string>(StringComparer.Ordinal);
        var deployments = new[]
        {
            new FoundryDeployment("prod-gpt", "gpt-4o", "OpenAI", "2024-11-20", "Succeeded", "GlobalStandard", 10, capabilities),
            new FoundryDeployment("priced-gpt", "gpt-4o", "OpenAI", null, "Succeeded", null, null, capabilities),
            new FoundryDeployment("router", "model-router", "OpenAI", null, "Succeeded", null, null, capabilities),
        };

        var registered = FoundryModelOverlay.RegisterDeployments(deployments);

        Assert.Equal(["prod-gpt"], registered);
        Assert.Equal("GPT-4o", ModelInfoLookup.GetModelInfo("prod-gpt")!.Model);
        Assert.Equal(ScriptedCost, ModelInfoLookup.GetModelInfo("priced-gpt")!.Cost);
        Assert.Null(ModelInfoLookup.GetModelInfo("router"));
    }

    [Fact]
    public void set_model_cost_updates_a_known_model_and_rejects_an_unknown_one()
    {
        ModelInfoLookup.SetModelCost("azureai/gpt-4o", ScriptedCost);

        var info = ModelInfoLookup.GetModelInfo("azureai/gpt-4o");
        Assert.Equal(ScriptedCost, info!.Cost);
        Assert.Equal(128000, info.ContextLength);
        // an override keyed the Python way applies to the bare deployment name Model.Name reports
        Assert.Equal(ScriptedCost, ModelInfoLookup.GetModelInfo("gpt-4o")!.Cost);
        Assert.Null(ModelInfoLookup.GetModelInfo("openai/gpt-4o")!.Cost);

        var ex = Assert.Throws<ArgumentException>(() => ModelInfoLookup.SetModelCost("unknown-deployment-xyz", ScriptedCost));
        Assert.StartsWith("Model 'unknown-deployment-xyz' not found.", ex.Message);
    }

    // ---- the override file --------------------------------------------------------------------------------

    [Fact]
    public void override_file_from_the_environment_wins_over_the_embedded_data()
    {
        var path = WriteConfig("""
            {
              "openai/gpt-4o": {"input": 2.5, "output": 10, "input_cache_write": 0, "input_cache_read": 1.25},
              "gpt-5.4-mini": {"input": 0.25, "output": 2.0, "input_cache_write": 0, "input_cache_read": 0.025},
              "my-custom-deployment": {"input": 1, "output": 2, "input_cache_write": 3, "input_cache_read": 4}
            }
            """);
        using var env = new EnvVarScope().Set(ModelCostConfig.EnvironmentVariable, path);
        ModelInfoLookup.ClearModelInfoCache();

        var gpt4o = ModelInfoLookup.GetModelInfo("openai/gpt-4o");
        Assert.Equal(new ModelCost(2.5, 10, 0, 1.25), gpt4o!.Cost);
        Assert.Equal(128000, gpt4o.ContextLength);
        Assert.Equal("GPT-4o", gpt4o.Model);

        var mini = ModelInfoLookup.GetModelInfo("gpt-5.4-mini");
        Assert.Equal(new ModelCost(0.25, 2.0, 0, 0.025), mini!.Cost);
        Assert.Equal(400000, mini.ContextLength);

        var custom = ModelInfoLookup.GetModelInfo("my-custom-deployment");
        Assert.Equal(new ModelCost(1, 2, 3, 4), custom!.Cost);
        Assert.Null(custom.ContextLength);

        Assert.Equal(0.00275, ModelCosts.ComputeCostForModel("openai/gpt-4o", new ModelUsage(400, 100, 1100) { InputTokensCacheRead = 600 }));
    }

    [Fact]
    public void override_file_errors_are_explicit_and_keep_failing_until_fixed()
    {
        var missingField = WriteConfig("""{ "gpt-4o": {"input": 2.5, "output": 10, "input_cache_read": 1.25} }""");
        using var env = new EnvVarScope().Set(ModelCostConfig.EnvironmentVariable, missingField);
        ModelInfoLookup.ClearModelInfoCache();

        var ex = Assert.Throws<InvalidDataException>(() => ModelInfoLookup.GetModelInfo("gpt-4o"));
        Assert.Contains("gpt-4o", ex.Message);
        Assert.Contains("input_cache_write", ex.Message);
        Assert.Contains(missingField, ex.Message);
        // the file was not applied, and every lookup keeps failing rather than silently pricing nothing
        Assert.Throws<InvalidDataException>(() => ModelInfoLookup.GetModelInfo("gpt-4o"));

        env.Set(ModelCostConfig.EnvironmentVariable, Path.Combine(_tempDir, "nope.json"));
        Assert.Throws<FileNotFoundException>(() => ModelInfoLookup.GetModelInfo("gpt-4o"));

        env.Set(ModelCostConfig.EnvironmentVariable, WriteConfig("{ not json", "broken.json"));
        Assert.Throws<InvalidDataException>(() => ModelInfoLookup.GetModelInfo("gpt-4o"));

        env.Set(ModelCostConfig.EnvironmentVariable, WriteConfig("""{ "gpt-4o": {"input": "2.5", "output": 10, "input_cache_write": 0, "input_cache_read": 1.25} }""", "string.json"));
        Assert.Contains("must be a number", Assert.Throws<InvalidDataException>(() => ModelInfoLookup.GetModelInfo("gpt-4o")).Message);

        env.Set(ModelCostConfig.EnvironmentVariable, WriteConfig("""{ "gpt-4o": {"input": 2.5, "output": 10, "input_cache_write": 0, "input_cache_read": 1.25} }""", "fixed.json"));
        Assert.Equal(new ModelCost(2.5, 10, 0, 1.25), ModelInfoLookup.GetModelInfo("gpt-4o")!.Cost);
    }

    // ---- cost on the model event and the limits --------------------------------------------------------------

    [Fact]
    public async Task cost_is_set_on_the_output_the_model_event_and_the_sample_usage()
    {
        ModelInfoLookup.SetModelInfo("scripted", new ModelInfo { Cost = ScriptedCost });
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("ok", new ModelUsage(3, 4, 7))));

        var output = await scope.Model.GenerateAsync("hi");

        Assert.Equal(0.007, output.Usage!.TotalCost);
        var e = Assert.Single(scope.Transcript.Events.OfType<ModelEvent>());
        Assert.Equal(0.007, e.Output.Usage!.TotalCost);
        Assert.Equal(0.007, scope.Context.Limits.UsageByModel["scripted"].TotalCost);
        Assert.Equal(0.007, scope.Context.Limits.TotalUsage.TotalCost);
        Assert.Equal(0.007, scope.Context.Limits.CostUsage);
    }

    [Fact]
    public async Task an_unknown_model_gets_no_cost_rather_than_zero()
    {
        var model = new Model(new ScriptedModelApi([ScriptedTurn.Text("ok", new ModelUsage(3, 4, 7))], "unknown-deployment-xyz"));
        using var scope = new SampleContextScope();

        var output = await model.GenerateAsync("hi");

        Assert.Null(output.Usage!.TotalCost);
        var e = Assert.Single(scope.Transcript.Events.OfType<ModelEvent>());
        Assert.Null(e.Output.Usage!.TotalCost);
        Assert.Null(scope.Context.Limits.UsageByModel["unknown-deployment-xyz"].TotalCost);
        Assert.Null(scope.Context.Limits.TotalUsage.TotalCost);
        Assert.Equal(0.0, scope.Context.Limits.CostUsage);
    }

    [Fact]
    public void cost_limit_accumulates_cost_and_raises_once_exceeded()
    {
        var limits = new Limits { CostLimit = 0.01 };

        limits.AddUsage(new ModelUsage(3, 4, 7) { TotalCost = 0.005 }, "m");
        var ex = Assert.Throws<LimitExceededException>(() => limits.AddUsage(new ModelUsage(3, 4, 7) { TotalCost = 0.006 }, "m"));

        Assert.Equal("cost", ex.Type);
        Assert.Equal("0.01", ex.LimitStr);
        Assert.Equal("Cost limit exceeded. value: $0.0110; limit: $0.0100", ex.Message);
        Assert.Equal(0.011, ex.Value, 10);
        Assert.Equal(0.011, limits.CostUsage, 10);
        Assert.Equal(0.011, limits.UsageByModel["m"].TotalCost!.Value, 10);
        Assert.Equal(0.011, limits.TotalUsage.TotalCost!.Value, 10);
    }

    [Fact]
    public void cost_limit_is_strict_and_unpriced_usage_does_not_count()
    {
        var limits = new Limits { CostLimit = 0.007 };

        limits.AddUsage(new ModelUsage(3, 4, 7) { TotalCost = 0.007 }, "m");
        limits.AddUsage(new ModelUsage(300, 400, 700), "unpriced");
        Assert.Throws<LimitExceededException>(() => limits.AddUsage(new ModelUsage(1, 1, 2) { TotalCost = 0.0001 }, "m"));

        Assert.Equal(0.0071, limits.CostUsage, 10);
    }

    [Fact]
    public void cost_limit_is_recorded_after_the_token_check_like_python()
    {
        var limits = new Limits { TokenLimit = 5, CostLimit = 0.001 };

        var ex = Assert.Throws<LimitExceededException>(() => limits.AddUsage(new ModelUsage(3, 4, 7) { TotalCost = 0.007 }, "m"));

        Assert.Equal("token", ex.Type);
        Assert.Equal(0.0, limits.CostUsage);
        Assert.Equal(0.007, limits.TotalUsage.TotalCost);
    }

    /// <summary>Python's <c>_CostLimit._check_self</c> records a <c>SampleLimitEvent(type="cost")</c> before raising, like every other limit.</summary>
    [Fact]
    public async Task cost_limit_trip_records_a_sample_limit_event()
    {
        ModelInfoLookup.SetModelInfo("scripted", new ModelInfo { Cost = ScriptedCost });
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("ok", new ModelUsage(3, 4, 7))), limits: new Limits { CostLimit = 0.005 });

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => scope.Model.GenerateAsync("hi"));

        Assert.Equal("cost", ex.Type);
        var e = Assert.Single(scope.Transcript.Events.OfType<SampleLimitEvent>());
        Assert.Equal("cost", e.Type);
        Assert.Equal(0.005, e.Limit);
        Assert.Equal("Cost limit exceeded. value: $0.0070; limit: $0.0050", e.Message);
        Assert.Equal(ex.Message, e.Message);
    }

    /// <summary>
    /// Python's <c>record_and_check_model_usage</c> records the usage on the whole limit tree and checks the token limits
    /// before it records and checks cost: a call that trips both a scoped token limit and the cost limit raises the
    /// token limit, with its tokens counted in the tree and its cost left unrecorded.
    /// </summary>
    [Fact]
    public async Task a_call_exceeding_a_scoped_token_limit_and_the_cost_limit_trips_the_token_limit_like_python()
    {
        ModelInfoLookup.SetModelInfo("scripted", new ModelInfo { Cost = ScriptedCost });
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("ok", new ModelUsage(3, 4, 7))), limits: new Limits { CostLimit = 0.001 });
        var tokens = new TokenLimit(5);
        using var tokenScope = tokens.Enter();

        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => scope.Model.GenerateAsync("hi"));

        Assert.Equal("token", ex.Type);
        Assert.Same(tokens, ex.SourceLimit);
        Assert.Equal(7.0, tokens.Usage);
        Assert.Equal(7, scope.Context.Limits.TotalUsage.TotalTokens);
        Assert.Equal(0.007, scope.Context.Limits.TotalUsage.TotalCost);
        Assert.Equal(0.0, scope.Context.Limits.CostUsage);
        var e = Assert.Single(scope.Transcript.Events.OfType<SampleLimitEvent>());
        Assert.Equal("token", e.Type);
    }

    [Fact]
    public void suspended_cost_limit_keeps_accumulating_without_raising()
    {
        var limits = new Limits { CostLimit = 0.01 };

        limits.Suspend();
        limits.AddUsage(new ModelUsage(3, 4, 7) { TotalCost = 5.0 }, "grader");
        limits.CheckCostLimit();

        Assert.False(limits.Enforced);
        Assert.Equal(5.0, limits.CostUsage);
    }

    [Fact]
    public void cost_limit_validation_and_the_unlimited_case()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Limits { CostLimit = -1.0 });
        _ = new Limits { CostLimit = 0.0 };

        var unlimited = new Limits();
        unlimited.AddUsage(new ModelUsage(3, 4, 7) { TotalCost = 100.0 }, "m");
        unlimited.CheckCostLimit();
        Assert.Equal(100.0, unlimited.CostUsage);
    }

    // ---- the eval runner ----------------------------------------------------------------------------------

    [Fact]
    public async Task eval_cost_limit_ends_the_solver_and_is_recorded_in_the_log()
    {
        ModelInfoLookup.SetModelInfo("scripted", new ModelInfo { Cost = ScriptedCost });
        var task = new EvalTask
        {
            Name = "cost",
            Dataset = new MemoryDataset([new Sample("How many cores?") { Target = "4" }]),
            Scorers = [Scorers.ModelGradedQa()],
            CostLimit = 0.005,
        };
        var api = new ScriptedModelApi(ScriptedTurn.Text("4 cores", new ModelUsage(3, 4, 7)), ScriptedTurn.Text("Correct.\n\nGRADE: C", new ModelUsage(3, 4, 7)));

        var log = await Eval.RunAsync(task, Options(api));

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("cost", sample.Limit!.Type);
        Assert.Equal(0.005, sample.Limit.Limit);
        Assert.Equal("Cost limit exceeded. value: $0.0070; limit: $0.0050", sample.Limit.Reason);
        Assert.Equal("C", sample.Scores!["model_graded_qa"].Text);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(0.007, sample.Events.OfType<ModelEvent>().First().Output.Usage!.TotalCost);
        Assert.Equal(0.014, sample.ModelUsage["scripted"].TotalCost!.Value, 10);
        Assert.Equal(0.014, log.Stats.ModelUsage["scripted"].TotalCost!.Value, 10);
        Assert.Equal(0.005, log.Eval.Config.CostLimit);
        // the transcript locates the trip point by event, as it can for every other limit type
        var limitEvent = Assert.Single(sample.Events.OfType<SampleLimitEvent>());
        Assert.Equal("cost", limitEvent.Type);
        Assert.Equal(0.005, limitEvent.Limit);
        Assert.Equal(sample.Limit.Reason, limitEvent.Message);

        var json = EvalLogWriter.Serialize(log);
        var root = JsonNode.Parse(json)!;
        Assert.Equal(0.005, (double?)root["eval"]!["config"]!["cost_limit"]);
        Assert.Equal(0.014, (double)root["stats"]!["model_usage"]!["scripted"]!["total_cost"]!, 10);
        Assert.Equal(0.014, (double)root["samples"]![0]!["model_usage"]!["scripted"]!["total_cost"]!, 10);
        Assert.Equal("cost", (string?)root["samples"]![0]!["limit"]!["type"]);
        var jsonEvent = Assert.Single(root["samples"]![0]!["events"]!.AsArray(), e => (string?)e!["event"] == "sample_limit");
        Assert.Equal("cost", (string?)jsonEvent!["type"]);
        Assert.Equal(0.005, (double?)jsonEvent["limit"]);
        var read = EvalLogWriter.Deserialize(json);
        Assert.Equal(0.005, read.Eval.Config.CostLimit);
        Assert.Equal(0.014, read.Stats.ModelUsage["scripted"].TotalCost!.Value, 10);
        Assert.Equal(0.007, read.Samples![0].Events.OfType<ModelEvent>().First().Output.Usage!.TotalCost);
        Assert.Equal("cost", Assert.Single(read.Samples[0].Events.OfType<SampleLimitEvent>()).Type);
    }

    [Fact]
    public async Task eval_cost_limit_without_cost_data_is_a_prerequisite_error()
    {
        var task = new EvalTask { Name = "cost", Dataset = new MemoryDataset([new Sample("hi") { Target = "a" }]), Scorers = [Scorers.Includes()] };
        var api = new ScriptedModelApi(ScriptedTurn.Text("a", new ModelUsage(3, 4, 7)));

        var ex = await Assert.ThrowsAsync<PrerequisiteError>(() => Eval.RunAsync(task, Options(api) with { CostLimit = 1.0 }));

        Assert.Contains("Missing cost data for: scripted", ex.Message);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task eval_options_model_cost_config_prices_the_run()
    {
        var path = WriteConfig("""{ "scripted": {"input": 1000, "output": 1000, "input_cache_write": 0, "input_cache_read": 0} }""");
        var task = new EvalTask { Name = "cost", Dataset = new MemoryDataset([new Sample("hi") { Target = "a" }]), Scorers = [Scorers.Includes()] };
        var api = new ScriptedModelApi(ScriptedTurn.Text("a", new ModelUsage(3, 4, 7)));

        var log = await Eval.RunAsync(task, Options(api) with { ModelCostConfig = path, CostLimit = 1.0 });

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Limit);
        Assert.Equal(0.007, log.Stats.ModelUsage["scripted"].TotalCost);
        Assert.Equal(1.0, log.Eval.Config.CostLimit);
    }
}
