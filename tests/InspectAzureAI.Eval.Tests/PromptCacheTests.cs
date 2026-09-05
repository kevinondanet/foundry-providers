using System.Text.Json;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port-level behaviour of the prompt cache: <c>model/_cache.py</c>, the cache path of <c>Model.generate</c>
/// (<c>model/_model.py</c>) and the maintenance semantics of <c>_cli/cache.py</c>. Every test gets its own
/// <c>INSPECT_CACHE_DIR</c>, as <c>tests/model/test_cache.py</c> does.
/// </summary>
public sealed class PromptCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inspect-prompt-cache-tests", Guid.NewGuid().ToString("N"));

    private readonly EnvVarScope _env;

    public PromptCacheTests()
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

    private sealed class CollectingSink : IModelEventSink
    {
        public List<ModelEvent> Events { get; } = [];

        public void OnModelEvent(ModelEvent e) => Events.Add(e);
    }

    private static CacheEntry Entry(
        GenerateConfig? config = null,
        IReadOnlyList<ChatMessage>? input = null,
        string model = "scripted",
        CachePolicy? policy = null,
        ToolChoice? toolChoice = null,
        IReadOnlyList<ToolInfo>? tools = null,
        string? baseUrl = null,
        int? epoch = null) =>
        new(baseUrl, config ?? new GenerateConfig(), input ?? [new ChatMessageUser("Hello")], model, policy ?? CachePolicy.Default, toolChoice, tools ?? [], epoch);

    private static ModelOutput Output(string text, StopReason stopReason = StopReason.Stop) => ModelOutput.FromContent("scripted", text, stopReason);

    private static (Model Model, ScriptedModelApi Api, CollectingSink Sink) Build(params ScriptedTurn[] turns)
    {
        var api = new ScriptedModelApi(turns);
        var sink = new CollectingSink();
        return (new Model(api) { EventSink = sink }, api, sink);
    }

    private string ModelDir(string model = "scripted") => Path.Combine(_root, "generate", model);

    [Theory]
    [InlineData("1W", 604800L)]
    [InlineData("12h", 43200L)]
    [InlineData("30m", 1800L)]
    [InlineData("90D", 7776000L)]
    [InlineData("45s", 45L)]
    [InlineData("2M", 5184000L)]
    [InlineData("1Y", 31536000L)]
    [InlineData("-1h", -3600L)]
    [InlineData(" 1h", 3600L)]
    public void expiry_grammar_matches_python(string period, long seconds)
    {
        // reference values computed with inspect_ai.model._cache._parse_expiry
        Assert.Equal(seconds, CachePolicy.ParseExpiry(period));
        Assert.Equal(seconds, new CachePolicy { Expiry = period }.ExpirySeconds);
        Assert.Equal(period, CachePolicy.FromString(period)!.Expiry);
    }

    [Theory]
    [InlineData("10x")]
    [InlineData("W")]
    [InlineData("")]
    [InlineData("1w")]
    [InlineData("1.5h")]
    [InlineData("h1")]
    public void invalid_expiry_is_rejected(string period)
    {
        var ex = Assert.Throws<ArgumentException>(() => CachePolicy.ParseExpiry(period));
        Assert.Contains("Invalid expiry", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new CachePolicy { Expiry = period });
        Assert.Null(CachePolicy.FromString(period));
    }

    [Fact]
    public void default_policy_and_bool_shorthand()
    {
        Assert.Equal("1W", CachePolicy.Default.Expiry);
        Assert.True(CachePolicy.Default.PerEpoch);
        Assert.Empty(CachePolicy.Default.Scopes);
        Assert.Equal(604800L, CachePolicy.Default.ExpirySeconds);
        Assert.Null(new CachePolicy { Expiry = null }.ExpirySeconds);

        CachePolicy? enabled = true;
        CachePolicy? disabled = false;
        Assert.Same(CachePolicy.Default, enabled);
        Assert.Null(disabled);
    }

    [Fact]
    public async Task generate_misses_then_hits_the_cache()
    {
        var (model, api, sink) = Build(ScriptedTurn.Text("first"), ScriptedTurn.Text("second"));

        var first = await model.GenerateAsync("What is the timestamp?", cache: true);
        var second = await model.GenerateAsync("What is the timestamp?", cache: CachePolicy.Default);

        Assert.Equal("first", first.Completion);
        Assert.Equal("first", second.Completion);
        Assert.Single(api.Requests);
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal(CacheMode.Write, sink.Events[0].Cache);
        Assert.NotNull(sink.Events[0].Call);
        Assert.Equal(CacheMode.Read, sink.Events[1].Cache);
        Assert.Null(sink.Events[1].Call);
        Assert.Null(sink.Events[1].Error);
        Assert.Equal("first", sink.Events[1].Output.Completion);
        Assert.Single(Directory.GetFiles(ModelDir()));
    }

    [Fact]
    public async Task without_a_policy_every_call_reaches_the_provider()
    {
        var (model, api, sink) = Build(ScriptedTurn.Text("a"), ScriptedTurn.Text("b"), ScriptedTurn.Text("c"));

        await model.GenerateAsync("hi");
        await model.GenerateAsync("hi", cache: false);
        await model.GenerateAsync("hi", cache: (CachePolicy?)null);

        Assert.Equal(3, api.Requests.Count);
        Assert.All(sink.Events, e => Assert.Null(e.Cache));
        Assert.False(Directory.Exists(ModelDir()));
    }

    [Fact]
    public async Task cache_hits_do_not_consume_token_usage()
    {
        var usage = new ModelUsage(10, 5, 15);
        var api = new ScriptedModelApi(ScriptedTurn.Text("a", usage), ScriptedTurn.Text("b", usage));
        using var scope = new SampleContextScope(api);

        await scope.Model.GenerateAsync("hi", cache: true);
        Assert.Equal(15, scope.Context.Limits.TotalUsage.TotalTokens);

        var hit = await scope.Model.GenerateAsync("hi", cache: true);
        Assert.Equal("a", hit.Completion);
        Assert.Equal(15, scope.Context.Limits.TotalUsage.TotalTokens);
        Assert.Single(api.Requests);
    }

    /// <summary>
    /// Python records the fallback rollup and the turn in the outer frame of <c>generate</c>, so a hit counts too:
    /// a cached response originally served by a fallback is still fallback-served, and a hit advances the
    /// conversation by one assistant message. Without this a react-style loop run with <c>cache: true</c> under a
    /// turn limit would replay cached generations without the limit ever tripping.
    /// </summary>
    [Fact]
    public async Task cache_hits_record_a_turn_and_the_fallback_rollup()
    {
        var served = ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, "a") with { Fallback = new ModelFallback("primary", "fallback") };
        var api = new ScriptedModelApi(ScriptedTurn.From(served), ScriptedTurn.Text("b"));
        using var scope = new SampleContextScope(api);
        using var accumulators = SampleModelAccumulators.Begin();
        var turns = new TurnLimit(2);
        using var turnScope = turns.Enter();

        await scope.Model.GenerateAsync("hi", cache: true);
        var hit = await scope.Model.GenerateAsync("hi", cache: true);

        Assert.Equal("a", hit.Completion);
        Assert.Single(api.Requests);
        Assert.Equal(2, turns.Turns);
        Assert.Equal(2, TurnLimit.TurnCount());
        Assert.Equal([new ModelFallback("primary", "fallback", 2)], accumulators.ModelFallbacks);

        // the third call is a hit as well, and it is the one that trips the limit (the tripping turn is recorded first)
        var ex = await Assert.ThrowsAsync<LimitExceededException>(() => scope.Model.GenerateAsync("hi", cache: true));

        Assert.Equal("turn", ex.Type);
        Assert.Equal(3, turns.Turns);
        Assert.Single(api.Requests);
        Assert.Equal([new ModelFallback("primary", "fallback", 3)], accumulators.ModelFallbacks);
        Assert.Contains(scope.Transcript.Events.OfType<SampleLimitEvent>(), e => e.Type == "turn" && e.Limit == 2);
        Assert.Equal(new CacheMode?[] { CacheMode.Write, CacheMode.Read, CacheMode.Read }, scope.Transcript.Events.OfType<ModelEvent>().Select(e => e.Cache));
    }

    [Fact]
    public async Task a_cache_hit_counts_as_a_turn()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("a"), ScriptedTurn.Text("b"));
        using var scope = new SampleContextScope(api);
        using (new TurnLimit(1).Enter())
        {
            await scope.Model.GenerateAsync("hi", cache: true);
            Assert.Equal(1, TurnLimit.TurnCount());

            // Python records the turn in the outer frame of generate(), so a hit advances the conversation like a provider call
            var ex = await Assert.ThrowsAsync<LimitExceededException>(() => scope.Model.GenerateAsync("hi", cache: true));
            Assert.Equal("turn", ex.Type);
            Assert.Equal(2, TurnLimit.TurnCount());
        }

        Assert.Single(api.Requests);
        Assert.Equal("turn", Assert.Single(scope.Transcript.Events.OfType<SampleLimitEvent>()).Type);
    }

    [Fact]
    public async Task retry_attempts_record_the_write_mode_and_a_later_call_reads()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Throw(new InvalidOperationException("flaky")), ScriptedTurn.Text("ok"))
        {
            ShouldRetry = _ => RetryDecision.Transient(null),
        };
        var sink = new CollectingSink();
        var model = new Model(api, retry: new ModelRetryOptions(MaxRetries: 2, Delay: (_, _) => Task.CompletedTask)) { EventSink = sink };

        Assert.Equal("ok", (await model.GenerateAsync("hi", cache: true)).Completion);
        Assert.Equal("ok", (await model.GenerateAsync("hi", cache: true)).Completion);

        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(new CacheMode?[] { CacheMode.Write, CacheMode.Write, CacheMode.Read }, sink.Events.Select(e => e.Cache));
        Assert.Equal("flaky", sink.Events[0].Error);
    }

    [Fact]
    public async Task expired_entries_are_a_miss_and_are_deleted()
    {
        var entry = Entry(policy: new CachePolicy { Expiry = "-1h" });
        Assert.True(await PromptCache.StoreAsync(entry, Output("stale")));
        var path = PromptCache.EntryPath(entry);
        Assert.True(File.Exists(path));

        Assert.Null(await PromptCache.FetchAsync(entry));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task fresh_and_indefinite_entries_are_served()
    {
        var fresh = Entry();
        Assert.True(await PromptCache.StoreAsync(fresh, Output("fresh")));
        Assert.Equal("fresh", (await PromptCache.FetchAsync(fresh))!.Completion);

        var forever = Entry(policy: new CachePolicy { Expiry = null });
        Assert.True(await PromptCache.StoreAsync(forever, Output("forever")));
        Assert.Equal("forever", (await PromptCache.FetchAsync(forever))!.Completion);
        Assert.Contains("\"expiry\": null", await File.ReadAllTextAsync(PromptCache.EntryPath(forever)), StringComparison.Ordinal);
        Assert.NotEqual(fresh.Key, forever.Key);
    }

    [Fact]
    public void per_epoch_policies_key_on_the_epoch()
    {
        Assert.NotEqual(Entry(epoch: 1).Key, Entry(epoch: 2).Key);
        Assert.NotEqual(Entry(epoch: 1).Key, Entry(epoch: null).Key);
        Assert.Equal(Entry(epoch: 1).Key, Entry(epoch: 1).Key);

        var shared = new CachePolicy { PerEpoch = false };
        Assert.Equal(Entry(policy: shared, epoch: 1).Key, Entry(policy: shared, epoch: 2).Key);
        Assert.NotEqual(Entry(policy: shared, epoch: 1).Key, Entry(epoch: 1).Key);
    }

    [Fact]
    public async Task the_epoch_flows_from_the_sample_state()
    {
        var api = new ScriptedModelApi(ScriptedTurn.Text("e1"), ScriptedTurn.Text("e2"), ScriptedTurn.Text("e3"));
        var model = new Model(api);

        async Task<string> Generate(int epoch)
        {
            var state = new TaskState("scripted", 1, epoch, "hi", []);
            using var scope = SampleContext.Begin(new SampleContext { ActiveModel = model, SampleState = state });
            return (await model.GenerateAsync("hi", cache: true)).Completion;
        }

        Assert.Equal("e1", await Generate(1));
        Assert.Equal("e2", await Generate(2));
        Assert.Equal("e1", await Generate(1));
        Assert.Equal("e2", await Generate(2));
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public void scopes_participate_in_the_key()
    {
        var a1 = new CachePolicy { Scopes = new Dictionary<string, string> { ["a"] = "1" } };
        var a2 = new CachePolicy { Scopes = new Dictionary<string, string> { ["a"] = "2" } };
        Assert.NotEqual(Entry(policy: a1).Key, Entry(policy: a2).Key);
        Assert.NotEqual(Entry(policy: a1).Key, Entry().Key);
        Assert.Equal(Entry(policy: a1).Key, Entry(policy: new CachePolicy { Scopes = new Dictionary<string, string> { ["a"] = "1" } }).Key);

        // canonical JSON: insertion order does not matter
        var ab = new CachePolicy { Scopes = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" } };
        var ba = new CachePolicy { Scopes = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" } };
        Assert.Equal(Entry(policy: ab).Key, Entry(policy: ba).Key);
    }

    [Fact]
    public void key_ignores_message_ids_and_connection_fields()
    {
        var baseKey = Entry().Key;
        Assert.Matches("^[0-9a-f]{32}$", baseKey);
        Assert.Equal(baseKey, Entry(input: [new ChatMessageUser("Hello") { Id = "another-id" }]).Key);
        Assert.Equal(baseKey, Entry(config: new GenerateConfig { MaxRetries = 3, Timeout = 30, MaxConnections = 2, StreamIdleTimeout = 60 }).Key);

        Assert.NotEqual(baseKey, Entry(config: new GenerateConfig { Temperature = 0.7 }).Key);
        Assert.NotEqual(baseKey, Entry(input: [new ChatMessageUser("Hello!")]).Key);
        Assert.NotEqual(baseKey, Entry(input: [new ChatMessageSystem("Hello")]).Key);
        Assert.NotEqual(baseKey, Entry(input: [new ChatMessageUser("Hello"), new ChatMessageUser("Hello")]).Key);
    }

    [Fact]
    public void key_covers_base_url_tools_tool_choice_and_expiry_but_not_the_model()
    {
        var baseKey = Entry().Key;
        Assert.NotEqual(baseKey, Entry(baseUrl: "https://example.test").Key);
        Assert.NotEqual(baseKey, Entry(tools: [new ToolInfo("add", "Adds numbers")]).Key);
        Assert.NotEqual(baseKey, Entry(toolChoice: ToolChoice.Auto).Key);
        Assert.NotEqual(Entry(toolChoice: ToolChoice.Auto).Key, Entry(toolChoice: new ToolFunction("add")).Key);
        Assert.NotEqual(baseKey, Entry(policy: new CachePolicy { Expiry = "12h" }).Key);

        // Python compares the parsed seconds, so 7D and 1W key identically
        Assert.Equal(baseKey, Entry(policy: new CachePolicy { Expiry = "7D" }).Key);
        // the model is the directory, not a key component (as in Python)
        Assert.Equal(baseKey, Entry(model: "other").Key);
    }

    [Fact]
    public void key_components_follow_python_order()
    {
        var entry = Entry(
            config: new GenerateConfig { Temperature = 0.5, MaxRetries = 2 },
            baseUrl: "https://example.test",
            toolChoice: ToolChoice.None,
            epoch: 3);
        var components = CacheKey.Components(entry);

        Assert.Equal(8, components.Count);
        Assert.Equal("{\"temperature\": 0.5}", CacheKey.CanonicalJson(components[0]));
        Assert.Equal("[{\"content\": \"Hello\", \"role\": \"user\"}]", CacheKey.CanonicalJson(components[1]));
        Assert.Equal("\"https://example.test\"", CacheKey.CanonicalJson(components[2]));
        Assert.Equal("\"none\"", CacheKey.CanonicalJson(components[3]));
        Assert.Equal("[]", CacheKey.CanonicalJson(components[4]));
        Assert.Equal("604800", CacheKey.CanonicalJson(components[5]));
        Assert.Equal("{}", CacheKey.CanonicalJson(components[6]));
        Assert.Equal("3", CacheKey.CanonicalJson(components[7]));

        Assert.Equal(7, CacheKey.Components(Entry(policy: new CachePolicy { PerEpoch = false })).Count);
        Assert.Equal("null", CacheKey.CanonicalJson(CacheKey.Components(Entry())[7]));
        Assert.Equal("{\"b\": [1, {\"a\": \"\\u00e9\"}], \"c\": null}", CacheKey.CanonicalJson(System.Text.Json.Nodes.JsonNode.Parse("{\"c\": null, \"b\": [1, {\"a\": \"é\"}]}")));
    }

    [Fact]
    public async Task prune_removes_only_expired_entries()
    {
        var fresh = Entry();
        var stale = Entry(policy: new CachePolicy { Expiry = "-1s" });
        var staleOther = Entry(model: "other/model", policy: new CachePolicy { Expiry = "-1s" });
        Assert.True(await PromptCache.StoreAsync(fresh, Output("fresh")));
        Assert.True(await PromptCache.StoreAsync(stale, Output("stale")));
        Assert.True(await PromptCache.StoreAsync(staleOther, Output("stale")));

        Assert.Equal(
            new[] { PromptCache.EntryPath(stale), PromptCache.EntryPath(staleOther) }.Order(StringComparer.Ordinal),
            CacheOps.CacheListExpired().Order(StringComparer.Ordinal));
        Assert.Equal(new[] { PromptCache.EntryPath(staleOther) }, CacheOps.CacheListExpired(["other/model"]));
        // a filter lists only the entries directly inside the named model directory (Python compares the directory exactly)
        Assert.Empty(CacheOps.CacheListExpired(["other"]));
        // a model that escapes the cache is ignored, and when nothing valid remains nothing is listed
        Assert.Empty(CacheOps.CacheListExpired(["../escape"]));

        CacheOps.CachePrune();

        Assert.True(File.Exists(PromptCache.EntryPath(fresh)));
        Assert.False(File.Exists(PromptCache.EntryPath(stale)));
        Assert.False(File.Exists(PromptCache.EntryPath(staleOther)));
        Assert.Empty(CacheOps.CacheListExpired());
        Assert.Equal("fresh", (await PromptCache.FetchAsync(fresh))!.Completion);
    }

    [Fact]
    public async Task prune_re_reads_explicit_files_and_keeps_unexpired_ones()
    {
        var fresh = Entry();
        Assert.True(await PromptCache.StoreAsync(fresh, Output("fresh")));
        var path = PromptCache.EntryPath(fresh);

        CacheOps.CachePrune([path, Path.Combine(ModelDir(), "missing")]);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task corrupt_entries_are_a_miss_with_a_warning()
    {
        var entry = Entry();
        var path = PromptCache.EntryPath(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "this is not json");
        var before = ProviderLogger.Warnings.Count;

        Assert.Null(await PromptCache.FetchAsync(entry));

        var warning = Assert.Single(ProviderLogger.Warnings.Skip(before));
        Assert.Contains(path, warning, StringComparison.Ordinal);
        // Python leaves the file alone; the next store overwrites it
        Assert.True(File.Exists(path));

        await File.WriteAllTextAsync(path, "{\"expiry\": null}");
        Assert.Null(await PromptCache.FetchAsync(entry));
        await File.WriteAllTextAsync(path, "{\"expiry\": \"yesterday\", \"output\": {}}");
        Assert.Null(await PromptCache.FetchAsync(entry));
        await File.WriteAllTextAsync(path, "{\"expiry\": 12, \"output\": {}}");
        Assert.Null(await PromptCache.FetchAsync(entry));
        Assert.Equal(before + 4, ProviderLogger.Warnings.Count);

        // maintenance skips it too, with a warning, rather than deleting or crashing
        Assert.Empty(CacheOps.CacheListExpired());
        Assert.Equal(before + 5, ProviderLogger.Warnings.Count);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task generate_treats_a_corrupt_entry_as_a_miss_and_repairs_it()
    {
        var (model, api, sink) = Build(ScriptedTurn.Text("first"), ScriptedTurn.Text("second"));
        await model.GenerateAsync("hi", cache: true);
        var path = Assert.Single(Directory.GetFiles(ModelDir()));
        await File.WriteAllTextAsync(path, "{ corrupt");
        var before = ProviderLogger.Warnings.Count;

        Assert.Equal("second", (await model.GenerateAsync("hi", cache: true)).Completion);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(before + 1, ProviderLogger.Warnings.Count);

        Assert.Equal("second", (await model.GenerateAsync("hi", cache: true)).Completion);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(CacheMode.Read, sink.Events[^1].Cache);
    }

    [Fact]
    public async Task content_filter_outputs_are_not_cached()
    {
        // port of test_cache_skips_content_filter
        var entry = Entry();
        Assert.False(await PromptCache.StoreAsync(entry, Output("refused", StopReason.ContentFilter)));
        Assert.Null(await PromptCache.FetchAsync(entry));

        Assert.True(await PromptCache.StoreAsync(entry, Output("partial", StopReason.MaxTokens)));
        Assert.True(await PromptCache.StoreAsync(entry, Output("Hi")));
        Assert.Equal("Hi", (await PromptCache.FetchAsync(entry))!.Completion);
    }

    [Fact]
    public async Task generate_marks_a_content_filter_attempt_as_write_but_stores_nothing()
    {
        var (model, api, sink) = Build(ScriptedTurn.From(Output("refused", StopReason.ContentFilter)), ScriptedTurn.Text("ok"));

        Assert.Equal("refused", (await model.GenerateAsync("hi", cache: true)).Completion);
        Assert.Equal(CacheMode.Write, sink.Events[0].Cache);
        Assert.False(Directory.Exists(ModelDir()));

        Assert.Equal("ok", (await model.GenerateAsync("hi", cache: true)).Completion);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task clear_removes_one_model_or_everything_but_never_escapes_the_cache()
    {
        Assert.True(await PromptCache.StoreAsync(Entry(model: "m1"), Output("a")));
        Assert.True(await PromptCache.StoreAsync(Entry(model: "org/m2"), Output("b")));

        Assert.True(CacheOps.CacheClear("m1"));
        Assert.False(Directory.Exists(ModelDir("m1")));
        Assert.True(Directory.Exists(ModelDir("org/m2")));
        Assert.False(CacheOps.CacheClear("m1"));

        Assert.False(CacheOps.CacheClear(".."));
        Assert.False(CacheOps.CacheClear("../escape"));
        Assert.True(Directory.Exists(_root));

        Assert.True(CacheOps.CacheClear());
        Assert.False(Directory.Exists(Path.Combine(_root, "generate")));
        Assert.True(Directory.Exists(CacheOps.CachePath()));
        Assert.False(CacheOps.CacheClear("org/m2"));
    }

    [Fact]
    public async Task size_groups_bytes_by_model_directory()
    {
        var a = Entry(model: "openai/gpt-4");
        var b = Entry(model: "mock", policy: new CachePolicy { Expiry = "2W" });
        var c = Entry(model: "mock", input: [new ChatMessageUser("Other")]);
        foreach (var entry in new[] { a, b, c })
        {
            Assert.True(await PromptCache.StoreAsync(entry, Output("x")));
        }

        static long Size(CacheEntry entry) => new FileInfo(PromptCache.EntryPath(entry)).Length;

        Assert.Equal(
            new[] { new ModelCacheSize("mock", Size(b) + Size(c)), new ModelCacheSize("openai/gpt-4", Size(a)) },
            CacheOps.CacheSize());
        Assert.Equal(new[] { new ModelCacheSize("openai/gpt-4", Size(a)) }, CacheOps.CacheSize(subdirs: ["gpt"]));
        Assert.Equal(new[] { new ModelCacheSize("mock", Size(b)) }, CacheOps.CacheSize(files: [PromptCache.EntryPath(b)]));
        Assert.Equal(
            new[] { new ModelCacheSize("mock", Size(b) + Size(c)), new ModelCacheSize("openai/gpt-4", Size(a)) },
            CacheOps.CacheSize(subdirs: ["mock"], files: [PromptCache.EntryPath(a)]));
        Assert.Empty(CacheOps.CacheSize(files: [Path.Combine(ModelDir("mock"), "missing")]));

        var outside = Path.Combine(_root, "outside.txt");
        await File.WriteAllTextAsync(outside, "not in the cache");
        Assert.Empty(CacheOps.CacheSize(files: [outside]));

        Assert.Equal("512  B", CacheOps.ReadableSize(512));
        Assert.Equal("1.50 KB", CacheOps.ReadableSize(1536));
        Assert.Equal("2.00 MB", CacheOps.ReadableSize(2 * 1024 * 1024));
    }

    [Fact]
    public void cache_path_honours_inspect_cache_dir()
    {
        Assert.Equal(Path.Combine(_root, "generate"), CacheOps.CachePath());
        Assert.True(Directory.Exists(CacheOps.CachePath()));
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "generate", "openai", "gpt-4")), Path.GetFullPath(CacheOps.CachePath("openai/gpt-4")));
        Assert.False(Directory.Exists(CacheOps.CachePath("openai/gpt-4")));

        Assert.True(CacheOps.IsInCache(CacheOps.CachePath("m")));
        Assert.False(CacheOps.IsInCache(CacheOps.CachePath()));
        Assert.False(CacheOps.IsInCache(_root));
        Assert.False(CacheOps.IsInCache(CacheOps.CachePath("m/../..")));
    }

    [Fact]
    public void user_cache_dir_matches_platformdirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            // platformdirs.user_cache_path("inspect_ai") on this platform
            Assert.Equal(Path.Combine(home, "Library", "Caches", "inspect_ai"), AppDirs.UserCachePath("inspect_ai"));
        }
        else if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.Equal(Path.Combine(local, "inspect_ai", "inspect_ai", "Cache"), AppDirs.UserCachePath("inspect_ai"));
        }
        else
        {
            using var xdg = new EnvVarScope().Set("XDG_CACHE_HOME", "/tmp/xdg-cache");
            Assert.Equal("/tmp/xdg-cache/inspect_ai", AppDirs.UserCachePath("inspect_ai"));
            xdg.Set("XDG_CACHE_HOME", null);
            Assert.Equal(Path.Combine(home, ".cache", "inspect_ai"), AppDirs.UserCachePath("inspect_ai"));
        }

        Assert.Throws<ArgumentException>(() => AppDirs.UserCachePath(""));
    }

    [Fact]
    public void model_event_cache_mode_round_trips_through_the_log()
    {
        var e = new ModelEvent
        {
            Model = "scripted",
            Input = [new ChatMessageUser("hi")],
            ToolChoice = ToolChoice.None,
            Config = new GenerateConfig(),
            Output = Output("a"),
            Cache = CacheMode.Read,
        };

        var json = JsonSerializer.Serialize<TranscriptEvent>(e, EvalLogWriter.Options);
        Assert.Contains("\"cache\": \"read\"", json, StringComparison.Ordinal);
        var read = Assert.IsType<ModelEvent>(JsonSerializer.Deserialize<TranscriptEvent>(json, EvalLogWriter.Options));
        Assert.Equal(CacheMode.Read, read.Cache);

        var none = JsonSerializer.Serialize<TranscriptEvent>(e with { Cache = null }, EvalLogWriter.Options);
        Assert.DoesNotContain("\"cache\"", none, StringComparison.Ordinal);
        Assert.Null(Assert.IsType<ModelEvent>(JsonSerializer.Deserialize<TranscriptEvent>(none, EvalLogWriter.Options)).Cache);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TranscriptEvent>(json.Replace("\"read\"", "\"maybe\"", StringComparison.Ordinal), EvalLogWriter.Options));
        Assert.Equal("write", CacheMode.Write.ToWire());
        Assert.Equal(CacheMode.Write, CacheModeExtensions.FromWire("write"));
        Assert.Throws<ArgumentException>(() => CacheModeExtensions.FromWire("maybe"));
    }

    [Fact]
    public async Task cancellation_aborts_fetch_and_store_without_partial_files()
    {
        var entry = Entry();
        Assert.True(await PromptCache.StoreAsync(entry, Output("a")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PromptCache.FetchAsync(entry, cts.Token));

        var other = Entry(input: [new ChatMessageUser("Other")]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PromptCache.StoreAsync(other, Output("b"), cts.Token));
        Assert.False(File.Exists(PromptCache.EntryPath(other)));
        Assert.Empty(Directory.GetFiles(ModelDir(), "*.tmp", SearchOption.AllDirectories));

        // a cancelled generate never writes an entry either
        var model = new Model(new ScriptedModelApi(ScriptedTurn.Text("c")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.GenerateAsync("new", cache: true, cancellationToken: cts.Token));
        Assert.Single(Directory.GetFiles(ModelDir()));
    }

    [Fact]
    public async Task store_failures_return_false_with_a_warning_and_generate_still_answers()
    {
        // the model directory cannot be created because a file sits at its path
        Directory.CreateDirectory(CacheOps.CachePath());
        await File.WriteAllTextAsync(CacheOps.CachePath("blocked"), "");
        var entry = Entry(model: "blocked");
        var before = ProviderLogger.Warnings.Count;

        Assert.False(await PromptCache.StoreAsync(entry, Output("a")));

        Assert.Contains(PromptCache.EntryPath(entry), Assert.Single(ProviderLogger.Warnings.Skip(before)), StringComparison.Ordinal);
        var model = new Model(new ScriptedModelApi([ScriptedTurn.Text("ok")], "blocked"));
        Assert.Equal("ok", (await model.GenerateAsync("hi", cache: true)).Completion);
    }
}
