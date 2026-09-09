using System.Net;
using System.Text.Json.Nodes;
using System.Web;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Eval.Tests;

/// <summary>Datasets.Hf over a fake datasets-server: config resolution, paging, mapping, ids, shuffle, limit, caching, tokens and retries.</summary>
public sealed class HfDatasetTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "hf-dataset-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousToken = Environment.GetEnvironmentVariable("HF_TOKEN");
    private readonly string? _previousTokenPath = Environment.GetEnvironmentVariable("HF_TOKEN_PATH");

    public HfDatasetTests()
    {
        // isolate from the developer's own HF_TOKEN / huggingface-cli login token file
        Environment.SetEnvironmentVariable("HF_TOKEN", null);
        Environment.SetEnvironmentVariable("HF_TOKEN_PATH", Path.Combine(_cacheDir, "no-token"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HF_TOKEN", _previousToken);
        Environment.SetEnvironmentVariable("HF_TOKEN_PATH", _previousTokenPath);
        if (Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
    }

    /// <summary>A fake datasets-server: one dataset with the given configs/splits and rows per (config, split).</summary>
    private sealed class FakeHub
    {
        public FakeHub(string dataset, IReadOnlyDictionary<(string Config, string Split), List<JsonObject>> data)
        {
            Handler = new FakeHttpHandler(call => Respond(dataset, data, call));
        }

        public FakeHttpHandler Handler { get; }

        public Func<FakeHttpCall, HttpResponseMessage?>? Intercept { get; set; }

        public int TruncateRowsAbovePageLength { get; set; } = int.MaxValue;

        private HttpResponseMessage Respond(string dataset, IReadOnlyDictionary<(string, string), List<JsonObject>> data, FakeHttpCall call)
        {
            if (Intercept?.Invoke(call) is { } intercepted)
            {
                return intercepted;
            }

            var uri = new Uri(call.Uri);
            var query = HttpUtility.ParseQueryString(uri.Query);
            if (query["dataset"] != dataset)
            {
                return FakeHttpHandler.Json(HttpStatusCode.NotFound, "{\"error\":\"The dataset does not exist on the Hub.\"}");
            }

            if (uri.AbsolutePath == "/splits")
            {
                var splits = new JsonArray(data.Keys.Select(key => (JsonNode)new JsonObject { ["dataset"] = dataset, ["config"] = key.Item1, ["split"] = key.Item2 }).ToArray());
                return FakeHttpHandler.Json(HttpStatusCode.OK, new JsonObject { ["splits"] = splits, ["pending"] = new JsonArray(), ["failed"] = new JsonArray() }.ToJsonString());
            }

            if (uri.AbsolutePath == "/rows")
            {
                if (!data.TryGetValue((query["config"]!, query["split"]!), out var rows))
                {
                    return FakeHttpHandler.Json(HttpStatusCode.UnprocessableEntity, "{\"error\":\"Parameters 'config' and 'split' are required\"}");
                }

                var offset = int.Parse(query["offset"]!);
                var length = int.Parse(query["length"]!);
                Assert.InRange(length, 1, HfDatasetLoader.PageLength);
                var page = rows.Skip(offset).Take(length).Select((row, i) => (JsonNode)new JsonObject
                {
                    ["row_idx"] = offset + i,
                    ["row"] = row.DeepClone(),
                    ["truncated_cells"] = length > TruncateRowsAbovePageLength ? new JsonArray("problem") : new JsonArray(),
                }).ToArray();
                var body = new JsonObject
                {
                    ["features"] = new JsonArray(),
                    ["rows"] = new JsonArray(page),
                    ["num_rows_total"] = rows.Count,
                    ["num_rows_per_page"] = HfDatasetLoader.PageLength,
                    ["partial"] = false,
                };
                return FakeHttpHandler.Json(HttpStatusCode.OK, body.ToJsonString());
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static List<JsonObject> Rows(int count, string config = "default") =>
        Enumerable.Range(0, count).Select(i => new JsonObject
        {
            ["problem"] = $"{config} question {i}",
            ["answer"] = i.ToString(),
            ["subject"] = i % 2 == 0 ? "algebra" : "geometry",
            ["options"] = new JsonArray("A" + i, "B" + i, "C" + i),
            ["letter"] = "ABC"[i % 3].ToString(),
        }).ToList();

    private static FakeHub Hub(int rows = 250) => new("org/math", new Dictionary<(string, string), List<JsonObject>>
    {
        [("default", "test")] = Rows(rows),
        [("default", "train")] = Rows(5),
    });

    private HfDatasetLoader Loader(FakeHub hub, Func<TimeSpan, CancellationToken, Task>? delay = null, Random? random = null) =>
        new(hub.Handler, _cacheDir, "https://fake-datasets-server.test/", delay, random);

    private static readonly FieldSpec MathFields = new(Input: "problem", Target: "answer", Choices: "options", Metadata: ["subject"]);

    [Fact]
    public async Task hf_dataset_pages_rows_and_maps_fields()
    {
        var hub = Hub();
        using var loader = Loader(hub);

        var dataset = await loader.LoadAsync(new HfDatasetRequest("org/math", "test") { SampleFields = MathFields });

        Assert.Equal(250, dataset.Count);
        Assert.Equal("org/math", dataset.Name);
        Assert.Equal("org/math", dataset.Location);
        Assert.False(dataset.Shuffled);
        Assert.Equal("default question 0", dataset[0].Input.Text);
        Assert.Equal("0", dataset[0].Target.Text);
        Assert.Equal(["A0", "B0", "C0"], dataset[0].Choices);
        Assert.Equal("algebra", dataset[0].Metadata!["subject"]);
        Assert.Equal("default question 249", dataset[249].Input.Text);
        Assert.Null(dataset[0].Id);

        // /splits then three pages of 100
        var uris = hub.Handler.Calls.Select(call => call.Uri).ToList();
        Assert.Equal(4, uris.Count);
        Assert.StartsWith("https://fake-datasets-server.test/splits?dataset=org%2Fmath", uris[0]);
        Assert.Equal("https://fake-datasets-server.test/rows?dataset=org%2Fmath&config=default&split=test&offset=0&length=100", uris[1]);
        Assert.Equal("https://fake-datasets-server.test/rows?dataset=org%2Fmath&config=default&split=test&offset=100&length=100", uris[2]);
        Assert.Equal("https://fake-datasets-server.test/rows?dataset=org%2Fmath&config=default&split=test&offset=200&length=100", uris[3]);
        Assert.All(hub.Handler.Calls, call => Assert.False(call.Headers.ContainsKey("Authorization")));
    }

    [Fact]
    public async Task hf_dataset_uses_record_to_sample_and_limit()
    {
        var hub = Hub();
        using var loader = Loader(hub);

        var dataset = await loader.LoadAsync(new HfDatasetRequest("org/math", "test")
        {
            Limit = 7,
            AutoId = true,
            RecordToSample = record => [new Sample(record["problem"]!.GetValue<string>()) { Target = new Target(record["answer"]!.GetValue<string>()) }],
        });

        Assert.Equal(7, dataset.Count);
        Assert.Equal(Enumerable.Range(1, 7), dataset.Select(sample => (int)sample.Id!));
        Assert.Equal("default question 6", dataset[6].Input.Text);
    }

    [Fact]
    public async Task hf_dataset_shuffle_is_seed_deterministic_and_ids_track_records()
    {
        var first = await Loader(Hub()).LoadAsync(new HfDatasetRequest("org/math", "test") { SampleFields = MathFields, Shuffle = true, Seed = 42, AutoId = true, Limit = 10 });
        var second = await Loader(Hub()).LoadAsync(new HfDatasetRequest("org/math", "test") { SampleFields = MathFields, Shuffle = true, Seed = 42, AutoId = true, Limit = 10, Cached = false });
        var other = await Loader(Hub()).LoadAsync(new HfDatasetRequest("org/math", "test") { SampleFields = MathFields, Shuffle = true, Seed = 7, AutoId = true, Cached = false });

        Assert.True(first.Shuffled);
        Assert.Equal(10, first.Count);
        Assert.Equal(first.Select(s => s.Input.Text), second.Select(s => s.Input.Text));
        Assert.NotEqual(Enumerable.Range(0, 250).Select(i => $"default question {i}"), other.Select(s => s.Input.Text));

        // the id is the record's original 1-based position, not its shuffled position (#4459)
        foreach (var sample in first.Concat(other))
        {
            Assert.Equal($"default question {(int)sample.Id! - 1}", sample.Input.Text);
        }
    }

    [Fact]
    public async Task hf_dataset_shuffle_keeps_multi_sample_record_groups_together()
    {
        using var loader = Loader(Hub(20));

        var dataset = await loader.LoadAsync(new HfDatasetRequest("org/math", "test")
        {
            Shuffle = true,
            Seed = 3,
            AutoId = true,
            RecordToSample = record =>
            {
                var q = record["problem"]!.GetValue<string>();
                return [new Sample(q + " a"), new Sample(q + " b")];
            },
        });

        Assert.Equal(40, dataset.Count);
        for (var i = 0; i < dataset.Count; i += 2)
        {
            Assert.EndsWith(" a", dataset[i].Input.Text);
            Assert.EndsWith(" b", dataset[i + 1].Input.Text);
            Assert.Equal(dataset[i].Input.Text![..^2], dataset[i + 1].Input.Text![..^2]);
            Assert.Equal((int)dataset[i].Id! + 1, (int)dataset[i + 1].Id!);
        }
    }

    [Fact]
    public async Task hf_dataset_shuffles_choices_with_seed()
    {
        using var loader = Loader(Hub(30));
        var request = new HfDatasetRequest("org/math", "test") { SampleFields = MathFields with { Target = "letter" }, ShuffleChoicesSeed = 11 };

        var plain = await loader.LoadAsync(new HfDatasetRequest("org/math", "test") { SampleFields = MathFields });
        var shuffled = await loader.LoadAsync(request);
        var again = await loader.LoadAsync(request);

        Assert.Equal(shuffled.Select(s => string.Join(",", s.Choices!)), again.Select(s => string.Join(",", s.Choices!)));
        Assert.All(shuffled, sample => Assert.Equal(3, sample.Choices!.Count));
        Assert.Contains(shuffled.Zip(plain), pair => !pair.First.Choices!.SequenceEqual(pair.Second.Choices!));
    }

    [Fact]
    public async Task hf_dataset_serves_repeat_reads_from_the_cache_without_network()
    {
        var hub = Hub(150);
        using var loader = Loader(hub);
        var request = new HfDatasetRequest("org/math", "test") { SampleFields = MathFields };

        var fetched = await loader.LoadAsync(request);
        var callsAfterFetch = hub.Handler.Calls.Count;
        var cached = await loader.LoadAsync(request);

        Assert.Equal(3, callsAfterFetch);
        Assert.Equal(callsAfterFetch, hub.Handler.Calls.Count);
        Assert.Equal(fetched.Select(s => s.Input.Text), cached.Select(s => s.Input.Text));
        Assert.Equal("algebra", cached[0].Metadata!["subject"]);

        var directory = loader.CacheDirectory(request);
        Assert.StartsWith(Path.Combine(_cacheDir, "org_math-"), directory);
        Assert.True(File.Exists(Path.Combine(directory, "rows.jsonl")));
        Assert.Equal(150, File.ReadAllLines(Path.Combine(directory, "rows.jsonl")).Length);

        // a different split / revision gets its own key; cached=false and a revision both refetch
        Assert.NotEqual(directory, loader.CacheDirectory(request with { Split = "train" }));
        Assert.NotEqual(directory, loader.CacheDirectory(request with { Revision = "abc123" }));
        await loader.LoadAsync(request with { Cached = false });
        Assert.Equal(callsAfterFetch + 3, hub.Handler.Calls.Count);
        await loader.LoadAsync(request with { Revision = "main" });
        Assert.Equal(callsAfterFetch + 6, hub.Handler.Calls.Count);
    }

    [Fact]
    public void hf_dataset_cache_key_matches_python()
    {
        // safe_filename("org/math") == "org_math"; mm3_hash(f"{path}{name}{data_dir}{split}{revision}{kwargs}") with None/{}
        using var loader = new HfDatasetLoader(cacheDir: _cacheDir);
        var directory = loader.CacheDirectory(new HfDatasetRequest("org/math", "test"));
        Assert.Equal("org_math-" + Log.MurmurHash3.Hash("org/mathNoneNonetestNone{}"), Path.GetFileName(directory));

        Assert.Equal("Hello_World_.txt", HfDatasetLoader.SafeFilename("Hello/World?.txt"));
        Assert.Equal("untitled", HfDatasetLoader.SafeFilename("///"));
        Assert.Equal("HuggingFaceH4_MATH-500", HfDatasetLoader.SafeFilename("HuggingFaceH4/MATH-500"));
    }

    [Fact]
    public async Task hf_dataset_sends_bearer_token_from_argument_or_environment()
    {
        var hub = Hub(3);
        using var loader = Loader(hub);

        await loader.LoadAsync(new HfDatasetRequest("org/math", "train") { SampleFields = MathFields, Token = "hf_explicit", Cached = false });
        Assert.All(hub.Handler.Calls, call => Assert.Equal("Bearer hf_explicit", call.Headers["Authorization"]));

        var previous = Environment.GetEnvironmentVariable("HF_TOKEN");
        Environment.SetEnvironmentVariable("HF_TOKEN", "hf_env");
        try
        {
            hub.Handler.Calls.Clear();
            await loader.LoadAsync(new HfDatasetRequest("org/math", "train") { SampleFields = MathFields, Cached = false });
            Assert.NotEmpty(hub.Handler.Calls);
            Assert.All(hub.Handler.Calls, call => Assert.Equal("Bearer hf_env", call.Headers["Authorization"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HF_TOKEN", previous);
        }
    }

    [Fact]
    public async Task hf_dataset_resolves_default_config_and_reports_unknown_splits_and_configs()
    {
        var multi = new FakeHub("org/multi", new Dictionary<(string, string), List<JsonObject>>
        {
            [("en", "test")] = Rows(2, "en"),
            [("fr", "test")] = Rows(2, "fr"),
        });
        using var loader = new HfDatasetLoader(multi.Handler, _cacheDir, "https://fake-datasets-server.test");

        var named = await loader.LoadAsync(new HfDatasetRequest("org/multi", "test") { Name = "fr", SampleFields = MathFields });
        Assert.Equal("fr question 0", named[0].Input.Text);

        var missingConfig = await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(new HfDatasetRequest("org/multi", "test")));
        Assert.StartsWith("Config name is missing.", missingConfig.Message);
        Assert.Contains("'en', 'fr'", missingConfig.Message);

        var badConfig = await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(new HfDatasetRequest("org/multi", "test") { Name = "de" }));
        Assert.Contains("BuilderConfig 'de' not found", badConfig.Message);

        var badSplit = await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(new HfDatasetRequest("org/multi", "validation") { Name = "en" }));
        Assert.Equal("Unknown split \"validation\". Should be one of ['test'].", badSplit.Message);

        var missing = await Assert.ThrowsAsync<HfHubHttpException>(() => loader.LoadAsync(new HfDatasetRequest("org/nope", "test")));
        Assert.Equal(404, missing.Status);
        Assert.Contains("The dataset does not exist on the Hub.", missing.Message);
    }

    [Fact]
    public async Task hf_dataset_retries_transient_failures_with_backoff()
    {
        var hub = Hub(3);
        var failures = 0;
        hub.Intercept = call =>
        {
            if (call.Uri.Contains("/rows") && failures++ < 2)
            {
                return FakeHttpHandler.Json(failures == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.BadGateway, "{\"error\":\"slow down\"}");
            }

            return null;
        };
        var waits = new List<TimeSpan>();
        using var loader = Loader(hub, delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, random: new Random(1));

        var dataset = await loader.LoadAsync(new HfDatasetRequest("org/math", "train") { SampleFields = MathFields });

        Assert.Equal(5, dataset.Count);
        Assert.Equal(2, waits.Count);
        Assert.InRange(waits[0].TotalSeconds, 0, 60);
        Assert.InRange(waits[1].TotalSeconds, 0, 120);

        // the third failure exhausts the three attempts; retry: false never retries
        failures = 0;
        hub.Intercept = call => call.Uri.Contains("/rows") && failures++ < 3 ? FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, "{}") : null;
        var exhausted = await Assert.ThrowsAsync<HfHubHttpException>(() => loader.LoadAsync(new HfDatasetRequest("org/math", "train") { Cached = false }));
        Assert.Equal(429, exhausted.Status);

        failures = 0;
        waits.Clear();
        hub.Intercept = call => call.Uri.Contains("/rows") && failures++ < 1 ? FakeHttpHandler.Json(HttpStatusCode.BadGateway, "{}") : null;
        await Assert.ThrowsAsync<HfHubHttpException>(() => loader.LoadAsync(new HfDatasetRequest("org/math", "train") { Cached = false, Retry = false }));
        Assert.Empty(waits);

        // a 403 (gated repo without a token) is not retried
        hub.Intercept = call => call.Uri.Contains("/splits") ? FakeHttpHandler.Json(HttpStatusCode.Forbidden, "{\"error\":\"gated\"}") : null;
        var gated = await Assert.ThrowsAsync<HfHubHttpException>(() => loader.LoadAsync(new HfDatasetRequest("org/math", "train") { Cached = false }));
        Assert.Equal(403, gated.Status);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task hf_dataset_retries_http_timeouts()
    {
        // HttpClient reports its timeout as a TaskCanceledException wrapping a TimeoutException with a cancelled token
        var hub = Hub(3);
        var timeouts = 0;
        hub.Intercept = call =>
        {
            if (call.Uri.Contains("/rows") && timeouts++ < 1)
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException("The operation was canceled."), cts.Token);
            }

            return null;
        };
        var waits = new List<TimeSpan>();
        using var loader = Loader(hub, delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, random: new Random(1));

        var dataset = await loader.LoadAsync(new HfDatasetRequest("org/math", "train") { SampleFields = MathFields });

        Assert.Equal(5, dataset.Count);
        Assert.Single(waits);

        // a caller's own cancellation is not retried
        timeouts = 0;
        hub.Intercept = call =>
        {
            if (call.Uri.Contains("/rows") && timeouts++ < 1)
            {
                throw new OperationCanceledException("caller cancelled");
            }

            return null;
        };
        await Assert.ThrowsAsync<OperationCanceledException>(() => loader.LoadAsync(new HfDatasetRequest("org/math", "train") { Cached = false }));
        Assert.Single(waits);
    }

    [Fact]
    public async Task hf_dataset_shrinks_pages_when_the_server_truncates_cells()
    {
        var hub = Hub(120);
        hub.TruncateRowsAbovePageLength = 25;
        using var loader = Loader(hub);

        var dataset = await loader.LoadAsync(new HfDatasetRequest("org/math", "test") { SampleFields = MathFields });

        Assert.Equal(120, dataset.Count);
        Assert.Equal(Enumerable.Range(0, 120).Select(i => $"default question {i}"), dataset.Select(s => s.Input.Text));
        Assert.Contains(hub.Handler.Calls, call => call.Uri.EndsWith("length=25"));

        hub.TruncateRowsAbovePageLength = 0;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(new HfDatasetRequest("org/math", "train") { Cached = false }));
        Assert.Contains("row size limit", error.Message);
    }

    [Fact]
    public void datasets_hf_is_the_public_entry_point()
    {
        var hub = Hub(4);
        var previous = Environment.GetEnvironmentVariable(HfDatasetLoader.CacheDirVar);
        var previousEndpoint = Environment.GetEnvironmentVariable("HF_DATASETS_SERVER");
        Environment.SetEnvironmentVariable(HfDatasetLoader.CacheDirVar, _cacheDir);
        Environment.SetEnvironmentVariable("HF_DATASETS_SERVER", "https://fake-datasets-server.test");
        try
        {
            var dataset = Datasets.Hf("org/math", "train", sampleFields: MathFields, limit: 2, autoId: true, handler: hub.Handler);

            Assert.Equal(2, dataset.Count);
            Assert.Equal(1, dataset[0].Id);
            Assert.True(Directory.Exists(Path.Combine(_cacheDir, "hf_datasets")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(HfDatasetLoader.CacheDirVar, previous);
            Environment.SetEnvironmentVariable("HF_DATASETS_SERVER", previousEndpoint);
        }
    }
}
