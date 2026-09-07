using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Log.Json;
using InspectAzureAI.Eval.Log.Tools;
using InspectAzureAI.Eval.Runner.Scoring;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

using Scorers = InspectAzureAI.Eval.Scorers.Scorers;

/// <summary>
/// Log tools: score edits (<c>log/_score.py</c>), tag/metadata edits and sample invalidation (<c>log/_edit.py</c>),
/// journal recovery (<c>log/_recover</c>), format conversion (<c>log/_convert.py</c>), viewer bundles
/// (<c>log/_bundle.py</c>) and the <c>inspect log</c> command helpers (<c>_cli/log.py</c>).
/// </summary>
public sealed class LogToolsTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 9, 5, 11, 52, 9, TimeSpan.Zero);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "inspect-log-tools-tests", Guid.NewGuid().ToString("N"));

    public LogToolsTests() => Directory.CreateDirectory(_tempDir);

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

    // ---------------------------------------------------------------- score edits

    [Fact]
    public void edit_score_adds_a_new_score_and_records_the_event_inside_the_scorers_span()
    {
        var log = MakeLog(Sample(1, 1));
        var edit = new ScoreEdit { Value = Edited<ScoreValue>.Set(4), Explanation = Edited<string>.Set("Span integration test") };

        var edited = EvalLogEdits.EditScore(log, 1, "grader", edit, recomputeMetrics: false);

        var sample = edited.Samples![0];
        var grader = sample.Scores!["grader"];
        Assert.Equal(4.0, grader.AsFloat());
        Assert.Equal("Span integration test", grader.Explanation);
        Assert.Null(grader.Answer);
        Assert.Null(grader.Metadata);
        Assert.Equal([edit], grader.History);
        Assert.Equal(["match", "grader"], sample.Scores!.Keys);

        var scorersSpan = sample.Events.OfType<SpanBeginEvent>().Last(e => e.Name == ScoreLogs.ScorersSpanName);
        var scoreEdit = Assert.Single(sample.Events.OfType<ScoreEditEvent>());
        Assert.Equal(scorersSpan.Id, scoreEdit.SpanId);
        Assert.Equal("grader", scoreEdit.ScoreName);
        Assert.Same(edit, scoreEdit.Edit);
        var editIndex = sample.Events.ToList().IndexOf(scoreEdit);
        var endIndex = sample.Events.ToList().FindIndex(e => e is SpanEndEvent end && end.Id == scorersSpan.Id);
        Assert.Equal(endIndex - 1, editIndex);
        Assert.Equal(log.Samples![0].Events.Count + 1, sample.Events.Count);
        // the original log is untouched
        Assert.DoesNotContain("grader", log.Samples![0].Scores!.Keys);
    }

    [Fact]
    public void edit_score_without_a_scorers_span_appends_the_event()
    {
        var log = MakeLog(Sample(1, 1) with { Events = [new SpanBeginEvent("s1", "solver", "solver"), new SpanEndEvent("s1")] });
        var edited = EvalLogEdits.EditScore(log, 1, "match", new ScoreEdit { Value = Edited<ScoreValue>.Set("I") }, recomputeMetrics: false);
        var last = Assert.IsType<ScoreEditEvent>(edited.Samples![0].Events[^1]);
        Assert.Null(last.SpanId);
        Assert.Equal("score_edit", last.Event);
    }

    [Fact]
    public void edit_score_requires_a_value_for_a_new_score_and_rejects_null_values()
    {
        var log = MakeLog(Sample(1, 1));
        var ex = Assert.Throws<ArgumentException>(() => EvalLogEdits.EditScore(log, 1, "grader", new ScoreEdit { Explanation = Edited<string>.Set("x") }, recomputeMetrics: false));
        Assert.Contains("Cannot add new score 'grader' without providing a value", ex.Message);
        Assert.Throws<ArgumentException>(() => EvalLogEdits.EditScore(log, 1, "match", new ScoreEdit { Value = Edited<ScoreValue>.Set(null) }, recomputeMetrics: false));
    }

    [Fact]
    public void edit_score_prepends_the_original_to_history_replaces_metadata_and_drops_the_legacy_reason()
    {
        var original = new Score("C") { Answer = "C", Metadata = new Dictionary<string, object?> { ["a"] = 1, [EvalLogEdits.LegacyUnscoredReasonKey] = "old" } };
        var log = MakeLog(Sample(1, 1) with { Scores = new Dictionary<string, Score> { ["match"] = original } });

        // metadata replaces (not merges); an unrelated edit keeps the legacy key
        var first = EvalLogEdits.EditScore(log, 1, "match", new ScoreEdit { Metadata = Edited<IReadOnlyDictionary<string, object?>>.Set(new Dictionary<string, object?> { ["b"] = 2, [EvalLogEdits.LegacyUnscoredReasonKey] = "old" }) }, recomputeMetrics: false);
        var score = first.Samples![0].Scores!["match"];
        Assert.Equal(["b", EvalLogEdits.LegacyUnscoredReasonKey], score.Metadata!.Keys);
        Assert.Equal("C", score.Text);
        Assert.Equal(2, score.History.Count);
        var seed = score.History[0];
        Assert.True(seed.Value.IsSet);
        Assert.Equal("C", seed.Value.Value!.Text);
        Assert.Equal("C", seed.Answer.Value);
        Assert.True(seed.Explanation.IsSet);
        Assert.Null(seed.Explanation.Value);
        Assert.Equal(original.Metadata, seed.Metadata.Value);

        // an explicit reason (here: cleared) supersedes the legacy key, and the history grows without a second seed
        var second = EvalLogEdits.EditScore(first, 1, "match", new ScoreEdit { Value = Edited<ScoreValue>.Set("I"), Reason = Edited<string>.Set(null) }, recomputeMetrics: false);
        score = second.Samples![0].Scores!["match"];
        Assert.Equal("I", score.Text);
        Assert.Null(score.Reason);
        Assert.Equal(["b"], score.Metadata!.Keys);
        Assert.Equal(3, score.History.Count);

        // a new score with a reason drops the legacy key from the supplied metadata too
        var third = EvalLogEdits.EditScore(second, 1, "fresh", new ScoreEdit
        {
            Value = Edited<ScoreValue>.Set(1),
            Reason = Edited<string>.Set("reviewed"),
            Metadata = Edited<IReadOnlyDictionary<string, object?>>.Set(new Dictionary<string, object?> { [EvalLogEdits.LegacyUnscoredReasonKey] = "x", ["c"] = 3 }),
        }, recomputeMetrics: false);
        Assert.Equal(["c"], third.Samples![0].Scores!["fresh"].Metadata!.Keys);
        Assert.Equal("reviewed", third.Samples![0].Scores!["fresh"].Reason);
        Assert.Equal(3, third.Samples![0].Events.OfType<ScoreEditEvent>().Count());
    }

    [Fact]
    public void edit_score_finds_samples_like_python()
    {
        var log = MakeLog(Sample(1, 1), Sample(1, 2), Sample("1", 1, "blue"));
        var edit = new ScoreEdit { Value = Edited<ScoreValue>.Set("I") };
        Assert.Contains("Multiple samples found with id 1", Assert.Throws<ArgumentException>(() => EvalLogEdits.EditScore(log, 1, "match", edit, false)).Message);
        Assert.Contains("id 2 not found", Assert.Throws<ArgumentException>(() => EvalLogEdits.EditScore(log, 2, "match", edit, false)).Message);
        Assert.Contains("id 1 and epoch 3 not found", Assert.Throws<ArgumentException>(() => EvalLogEdits.EditScore(log, 1, "match", edit, false, epoch: 3)).Message);
        Assert.Contains("no samples", Assert.Throws<ArgumentException>(() => EvalLogEdits.EditScore(log with { Samples = null }, 1, "match", edit, false)).Message);

        var byEpoch = EvalLogEdits.EditScore(log, 1, "match", edit, false, epoch: 2);
        Assert.Equal("I", byEpoch.Samples![1].Scores!["match"].Text);
        Assert.Equal("C", byEpoch.Samples![0].Scores!["match"].Text);

        // the string "1" is a different sample from the int 1
        var byString = EvalLogEdits.EditScore(log, "1", "match", edit, false);
        Assert.Equal("I", byString.Samples![2].Scores!["match"].Text);
        Assert.Equal("C", byString.Samples![0].Scores!["match"].Text);
        Assert.True(EvalLogEdits.IdEquals(1, 1L));
        Assert.False(EvalLogEdits.IdEquals(1, "1"));
    }

    [Fact]
    public void edit_score_recomputes_metrics_from_the_header_scorers()
    {
        var log = MakeLog(Sample(1, 1), Sample(2, 1)) with
        {
            Eval = Spec() with { Scorers = [new EvalScorer("match") { Metrics = new JsonArray(new JsonObject { ["name"] = "accuracy", ["options"] = new JsonObject() }, new JsonObject { ["name"] = "stderr", ["options"] = new JsonObject() }) }] },
        };
        var edited = EvalLogEdits.EditScore(log, 2, "match", new ScoreEdit { Value = Edited<ScoreValue>.Set("I") });
        var score = Assert.Single(edited.Results!.Scores);
        Assert.Equal("match", score.Name);
        Assert.Equal(0.5, score.Metrics["accuracy"].Value);
        Assert.Equal(["accuracy", "stderr"], score.Metrics.Keys);
        Assert.Equal(2, edited.Results.TotalSamples);
        Assert.NotNull(edited.Reductions);

        // a header metric this port cannot re-create is an error, not a silently different metric; explicit scorers bypass the header
        var custom = log with { Eval = log.Eval with { Scorers = [new EvalScorer("match") { Metrics = new JsonArray(new JsonObject { ["name"] = "my_metric", ["options"] = new JsonObject() }) }] } };
        Assert.Throws<NotSupportedException>(() => EvalLogEdits.EditScore(custom, 2, "match", new ScoreEdit { Value = Edited<ScoreValue>.Set("I") }));
        var explicitScorers = EvalLogEdits.EditScore(custom, 2, "match", new ScoreEdit { Value = Edited<ScoreValue>.Set("I") }, scorers: [Scorers.Custom("match", (_, _, _) => throw new InvalidOperationException(), Metrics.Mean())]);
        Assert.Equal(["mean"], Assert.Single(explicitScorers.Results!.Scores).Metrics.Keys);
        // scores no header scorer accounts for get accuracy and stderr, as Python's ScorerInfo.from_name
        var unknown = EvalLogEdits.EditScore(log with { Eval = log.Eval with { Scorers = null } }, 2, "match", new ScoreEdit { Value = Edited<ScoreValue>.Set("I") });
        Assert.Equal(["accuracy", "stderr"], Assert.Single(unknown.Results!.Scores).Metrics.Keys);
    }

    [PythonFact]
    public void score_edit_matches_python_edit_score()
    {
        var log = MakeLog(Sample(1, 1), Sample(2, 1)) with
        {
            Eval = Spec() with { Scorers = [new EvalScorer("match") { Metrics = new JsonArray(new JsonObject { ["name"] = "accuracy", ["options"] = new JsonObject() }, new JsonObject { ["name"] = "stderr", ["options"] = new JsonObject() }) }] },
        };
        var input = Path.Combine(_tempDir, "input.json");
        EvalLogWriter.Write(log, input);
        var output = Path.Combine(_tempDir, "python-edited.json");
        PythonReference.Run($$"""
            from inspect_ai.log._file import read_eval_log, write_eval_log
            from inspect_ai.log._score import edit_score
            from inspect_ai.scorer._metric import ScoreEdit
            log = read_eval_log(r'''{{input}}''')
            edit_score(log, 2, "match", ScoreEdit(value="I", explanation="edited by python", metadata={"reviewer": "alice"}))
            edit_score(log, 1, "grader", ScoreEdit(value=0.5, answer="half"))
            write_eval_log(log, r'''{{output}}''')
            """);
        var python = EvalLogFiles.ReadEvalLog(output);

        var edited = EvalLogEdits.EditScore(log, 2, "match", new ScoreEdit
        {
            Value = Edited<ScoreValue>.Set("I"),
            Explanation = Edited<string>.Set("edited by python"),
            Metadata = Edited<IReadOnlyDictionary<string, object?>>.Set(new Dictionary<string, object?> { ["reviewer"] = "alice" }),
        });
        edited = EvalLogEdits.EditScore(edited, 1, "grader", new ScoreEdit { Value = Edited<ScoreValue>.Set(0.5), Answer = Edited<string>.Set("half") });
        // compare the persisted form: a cleared field is written as absent on both sides and reads back as unchanged
        var ours = Path.Combine(_tempDir, "our-edited.json");
        EvalLogWriter.Write(edited, ours);
        edited = EvalLogFiles.ReadEvalLog(ours);

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(Canon(ToNode(python.Samples![i].Scores)), Canon(ToNode(edited.Samples![i].Scores)));
            var pythonEvents = python.Samples![i].Events.Select(EventShape).ToList();
            var ourEvents = edited.Samples![i].Events.Select(EventShape).ToList();
            Assert.Equal(pythonEvents, ourEvents);
        }

        Assert.Equal(Canon(ToNode(python.Results!.Scores)), Canon(ToNode(edited.Results!.Scores)));
        Assert.Equal(Canon(ToNode(python.Reductions)), Canon(ToNode(edited.Reductions)));
    }

    // ---------------------------------------------------------------- tags, metadata and invalidation

    [Fact]
    public async Task tag_and_metadata_edits_round_trip_through_both_formats()
    {
        var log = MakeLog(Sample(1, 1)) with { Eval = Spec() with { Tags = ["zeta", "alpha"], Metadata = new Dictionary<string, object?> { ["k"] = "v" } } };
        var provenance = new ProvenanceData("tester") { Reason = "metadata edit test", Timestamp = Created };
        var edited = EvalLogEdits.EditEvalLog(
            log,
            [new TagsEdit { TagsAdd = ["qa_reviewed"], TagsRemove = ["zeta"] }, new MetadataEdit { MetadataSet = new Dictionary<string, object?> { ["null_key"] = null, ["ok_key"] = "v" }, MetadataRemove = ["k"] }],
            provenance);
        Assert.Equal(["alpha", "qa_reviewed"], edited.Tags);
        Assert.Equal(["null_key", "ok_key"], edited.Metadata.Keys);
        Assert.Null(edited.Metadata["null_key"]);
        Assert.Same(log, EvalLogEdits.EditEvalLog(edited, [new MetadataEdit { MetadataSet = new Dictionary<string, object?> { ["null_key"] = null } }], provenance) == edited ? log : log);

        foreach (var extension in new[] { ".eval", ".json" })
        {
            var path = Path.Combine(_tempDir, "edited" + extension);
            await EvalLogFiles.WriteEvalLogAsync(edited, path);
            var reread = EvalLogFiles.ReadEvalLog(path);
            Assert.Equal(["alpha", "qa_reviewed"], reread.Tags);
            Assert.True(reread.Metadata.ContainsKey("null_key"));
            Assert.Null(reread.Metadata["null_key"]);
            Assert.Equal("v", reread.Metadata["ok_key"]);
            var update = Assert.Single(reread.LogUpdates!);
            Assert.Equal("tester", update.Provenance.Author);
            var last = Assert.IsType<MetadataEdit>(update.Edits[^1]);
            Assert.Equal(["null_key", "ok_key"], last.MetadataSet.Keys);
            Assert.Equal(["k"], last.MetadataRemove);
            Assert.Equal(EvalLogEdits.RecomputeTagsAndMetadata(reread).Tags, reread.Tags);
        }
    }

    [Fact]
    public void invalidate_and_uninvalidate_samples_follow_python()
    {
        var log = MakeLog(Enumerable.Range(0, 10).Select(i => Sample("test_sample", i) with { Uuid = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray());
        var provenance = new ProvenanceData("test_person") { Reason = "test_reason" };

        Assert.Same(log, EvalLogEdits.InvalidateSamples(log, [], provenance));
        var one = EvalLogEdits.InvalidateSamples(log, ["1"], provenance);
        Assert.True(one.Invalidated);
        Assert.Same(provenance, one.Samples![0].Invalidation);
        Assert.All(one.Samples!.Skip(1), sample => Assert.Null(sample.Invalidation));
        var all = EvalLogEdits.InvalidateAllSamples(log, provenance);
        Assert.All(all.Samples!, sample => Assert.Equal("test_reason", sample.Invalidation!.Reason));
        var byUuid = EvalLogEdits.InvalidateSamples(log, Enumerable.Range(1, 10).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), provenance);
        Assert.All(byUuid.Samples!, sample => Assert.NotNull(sample.Invalidation));
        Assert.Contains("['notexists'] not found", Assert.Throws<ArgumentException>(() => EvalLogEdits.InvalidateSamples(log, ["notexists"], provenance)).Message);
        Assert.False(log.Invalidated);

        var invalidated = EvalLogEdits.InvalidateSamples(log, ["1", "4"], provenance);
        Assert.Same(invalidated, EvalLogEdits.UninvalidateSamples(invalidated, []));
        var partial = EvalLogEdits.UninvalidateSamples(invalidated, ["1"]);
        Assert.True(partial.Invalidated);
        Assert.Null(partial.Samples![0].Invalidation);
        Assert.NotNull(partial.Samples![3].Invalidation);
        Assert.False(EvalLogEdits.UninvalidateSamples(invalidated, ["1", "4"]).Invalidated);
        Assert.False(EvalLogEdits.UninvalidateAllSamples(invalidated).Invalidated);
        Assert.Throws<ArgumentException>(() => EvalLogEdits.UninvalidateSamples(invalidated, ["notexists"]));
        // an already invalidated sample keeps its provenance
        var again = EvalLogEdits.InvalidateSamples(invalidated, ["1"], new ProvenanceData("other"));
        Assert.Equal("test_person", again.Samples![0].Invalidation!.Author);
    }

    // ---------------------------------------------------------------- recovery

    [Fact]
    public async Task recover_eval_log_rebuilds_a_complete_log_from_the_journal_of_a_write_stopped_midway()
    {
        var spec = Spec();
        var crashedPath = await WriteCrashedLogAsync(spec, Sample(1, 1), Sample("b", 1, "blue") with { Error = new EvalError("boom", "trace") });
        Assert.DoesNotContain(EvalLogFormat.HeaderJson, EvalRecorder.ReadMemberNames(crashedPath));
        Assert.Equal(EvalStatus.Started, EvalLogFiles.ReadEvalLog(crashedPath, headerOnly: true).Status);

        var crashed = await EvalLogRecovery.ReadCrashedEvalLogAsync(crashedPath);
        Assert.Equal(["samples/1_epoch_1.json", "samples/b_epoch_1.json"], crashed.SampleEntries);
        Assert.Equal(2, crashed.Summaries.Count);
        Assert.Equal(EvalLog.SchemaVersion, crashed.Version);
        Assert.Equal("tiny", crashed.Eval.Task);
        Assert.Single(crashed.ConfigUpdates);

        var result = await EvalLogRecovery.RecoverEvalLogAsync(crashedPath);
        Assert.Equal(2, result.SampleCount);
        Assert.Equal(1, result.FailedCount);
        var recoveredPath = EvalLogRecovery.DefaultOutputPath(crashedPath);
        Assert.True(File.Exists(recoveredPath));
        Assert.Equal(recoveredPath, result.Log.Location);
        Assert.Equal(EvalStatus.Error, result.Log.Status);
        Assert.Equal("Eval recovered from crash", result.Log.Error!.Message);
        Assert.Contains("journal", result.Log.Error.Traceback);

        var recovered = EvalLogFiles.ReadEvalLog(recoveredPath);
        Assert.Contains(EvalLogFormat.HeaderJson, EvalRecorder.ReadMemberNames(recoveredPath));
        Assert.Equal(EvalStatus.Error, recovered.Status);
        Assert.Equal([1, "b"], recovered.Samples!.Select(sample => sample.Id).ToArray());
        Assert.Equal("boom", recovered.Samples![1].Error!.Message);
        Assert.Equal("tiny", recovered.Eval.Task);
        Assert.Equal(spec.RunId, recovered.Eval.RunId);
        Assert.Equal(2, recovered.Results!.TotalSamples);
        Assert.Equal(1, recovered.Results.CompletedSamples);
        var score = Assert.Single(recovered.Results.Scores);
        Assert.Equal("match", score.Name);
        Assert.Equal(1.0, score.Metrics["accuracy"].Value);
        Assert.Equal(["accuracy", "stderr"], score.Metrics.Keys);
        Assert.Equal("match", Assert.Single(recovered.Reductions!).Scorer);
        Assert.Equal(new ModelUsage(2, 2, 4), recovered.Stats.ModelUsage["m"]);
        Assert.Equal(new ModelUsage(1, 1, 2), recovered.Stats.RoleUsage["grader"]);
        Assert.Equal(Created.AddSeconds(-30), recovered.Stats.StartedAt);
        Assert.True(recovered.Stats.CompletedAt > Created);
        var update = Assert.Single(recovered.ConfigUpdates!);
        Assert.Equal("task", update.Scope);
        // the crashed file is untouched
        Assert.DoesNotContain(EvalLogFormat.HeaderJson, EvalRecorder.ReadMemberNames(crashedPath));
    }

    [Fact]
    public async Task recovery_from_an_empty_journal_and_with_no_scores_still_finishes_the_log()
    {
        var crashedPath = await WriteCrashedLogAsync(Spec());
        var result = await EvalLogRecovery.RecoverEvalLogAsync(crashedPath, Path.Combine(_tempDir, "out", "empty.eval"));
        Assert.Equal(0, result.SampleCount);
        var recovered = EvalLogFiles.ReadEvalLog(Path.Combine(_tempDir, "out", "empty.eval"));
        Assert.Equal(EvalStatus.Error, recovered.Status);
        Assert.Empty(recovered.Samples!);
        Assert.Equal(0, recovered.Results!.TotalSamples);
        Assert.Empty(recovered.Results.Scores);
        Assert.Null(recovered.Reductions);
        Assert.Equal(Created, recovered.Stats.StartedAt);
    }

    [Fact]
    public async Task recovery_warns_and_omits_results_when_the_header_metrics_cannot_be_recomputed()
    {
        var spec = Spec() with { Scorers = [new EvalScorer("match") { Metrics = new JsonArray(new JsonObject { ["name"] = "my_metric", ["options"] = new JsonObject() }) }] };
        var crashedPath = await WriteCrashedLogAsync(spec, Sample(1, 1));
        ProviderLogger.Reset();
        var result = await EvalLogRecovery.RecoverEvalLogAsync(crashedPath);
        Assert.Null(result.Log.Results);
        Assert.Contains(ProviderLogger.Warnings, warning => warning.Contains("Unable to recompute metrics for recovered log", StringComparison.Ordinal));
        Assert.Single(EvalLogFiles.ReadEvalLog(EvalLogRecovery.DefaultOutputPath(crashedPath)).Samples!);
    }

    [Fact]
    public async Task recovery_refuses_complete_logs_and_non_journals()
    {
        var complete = Path.Combine(_tempDir, "complete.eval");
        await EvalLogFiles.WriteEvalLogAsync(MakeLog(Sample(1, 1)), complete);
        var notCrashed = await Assert.ThrowsAsync<RecoveryNotAvailableException>(() => EvalLogRecovery.RecoverEvalLogAsync(complete));
        Assert.Contains("not crashed", notCrashed.Message);

        var noStart = Path.Combine(_tempDir, "nostart.eval");
        using (var zip = ZipFile.Open(noStart, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("dummy.txt").Open());
            writer.Write("not a valid eval log");
        }

        var invalid = await Assert.ThrowsAsync<RecoveryNotAvailableException>(() => EvalLogRecovery.ReadCrashedEvalLogAsync(noStart));
        Assert.Contains("invalid", invalid.Message);

        var json = Path.Combine(_tempDir, "plain.json");
        EvalLogWriter.Write(MakeLog(Sample(1, 1)), json);
        await Assert.ThrowsAsync<InvalidDataException>(() => EvalLogRecovery.RecoverEvalLogAsync(json));

        Assert.Equal("/tmp/mylog-recovered.eval", EvalLogRecovery.DefaultOutputPath("/tmp/mylog.eval"));
        Assert.Equal("logs/test-recovered", EvalLogRecovery.DefaultOutputPath("logs/test"));
    }

    [Fact]
    public async Task recovery_guards_against_a_successful_log_of_the_task_and_can_overwrite_in_place()
    {
        var spec = Spec();
        var crashedPath = await WriteCrashedLogAsync(spec, Sample(1, 1));
        var successPath = Path.Combine(_tempDir, "success.eval");
        await EvalLogFiles.WriteEvalLogAsync(MakeLog(Sample(1, 1)) with { Status = EvalStatus.Success }, successPath);
        var refused = await Assert.ThrowsAsync<RecoveryNotAvailableException>(() => EvalLogRecovery.RecoverEvalLogAsync(crashedPath));
        Assert.Contains("A successful log for task 'tiny' already exists", refused.Message);
        Assert.False(File.Exists(EvalLogRecovery.DefaultOutputPath(crashedPath)));

        var elsewhere = Path.Combine(_tempDir, "elsewhere", "recovered.eval");
        var result = await EvalLogRecovery.RecoverEvalLogAsync(crashedPath, elsewhere);
        Assert.Equal(elsewhere, result.Log.Location);
        Assert.Equal(1, result.SampleCount);

        File.Delete(successPath);
        var overwritten = await EvalLogRecovery.RecoverEvalLogAsync(crashedPath, overwrite: true);
        Assert.Equal(crashedPath, overwritten.Log.Location);
        Assert.Equal(EvalStatus.Error, overwritten.Log.Status);
        Assert.False(File.Exists(EvalLogRecovery.DefaultOutputPath(crashedPath)));
        Assert.Equal(EvalStatus.Error, EvalLogFiles.ReadEvalLog(crashedPath).Status);
        Assert.Single(overwritten.Log.Samples!);
    }

    [Fact]
    public async Task recoverable_eval_logs_lists_crashed_journals_without_a_recovered_sibling()
    {
        var logDir = Path.Combine(_tempDir, "logs");
        var spec = Spec();
        var crashed = await WriteCrashedLogAsync(logDir, spec, Sample(1, 1), Sample("b", 1));
        var alreadyRecovered = await WriteCrashedLogAsync(logDir, spec with { TaskId = "other", Task = "other" }, Sample(1, 1));
        await EvalLogRecovery.RecoverEvalLogAsync(alreadyRecovered);
        await EvalLogFiles.WriteEvalLogAsync(MakeLog(Sample(1, 1)) with { Status = EvalStatus.Success, Eval = spec with { TaskId = "done", Task = "done" } }, Path.Combine(logDir, "done.eval"));
        EvalLogWriter.Write(MakeLog(Sample(1, 1)) with { Status = EvalStatus.Started }, Path.Combine(logDir, "2026-09-05T11-52-09-00-00_started_json.json"));

        var recoverable = await EvalLogRecovery.RecoverableEvalLogsAsync(logDir);
        var entry = Assert.Single(recoverable);
        Assert.Equal(crashed, entry.Log.Name);
        Assert.Equal(2, entry.FlushedSamples);
        Assert.Equal(4, entry.TotalSamples);
        Assert.Equal(0, entry.CompletedSamples);
        Assert.Equal(0, entry.InProgressSamples);
        Assert.Equal(EvalLogRecovery.JournalSource, entry.Source);

        var json = JsonNode.Parse(await LogCommands.RecoverableLogsJsonAsync(logDir))!.AsArray();
        var item = Assert.Single(json)!.AsObject();
        Assert.Equal(["name", "task", "total_samples", "flushed_samples", "completed_samples", "in_progress_samples", "source"], item.Select(pair => pair.Key));
        Assert.Equal("tiny", (string)item["task"]!);
        Assert.Equal("journal", (string)item["source"]!);

        using var env = new EnvVarScope().Set("INSPECT_LOG_DIR", logDir);
        Assert.Single(await EvalLogRecovery.RecoverableEvalLogsAsync());
    }

    [Fact]
    public async Task recovery_honours_cancellation_and_leaves_no_output()
    {
        var crashedPath = await WriteCrashedLogAsync(Spec(), Sample(1, 1));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvalLogRecovery.RecoverEvalLogAsync(crashedPath, cancellationToken: cts.Token));
        var crashed = await EvalLogRecovery.ReadCrashedEvalLogAsync(crashedPath);
        var output = Path.Combine(_tempDir, "cancelled.eval");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvalLogRecovery.WriteRecoveredEvalLogAsync(crashed, output, cancellationToken: cts.Token));
        Assert.False(File.Exists(output));
    }

    // ---------------------------------------------------------------- conversion

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, 1)]
    public async Task convert_json_to_eval_and_back_gives_the_same_log(bool stream, int? concurrency)
    {
        var log = MakeLog(Sample(1, 1), Sample("b", 1, "blue"), Sample(1, 2)) with
        {
            Status = EvalStatus.Error,
            Error = new EvalError("failed", "trace"),
            Results = new EvalResults { TotalSamples = 3, CompletedSamples = 3, Scores = [new EvalScore("match", "match") { Metrics = new Dictionary<string, EvalMetric> { ["accuracy"] = new("accuracy", 1.0) } }] },
            Invalidated = true,
            LogUpdates = [new LogUpdate(new ProvenanceData("alice") { Timestamp = Created }) { Edits = [new TagsEdit { TagsAdd = ["qa"] }] }],
        };
        var source = Path.Combine(_tempDir, "2026-09-05T11-52-09-00-00_tiny_DMp9V2YEhradPcNhv3qARw.json");
        EvalLogWriter.Write(log, source);

        var evalDir = Path.Combine(_tempDir, "as-eval");
        await LogConversion.ConvertEvalLogsAsync(source, LogFormat.Eval, evalDir, stream: stream, streamConcurrency: concurrency);
        var evalFile = Path.Combine(evalDir, "2026-09-05T11-52-09-00-00_tiny_DMp9V2YEhradPcNhv3qARw.eval");
        Assert.True(File.Exists(evalFile));
        var jsonDir = Path.Combine(_tempDir, "as-json");
        await LogConversion.ConvertEvalLogsAsync(evalFile, LogFormat.Json, jsonDir, stream: stream, streamConcurrency: concurrency);
        var jsonFile = Path.Combine(jsonDir, "2026-09-05T11-52-09-00-00_tiny_DMp9V2YEhradPcNhv3qARw.json");

        var original = EvalLogFiles.ReadEvalLog(source);
        var converted = EvalLogFiles.ReadEvalLog(jsonFile);
        Assert.Equal(Canonical(original), Canonical(converted));
        Assert.Equal(["qa"], converted.Tags);
        Assert.Equal("failed", converted.Error!.Message);
        Assert.Equal(3, converted.Samples!.Count);
        Assert.Equal(Canonical(original), Canonical(EvalLogFiles.ReadEvalLog(evalFile)));
    }

    [Theory]
    [InlineData(LogFormat.Eval, 1)]
    [InlineData(LogFormat.Eval, 2)]
    [InlineData(LogFormat.Eval, null)]
    [InlineData(LogFormat.Json, 1)]
    public async Task stream_conversion_in_place_preserves_every_sample(LogFormat format, int? concurrency)
    {
        var path = Path.Combine(_tempDir, "original" + format.Extension());
        await EvalLogFiles.WriteEvalLogAsync(MakeLog(Sample(1, 1), Sample(2, 1), Sample(3, 1)), path);
        var before = Canonical(EvalLogFiles.ReadEvalLog(path));

        await LogConversion.ConvertEvalLogsAsync(path, format, _tempDir, overwrite: true, stream: true, streamConcurrency: concurrency);

        Assert.Equal(before, Canonical(EvalLogFiles.ReadEvalLog(path)));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(_tempDir)));
    }

    [Fact]
    public async Task stream_conversion_failure_preserves_the_original_after_a_flush()
    {
        var path = Path.Combine(_tempDir, "original.eval");
        await EvalLogFiles.WriteEvalLogAsync(MakeLog(Sample(1, 1), Sample(2, 1), Sample(3, 1)), path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("samples/2_epoch_1.json")!.Delete();
        }

        var before = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => LogConversion.ConvertEvalLogsAsync(
            path, LogFormat.Eval, _tempDir, overwrite: true, stream: true, streamConcurrency: 1));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(_tempDir)));
    }

    [Fact]
    public async Task convert_directory_preserves_relative_paths_and_enforces_overwrite()
    {
        var logDir = Path.Combine(_tempDir, "logs");
        var nested = Path.Combine(logDir, "sub");
        Directory.CreateDirectory(nested);
        EvalLogWriter.Write(MakeLog(Sample(1, 1)), Path.Combine(logDir, "2026-09-05T11-52-09-00-00_a_id.json"));
        await EvalLogFiles.WriteEvalLogAsync(MakeLog(Sample(1, 1)), Path.Combine(nested, "custom.eval"));
        File.WriteAllText(Path.Combine(nested, "notes.txt"), "ignored");

        var outputDir = Path.Combine(_tempDir, "out") + Path.DirectorySeparatorChar;
        await LogConversion.ConvertEvalLogsAsync(logDir, LogFormat.Eval, outputDir);
        Assert.True(File.Exists(Path.Combine(_tempDir, "out", "2026-09-05T11-52-09-00-00_a_id.eval")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "out", "sub", "custom.eval")));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_tempDir, "out"), "*", SearchOption.AllDirectories).Length);

        var exists = await Assert.ThrowsAsync<IOException>(() => LogConversion.ConvertEvalLogsAsync(logDir, LogFormat.Eval, outputDir));
        Assert.Contains("already exists", exists.Message);
        await LogConversion.ConvertEvalLogsAsync(logDir, LogFormat.Eval, outputDir, overwrite: true);
        Assert.Contains("does not exist", (await Assert.ThrowsAsync<PrerequisiteError>(() => LogConversion.ConvertEvalLogsAsync(Path.Combine(_tempDir, "missing"), LogFormat.Eval, outputDir))).Message);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LogConversion.ConvertEvalLogsAsync(logDir, LogFormat.Eval, outputDir, overwrite: true, stream: true, streamConcurrency: 0));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LogConversion.ConvertEvalLogsAsync(Path.Combine(nested, "custom.eval"), LogFormat.Json, Path.Combine(_tempDir, "cancelled"), stream: true, cancellationToken: cts.Token));
        Assert.False(File.Exists(Path.Combine(_tempDir, "cancelled", "custom.json")));
    }

    // ---------------------------------------------------------------- bundling

    [Fact]
    public async Task bundle_log_dir_writes_the_viewer_the_logs_the_listing_and_robots()
    {
        var dist = FakeDist();
        var logDir = Path.Combine(_tempDir, "logs");
        var (first, second) = await WriteBundleLogsAsync(logDir);
        var outputDir = Path.Combine(_tempDir, "view");

        await LogBundle.BundleLogDirAsync(logDir, outputDir, viewerDistDir: dist);

        var index = File.ReadAllText(Path.Combine(outputDir, "index.html"));
        var expectedContext = PythonJson.Dumps(new JsonObject { ["log_dir"] = "logs", ["abs_log_dir"] = Path.GetFullPath(logDir) });
        Assert.Contains($"  <script id=\"log_dir_context\" type=\"application/json\">{expectedContext}</script>\n  </head>", index);
        Assert.Equal("User-agent: *\nDisallow: /\n", File.ReadAllText(Path.Combine(outputDir, "robots.txt")));
        Assert.True(File.Exists(Path.Combine(outputDir, "assets", "index.js")));
        Assert.True(File.Exists(Path.Combine(outputDir, "assets", "index.css")));
        Assert.True(File.Exists(Path.Combine(outputDir, "logs", Path.GetFileName(first))));
        Assert.True(File.Exists(Path.Combine(outputDir, "logs", "nested", Path.GetFileName(second))));
        Assert.True(File.Exists(Path.Combine(outputDir, "logs", "nested", "eval-set.json")));
        Assert.False(File.Exists(Path.Combine(outputDir, "logs", "ignored.txt")));

        var listing = JsonNode.Parse(File.ReadAllText(Path.Combine(outputDir, "logs", "listing.json")))!.AsObject();
        Assert.Equal(new[] { Path.GetFileName(first), "nested/" + Path.GetFileName(second) }.Order(StringComparer.Ordinal), listing.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        var overview = listing[Path.GetFileName(first)]!.AsObject();
        Assert.Equal(["eval_id", "run_id", "task", "task_id", "task_version", "version", "status", "invalidated", "model", "model_roles", "started_at", "completed_at", "primary_metric"], overview.Select(pair => pair.Key));
        Assert.Equal("tiny", (string)overview["task"]!);
        Assert.Equal(0, (int)overview["task_version"]!);
        Assert.Equal("success", (string)overview["status"]!);
        Assert.Equal("mockllm/model", (string)overview["model"]!);
        Assert.Equal("grader-a,grader-b", (string)overview["model_roles"]!["grader"]!);
        Assert.Equal("accuracy", (string)overview["primary_metric"]!["name"]!);
        Assert.Equal(0.5, (double)overview["primary_metric"]!["value"]!);
        Assert.Equal("2026-09-05T11:52:09+00:00", (string)overview["started_at"]!);
        var nestedOverview = listing["nested/" + Path.GetFileName(second)]!.AsObject();
        Assert.Equal("error", (string)nestedOverview["status"]!);
        Assert.Equal("crashed", (string)nestedOverview["error"]!["message"]!);
        Assert.Equal("", (string)nestedOverview["completed_at"]!);
        Assert.Null(nestedOverview["primary_metric"]);
        Assert.Null(nestedOverview["model_roles"]);
        Assert.Equal("v1", (string)nestedOverview["task_version"]!);

        // overwrite replaces the previous bundle wholesale
        File.WriteAllText(Path.Combine(outputDir, "stale.txt"), "old");
        await LogBundle.BundleLogDirAsync(logDir, outputDir, overwrite: true, viewerDistDir: dist);
        Assert.False(File.Exists(Path.Combine(outputDir, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(outputDir, "index.html")));
    }

    [Fact]
    public async Task bundle_log_dir_validates_its_inputs_like_python()
    {
        var dist = FakeDist();
        var logDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(logDir);
        using var env = new EnvVarScope().Set(LogBundle.OutputDirEnvironmentVariable, null).Set(ViewerAssets.DistDirEnvironmentVariable, null).Set("INSPECT_LOG_DIR", logDir);

        Assert.Contains("must provide an 'output_dir'", (await Assert.ThrowsAsync<PrerequisiteError>(() => LogBundle.BundleLogDirAsync(logDir, viewerDistDir: dist))).Message);
        Assert.Contains("cannot be a subdirectory", (await Assert.ThrowsAsync<PrerequisiteError>(() => LogBundle.BundleLogDirAsync(logDir, Path.Combine(logDir, "output"), viewerDistDir: dist))).Message);
        await Assert.ThrowsAsync<NotSupportedException>(() => LogBundle.BundleLogDirAsync(logDir, "hf/user/space", viewerDistDir: dist));
        var existing = Path.Combine(_tempDir, "existing");
        Directory.CreateDirectory(existing);
        Assert.Contains("already exists", (await Assert.ThrowsAsync<PrerequisiteError>(() => LogBundle.BundleLogDirAsync(logDir, existing, viewerDistDir: dist))).Message);
        Assert.Contains("not configured", (await Assert.ThrowsAsync<PrerequisiteError>(() => LogBundle.BundleLogDirAsync(logDir, Path.Combine(_tempDir, "out")))).Message);
        Assert.Contains("index.html", (await Assert.ThrowsAsync<PrerequisiteError>(() => LogBundle.BundleLogDirAsync(logDir, Path.Combine(_tempDir, "out"), viewerDistDir: _tempDir))).Message);
        Assert.Contains("doesn't contain any log files", (await Assert.ThrowsAsync<PrerequisiteError>(() => LogBundle.BundleLogDirAsync(logDir, Path.Combine(_tempDir, "out"), viewerDistDir: dist))).Message);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "out")));
        Assert.Contains("doesn't exist", (await Assert.ThrowsAsync<PrerequisiteError>(() => LogBundle.BundleLogDirAsync(Path.Combine(_tempDir, "nope"), Path.Combine(_tempDir, "out"), viewerDistDir: dist))).Message);

        // the log dir and output dir default to the environment, and the viewer dist can come from the environment too
        await WriteBundleLogsAsync(logDir);
        env.Set(LogBundle.OutputDirEnvironmentVariable, Path.Combine(_tempDir, "env-out")).Set(ViewerAssets.DistDirEnvironmentVariable, dist);
        await LogBundle.BundleLogDirAsync();
        Assert.True(File.Exists(Path.Combine(_tempDir, "env-out", "logs", "listing.json")));
    }

    [Fact]
    public async Task embed_log_dir_places_the_viewer_beside_the_logs()
    {
        var dist = FakeDist();
        var logDir = Path.Combine(_tempDir, "logs");
        var (first, _) = await WriteBundleLogsAsync(logDir);
        await LogBundle.EmbedLogDirAsync(logDir, viewerDistDir: dist);
        foreach (var expected in new[] { "index.html", "robots.txt", "listing.json", Path.Combine("assets", "index.js"), Path.Combine("assets", "index.css"), Path.GetFileName(first) })
        {
            Assert.True(File.Exists(Path.Combine(logDir, expected)), expected);
        }

        Assert.Contains("\"log_dir\": \".\"", File.ReadAllText(Path.Combine(logDir, "index.html")));
        Assert.False(Directory.Exists(Path.Combine(logDir, "viewer")));
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(Path.Combine(logDir, "listing.json")))!.AsObject().Count);
        await Assert.ThrowsAsync<PrerequisiteError>(() => LogBundle.EmbedLogDirAsync(Path.Combine(_tempDir, "nope"), viewerDistDir: dist));
    }

    [PythonFact]
    public async Task listing_manifest_and_injected_configuration_match_python()
    {
        var logDir = Path.Combine(_tempDir, "logs");
        await WriteBundleLogsAsync(logDir);
        var ours = Path.Combine(_tempDir, "ours");
        await LogBundle.WriteLogListingAsync(logDir, outputDir: ours);
        var theirs = Path.Combine(_tempDir, "theirs");
        Directory.CreateDirectory(theirs);
        var html = Path.Combine(theirs, "index.html");
        File.WriteAllText(html, FakeIndexHtml);
        PythonReference.Run($$"""
            from inspect_ai.log._file import write_log_listing
            from inspect_ai.log._bundle import inject_configuration
            write_log_listing(r'''{{logDir}}''', output_dir=r'''{{theirs}}''')
            inject_configuration(r'''{{html}}''', log_dir="logs", abs_log_dir=r'''{{Path.GetFullPath(logDir)}}''')
            """);
        Assert.Equal(
            Canon(JsonNode.Parse(File.ReadAllText(Path.Combine(theirs, "listing.json")))),
            Canon(JsonNode.Parse(File.ReadAllText(Path.Combine(ours, "listing.json")))));

        var oursHtml = Path.Combine(ours, "index.html");
        File.WriteAllText(oursHtml, FakeIndexHtml);
        LogBundle.InjectConfiguration(oursHtml, "logs", Path.GetFullPath(logDir));
        Assert.Equal(File.ReadAllText(html), File.ReadAllText(oursHtml));
    }

    // ---------------------------------------------------------------- the log commands

    [Fact]
    public async Task list_logs_follows_the_cli_in_text_and_json()
    {
        var logDir = Path.Combine(_tempDir, "logs");
        var (first, second) = await WriteBundleLogsAsync(logDir);
        var logs = LogCommands.ListLogs(logDir, absolute: true);
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), logs.Select(log => log.Name).Order(StringComparer.Ordinal));
        var relative = LogCommands.ListLogs(logDir);
        Assert.All(relative, log => Assert.False(Path.IsPathRooted(log.Name)));
        Assert.Equal(new[] { first, second }.Order(StringComparer.Ordinal), relative.Select(log => Path.GetFullPath(log.Name)).Order(StringComparer.Ordinal));
        Assert.Equal(first, Assert.Single(LogCommands.ListLogs(logDir, status: EvalStatus.Success, absolute: true)).Name);
        Assert.Single(LogCommands.ListLogs(logDir, absolute: true, recursive: false));
        Assert.Equal(string.Join("\n", logs.Select(log => log.Name)), LogCommands.ListLogsText(logDir, absolute: true));

        var json = JsonNode.Parse(LogCommands.ListLogsJson(logDir, absolute: true))!.AsArray();
        Assert.Equal(2, json.Count);
        var item = json.Single(node => (string)node!["name"]! == first)!.AsObject();
        Assert.Equal(["name", "type", "size", "mtime", "task", "task_id", "suffix"], item.Select(pair => pair.Key));
        Assert.Equal("file", (string)item["type"]!);
        Assert.Equal(new FileInfo(first).Length, (long)item["size"]!);
        Assert.Equal(logs.Single(log => log.Name == first).Mtime!.Value * 1000, (double)item["mtime"]!, 3);
        Assert.Equal("tiny", (string)item["task"]!);
        Assert.Equal("DMp9V2YEhradPcNhv3qARw", (string)item["task_id"]!);
        Assert.Null(item["suffix"]);
        Assert.Contains("\"suffix\": null", LogCommands.ListLogsJson(logDir));
        Assert.Equal("[]", LogCommands.ListLogsJson(Path.Combine(_tempDir, "nothing")));
    }

    [Fact]
    public async Task dump_headers_and_schema_follow_the_cli()
    {
        var logDir = Path.Combine(_tempDir, "logs");
        var (first, second) = await WriteBundleLogsAsync(logDir);
        Assert.Equal(EvalLogFiles.EvalLogJson(EvalLogFiles.ReadEvalLog(first)), LogCommands.Dump(first));
        Assert.Null(JsonNode.Parse(PythonJsonFormat.SanitizeNonFinite(LogCommands.Dump(first, headerOnly: true)))!["samples"]);
        Assert.Equal(EvalLogFiles.EvalLogJson(EvalLogFiles.ReadEvalLog(first, resolveAttachments: ResolveAttachments.Full)), LogCommands.Dump(first, resolveAttachments: ResolveAttachments.Full));

        var headers = LogCommands.HeadersJson([first, second]);
        Assert.StartsWith("[\n  {\n    \"version\": 2,", headers);
        Assert.DoesNotContain("é", headers);
        Assert.Contains("caf\\u00e9 \\ud83d\\udca1", headers);
        var parsed = JsonNode.Parse(PythonJsonFormat.SanitizeNonFinite(headers))!.AsArray();
        Assert.Equal(2, parsed.Count);
        Assert.All(parsed, node => Assert.Null(node!["samples"]));
        Assert.Equal("café \U0001F4A1", (string)parsed[0]!["eval"]!["metadata"]!["note"]!);
        Assert.Equal("[]", LogCommands.HeadersJson([]));
        Assert.Equal("{\"a\": \"\\u00e9\\\"x\\\"\", \"b\\u00e9\": 1}", LogCommands.EnsureAscii("{\"a\": \"é\\\"x\\\"\", \"bé\": 1}"));

        var schema = Path.Combine(_tempDir, "schema.json");
        File.WriteAllText(schema, "{\"openapi\": \"3.1.0\"}");
        using var env = new EnvVarScope().Set(ViewerAssets.SchemaPathEnvironmentVariable, null).Set(ViewerAssets.DistDirEnvironmentVariable, null);
        Assert.Equal("{\"openapi\": \"3.1.0\"}", LogCommands.SchemaJson(schema));
        Assert.Contains("not configured", Assert.Throws<PrerequisiteError>(() => LogCommands.SchemaJson()).Message);
        env.Set(ViewerAssets.SchemaPathEnvironmentVariable, schema);
        Assert.Equal("{\"openapi\": \"3.1.0\"}", LogCommands.SchemaJson());
        env.Set(ViewerAssets.SchemaPathEnvironmentVariable, null);
        var view = Path.Combine(_tempDir, "_view");
        Directory.CreateDirectory(Path.Combine(view, "dist"));
        File.WriteAllText(Path.Combine(view, ViewerAssets.SchemaFileName), "{}");
        env.Set(ViewerAssets.DistDirEnvironmentVariable, Path.Combine(view, "dist"));
        Assert.Equal("{}", LogCommands.SchemaJson());
        Assert.Throws<PrerequisiteError>(() => LogCommands.SchemaJson(Path.Combine(_tempDir, "missing.json")));
    }

    [PythonFact]
    public async Task list_and_headers_json_match_the_python_cli()
    {
        var logDir = Path.Combine(_tempDir, "logs");
        var (first, second) = await WriteBundleLogsAsync(logDir);
        var python = PythonReference.Run($$"""
            import json
            from inspect_ai._cli.log import headers
            from inspect_ai.log import list_eval_logs
            from fsspec.core import split_protocol
            logs = list_eval_logs(r'''{{logDir}}''')
            for log in logs:
                _, path = split_protocol(log.name)
                log.name = path
            print(json.dumps([log.model_dump() for log in logs], indent=2))
            print("=====")
            headers([r'''{{TinyFixture}}''', r'''{{TinyFixture}}'''])
            """).Split("=====\n");
        var pythonList = JsonNode.Parse(python[0])!.AsArray().OrderBy(node => (string)node!["name"]!, StringComparer.Ordinal).ToList();
        var ourList = JsonNode.Parse(LogCommands.ListLogsJson(logDir, absolute: true))!.AsArray().OrderBy(node => (string)node!["name"]!, StringComparer.Ordinal).ToList();
        Assert.Equal(pythonList.Count, ourList.Count);
        for (var i = 0; i < pythonList.Count; i++)
        {
            foreach (var key in new[] { "name", "type", "size", "task", "task_id", "suffix" })
            {
                Assert.Equal(Canon(pythonList[i]![key]), Canon(ourList[i]![key]));
            }

            Assert.Equal((double)pythonList[i]!["mtime"]!, (double)ourList[i]!["mtime"]!, 1.0);
        }

        // the header content: equal except for the args-passed fields, which the C# EvalSpec writer omits when unset
        // and Python's validator fills in on read (a log-schema port choice, not part of this formatting)
        var pythonHeaders = JsonNode.Parse(PythonJsonFormat.SanitizeNonFinite(python[1].Trim()))!.AsArray();
        var ourHeaders = JsonNode.Parse(PythonJsonFormat.SanitizeNonFinite(LogCommands.HeadersJson([TinyFixture, TinyFixture])))!.AsArray();
        foreach (var node in pythonHeaders.Concat(ourHeaders))
        {
            node!["eval"]!.AsObject().Remove("solver_args_passed");
            node["eval"]!.AsObject().Remove("task_args_passed");
        }

        Assert.Equal(Canon(pythonHeaders), Canon(ourHeaders));

        // the formatting: byte for byte what json.dumps(..., indent=2) makes of the same log JSON (ensure_ascii, NaN kept)
        var firstJson = Path.Combine(_tempDir, "first.json");
        var secondJson = Path.Combine(_tempDir, "second.json");
        File.WriteAllText(firstJson, EvalLogFiles.EvalLogJson(EvalLogFiles.ReadEvalLog(first, headerOnly: true)));
        File.WriteAllText(secondJson, EvalLogFiles.EvalLogJson(EvalLogFiles.ReadEvalLog(second, headerOnly: true)));
        var dumped = PythonReference.Run($$"""
            import json
            print(json.dumps([json.load(open(r'''{{firstJson}}''')), json.load(open(r'''{{secondJson}}'''))], indent=2))
            """);
        Assert.Equal(dumped, LogCommands.HeadersJson([first, second]));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A Python-written log (see EvalFormatTests): the reference for output that must match Python byte for byte.</summary>
    private static string TinyFixture => FixtureRoot("eval-logs", "tiny", "2026-09-05T11-52-09-00-00_tiny_DMp9V2YEhradPcNhv3qARw.eval");

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

    private const string FakeIndexHtml = "<!doctype html>\n<html>\n  <head>\n    <title>Inspect View</title>\n  </head>\n  <body>café</body>\n</html>\n";

    private string FakeDist()
    {
        var dist = Path.Combine(_tempDir, "dist");
        Directory.CreateDirectory(Path.Combine(dist, "assets"));
        File.WriteAllText(Path.Combine(dist, "index.html"), FakeIndexHtml);
        File.WriteAllText(Path.Combine(dist, "assets", "index.js"), "console.log('viewer')");
        File.WriteAllText(Path.Combine(dist, "assets", "index.css"), "body{}");
        return dist;
    }

    /// <summary>A successful log at the root of <paramref name="logDir"/> and an errored one (with an eval-set file) under <c>nested/</c>.</summary>
    private static async Task<(string First, string Second)> WriteBundleLogsAsync(string logDir)
    {
        var spec = Spec() with
        {
            ModelRoles = new Dictionary<string, IReadOnlyList<ModelConfig>> { ["grader"] = [new ModelConfig("grader-a"), new ModelConfig("grader-b")] },
            Metadata = new Dictionary<string, object?> { ["note"] = "café \U0001F4A1" },
        };
        var first = Path.Combine(logDir, "2026-09-05T11-52-09-00-00_tiny_DMp9V2YEhradPcNhv3qARw.eval");
        await EvalLogFiles.WriteEvalLogAsync(MakeLog(Sample(1, 1), Sample("b", 1, "blue")) with
        {
            Eval = spec,
            Status = EvalStatus.Success,
            Stats = new EvalStats { StartedAt = Created, CompletedAt = Created.AddSeconds(3) },
            Results = new EvalResults { TotalSamples = 2, CompletedSamples = 2, Scores = [new EvalScore("match", "match") { Metrics = new Dictionary<string, EvalMetric> { ["accuracy"] = new("accuracy", 0.5), ["stderr"] = new("stderr", 0.5) } }] },
        }, first);
        var nested = Path.Combine(logDir, "nested");
        var second = Path.Combine(nested, "2026-09-05T11-53-09-00-00_other_XZ1zLy7XEvZdVfw3xrdqhz.eval");
        await EvalLogFiles.WriteEvalLogAsync(MakeLog(Sample(1, 1)) with
        {
            Eval = Spec() with { Task = "other", TaskId = "XZ1zLy7XEvZdVfw3xrdqhz", TaskVersion = "v1", Created = Created.AddMinutes(1) },
            Status = EvalStatus.Error,
            Error = new EvalError("crashed", "trace"),
            Stats = new EvalStats { StartedAt = Created.AddMinutes(1) },
        }, second);
        File.WriteAllText(Path.Combine(nested, "eval-set.json"), "{\"eval_set_id\": \"x\"}");
        File.WriteAllText(Path.Combine(nested, "ignored.txt"), "not a log");
        return (first, second);
    }

    /// <summary>
    /// Writes a log the way a crashed eval leaves it: start journaled, <paramref name="flushed"/> samples flushed
    /// (plus one config update), one more sample buffered but never flushed, and the recorder disposed without
    /// <c>LogFinishAsync</c>.
    /// </summary>
    private Task<string> WriteCrashedLogAsync(EvalSpec spec, params EvalSample[] flushed) => WriteCrashedLogAsync(_tempDir, spec, flushed);

    private static async Task<string> WriteCrashedLogAsync(string directory, EvalSpec spec, params EvalSample[] flushed)
    {
        var recorder = new EvalRecorder(directory);
        string path;
        await using (recorder.ConfigureAwait(false))
        {
            path = await recorder.LogInitAsync(spec);
            await recorder.LogStartAsync(spec, new EvalPlan());
            foreach (var sample in flushed)
            {
                await recorder.LogSampleAsync(spec, sample);
            }

            await recorder.FlushAsync(spec);
            await recorder.LogConfigUpdateAsync(spec, new ConfigUpdate([new ConfigValueChange("eval", "max_samples") { Value = 4 }], "task", new ProvenanceData("operator") { Timestamp = Created }));
            await recorder.LogSampleAsync(spec, Sample("unflushed", 1));
        }

        return path;
    }

    private static EvalSpec Spec() => new()
    {
        Task = "tiny",
        TaskId = "DMp9V2YEhradPcNhv3qARw",
        RunId = "ZFZxv6hFpLhEzGXdbWDyJA",
        EvalId = "7fBsn5zd3MLPWHFhdAqGGb",
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
        Events = ScorerEvents(),
        ModelUsage = new Dictionary<string, ModelUsage> { ["m"] = new ModelUsage(1, 1, 2) },
        RoleUsage = id is string ? new Dictionary<string, ModelUsage> { ["grader"] = new ModelUsage(1, 1, 2) } : new Dictionary<string, ModelUsage>(),
        Uuid = $"uuid-{EvalLogFormat.IdText(id)}-{epoch}",
        StartedAt = id is string ? Created.AddSeconds(-30) : Created,
        CompletedAt = Created.AddSeconds(1),
        TotalTime = 1.0,
        WorkingTime = 0.5,
    };

    private static List<TranscriptEvent> ScorerEvents()
    {
        var transcript = new Transcript();
        using (transcript.Span("solver", "solver"))
        {
        }

        using (transcript.Span(ScoreLogs.ScorersSpanName, ScoreLogs.ScorersSpanName))
        {
            using var scorer = transcript.Span("match", ScoreLogs.ScorerSpanType);
            transcript.Add(new ScoreEvent(new Score("C"), "4") { Scorer = "match" });
        }

        return transcript.Events.Select(e => e with { Timestamp = Created }).ToList();
    }

    private static EvalLog MakeLog(params EvalSample[] samples) => new()
    {
        Status = EvalStatus.Success,
        Eval = Spec(),
        Samples = samples,
        Stats = new EvalStats { StartedAt = Created, CompletedAt = Created.AddSeconds(2) },
    };

    private static string Canonical(EvalLog log) => EvalLogWriter.Serialize(EvalLogWriter.ResolveAttachments(log with { Location = null }, ResolveAttachments.Full));

    private static JsonNode? ToNode<T>(T value) => JsonNode.Parse(PythonJsonFormat.SanitizeNonFinite(JsonSerializer.Serialize(value, EvalLogWriter.Options)));

    /// <summary>An event's JSON without the fields that differ per process (uuid, timestamp, span ids).</summary>
    private static string EventShape(TranscriptEvent e)
    {
        var node = ToNode(e)!.AsObject();
        foreach (var key in new[] { "uuid", "timestamp", "span_id", "id", "parent_id", "working_start" })
        {
            node.Remove(key);
        }

        return Canon(node);
    }

    private static string Canon(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + Canon(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(Canon)) + "]",
        JsonValue value => value.TryGetValue<double>(out var number) ? number.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : value.ToJsonString(),
        _ => node.ToJsonString(),
    };
}
