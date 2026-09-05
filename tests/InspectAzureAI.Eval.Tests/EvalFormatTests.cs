using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Log.Json;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;

/// <summary>The <c>.eval</c> zip format: incremental writer, readers (full, header-only, chunked), attachments, listing and the runner's format selection.</summary>
public sealed class EvalFormatTests : IDisposable
{
    private const string TinyEval = "tiny/2026-09-05T11-52-09-00-00_tiny_DMp9V2YEhradPcNhv3qARw.eval";

    private const string TinyChunkedEval = "tiny/chunked/2026-09-05T11-52-09-00-00_tiny_DMp9V2YEhradPcNhv3qARw.eval";

    private static readonly DateTimeOffset Created = new(2026, 9, 5, 11, 52, 9, TimeSpan.Zero);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inspect-eval-format-tests", Guid.NewGuid().ToString("N"));

    public EvalFormatTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------- building blocks

    [Fact]
    public void zstd_decoder_matches_the_reference_fixtures()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(FixtureRoot("zstd", "manifest.json")))!.AsArray();
        Assert.True(manifest.Count >= 10);
        foreach (var entry in manifest)
        {
            var name = (string)entry!["name"]!;
            var compressed = File.ReadAllBytes(FixtureRoot("zstd", name + ".zst"));
            Assert.Equal((int)entry["compressed"]!, compressed.Length);
            var data = ZstdDecoder.Decompress(compressed, (long)entry["size"]!);
            Assert.Equal((int)entry["size"]!, data.Length);
            Assert.Equal((string)entry["sha256"]!, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
            // the size hint is only a hint
            Assert.Equal(data, ZstdDecoder.Decompress(compressed));
        }

        Assert.Throws<InvalidDataException>(() => ZstdDecoder.Decompress("not zstd"u8));
    }

    [PythonFact]
    public void murmur_hash_matches_python_mm3_hash()
    {
        string[] strings =
        [
            "",
            "hello",
            "The quick brown fox jumps over the lazy dog",
            "héllo wörld \U0001F4A1 — naïve café",
            new string('x', 1000),
            "attachment content\nwith newlines\tand \"quotes\" and \\backslashes",
            "0123456789abcdef",
            "0123456789abcdefg",
        ];
        var json = JsonSerializer.Serialize(strings, new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default });
        var expected = JsonSerializer.Deserialize<string[]>(PythonReference.Run($$"""
            import json
            from inspect_ai._util.hash import mm3_hash
            print(json.dumps([mm3_hash(s) for s in json.loads(r'''{{json}}''')]))
            """))!;
        Assert.Equal(expected, strings.Select(MurmurHash3.Hash).ToArray());
    }

    [Fact]
    public void log_file_naming_follows_python()
    {
        var spec = Spec();
        Assert.Equal("2026-09-05T11-52-09-00-00_tiny_DMp9V2YEhradPcNhv3qARw", LogFileNaming.LogFileKey(spec));
        Assert.Equal(Path.Combine("logs", "2026-09-05T11-52-09-00-00_tiny_DMp9V2YEhradPcNhv3qARw.eval"), LogFileNaming.LogFilePath("logs", spec, LogFormat.Eval));
        Assert.Equal("name", LogFileNaming.TaskDisplayName("pkg/name"));
        Assert.Equal("hf/name", LogFileNaming.TaskDisplayName("hf/name"));
        using var env = new EnvVarScope().Set("INSPECT_EVAL_LOG_FILE_PATTERN", "{task}_{model}_{id}");
        var withModel = LogFileNaming.LogFileKey(spec);
        Assert.Contains("mockllm-model", withModel);
        Assert.DoesNotContain("+", withModel);
        Assert.Equal("2026-09-05T11-52-09-00-00_tiny__DMp9V2YEhradPcNhv3qARw", LogFileNaming.LogFileKey(spec with { Model = LogFileNaming.ModelNone }));
    }

    [Fact]
    public void log_formats_resolve_from_locations_bytes_and_environment()
    {
        Assert.Equal(LogFormat.Eval, LogFormats.ForLocation("a/b.eval"));
        Assert.Equal(LogFormat.Json, LogFormats.ForLocation("a/b.json"));
        Assert.Throws<ArgumentException>(() => LogFormats.ForLocation("a/b.txt"));
        Assert.Equal(LogFormat.Eval, LogFormats.ForBytes("PK"u8));
        Assert.Equal(LogFormat.Json, LogFormats.ForBytes("{\"ve"u8));
        Assert.Throws<ArgumentException>(() => LogFormats.ForBytes("<xml"u8));
        Assert.Equal(LogFormat.Eval, LogFormats.Parse("EVAL"));
        Assert.Throws<ArgumentException>(() => LogFormats.Parse("xml"));
        Assert.Equal(".eval", LogFormat.Eval.Extension());
        using (new EnvVarScope().Set("INSPECT_LOG_FORMAT", null).Set("INSPECT_EVAL_LOG_FORMAT", null))
        {
            Assert.Null(LogFormats.FromEnvironment());
        }

        using (new EnvVarScope().Set("INSPECT_LOG_FORMAT", null).Set("INSPECT_EVAL_LOG_FORMAT", "json"))
        {
            Assert.Equal(LogFormat.Json, LogFormats.FromEnvironment());
        }

        using (new EnvVarScope().Set("INSPECT_LOG_FORMAT", "bogus"))
        {
            Assert.Throws<ArgumentException>(() => LogFormats.FromEnvironment());
        }

        using (new EnvVarScope().Set("INSPECT_LOG_DIR", Path.Combine(_tempDir, "envlogs")))
        {
            Assert.Equal(Path.Combine(_tempDir, "envlogs"), LogFormats.DefaultLogDir);
            Assert.Equal(Path.Combine(_tempDir, "envlogs"), new EvalOptions { Model = new Model(new ScriptedModelApi()) }.LogDir);
        }
    }

    [Fact]
    public void default_log_buffers_match_python()
    {
        var eval = new EvalRecorder(Path.Combine(_tempDir, "e"));
        Assert.Equal(1, eval.DefaultLogBuffer(1, highThroughput: false));
        Assert.Equal(3, eval.DefaultLogBuffer(9, highThroughput: false));
        Assert.Equal(10, eval.DefaultLogBuffer(300, highThroughput: false));
        Assert.Equal(10, eval.DefaultLogBuffer(100, highThroughput: true));
        Assert.Equal(20, eval.DefaultLogBuffer(400, highThroughput: true));
        var json = new JsonRecorder(Path.Combine(_tempDir, "j"));
        Assert.Equal(10, json.DefaultLogBuffer(5, highThroughput: false));
        Assert.Equal(20, json.DefaultLogBuffer(200, highThroughput: true));
        Assert.True(eval.IsWriteable());
        Assert.True(EvalRecorder.HandlesLocation("x.eval"));
        Assert.False(EvalRecorder.HandlesLocation("x.json"));
        Assert.True(JsonRecorder.HandlesBytes("{"u8));
    }

    // ---------------------------------------------------------------- the incremental writer

    [Fact]
    public async Task eval_recorder_writes_members_incrementally_like_python()
    {
        var spec = Spec();
        var recorder = new EvalRecorder(_tempDir);
        await using var scope = recorder.ConfigureAwait(false);
        var path = await recorder.LogInitAsync(spec);
        Assert.Equal(Path.Combine(_tempDir, "2026-09-05T11-52-09-00-00_tiny_DMp9V2YEhradPcNhv3qARw.eval"), path);
        Assert.False(File.Exists(path));
        await recorder.LogStartAsync(spec, new EvalPlan());
        await recorder.FlushAsync(spec);
        Assert.Equal(["_journal/start.json"], MemberNames(path));

        var started = EvalLogFiles.ReadEvalLog(path, headerOnly: true);
        Assert.Equal(EvalStatus.Started, started.Status);
        Assert.Equal("tiny", started.Eval.Task);
        Assert.Equal(2, started.Eval.Config.Epochs);
        Assert.Null(started.Samples);
        Assert.Equal(path, started.Location);
        Assert.Empty(EvalLogFiles.ReadEvalLog(path).Samples!);

        await recorder.LogSampleAsync(spec, Sample(1, 1));
        await recorder.LogSampleAsync(spec, Sample("b", 1, "blue"));
        var summaries = await recorder.SampleSummariesAsync(spec);
        Assert.Equal(2, summaries!.Count);
        Assert.NotNull(await recorder.BufferedSampleAsync(spec, 1, 1));
        Assert.Null(await recorder.BufferedSampleAsync(spec, "1", 1));
        await recorder.FlushAsync(spec);
        Assert.Equal(["_journal/start.json", "samples/1_epoch_1.json", "samples/b_epoch_1.json", "_journal/summaries/1.json"], MemberNames(path));
        Assert.Null(await recorder.BufferedSampleAsync(spec, 1, 1));

        var inProgress = EvalLogFiles.ReadEvalLog(path);
        Assert.Equal(EvalStatus.Started, inProgress.Status);
        Assert.Equal([1, "b"], inProgress.Samples!.Select(sample => sample.Id).ToArray());
        Assert.Equal(2, EvalLogFiles.ReadEvalLogSampleSummaries(path).Count);

        await recorder.LogSampleAsync(spec, Sample(1, 2));
        var results = new EvalResults
        {
            TotalSamples = 3,
            CompletedSamples = 3,
            Scores = [new EvalScore("match", "match") { Metrics = new Dictionary<string, EvalMetric> { ["accuracy"] = new("accuracy", 1.0) } }],
        };
        var reductions = new List<EvalSampleReductions> { new("match", [new EvalSampleScore(new Score(1.0)) { SampleId = 1 }, new EvalSampleScore(new Score(1.0)) { SampleId = "b" }]) };
        var log = await recorder.LogFinishAsync(spec, EvalStatus.Success, new EvalStats { StartedAt = Created, CompletedAt = Created.AddSeconds(5) }, results, reductions);
        Assert.Equal(
            ["_journal/start.json", "samples/1_epoch_1.json", "samples/b_epoch_1.json", "_journal/summaries/1.json", "samples/1_epoch_2.json", "_journal/summaries/2.json", "summaries.json", "reductions.json", "header.json"],
            MemberNames(path));

        Assert.Equal(path, log.Location);
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(1.0, log.Results!.Scores[0].Metrics["accuracy"].Value);
        var lazy = Assert.IsType<LazyList<EvalSample>>(log.Samples);
        Assert.False(lazy.IsLoaded);
        Assert.Equal(3, log.Samples!.Count);
        Assert.True(lazy.IsLoaded);
        Assert.Equal([1, "b", 1], log.Samples.Select(sample => sample.Id).ToArray());
        Assert.Equal([1, 1, 2], log.Samples.Select(sample => sample.Epoch).ToArray());
        Assert.Single(log.Reductions!);
        Assert.Null(await recorder.SampleSummariesAsync(spec));

        var header = ReadMemberText(path, "header.json");
        Assert.StartsWith("{\"version\":2,\"status\":\"success\",\"eval\":{", header);
        Assert.DoesNotContain("\n", header);
        Assert.DoesNotContain("\"samples\":[", header);
        Assert.Equal(3, JsonNode.Parse(ReadMemberText(path, "summaries.json"))!.AsArray().Count);
        Assert.Equal(2, JsonNode.Parse(ReadMemberText(path, "_journal/summaries/1.json"))!.AsArray().Count);
        var start = JsonNode.Parse(ReadMemberText(path, "_journal/start.json"))!.AsObject();
        Assert.Equal(["version", "eval", "plan"], start.Select(pair => pair.Key).ToArray());
        Assert.Equal(2, (int)start["version"]!);

        var read = EvalLogFiles.ReadEvalLog(path);
        Assert.Equal(EvalStatus.Success, read.Status);
        Assert.Equal(3, read.Samples!.Count);
        Assert.Equal("blue", read.Samples[1].Scores!["match"].Answer);
        Assert.Equal(2, read.Samples[1].Messages.Count);
        Assert.Equal(2, read.Samples[1].Events.Count);
        Assert.Single(read.Reductions!);
        Assert.Null(EvalLogFiles.ReadEvalLog(path, headerOnly: true).Samples);
        Assert.NotNull(EvalLogFiles.ReadEvalLog(path, headerOnly: true).Reductions);
    }

    [Fact]
    public async Task re_logging_a_sample_supersedes_the_prior_record()
    {
        var spec = Spec();
        var recorder = new EvalRecorder(_tempDir);
        await using var scope = recorder.ConfigureAwait(false);
        var path = await recorder.LogInitAsync(spec);
        await recorder.LogStartAsync(spec, new EvalPlan());
        await recorder.LogSampleAsync(spec, Sample(1, 1, "first"));
        await recorder.FlushAsync(spec);
        await recorder.LogSampleAsync(spec, Sample(1, 1, "second"));
        Assert.Equal("second", Assert.Single(await recorder.SampleSummariesAsync(spec) ?? []).Scores!["match"].Answer);
        await recorder.LogFinishAsync(spec, EvalStatus.Success, new EvalStats(), null, null);

        Assert.Equal(2, MemberNames(path).Count(name => name == "samples/1_epoch_1.json"));
        var read = EvalLogFiles.ReadEvalLog(path);
        Assert.Equal("second", Assert.Single(read.Samples!).Scores!["match"].Answer);
        Assert.Equal("second", Assert.Single(EvalLogFiles.ReadEvalLogSampleSummaries(path)).Scores!["match"].Answer);
        Assert.Equal("second", EvalLogFiles.ReadEvalLogSample(path, 1, 1).Scores!["match"].Answer);
    }

    [Fact]
    public async Task write_through_samples_land_in_the_zip_before_the_flush()
    {
        var spec = Spec();
        var recorder = new EvalRecorder(_tempDir);
        await using var scope = recorder.ConfigureAwait(false);
        var path = await recorder.LogInitAsync(spec);
        await recorder.LogStartAsync(spec, new EvalPlan());
        await recorder.LogSampleAsync(spec, Sample(1, 1), writeThrough: true);
        var buffered = await recorder.BufferedSampleAsync(spec, 1, 1);
        Assert.NotNull(buffered);
        Assert.Empty(buffered.Events);
        Assert.Single(await recorder.SampleSummariesAsync(spec) ?? []);
        await recorder.FlushAsync(spec);
        Assert.Equal(["_journal/start.json", "samples/1_epoch_1.json", "_journal/summaries/1.json"], MemberNames(path));
        Assert.Null(await recorder.BufferedSampleAsync(spec, 1, 1));
        Assert.Equal(2, EvalLogFiles.ReadEvalLogSample(path, 1, 1).Events.Count);
        await recorder.LogFinishAsync(spec, EvalStatus.Success, new EvalStats(), null, null);
        Assert.Single(EvalLogFiles.ReadEvalLog(path).Samples!);
    }

    [Fact]
    public async Task config_updates_are_journaled_and_folded_into_the_header()
    {
        var spec = Spec();
        var recorder = new EvalRecorder(_tempDir);
        await using var scope = recorder.ConfigureAwait(false);
        var path = await recorder.LogInitAsync(spec);
        await recorder.LogStartAsync(spec, new EvalPlan());
        var update = new ConfigUpdate([new ConfigValueChange("eval", "max_connections") { Value = 4, Previous = 2 }], "task", new ProvenanceData("kev"));
        await recorder.LogConfigUpdateAsync(spec, update);
        Assert.False(File.Exists(path));
        await recorder.FlushAsync(spec);
        Assert.Contains("_journal/config_updates/1.json", MemberNames(path));
        var inProgress = EvalLogFiles.ReadEvalLog(path, headerOnly: true);
        Assert.Equal("max_connections", Assert.Single(Assert.Single(inProgress.ConfigUpdates!).Changes).Name);
        await recorder.LogConfigUpdateAsync(spec, update with { Scope = "process" });
        Assert.Contains("_journal/config_updates/2.json", MemberNames(path));
        var log = await recorder.LogFinishAsync(spec, EvalStatus.Success, new EvalStats(), null, null);
        Assert.Equal(["task", "process"], log.ConfigUpdates!.Select(u => u.Scope).ToArray());
        Assert.Equal(["task", "process"], EvalLogFiles.ReadEvalLog(path).ConfigUpdates!.Select(u => u.Scope).ToArray());
    }

    [Fact]
    public async Task re_initialising_over_an_existing_log_seeds_its_summaries()
    {
        var spec = Spec();
        string path;
        var first = new EvalRecorder(_tempDir);
        await using (first.ConfigureAwait(false))
        {
            path = await first.LogInitAsync(spec);
            await first.LogStartAsync(spec, new EvalPlan());
            await first.LogSampleAsync(spec, Sample(1, 1, "old"));
            await first.LogSampleAsync(spec, Sample("b", 1));
            await first.LogFinishAsync(spec, EvalStatus.Success, new EvalStats(), null, null);
        }

        var second = new EvalRecorder(_tempDir);
        await using (second.ConfigureAwait(false))
        {
            Assert.Equal(path, await second.LogInitAsync(spec, path));
            Assert.Equal(2, (await second.SampleSummariesAsync(spec))!.Count);
            await second.LogStartAsync(spec, new EvalPlan());
            await second.LogSampleAsync(spec, Sample(1, 1, "new"));
            var log = await second.LogFinishAsync(spec, EvalStatus.Success, new EvalStats(), null, null);
            var summaries = EvalLogFiles.ReadEvalLogSampleSummaries(path);
            Assert.Equal(2, summaries.Count);
            Assert.Equal("new", summaries.Single(s => Equals(s.Id, 1)).Scores!["match"].Answer);
            Assert.Equal("new", EvalLogFiles.ReadEvalLogSample(path, 1, 1).Scores!["match"].Answer);
            Assert.Single(log.Samples!);
        }

        var clean = new EvalRecorder(_tempDir);
        await using (clean.ConfigureAwait(false))
        {
            await clean.LogInitAsync(spec, path, clean: true);
            Assert.Empty((await clean.SampleSummariesAsync(spec))!);
        }
    }

    [Fact]
    public async Task disposing_an_unfinished_recorder_leaves_no_log_behind()
    {
        var spec = Spec();
        var recorder = new EvalRecorder(_tempDir);
        var path = await recorder.LogInitAsync(spec);
        await recorder.LogStartAsync(spec, new EvalPlan());
        await recorder.LogSampleAsync(spec, Sample(1, 1));
        await recorder.DisposeAsync();
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_tempDir));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => recorder.LogSampleAsync(spec, Sample(1, 1)));
        var fresh = new EvalRecorder(_tempDir);
        await using var scope = fresh.ConfigureAwait(false);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fresh.LogSampleAsync(spec, Sample(1, 1)));
    }

    [Fact]
    public async Task a_cancelled_flush_leaves_no_partial_file_and_the_recorder_keeps_working()
    {
        var spec = Spec();
        var recorder = new EvalRecorder(_tempDir);
        await using var scope = recorder.ConfigureAwait(false);
        var path = await recorder.LogInitAsync(spec);
        await recorder.LogStartAsync(spec, new EvalPlan());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.FlushAsync(spec, cancelled.Token));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
        await recorder.FlushAsync(spec);
        Assert.Equal(["_journal/start.json"], MemberNames(path));
    }

    // ---------------------------------------------------------------- round trips and the python fixtures

    [Fact]
    public void eval_round_trip_of_the_python_json_fixture_preserves_every_sample()
    {
        var original = EvalLogWriter.Read(FixturePath("python_eval_log.json"));
        var evalPath = Path.Combine(_tempDir, "round.eval");
        EvalLogFiles.WriteEvalLog(original, evalPath);
        Assert.True(EvalRecorder.HandlesBytes(File.ReadAllBytes(evalPath).AsSpan(0, 4)));

        var read = EvalLogFiles.ReadEvalLog(evalPath);
        Assert.Equal(evalPath, read.Location);
        Assert.Equal(Canonical(original), Canonical(read));

        var jsonPath = Path.Combine(_tempDir, "round.json");
        EvalLogFiles.WriteEvalLog(read, jsonPath);
        Assert.Equal(Canonical(original), Canonical(EvalLogFiles.ReadEvalLog(jsonPath)));

        // the same file through EvalLogWriter's format-agnostic entry points
        Assert.Equal(Canonical(original), Canonical(EvalLogWriter.Read(evalPath)));
        Assert.Null(EvalLogWriter.ReadHeader(evalPath).Samples);
        var viaWriter = Path.Combine(_tempDir, "via-writer.eval");
        EvalLogWriter.Write(original, viaWriter);
        Assert.Contains("header.json", MemberNames(viaWriter));
    }

    [Fact]
    public void the_same_log_written_to_both_formats_reads_identically()
    {
        // port of test_log_format_equality / test_log_format_round_trip_cross on inspect_ai's own fixture
        var original = EvalLogFiles.ReadEvalLog(FixturePath("python", "log_formats.json"));
        var jsonPath = Path.Combine(_tempDir, "equality.json");
        var evalPath = Path.Combine(_tempDir, "equality.eval");
        EvalLogFiles.WriteEvalLog(original, jsonPath, format: LogFormat.Json);
        EvalLogFiles.WriteEvalLog(original, evalPath, format: LogFormat.Eval);
        var fromJson = EvalLogFiles.ReadEvalLog(jsonPath, format: LogFormat.Json);
        var fromEval = EvalLogFiles.ReadEvalLog(evalPath, format: LogFormat.Eval);
        Assert.Equal(Canonical(fromJson), Canonical(fromEval));
        Assert.Equal(Canonical(original), Canonical(fromEval));
        Assert.Equal(" Yes", fromEval.Samples![0].Target.Text);

        // the python-written .eval of the same era reads on its own
        var fixture = EvalLogFiles.ReadEvalLog(FixturePath("python", "log_formats.eval"));
        Assert.Single(fixture.Samples!);
        Assert.Equal(" Yes", fixture.Samples![0].Target.Text);
        Assert.Equal(WithoutEvalId(original).Eval.Task, WithoutEvalId(fixture).Eval.Task);
        Assert.Equal(" Yes", EvalLogFiles.ReadEvalLogSample(FixturePath("python", "log_formats.eval"), 1, 1).Target.Text);
    }

    [Theory]
    [InlineData("log_formats.eval")]
    [InlineData("log_read_sample.eval")]
    [InlineData("log_streaming.eval")]
    [InlineData("log_message_deduplication.eval")]
    public void reads_inspect_ai_test_fixtures(string name)
    {
        var path = FixturePath("python", name);
        var log = EvalLogFiles.ReadEvalLog(path);
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.NotEmpty(log.Samples!);
        Assert.All(log.Samples!, sample => Assert.NotEmpty(sample.Messages));
        var first = log.Samples![0];
        if (first.Uuid is { } uuid)
        {
            var byUuid = EvalLogFiles.ReadEvalLogSample(path, uuid: uuid);
            Assert.Equal((first.Id, first.Epoch), (byUuid.Id, byUuid.Epoch));
        }

        Assert.Equal(JsonSerializer.Serialize(first, EvalLogWriter.Options), JsonSerializer.Serialize(EvalLogFiles.ReadEvalLogSample(path, first.Id, first.Epoch), EvalLogWriter.Options));
        Assert.Equal(log.Samples.Count, EvalLogFiles.ReadEvalLogSampleSummaries(path).Count);
        Assert.Null(EvalLogFiles.ReadEvalLog(path, headerOnly: true).Samples);
    }

    [Fact]
    public void reads_the_python_written_tiny_eval_fixture()
    {
        var path = FixturePath(TinyEval);
        var log = EvalLogFiles.ReadEvalLog(path);
        Assert.Equal(path, log.Location);
        Assert.Equal(2, log.Version);
        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal("tiny", log.Eval.Task);
        Assert.Equal("DMp9V2YEhradPcNhv3qARw", log.Eval.TaskId);
        Assert.Equal("mockllm/model", log.Eval.Model);
        Assert.Equal(2, log.Eval.Config.Epochs);
        Assert.Equal([1, "colour"], log.Eval.Dataset.SampleIds!.ToArray());
        Assert.Equal(4, log.Results!.TotalSamples);
        Assert.Equal(4, log.Results.CompletedSamples);
        var score = Assert.Single(log.Results.Scores);
        Assert.Equal("match", score.Name);
        Assert.Equal(0.0, score.Metrics["accuracy"].Value);
        Assert.Equal(log.Samples!.Sum(sample => sample.ModelUsage["mockllm/model"].TotalTokens), log.Stats.ModelUsage["mockllm/model"].TotalTokens);
        Assert.Equal("match", Assert.Single(log.Reductions!).Scorer);
        Assert.Equal(2, log.Reductions![0].Samples.Count);

        var samples = log.Samples!;
        Assert.Equal([1, "colour", 1, "colour"], samples.Select(sample => sample.Id).ToArray());
        Assert.Equal([1, 1, 2, 2], samples.Select(sample => sample.Epoch).ToArray());
        var sample = samples[0];
        Assert.Equal("What is 2+2?", sample.Input.Text);
        Assert.Equal("4", sample.Target.Text);
        Assert.Equal("easy", sample.Metadata["difficulty"]);
        Assert.Equal("I", sample.Scores!["match"].Text);
        Assert.Equal("Default output from mockllm/model", sample.Scores["match"].Answer);
        Assert.Equal(3, sample.Messages.Count);
        Assert.Equal("Answer briefly.", sample.Messages[0].Text);
        Assert.Equal("Default output from mockllm/model", sample.Output.Completion);
        Assert.Equal(44, sample.ModelUsage["mockllm/model"].TotalTokens);
        Assert.Equal("hbrCCMXEp9gwuFBo6riCzX", sample.Uuid);
        Assert.Equal(1.884, sample.TotalTime);
        Assert.Equal(17, sample.Events.Count);
        Assert.Equal("span_begin", sample.Events[0].Event);
        Assert.Equal("fjrz2rYh75DXtzb734nzLo", sample.Events[0].Uuid);
        var modelEvent = Assert.Single(sample.Events.OfType<ModelEvent>());
        Assert.Equal(2, modelEvent.Input.Count);
        Assert.Null(modelEvent.InputRefs);
        Assert.Null(sample.EventsData);
        Assert.Equal(["red", "blue"], samples[1].Target.Values);
        Assert.Empty(samples[1].Metadata);

        var header = EvalLogFiles.ReadEvalLog(path, headerOnly: true);
        Assert.Null(header.Samples);
        Assert.Equal(EvalStatus.Success, header.Status);
        Assert.Equal(4, header.Results!.TotalSamples);

        var byUuid = EvalLogFiles.ReadEvalLogSample(path, uuid: "hbrCCMXEp9gwuFBo6riCzX");
        Assert.Equal(1, byUuid.Id);
        Assert.Equal(1, byUuid.Epoch);
        var colour = EvalLogFiles.ReadEvalLogSample(path, "colour", 2);
        Assert.Equal(2, colour.Epoch);
        var thin = EvalLogFiles.ReadEvalLogSample(path, 1, 1, excludeFields: new HashSet<string> { "store", "events" });
        Assert.Empty(thin.Events);
        Assert.Empty(thin.Store);
        Assert.Equal("What is 2+2?", thin.Input.Text);

        var summaries = EvalLogFiles.ReadEvalLogSampleSummaries(path);
        Assert.Equal(4, summaries.Count);
        Assert.Equal("I", summaries[0].Scores!["match"].Text);
        Assert.Equal([(1, 1), (1, 2)], EvalRecorder.ReadLogSampleIds(path).Where(id => Equals(id.Id, 1)).Select(id => (id.Id, id.Epoch)).ToArray());
        Assert.Equal(4, EvalLogFiles.ReadEvalLogSamples(path).Count());
    }

    [Fact]
    public void reads_a_chunked_sample_layout_back_into_the_monolith_shape()
    {
        var monolith = EvalLogFiles.ReadEvalLog(FixturePath(TinyEval));
        var chunkedPath = FixturePath(TinyChunkedEval);
        var chunked = EvalLogFiles.ReadEvalLog(chunkedPath);
        Assert.Equal(4, chunked.Samples!.Count);
        Assert.Equal(Canonical(monolith), Canonical(chunked));

        var sample = EvalLogFiles.ReadEvalLogSample(chunkedPath, 1, 1);
        Assert.Equal(JsonSerializer.Serialize(monolith.Samples![0], EvalLogWriter.Options), JsonSerializer.Serialize(sample, EvalLogWriter.Options));
        Assert.Equal("easy", sample.Metadata["difficulty"]);
        Assert.Equal(17, sample.Events.Count);
        Assert.Equal(3, sample.Messages.Count);
        var thin = EvalLogFiles.ReadEvalLogSample(chunkedPath, "colour", 1, excludeFields: new HashSet<string> { "events", "messages" });
        Assert.Empty(thin.Events);
        Assert.Empty(thin.Messages);
        Assert.Equal(["red", "blue"], thin.Target.Values);
        Assert.Throws<KeyNotFoundException>(() => EvalLogFiles.ReadEvalLogSample(chunkedPath, 7, 1));
        Assert.Equal(4, EvalLogFiles.ReadEvalLogSampleSummaries(chunkedPath).Count);
    }

    [Fact]
    public void chunk_math_matches_python()
    {
        Assert.Equal([new ChunkRange(0, 3), new ChunkRange(3, 6), new ChunkRange(6, 7)], ChunkedSampleFormat.ChunkRanges(7, 3));
        Assert.Empty(ChunkedSampleFormat.ChunkRanges(0, 3));
        Assert.Equal([3, 6, 7], ChunkedSampleFormat.ChunkBoundaries(7, 3));
        Assert.Equal([2, 3, 5], ChunkedSampleFormat.AttachmentChunkBoundaries([4, 4, 20, 1, 1], 10));
        Assert.Empty(ChunkedSampleFormat.AttachmentChunkBoundaries([], 10));
        Assert.Equal([new ChunkRange(0, 2), new ChunkRange(2, 5)], ChunkedSampleFormat.BoundaryRanges([2, 5]));
        Assert.Equal("samples/1_epoch_2/events/1000.json", ChunkedSampleFormat.ChunkEntryName(1, 2, "events", 1000));
        Assert.Equal("samples/a_epoch_1/sample.json", ChunkedSampleFormat.ShellEntryName("a", 1));
        var names = new HashSet<string> { "samples/1_epoch_1.json", "samples/a_epoch_1/sample.json" };
        Assert.Equal(SampleShape.Monolith, ChunkedSampleFormat.ClassifySampleShape(names, 1, 1));
        Assert.Equal(SampleShape.Chunked, ChunkedSampleFormat.ClassifySampleShape(names, "a", 1));
        Assert.Null(ChunkedSampleFormat.ClassifySampleShape(names, "a", 2));

        var events = new List<TranscriptEvent>
        {
            new SpanBeginEvent("s", "solver", "solver"),
            new StepEvent("a", "solver", "begin") { SpanId = "s" },
            new StepEvent("a", "solver", "end") { SpanId = "s" },
            new SpanEndEvent("s"),
        };
        var stats = ChunkedSampleFormat.EventStatsFor(events, [3, 4]);
        Assert.Equal(1, stats.Version);
        Assert.Equal(2, stats.Chunks.Count);
        Assert.Equal(new Dictionary<string, int> { ["span_begin"] = 1, ["step"] = 2 }, stats.Chunks[0].TypeCounts);
        Assert.Equal(new ChunkEdgeEvent("span_begin"), stats.Chunks[0].First);
        Assert.Equal(new ChunkEdgeEvent("step", "s"), stats.Chunks[0].Last);
        Assert.Equal(3, stats.Chunks[1].Start);
        var json = JsonSerializer.Serialize(stats, EvalLogWriter.Options);
        Assert.StartsWith("{\n  \"version\": 1,\n  \"chunks\": [", json);
        Assert.DoesNotContain("\"span_id\": null", json);
    }

    [Fact]
    public void attachments_are_condensed_on_write_and_resolved_on_request()
    {
        var longText = new string('a', 300);
        var sample = Sample(1, 1) with { Events = [new InfoEvent("test", JsonValue.Create(longText)) { Timestamp = Created }] };
        var spec = Spec();
        var log = new EvalLog { Eval = spec, Status = EvalStatus.Success, Samples = [sample] };
        var path = Path.Combine(_tempDir, "attachments.eval");
        EvalLogFiles.WriteEvalLog(log, path);

        var raw = EvalLogFiles.ReadEvalLog(path);
        var hash = MurmurHash3.Hash(longText);
        Assert.Equal(longText, Assert.Single(raw.Samples!).Attachments[hash]);
        Assert.Contains($"attachment://{hash}", ReadMemberText(path, "samples/1_epoch_1.json"));
        var resolved = EvalLogFiles.ReadEvalLog(path, resolveAttachments: ResolveAttachments.Full);
        Assert.Empty(resolved.Samples![0].Attachments);
        Assert.Equal(longText, ((InfoEvent)resolved.Samples[0].Events[0]).Data!.GetValue<string>());
        Assert.Equal(longText, ((InfoEvent)EvalLogFiles.ReadEvalLogSample(path, 1, 1, resolveAttachments: ResolveAttachments.Core).Events[0]).Data!.GetValue<string>());
    }

    [Fact]
    public void header_only_write_replaces_the_header_and_keeps_the_samples_on_disk()
    {
        var original = EvalLogFiles.ReadEvalLog(FixturePath(TinyEval));
        var path = Path.Combine(_tempDir, "header.eval");
        EvalLogFiles.WriteEvalLog(original, path);
        var edited = EvalLogEditing.EditEvalLog(original with { Samples = [] }, [new TagsEdit { TagsAdd = ["reviewed"] }], new ProvenanceData("kev"));
        EvalLogFiles.WriteEvalLog(edited, path, headerOnly: true);

        Assert.Equal(1, MemberNames(path).Count(name => name == "header.json"));
        Assert.Equal("header.json", MemberNames(path)[^1]);
        var read = EvalLogFiles.ReadEvalLog(path);
        Assert.Equal(["reviewed"], read.Tags);
        Assert.Single(read.LogUpdates!);
        Assert.Equal(4, read.Samples!.Count);
        Assert.Equal(Canonical(original with { Tags = ["reviewed"], LogUpdates = read.LogUpdates }), Canonical(read));

        // the json format grafts the samples on disk onto the header the same way
        var jsonPath = Path.Combine(_tempDir, "header.json");
        EvalLogFiles.WriteEvalLog(original, jsonPath);
        EvalLogFiles.WriteEvalLog(edited, jsonPath, headerOnly: true);
        var readJson = EvalLogFiles.ReadEvalLog(jsonPath);
        Assert.Equal(["reviewed"], readJson.Tags);
        Assert.Equal(4, readJson.Samples!.Count);
        var fresh = Path.Combine(_tempDir, "fresh.json");
        EvalLogFiles.WriteEvalLog(original, fresh, headerOnly: true);
        Assert.Null(EvalLogFiles.ReadEvalLog(fresh).Samples);
    }

    [Fact]
    public void reads_logs_from_bytes_with_format_detection()
    {
        using var evalStream = File.OpenRead(FixturePath(TinyEval));
        var fromEval = EvalLogFiles.ReadEvalLog(evalStream);
        Assert.Null(fromEval.Location);
        Assert.Equal(4, fromEval.Samples!.Count);
        using var jsonStream = File.OpenRead(FixturePath("python", "log_formats.json"));
        var fromJson = EvalLogFiles.ReadEvalLog(jsonStream, headerOnly: true);
        Assert.Null(fromJson.Samples);
        Assert.Null(fromJson.Location);
        using var bytes = new MemoryStream(File.ReadAllBytes(FixturePath(TinyEval)));
        Assert.Equal(4, EvalRecorder.ReadLogBytes(bytes).Samples!.Count);
    }

    [Fact]
    public void malformed_and_unsupported_logs_are_rejected()
    {
        var notZip = Path.Combine(_tempDir, "text.eval");
        File.WriteAllText(notZip, "this is not a zip archive at all, just a plain text file");
        Assert.Throws<InvalidDataException>(() => EvalLogFiles.ReadEvalLog(notZip));

        var empty = Path.Combine(_tempDir, "empty.eval");
        using (var archive = new ZipArchive(File.Create(empty), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("unrelated.json").Open());
            writer.Write("{}");
        }

        Assert.Throws<InvalidDataException>(() => EvalLogFiles.ReadEvalLog(empty));

        var header = JsonNode.Parse(ReadMemberText(FixturePath(TinyEval), "header.json"))!.AsObject();
        header["version"] = 3;
        var future = Path.Combine(_tempDir, "future.eval");
        using (var archive = new ZipArchive(File.Create(future), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("header.json").Open());
            writer.Write(header.ToJsonString());
        }

        // Python's .eval reader has no version gate (its JSON reader does): the version is kept as read
        Assert.Equal(3, EvalLogFiles.ReadEvalLog(future).Version);
        Assert.Throws<InvalidDataException>(() => EvalLogFiles.ReadEvalLog(FixturePath("python", "log_version_3.txt"), format: LogFormat.Json));

        var path = FixturePath(TinyEval);
        Assert.Throws<ArgumentException>(() => EvalLogFiles.ReadEvalLogSample(path));
        Assert.Throws<KeyNotFoundException>(() => EvalLogFiles.ReadEvalLogSample(path, 99, 1));
        Assert.Throws<KeyNotFoundException>(() => EvalLogFiles.ReadEvalLogSample(path, 1, 3));
        Assert.Throws<KeyNotFoundException>(() => EvalLogFiles.ReadEvalLogSample(path, uuid: "nope"));
        Assert.Throws<ArgumentException>(() => EvalLogFiles.ReadEvalLog("log.txt"));
        Assert.Throws<ArgumentException>(() => EvalLogFiles.WriteEvalLog(new EvalLog { Eval = Spec() }));
        Assert.Throws<InvalidDataException>(() => EvalLogFiles.ReadEvalLogSamples(Path.Combine(_tempDir, "empty.eval")).ToList());
        var noIds = Path.Combine(_tempDir, "no-ids.eval");
        EvalLogFiles.WriteEvalLog(new EvalLog { Eval = Spec() with { Dataset = new EvalDataset() }, Status = EvalStatus.Success }, noIds);
        Assert.Throws<InvalidOperationException>(() => EvalLogFiles.ReadEvalLogSamples(noIds).ToList());
    }

    [Fact]
    public void read_eval_log_samples_requires_a_complete_log_unless_told_otherwise()
    {
        var original = EvalLogFiles.ReadEvalLog(FixturePath(TinyEval));
        var path = Path.Combine(_tempDir, "partial.eval");
        EvalLogFiles.WriteEvalLog(original with { Status = EvalStatus.Cancelled, Samples = original.Samples!.Take(3).ToList() }, path);
        Assert.Throws<InvalidOperationException>(() => EvalLogFiles.ReadEvalLogSamples(path).ToList());
        var available = EvalLogFiles.ReadEvalLogSamples(path, allSamplesRequired: false).ToList();
        Assert.Equal(3, available.Count);
        Assert.Equal([1, 1, "colour"], available.Select(sample => sample.Id).ToArray());
    }

    // ---------------------------------------------------------------- listing

    [Fact]
    public void lists_eval_logs_like_python()
    {
        var dir = FixtureRoot("eval-logs", "list_logs");
        var logs = EvalLogFiles.ListEvalLogs(dir, formats: [LogFormat.Eval, LogFormat.Json]);
        Assert.Equal(3, logs.Count);
        Assert.DoesNotContain(logs, log => log.Name.EndsWith("ignore.json", StringComparison.Ordinal));
        Assert.All(logs, log => Assert.Equal("file", log.Type));
        Assert.All(logs, log => Assert.True(log.Size > 0 && log.Mtime > 0));

        var json = logs.Single(log => log.Name.EndsWith("2024-11-05T13-31-45-05-00_input-task_8zXjbRzCWrL9GXiXo2vus9.json", StringComparison.Ordinal));
        Assert.Equal("input-task", json.Task);
        Assert.Equal("8zXjbRzCWrL9GXiXo2vus9", json.TaskId);
        Assert.Null(json.Suffix);
        var custom = logs.Single(log => log.Name.EndsWith("custom.eval", StringComparison.Ordinal));
        var customHeader = EvalLogFiles.ReadEvalLog(custom.Name, headerOnly: true);
        Assert.Equal(customHeader.Eval.Task, custom.Task);
        Assert.Equal(customHeader.Eval.TaskId, custom.TaskId);

        // .eval files are listed whatever the formats filter says (Python's is_log_file); json needs the timestamp prefix
        Assert.Equal(2, EvalLogFiles.ListEvalLogs(dir, formats: [LogFormat.Eval]).Count);
        Assert.Equal(3, EvalLogFiles.ListEvalLogs(dir, formats: [LogFormat.Json]).Count);
        Assert.Equal(3, EvalLogFiles.ListEvalLogs(dir).Count);
        var task = EvalLogFiles.ReadEvalLog(json.Name, headerOnly: true).Eval.Task;
        Assert.Equal(3, EvalLogFiles.ListEvalLogs(dir, filter: log => log.Eval.Task == task).Count);
        Assert.Empty(EvalLogFiles.ListEvalLogs(dir, filter: log => log.Eval.Task == "other"));
        Assert.Empty(EvalLogFiles.ListEvalLogs(Path.Combine(_tempDir, "does-not-exist")));

        // recursion and ordering by modification time
        var nested = Path.Combine(_tempDir, "nested", "deeper");
        Directory.CreateDirectory(nested);
        var older = Path.Combine(_tempDir, "nested", "2024-11-05T13-31-45-05-00_old_a.json");
        var newer = Path.Combine(nested, "2024-11-06T13-31-45-05-00_new_b.eval");
        File.Copy(json.Name, older);
        File.Copy(custom.Name, newer);
        File.SetLastWriteTimeUtc(older, new DateTime(2024, 11, 5, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2024, 11, 6, 0, 0, 0, DateTimeKind.Utc));
        var recursive = EvalLogFiles.ListEvalLogs(Path.Combine(_tempDir, "nested"));
        Assert.Equal(["new", "old"], recursive.Select(log => log.Task).ToArray());
        Assert.Equal(["old", "new"], EvalLogFiles.ListEvalLogs(Path.Combine(_tempDir, "nested"), descending: false).Select(log => log.Task).ToArray());
        Assert.Equal(["old"], EvalLogFiles.ListEvalLogs(Path.Combine(_tempDir, "nested"), recursive: false).Select(log => log.Task).ToArray());
        Assert.Equal("deeper/2024-11-06T13-31-45-05-00_new_b.eval", EvalLogFiles.ManifestEvalLogName(recursive[0], Path.Combine(_tempDir, "nested"), Path.DirectorySeparatorChar.ToString()));

        EvalLogFiles.WriteLogDirManifest(Path.Combine(_tempDir, "nested"));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(_tempDir, "nested", "logs.json")))!.AsObject();
        Assert.Equal(["deeper/2024-11-06T13-31-45-05-00_new_b.eval", "2024-11-05T13-31-45-05-00_old_a.json"], manifest.Select(pair => pair.Key).ToArray());
        Assert.Equal(task, (string)manifest["deeper/2024-11-06T13-31-45-05-00_new_b.eval"]!["eval"]!["task"]!);
        Assert.Null(manifest["deeper/2024-11-06T13-31-45-05-00_new_b.eval"]!["samples"]);

        using (new EnvVarScope().Set("INSPECT_LOG_DIR", dir))
        {
            Assert.Equal(3, EvalLogFiles.ListEvalLogs().Count);
        }
    }

    [Fact]
    public void log_file_names_parse_like_python()
    {
        Assert.True(EvalLogFiles.IsLogFile("logs/2024-11-05T13-31-45-05-00_task_id.json", [".json"]));
        Assert.True(EvalLogFiles.IsLogFile("logs\\2024-11-05T13:31:45-05:00_task_id.json", [".json"]));
        Assert.False(EvalLogFiles.IsLogFile("logs/ignore.json", [".json"]));
        Assert.False(EvalLogFiles.IsLogFile("logs/2024-11-05T13-31-45-05-00_task_id.json", [".eval"]));
        Assert.True(EvalLogFiles.IsLogFile("logs/custom.eval", [".json"]));
        var info = EvalLogFiles.LogFileInfo(new FileEntry("/x/2024-11-05T13-31-45-05-00_task_abc-scored.eval", "file", 10, 1.0));
        Assert.Equal(("task", "abc", "scored"), (info.Task, info.TaskId, info.Suffix));
        var withModel = EvalLogFiles.LogFileInfo(new FileEntry("/x/2024-11-05T13-31-45-05-00_task_model_abc.eval", "file", 10, 1.0));
        Assert.Equal(("task", "abc"), (withModel.Task, withModel.TaskId));
        var two = EvalLogFiles.LogFileInfo(new FileEntry("/x/2024-11-05T13-31-45-05-00_task.eval", "file", 10, 1.0));
        Assert.Equal(("task", ""), (two.Task, two.TaskId));
        var unreadable = EvalLogFiles.LogFileInfo(new FileEntry(Path.Combine(_tempDir, "missing.eval"), "file", 10, 1.0));
        Assert.Equal(("", ""), (unreadable.Task, unreadable.TaskId));
        var unsorted = EvalLogFiles.LogFilesFromLs([new FileEntry("/x/b.eval", "file", 1, 1.0), new FileEntry("/x/a.eval", "file", 1, 2.0), new FileEntry("/x/dir", "directory", 0, 3.0)], sort: false);
        Assert.Equal(["/x/b.eval", "/x/a.eval"], unsorted.Select(log => log.Name).ToArray());
    }

    // ---------------------------------------------------------------- python interop

    [PythonInteropFact]
    public void python_reads_an_eval_written_by_csharp()
    {
        var original = EvalLogWriter.Read(FixturePath("python_eval_log.json"));
        var path = Path.Combine(_tempDir, "from-csharp.eval");
        EvalLogFiles.WriteEvalLog(original, path);
        var written = EvalLogFiles.ReadEvalLog(path);

        var rewritten = Path.Combine(_tempDir, "from-python.eval").Replace("\\", "/");
        var report = JsonNode.Parse(PythonJsonFormat.SanitizeNonFinite(RunPython($$"""
            import json, zipfile
            from inspect_ai.log import read_eval_log, write_eval_log
            path = r'''{{path.Replace("\\", "/")}}'''
            log = read_eval_log(path)
            names = zipfile.ZipFile(path).namelist()
            def value(v):
                return v if isinstance(v, (int, float, str, bool)) or v is None else json.loads(json.dumps(v))
            out = {
                "task": log.eval.task,
                "status": log.status,
                "names": names,
                "samples": [
                    {
                        "id": s.id,
                        "epoch": s.epoch,
                        "scores": {k: value(v.value) for k, v in (s.scores or {}).items()},
                        "events": [e.event for e in s.events],
                        "messages": len(s.messages),
                        "attachments": len(s.attachments),
                        "model_input": [len(e.input) for e in s.events if e.event == "model"],
                    }
                    for s in log.samples
                ],
                "metrics": {sc.name: {m: v.value for m, v in sc.metrics.items()} for sc in (log.results.scores if log.results else [])},
                "reductions": [r.scorer for r in (log.reductions or [])],
            }
            write_eval_log(log, r'''{{rewritten}}''')
            print(json.dumps(out))
            """)))!.AsObject();

        Assert.Equal(written.Eval.Task, (string)report["task"]!);
        Assert.Equal(written.Status.ToString().ToLowerInvariant(), (string)report["status"]!);
        var names = report["names"]!.AsArray().Select(name => (string)name!).ToList();
        Assert.Equal("_journal/start.json", names[0]);
        Assert.Equal("header.json", names[^1]);
        Assert.Contains("summaries.json", names);
        var samples = report["samples"]!.AsArray();
        Assert.Equal(written.Samples!.Count, samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            var expected = written.Samples[i];
            var actual = samples[i]!.AsObject();
            Assert.Equal(expected.Id, PlainId(actual["id"]));
            Assert.Equal(expected.Epoch, (int)actual["epoch"]!);
            Assert.Equal(expected.Events.Select(e => e.Event).ToArray(), actual["events"]!.AsArray().Select(e => (string)e!).ToArray());
            Assert.Equal(expected.Messages.Count, (int)actual["messages"]!);
            Assert.Equal(expected.Attachments.Count, (int)actual["attachments"]!);
            Assert.Equal(expected.Events.OfType<ModelEvent>().Select(e => e.Input.Count).ToArray(), actual["model_input"]!.AsArray().Select(n => (int)n!).ToArray());
            Assert.Equal(Canon(ToNode(expected.Scores?.ToDictionary(pair => pair.Key, pair => pair.Value.Value) ?? [])), Canon(actual["scores"]));
        }

        var expectedMetrics = written.Results?.Scores.ToDictionary(score => score.Name, score => score.Metrics.ToDictionary(pair => pair.Key, pair => pair.Value.Value)) ?? [];
        Assert.Equal(Canon(ToNode(expectedMetrics)), Canon(report["metrics"]));
        Assert.Equal(written.Reductions?.Select(r => r.Scorer).ToArray() ?? [], report["reductions"]!.AsArray().Select(r => (string)r!).ToArray());

        // and the log Python wrote back (zstandard members) reads identically here
        var roundTrip = EvalLogFiles.ReadEvalLog(rewritten);
        Assert.Equal(Canonical(written), Canonical(roundTrip));
    }

    // ---------------------------------------------------------------- the runner

    [Fact]
    public async Task runner_writes_an_eval_log_by_default()
    {
        using var env = new EnvVarScope().Set("INSPECT_LOG_FORMAT", null).Set("INSPECT_EVAL_LOG_FORMAT", null);
        var logDir = Path.Combine(_tempDir, "logs");
        var task = QuizTask();
        var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(QuizApi()), LogDir = logDir, MaxSamples = 2 });

        Assert.EndsWith(".eval", log.Location);
        var file = Assert.Single(Directory.GetFiles(logDir));
        Assert.Equal(log.Location, file);
        Assert.Empty(Directory.GetFiles(logDir, "*.tmp"));
        var names = MemberNames(file);
        Assert.Equal("_journal/start.json", names[0]);
        Assert.Contains("samples/1_epoch_1.json", names);
        Assert.Contains("samples/2_epoch_1.json", names);
        Assert.Contains("_journal/summaries/1.json", names);
        Assert.Equal(["summaries.json", "header.json"], names[^2..]);

        var read = EvalLogWriter.Read(file);
        Assert.Equal(EvalStatus.Success, read.Status);
        Assert.Equal(2, read.Samples!.Count);
        Assert.Equal(2, read.Results!.CompletedSamples);
        Assert.Equal(1.0, read.Results.Scores[0].Metrics["accuracy"].Value);
        Assert.Equal(Canonical(log), Canonical(read));
        Assert.Equal(2, EvalLogFiles.ReadEvalLogSampleSummaries(file).Count);
        Assert.Null(EvalLogFiles.ReadEvalLog(file, headerOnly: true).Samples);
    }

    [Fact]
    public async Task runner_honours_log_format_option_and_environment()
    {
        var explicitDir = Path.Combine(_tempDir, "explicit");
        var log = await Eval.RunAsync(QuizTask(), new EvalOptions { Model = new Model(QuizApi()), LogDir = explicitDir, MaxSamples = 1, LogFormat = LogFormat.Json });
        Assert.EndsWith(".json", log.Location);
        Assert.StartsWith("{\n  \"version\": 2,", File.ReadAllText(log.Location!));
        Assert.Equal(2, EvalLogFiles.ReadEvalLog(log.Location!).Samples!.Count);

        var envDir = Path.Combine(_tempDir, "env");
        using (new EnvVarScope().Set("INSPECT_LOG_FORMAT", "JSON").Set("INSPECT_EVAL_LOG_FORMAT", null).Set("INSPECT_LOG_DIR", envDir))
        {
            var fromEnv = await Eval.RunAsync(QuizTask(), new EvalOptions { Model = new Model(QuizApi()), MaxSamples = 1 });
            Assert.EndsWith(".json", fromEnv.Location);
            Assert.StartsWith(envDir, fromEnv.Location);
            Assert.Single(Directory.GetFiles(envDir, "*.json"));
        }

        using (new EnvVarScope().Set("INSPECT_LOG_FORMAT", "eval").Set("INSPECT_EVAL_LOG_FORMAT", null))
        {
            var fromEnv = await Eval.RunAsync(QuizTask(), new EvalOptions { Model = new Model(QuizApi()), LogDir = Path.Combine(_tempDir, "env2"), MaxSamples = 1, LogFormat = LogFormat.Json });
            Assert.EndsWith(".json", fromEnv.Location);
        }
    }

    [Fact]
    public async Task runner_keeps_completed_samples_in_the_eval_log_when_cancelled()
    {
        using var env = new EnvVarScope().Set("INSPECT_LOG_FORMAT", null).Set("INSPECT_EVAL_LOG_FORMAT", null);
        var logDir = Path.Combine(_tempDir, "cancelled");
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var task = new EvalTask
        {
            Name = "cancel",
            Dataset = new MemoryDataset([new Sample("q1") { Target = "ok" }, new Sample("q2") { Target = "ok" }]),
            Scorers = [Scorers.Includes()],
            Solver = async (state, generate, ct) =>
            {
                if (Equals(state.SampleId, 2))
                {
                    gate.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                }

                return await generate(state, cancellationToken: ct);
            },
        };
        var run = Eval.RunAsync(task, new EvalOptions { Model = new Model(QuizApi()), LogDir = logDir, MaxSamples = 2 }, cts.Token);
        await gate.Task;
        await Task.Delay(200);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var file = Assert.Single(Directory.GetFiles(logDir, "*.eval"));
        var log = EvalLogFiles.ReadEvalLog(file);
        Assert.Equal(EvalStatus.Cancelled, log.Status);
        Assert.Contains(log.Samples!, sample => Equals(sample.Id, 1) && sample.Error is null);
    }

    // ---------------------------------------------------------------- helpers

    private static EvalTask QuizTask() => new()
    {
        Name = "quiz",
        Dataset = new MemoryDataset([new Sample("q1") { Target = "ok" }, new Sample("q2") { Target = "ok" }]),
        Scorers = [Scorers.Includes()],
    };

    private static ScriptedModelApi QuizApi() => new(Enumerable.Range(0, 4).Select(_ => ScriptedTurn.Text("ok", new ModelUsage(1, 1, 2))));

    private static EvalSpec Spec() => new()
    {
        Task = "tiny",
        TaskId = "DMp9V2YEhradPcNhv3qARw",
        RunId = "ZFZxv6hFpLhEzGXdbWDyJA",
        Created = Created,
        Model = "mockllm/model",
        Dataset = new EvalDataset { Samples = 2, SampleIds = [1, "b"] },
        Config = new EvalConfig { Epochs = 2 },
    };

    private static EvalSample Sample(object id, int epoch, string answer = "4") => new()
    {
        Id = id,
        Epoch = epoch,
        Input = "What is 2+2?",
        Target = "4",
        Messages = [new ChatMessageUser("What is 2+2?"), new ChatMessageAssistant(answer, model: "m", source: "generate")],
        Output = new ModelOutput { Model = "m", Choices = [new ChatCompletionChoice(new ChatMessageAssistant(answer, model: "m", source: "generate"), StopReason.Stop)] },
        Scores = new Dictionary<string, Score> { ["match"] = new Score("C") { Answer = answer } },
        Metadata = new Dictionary<string, object?> { ["difficulty"] = "easy" },
        Events = [new SpanBeginEvent("span1", "solver", "solver") { Timestamp = Created }, new SpanEndEvent("span1") { Timestamp = Created.AddSeconds(1) }],
        Uuid = $"uuid-{EvalLogFormat.IdText(id)}-{epoch}",
        StartedAt = Created,
        CompletedAt = Created.AddSeconds(1),
        TotalTime = 1.0,
        WorkingTime = 0.5,
    };

    /// <summary>A log written before <c>eval_id</c> existed gets a fresh id on every read; blank it for comparisons.</summary>
    private static EvalLog WithoutEvalId(EvalLog log) => log with { Eval = log.Eval with { EvalId = "" } };

    /// <summary>Canonical text of a JSON tree: sorted keys, numbers as doubles, Python's non-finite sentinels as constants.</summary>
    private static string Canon(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canon(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canon)) + "]",
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<double>(out var d) => NumberText(d),
        JsonValue v when v.TryGetValue<string>(out var s) => PythonJsonFormat.TryNonFinite(s, out var nf) ? NumberText(nf) : JsonSerializer.Serialize(s),
        _ => node.ToJsonString(),
    };

    private static string NumberText(double value) => double.IsNaN(value) ? "NaN" : value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Serializes with the log options and re-parses, tolerating the bare non-finite constants they emit.</summary>
    private static JsonNode? ToNode<T>(T value) => JsonNode.Parse(PythonJsonFormat.SanitizeNonFinite(JsonSerializer.Serialize(value, EvalLogWriter.Options)));

    /// <summary>The log as Python JSON with the location dropped and attachments resolved, so logs that differ only in condensation compare equal.</summary>
    private static string Canonical(EvalLog log) => EvalLogWriter.Serialize(EvalLogWriter.ResolveAttachments(log with { Location = null }, ResolveAttachments.Full));

    private static object PlainId(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var i) ? i : (string)node!;

    private static List<string> MemberNames(string path)
    {
        // the BCL reader validates the zip structure this port writes; python-written (zstandard) members need the port's own reader
        using var archive = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).ToList();
        Assert.Equal(names, EvalRecorder.ReadMemberNames(path));
        return names;
    }

    private static string ReadMemberText(string path, string name) => Encoding.UTF8.GetString(EvalRecorder.ReadMember(path, name));

    private static string FixturePath(params string[] parts) => FixtureRoot(["eval-logs", .. parts]);

    private static string FixtureRoot(params string[] parts)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine([directory, "fixtures", .. parts]);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException($"No fixtures/{string.Join('/', parts)} above {AppContext.BaseDirectory}");
    }

    private static string RunPython(string script)
    {
        var interpreter = Environment.GetEnvironmentVariable("INSPECT_PY");
        if (string.IsNullOrEmpty(interpreter))
        {
            throw new InvalidOperationException("INSPECT_PY is not set.");
        }

        var start = new ProcessStartInfo(interpreter, "-") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Python could not be started.");
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"python exited with {process.ExitCode}:\n{stderr}");
        }

        return stdout.Trim();
    }
}
