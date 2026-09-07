using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Runner.Scoring;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;

/// <summary>Port of <c>tests/_eval/test_score.py</c> and the non-interactive parts of <c>tests/_cli/test_score.py</c> for <see cref="ScoreLogs"/>.</summary>
public sealed class ScoreLogsTests
{
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, EvalLogWriter.Options);

    private static string TempPath(string name) => Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"), name);

    private static string FixturePath(string name)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "fixtures", "eval-logs", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException($"No fixtures/eval-logs/{name} above {AppContext.BaseDirectory}");
    }

    private static EvalSample MakeSample(
        object id,
        string completion,
        string target,
        int epoch = 1,
        IReadOnlyDictionary<string, Score>? scores = null,
        IReadOnlyList<TranscriptEvent>? events = null,
        EvalError? error = null,
        IReadOnlyDictionary<string, ModelUsage>? usage = null) =>
        new()
        {
            Id = id,
            Epoch = epoch,
            Input = "question",
            Target = target,
            Messages = [new ChatMessageUser("question"), new ChatMessageAssistant(completion)],
            Output = ModelOutput.FromContent("scripted", completion),
            Scores = scores,
            Events = events ?? [],
            Error = error,
            ModelUsage = usage ?? new Dictionary<string, ModelUsage>(StringComparer.Ordinal),
        };

    private static EvalLog MakeLog(params EvalSample[] samples) =>
        new()
        {
            Status = EvalStatus.Success,
            Eval = new EvalSpec { Task = "rescore", Dataset = new EvalDataset { Samples = samples.Length }, Model = "header-model" },
            Samples = samples,
        };

    /// <summary>A log scored once with <c>includes</c> (sample 1 correct, sample 2 incorrect): the starting point of the append and overwrite cases.</summary>
    private static Task<EvalLog> ScoredLogAsync() =>
        ScoreLogs.ScoreAsync(MakeLog(MakeSample(1, "The answer is 4.", "4"), MakeSample(2, "no idea", "4")), [Scorers.Includes()], ScoreAction.Overwrite);

    private static ScorerDef Constant(string name, double value) =>
        Scorers.Custom(name, (_, _, _) => Task.FromResult(new Score(value)), Metrics.Accuracy(), Metrics.Stderr());

    private static List<TranscriptEvent> ScorerEvents(params (string Name, double Value)[] scores)
    {
        var transcript = new Transcript();
        using (transcript.Span(ScoreLogs.ScorersSpanName, ScoreLogs.ScorersSpanName))
        {
            foreach (var (name, value) in scores)
            {
                using var span = transcript.Span(name, ScoreLogs.ScorerSpanType);
                transcript.Add(new ScoreEvent(new Score(value), "target") { Scorer = name });
            }
        }

        return transcript.Events.ToList();
    }

    private static SpanBeginEvent? LastScorersSpan(IEnumerable<TranscriptEvent> events) =>
        events.OfType<SpanBeginEvent>().LastOrDefault(e => e.Name == ScoreLogs.ScorersSpanName);

    // --- _get_updated_scores / _get_updated_events -----------------------------------------------------------------

    [Fact]
    public void updated_scores_append_dedupes_names_and_overwrite_keeps_only_the_new_ones()
    {
        var sample = MakeSample("1", "x", "y", scores: new Dictionary<string, Score> { ["old-scorer"] = new(0.1) });
        var incoming = new Dictionary<string, SampleScore>
        {
            ["old-scorer"] = new(new Score(0.2)),
            ["new-scorer"] = new(new Score(0.5)),
        };

        var appended = ScoreMerging.UpdatedScores(sample, incoming, ScoreAction.Append);
        Assert.Equal(["old-scorer", "old-scorer-1", "new-scorer"], appended.Keys);
        Assert.Equal([0.1, 0.2, 0.5], appended.Values.Select(s => s.AsFloat()));

        var overwritten = ScoreMerging.UpdatedScores(sample, incoming, ScoreAction.Overwrite);
        Assert.Equal(["old-scorer", "new-scorer"], overwritten.Keys);
        Assert.Equal([0.2, 0.5], overwritten.Values.Select(s => s.AsFloat()));
    }

    [Theory]
    [InlineData(ScoreAction.Append, "old-scorer", "old-scorer,new-scorer", "old-scorer,old-scorer,new-scorer", false)]
    [InlineData(ScoreAction.Append, "", "old-scorer,new-scorer", "old-scorer,new-scorer", true)]
    [InlineData(ScoreAction.Overwrite, "old-scorer", "old-scorer,new-scorer", "old-scorer,new-scorer", true)]
    [InlineData(ScoreAction.Overwrite, "", "old-scorer,new-scorer", "old-scorer,new-scorer", true)]
    public void updated_events_merge_into_the_existing_scorers_span_or_replace_it(ScoreAction action, string existing, string incoming, string expected, bool expectNewScorersSpan)
    {
        static (string, double)[] Parse(string names) => names.Length == 0 ? [] : names.Split(',').Select((n, i) => (n, 0.1 * (i + 1))).ToArray();
        var baseEvents = new List<TranscriptEvent> { new InfoEvent("sample_init", null), new InfoEvent("model", null) };
        var existingEvents = baseEvents.Concat(existing.Length == 0 ? [] : ScorerEvents(Parse(existing))).ToList();
        var expectedEvents = baseEvents.Concat(ScorerEvents(Parse(expected))).ToList();
        var newEvents = ScorerEvents(Parse(incoming));
        var sample = MakeSample("1", "x", "target", events: existingEvents);

        var updated = ScoreMerging.UpdatedEvents(sample, newEvents, action);

        Assert.Equal(expectedEvents.Count, updated.Count);
        Assert.Equal(baseEvents, updated.Take(baseEvents.Count));
        foreach (var (actual, wanted) in updated.Skip(baseEvents.Count).Zip(expectedEvents.Skip(baseEvents.Count)))
        {
            Assert.IsType(wanted.GetType(), actual);
            switch (wanted)
            {
                case SpanBeginEvent begin:
                    Assert.Equal((begin.Name, begin.Type), (((SpanBeginEvent)actual).Name, ((SpanBeginEvent)actual).Type));
                    break;
                case ScoreEvent score:
                    Assert.Equal(score.Scorer, ((ScoreEvent)actual).Scorer);
                    break;
            }
        }

        var existingSpan = LastScorersSpan(existingEvents);
        var updatedSpan = LastScorersSpan(updated);
        Assert.Equal(existing.Length == 0, existingSpan is null);
        Assert.NotNull(updatedSpan);
        Assert.Equal(!expectNewScorersSpan, existingSpan?.Id == updatedSpan.Id);
        // every scorer span sits inside the (single) scorers span
        var scorerSpans = updated.OfType<SpanBeginEvent>().Where(e => e.Type == ScoreLogs.ScorerSpanType).ToList();
        Assert.Equal(expected.Split(','), scorerSpans.Select(e => e.Name));
        Assert.All(scorerSpans, e => Assert.Equal(updatedSpan.Id, e.ParentId));
        Assert.Single(updated.OfType<SpanBeginEvent>(), e => e.Name == ScoreLogs.ScorersSpanName);
    }

    // --- append versus overwrite ----------------------------------------------------------------------------------

    [Fact]
    public async Task overwrite_scores_every_sample_and_builds_results_reductions_and_the_header()
    {
        var log = await ScoredLogAsync();

        Assert.Equal(["C", "I"], log.Samples!.Select(s => s.Scores!["includes"].Text));
        var score = Assert.Single(log.Results!.Scores);
        Assert.Equal("includes", score.Name);
        Assert.Null(score.Reducer);
        Assert.Equal(2, score.ScoredSamples);
        Assert.Equal(0.5, score.Metrics["accuracy"].Value);
        Assert.Equal(0.5, score.Metrics["stderr"].Value, 6);
        Assert.Equal(2, log.Results.TotalSamples);
        Assert.Equal(2, log.Results.CompletedSamples);
        var reduction = Assert.Single(log.Reductions!);
        Assert.Equal("includes", reduction.Scorer);
        Assert.Null(reduction.Reducer);
        Assert.Equal([1, 2], reduction.Samples.Select(s => (int)s.SampleId!));
        Assert.Equal("includes", Assert.Single(log.Eval.Scorers!).Name);
        Assert.Equal("""[{"name":"accuracy","options":{}},{"name":"stderr","options":{}}]""", log.Eval.Scorers![0].Metrics!.ToJsonString());
        Assert.Equal(new HeadlineMetric { Scorer = "includes", Score = "includes", Metric = "accuracy" }, log.Results.Headline);
    }

    [Fact]
    public async Task append_keeps_the_existing_scores_results_reductions_and_header_scorers()
    {
        var log = await ScoredLogAsync();

        var appended = await ScoreLogs.ScoreAsync(log, [Constant("bonus", 1.0)], ScoreAction.Append);

        Assert.Equal(["includes", "bonus"], appended.Results!.Scores.Select(s => s.Name));
        Assert.Equal(["includes", "bonus"], appended.Reductions!.Select(r => r.Scorer));
        Assert.Equal(["includes", "bonus"], appended.Eval.Scorers!.Select(s => s.Name));
        Assert.All(appended.Samples!, s => Assert.Equal(["includes", "bonus"], s.Scores!.Keys));
        Assert.Equal(1.0, appended.Results.Scores[1].Metrics["accuracy"].Value);
        // the new scorer span joined the existing scorers span
        var scorersSpan = Assert.Single(appended.Samples![0].Events.OfType<SpanBeginEvent>(), e => e.Name == ScoreLogs.ScorersSpanName);
        Assert.Equal(LastScorersSpan(log.Samples![0].Events)!.Id, scorersSpan.Id);
        Assert.Equal(["includes", "bonus"], appended.Samples[0].Events.OfType<SpanBeginEvent>().Where(e => e.Type == ScoreLogs.ScorerSpanType).Select(e => e.Name));
        Assert.Equal(["includes", "bonus"], appended.Samples[0].Events.OfType<ScoreEvent>().Select(e => e.Scorer));
        // the input log was not mutated
        Assert.Single(log.Results!.Scores);
        Assert.Equal(["includes"], log.Samples[0].Scores!.Keys);
    }

    [Fact]
    public async Task append_of_an_already_used_scorer_name_gets_a_numeric_suffix()
    {
        var log = await ScoredLogAsync();

        var appended = await ScoreLogs.ScoreAsync(log, [Scorers.Includes()], ScoreAction.Append);

        Assert.Equal(["includes", "includes1"], appended.Results!.Scores.Select(s => s.Name));
        Assert.All(appended.Samples!, s => Assert.Equal(["includes", "includes1"], s.Scores!.Keys));
        Assert.Equal(["includes", "includes1"], appended.Samples![0].Events.OfType<ScoreEvent>().Select(e => e.Scorer));
    }

    [Fact]
    public async Task overwrite_replaces_the_scores_results_reductions_header_scorers_and_scorers_span()
    {
        var log = await ScoredLogAsync();

        var overwritten = await ScoreLogs.ScoreAsync(log, [Constant("bonus", 1.0)], ScoreAction.Overwrite);

        Assert.Equal(["bonus"], overwritten.Results!.Scores.Select(s => s.Name));
        Assert.Equal(["bonus"], overwritten.Reductions!.Select(r => r.Scorer));
        Assert.Equal(["bonus"], overwritten.Eval.Scorers!.Select(s => s.Name));
        Assert.All(overwritten.Samples!, s => Assert.Equal(["bonus"], s.Scores!.Keys));
        var scorersSpan = Assert.Single(overwritten.Samples![0].Events.OfType<SpanBeginEvent>(), e => e.Name == ScoreLogs.ScorersSpanName);
        Assert.NotEqual(LastScorersSpan(log.Samples![0].Events)!.Id, scorersSpan.Id);
        Assert.Equal(["bonus"], overwritten.Samples[0].Events.OfType<ScoreEvent>().Select(e => e.Scorer));
    }

    [Fact]
    public async Task scorers_see_the_existing_scores_on_append_and_none_on_overwrite()
    {
        var log = await ScoredLogAsync();
        var seen = new Dictionary<(string Id, string Scorer), string[]>();
        ScorerDef Spy(string name) => Scorers.Custom(name, (state, _, _) =>
        {
            lock (seen)
            {
                seen[(state.SampleId.ToString()!, name)] = state.Scores!.Keys.Order(StringComparer.Ordinal).ToArray();
            }

            return Task.FromResult(new Score(1.0));
        }, Metrics.Accuracy());

        await ScoreLogs.ScoreAsync(log, [Spy("first"), Spy("second")], ScoreAction.Append);
        Assert.Equal(["includes"], seen[("1", "first")]);
        Assert.Equal(["first", "includes"], seen[("1", "second")]);

        seen.Clear();
        await ScoreLogs.ScoreAsync(log, [Spy("first"), Spy("second")], ScoreAction.Overwrite);
        Assert.Empty(seen[("1", "first")]);
        Assert.Equal(["first"], seen[("1", "second")]);
    }

    [Fact]
    public async Task a_scorer_that_writes_its_own_score_into_the_state_is_an_error()
    {
        var log = await ScoredLogAsync();
        var rogue = Scorers.Custom("rogue", (state, _, _) =>
        {
            state.Scores!["rogue"] = new Score(0.5);
            return Task.FromResult(new Score(0.5));
        }, Metrics.Accuracy());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ScoreLogs.ScoreAsync(log, [rogue], ScoreAction.Append));
        Assert.Equal("Scorer rogue has modified state.scores", ex.Message);
    }

    // --- recomputed results equal the runner's --------------------------------------------------------------------

    [Fact]
    public async Task recomputed_results_and_reductions_equal_the_runner_results_for_the_same_samples()
    {
        var logDir = TempPath("logs");
        try
        {
            var api = new ScriptedModelApi(ScriptedTurn.Text("yes"), ScriptedTurn.Text("no"), ScriptedTurn.Text("yes"), ScriptedTurn.Text("yes"));
            var task = new EvalTask
            {
                Name = "epochs",
                Dataset = new MemoryDataset([new Sample("say yes") { Id = "a", Target = "yes" }, new Sample("say yes") { Id = "b", Target = "yes" }]),
                Scorers = [Scorers.Includes()],
                Epochs = new Epochs(2, [Reducers.Max(), Reducers.Mean()]),
            };
            var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = logDir, MaxSamples = 1 });

            // the runner now records the reducers, the reductions and the headline like Python
            Assert.Equal(["max", "mean"], log.Eval.Config.EpochsReducer);
            Assert.Equal(["max", "mean"], log.Results!.Scores.Select(s => s.Reducer));
            Assert.Equal(["max", "mean"], log.Reductions!.Select(r => r.Reducer));
            Assert.All(log.Reductions!, r => Assert.Equal(["a", "b"], r.Samples.Select(s => (string)s.SampleId!)));
            Assert.Equal(new HeadlineMetric { Scorer = "includes", Score = "includes", Metric = "accuracy", Reducer = "max" }, log.Results.Headline);

            var computed = ScoreLogs.ComputeResults(log.Samples!, task.Scorers, task.Epochs.Reducers);
            Assert.Equal(Json(log.Results), Json(computed.Results));
            Assert.Equal(Json(log.Reductions), Json(computed.Reductions));

            var recomputed = ScoreLogs.RecomputeMetrics(log, task.Scorers);
            Assert.Equal(Json(log.Results), Json(recomputed.Results));
            Assert.Equal(Json(log.Reductions), Json(recomputed.Reductions));

            var rescored = await ScoreLogs.ScoreAsync(log, task.Scorers, ScoreAction.Overwrite);
            Assert.Equal(Json(log.Results), Json(rescored.Results));
            Assert.Equal(Json(log.Reductions), Json(rescored.Reductions));
            Assert.Equal(log.Samples!.Select(s => s.Scores!["includes"].Text), rescored.Samples!.Select(s => s.Scores!["includes"].Text));
        }
        finally
        {
            Directory.Delete(logDir, recursive: true);
        }
    }

    [Fact]
    public void compute_results_gives_default_metrics_to_scores_no_scorer_accounts_for()
    {
        var samples = new[]
        {
            MakeSample(1, "x", "y", scores: new Dictionary<string, Score> { ["match"] = new(1.0), ["custom"] = new(0.0) }),
            MakeSample(2, "x", "y", scores: new Dictionary<string, Score> { ["match"] = new(1.0), ["custom"] = new(1.0) }),
        };

        var computed = ScoreLogs.ComputeResults(samples, [Scorers.Custom("match", (_, _, _) => throw new NotSupportedException(), Metrics.Mean())]);

        Assert.Equal(["match", "custom"], computed.Results.Scores.Select(s => s.Name));
        Assert.Equal(["mean"], computed.Results.Scores[0].Metrics.Keys);
        Assert.Equal(["accuracy", "stderr"], computed.Results.Scores[1].Metrics.Keys);
        Assert.Equal(0.5, computed.Results.Scores[1].Metrics["accuracy"].Value);
        Assert.Equal(["match", "custom"], computed.Reductions!.Select(r => r.Scorer));
    }

    // --- epochs reducers --------------------------------------------------------------------------------------------

    [Fact]
    public async Task an_explicit_epochs_reducer_produces_its_views_and_is_recorded_in_the_header()
    {
        var log = MakeLog(MakeSample("q", "yes", "yes", epoch: 1), MakeSample("q", "no", "yes", epoch: 2));

        var scored = await ScoreLogs.ScoreAsync(log, [Scorers.Includes()], ScoreAction.Overwrite, [Reducers.Max(), Reducers.Mean()]);

        Assert.Equal(["max", "mean"], scored.Eval.Config.EpochsReducer);
        Assert.Equal(["max", "mean"], scored.Results!.Scores.Select(s => s.Reducer));
        Assert.Equal(1.0, scored.Results.Scores[0].Metrics["accuracy"].Value);
        Assert.Equal(0.5, scored.Results.Scores[1].Metrics["accuracy"].Value);
        Assert.Equal(["max", "mean"], scored.Reductions!.Select(r => r.Reducer));
        // max keeps the winning score's original value ("C"), as Python's max_score does, so convert with value_to_float
        Assert.Equal([1.0, 0.5], scored.Reductions!.Select(r => ValueToFloat.Default(Assert.Single(r.Samples).Score.Value)));
        Assert.Equal(2, scored.Results.TotalSamples);
    }

    [Fact]
    public async Task the_reducer_defaults_to_the_one_recorded_in_the_log_header()
    {
        var log = MakeLog(MakeSample("q", "yes", "yes", epoch: 1), MakeSample("q", "no", "yes", epoch: 2));

        var implicitMean = await ScoreLogs.ScoreAsync(log, [Scorers.Includes()], ScoreAction.Overwrite);
        var score = Assert.Single(implicitMean.Results!.Scores);
        Assert.Null(score.Reducer);
        Assert.Equal(0.5, score.Metrics["accuracy"].Value);
        Assert.Null(implicitMean.Eval.Config.EpochsReducer);

        var headerMax = await ScoreLogs.ScoreAsync(log with { Eval = log.Eval with { Config = new EvalConfig { EpochsReducer = ["max"] } } }, [Scorers.Includes()], ScoreAction.Overwrite);
        score = Assert.Single(headerMax.Results!.Scores);
        Assert.Equal("max", score.Reducer);
        Assert.Equal(1.0, score.Metrics["accuracy"].Value);
        Assert.Equal(["max"], headerMax.Eval.Config.EpochsReducer);
    }

    [Fact]
    public async Task an_epochs_reducer_without_a_registry_name_cannot_be_recorded()
    {
        ScoreReducer custom = scores => scores[0];
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => ScoreLogs.ScoreAsync(MakeLog(MakeSample(1, "x", "x")), [Scorers.Includes()], ScoreAction.Overwrite, [custom]));
        Assert.Contains("registry name", ex.Message, StringComparison.Ordinal);
    }

    // --- errored samples --------------------------------------------------------------------------------------------

    [Fact]
    public async Task errored_samples_are_scored_too_but_not_counted_as_completed()
    {
        var errored = MakeSample(2, "", "4", error: new EvalError("boom")) with { Messages = [], Output = new ModelOutput() };
        var log = MakeLog(MakeSample(1, "4", "4"), errored);

        var scored = await ScoreLogs.ScoreAsync(log, [Scorers.Includes()], ScoreAction.Overwrite);

        Assert.Equal(["C", "I"], scored.Samples!.Select(s => s.Scores!["includes"].Text));
        Assert.Equal(2, scored.Results!.TotalSamples);
        Assert.Equal(1, scored.Results.CompletedSamples);
        var score = Assert.Single(scored.Results.Scores);
        Assert.Equal(2, score.ScoredSamples);
        Assert.Equal(0.5, score.Metrics["accuracy"].Value);
        Assert.NotNull(scored.Samples![1].Error);
    }

    // --- failures and cancellation --------------------------------------------------------------------------------

    [Fact]
    public async Task a_failing_scorer_fails_the_pass_and_cancels_the_samples_still_scoring()
    {
        var cancelled = new TaskCompletionSource();
        var scorer = Scorers.Custom("flaky", async (state, _, ct) =>
        {
            if (state.SampleId is 2)
            {
                throw new InvalidOperationException("boom");
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            return new Score(1.0);
        }, Metrics.Accuracy());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ScoreLogs.ScoreAsync(MakeLog(MakeSample(1, "x", "x"), MakeSample(2, "x", "x")), [scorer]));
        Assert.Equal("boom", ex.Message);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task caller_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        var scorer = Scorers.Custom("slow", async (_, _, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new Score(1.0);
        }, Metrics.Accuracy());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScoreLogs.ScoreAsync(MakeLog(MakeSample(1, "x", "x")), [scorer], cancellationToken: cts.Token));
    }

    [Fact]
    public async Task a_log_without_samples_or_without_scorers_is_rejected()
    {
        var log = MakeLog(MakeSample(1, "x", "x"));
        await Assert.ThrowsAsync<ArgumentException>(() => ScoreLogs.ScoreAsync(log with { Samples = null }, [Scorers.Includes()]));
        await Assert.ThrowsAsync<ArgumentException>(() => ScoreLogs.ScoreAsync(log with { Samples = [] }, [Scorers.Includes()]));
        await Assert.ThrowsAsync<ArgumentException>(() => ScoreLogs.ScoreAsync(log, []));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ScoreLogs.ScoreAsync(log, [Scorers.Includes()], options: new ScoreLogOptions { MaxSamples = 0 }));
        await Assert.ThrowsAsync<NotSupportedException>(() => ScoreLogs.ScoreAsync(TempPath("run.eval"), [Scorers.Includes()]));
        Assert.Throws<ArgumentException>(() => ScoreLogs.RecomputeMetrics(log with { Samples = null }, [Scorers.Includes()]));
    }

    [Fact]
    public async Task resolve_action_appends_to_a_scored_log_and_overwrites_an_unscored_one()
    {
        var unscored = MakeLog(MakeSample(1, "x", "x"));
        Assert.Equal(ScoreAction.Overwrite, ScoreLogs.ResolveAction(unscored, null));
        Assert.Equal(ScoreAction.Append, ScoreLogs.ResolveAction(await ScoredLogAsync(), null));
        Assert.Equal(ScoreAction.Overwrite, ScoreLogs.ResolveAction(await ScoredLogAsync(), ScoreAction.Overwrite));
        Assert.Equal(ScoreAction.Overwrite, ScoreLogs.ResolveAction(unscored with { Results = new EvalResults { TotalSamples = 1 } }, null));
    }

    // --- model and model roles ------------------------------------------------------------------------------------

    [Fact]
    public async Task a_scorer_that_generates_needs_a_supplied_model()
    {
        var grader = Scorers.Custom("grader", async (_, _, ct) =>
        {
            var output = await SampleContext.Require().ActiveModel.GenerateAsync("grade this", cancellationToken: ct);
            return new Score(output.Completion);
        }, Metrics.Accuracy());
        var log = MakeLog(MakeSample(1, "x", "x"));

        var ex = await Assert.ThrowsAsync<PrerequisiteError>(() => ScoreLogs.ScoreAsync(log, [grader]));
        Assert.Contains("header-model", ex.Message, StringComparison.Ordinal);

        var scored = await ScoreLogs.ScoreAsync(log, [grader], options: new ScoreLogOptions { Model = new Model(new ScriptedModelApi(ScriptedTurn.Text("graded"))) });
        Assert.Equal("graded", scored.Samples![0].Scores!["grader"].Text);
        Assert.Equal("grader", scored.Samples[0].Events.OfType<ScoreEvent>().Single().Scorer);
    }

    [Fact]
    public async Task model_roles_bind_for_the_scorers_and_fall_back_to_the_active_model()
    {
        var judged = Scorers.Custom("judged", (_, _, _) => Task.FromResult(new Score(ModelRoles.GetModel("judge").Name)), Metrics.Accuracy());
        var log = MakeLog(MakeSample(1, "x", "x"));

        var withRole = await ScoreLogs.ScoreAsync(log, [judged], options: new ScoreLogOptions { ModelRoles = new Dictionary<string, object> { ["judge"] = new Model(new ScriptedModelApi()) } });
        Assert.Equal("scripted", withRole.Samples![0].Scores!["judged"].Text);

        var withoutRole = await ScoreLogs.ScoreAsync(log, [judged]);
        Assert.Equal("header-model", withoutRole.Samples![0].Scores!["judged"].Text);
    }

    // --- headline, header metrics, score events ---------------------------------------------------------------------

    [Fact]
    public async Task the_headline_is_resolved_against_the_merged_results()
    {
        var log = await ScoredLogAsync();

        var declared = await ScoreLogs.ScoreAsync(log with { Eval = log.Eval with { HeadlineMetric = new HeadlineMetric { Scorer = "bonus", Metric = "stderr" } } }, [Constant("bonus", 1.0)], ScoreAction.Append);
        Assert.Equal(new HeadlineMetric { Scorer = "bonus", Score = "bonus", Metric = "stderr" }, declared.Results!.Headline);

        var stale = await ScoreLogs.ScoreAsync(log with { Eval = log.Eval with { HeadlineMetric = new HeadlineMetric { Scorer = "nope" } } }, [Constant("bonus", 1.0)], ScoreAction.Append);
        Assert.Equal(new HeadlineMetric { Scorer = "includes", Score = "includes", Metric = "accuracy" }, stale.Results!.Headline);
    }

    [Fact]
    public async Task task_level_metrics_in_the_header_apply_on_overwrite_only()
    {
        var log = await ScoredLogAsync();
        var withMetrics = log with { Eval = log.Eval with { Metrics = JsonNode.Parse("""[{"name": "accuracy", "options": {}}]""") } };

        var overwritten = await ScoreLogs.ScoreAsync(withMetrics, [Scorers.Includes()], ScoreAction.Overwrite);
        Assert.Equal(["accuracy"], Assert.Single(overwritten.Results!.Scores).Metrics.Keys);
        Assert.Equal("""[{"name":"accuracy","options":{}}]""", overwritten.Eval.Scorers![0].Metrics!.ToJsonString());

        var appended = await ScoreLogs.ScoreAsync(withMetrics, [Constant("bonus", 1.0)], ScoreAction.Append);
        Assert.Equal(["accuracy", "stderr"], appended.Results!.Scores[1].Metrics.Keys);

        var explicitMetrics = await ScoreLogs.ScoreAsync(withMetrics, [Scorers.Includes()], ScoreAction.Overwrite, options: new ScoreLogOptions { Metrics = [Metrics.Mean()] });
        Assert.Equal(["mean"], Assert.Single(explicitMetrics.Results!.Scores).Metrics.Keys);

        // regression for inspect_ai#3238: a metric the header names but that cannot be re-created only matters on overwrite
        var unavailable = log with { Eval = log.Eval with { Metrics = JsonNode.Parse("""[{"name": "fake_package/nonexistent_metric"}]""") } };
        await Assert.ThrowsAsync<NotSupportedException>(() => ScoreLogs.ScoreAsync(unavailable, [Scorers.Includes()], ScoreAction.Overwrite));
        var stillAppends = await ScoreLogs.ScoreAsync(unavailable, [Constant("bonus", 1.0)], ScoreAction.Append);
        Assert.Equal(["includes", "bonus"], stillAppends.Results!.Scores.Select(s => s.Name));
    }

    [Fact]
    public void header_metrics_are_recreated_by_name_with_their_options()
    {
        Assert.Equal("stderr", LogHeader.MetricFromLog("inspect_ai/stderr", new JsonObject { ["cluster"] = "group" }).Name);
        Assert.Equal("ci_wilson", LogHeader.MetricFromLog("ci_wilson", new JsonObject { ["level"] = 0.9 }).Name);
        Assert.Throws<NotSupportedException>(() => LogHeader.MetricFromLog("accuracy", new JsonObject { ["to_float"] = "custom" }));
        Assert.Throws<NotSupportedException>(() => LogHeader.MetricsFromLogHeader(MakeLog(MakeSample(1, "x", "x")) with { Eval = new EvalSpec { Task = "t", Dataset = new EvalDataset(), Model = "m", Metrics = JsonNode.Parse("""{"key": [{"name": "accuracy"}]}""") } }));
        Assert.Null(LogHeader.MetricsFromLogHeader(MakeLog(MakeSample(1, "x", "x")) with { Eval = new EvalSpec { Task = "t", Dataset = new EvalDataset(), Model = "m", Metrics = JsonNode.Parse("[]") } }));
    }

    [Fact]
    public async Task score_events_carry_the_scorer_name_and_the_sample_usage_inside_scorer_spans()
    {
        var usage = new Dictionary<string, ModelUsage> { ["scripted"] = new ModelUsage(5, 1, 6) };
        var log = MakeLog(MakeSample(1, "4", "4", usage: usage));

        var scored = await ScoreLogs.ScoreAsync(log, [Scorers.Includes()], ScoreAction.Overwrite);

        var events = scored.Samples![0].Events;
        var scorers = Assert.Single(events.OfType<SpanBeginEvent>(), e => e.Name == ScoreLogs.ScorersSpanName);
        Assert.Equal(ScoreLogs.ScorersSpanName, scorers.Type);
        Assert.Null(scorers.ParentId);
        var scorer = Assert.Single(events.OfType<SpanBeginEvent>(), e => e.Type == ScoreLogs.ScorerSpanType);
        Assert.Equal(("includes", scorers.Id), (scorer.Name, scorer.ParentId));
        var score = Assert.Single(events.OfType<ScoreEvent>());
        Assert.Equal("includes", score.Scorer);
        Assert.Equal(scorer.Id, score.SpanId);
        Assert.Equal(6, score.ModelUsage!["scripted"].TotalTokens);
        Assert.Null(score.RoleUsage);
        Assert.Equal(["4"], score.Target!.Values);
        Assert.Equal(2, events.OfType<SpanEndEvent>().Count());
    }

    // --- files: the path overload -------------------------------------------------------------------------------------

    [Fact]
    public async Task the_path_overload_scores_the_file_in_place_or_to_the_output_path()
    {
        var path = TempPath("run.json");
        EvalLogWriter.Write(MakeLog(MakeSample(1, "4", "4"), MakeSample(2, "no", "4")), path);

        var scored = await ScoreLogs.ScoreAsync(path, [Scorers.Includes()]);
        Assert.Equal(path, scored.Location);
        var read = EvalLogWriter.Read(path);
        Assert.Equal(["C", "I"], read.Samples!.Select(s => s.Scores!["includes"].Text));
        Assert.Equal(Json(scored.Results), Json(read.Results));
        Assert.Equal(Json(scored.Reductions), Json(read.Reductions));
        Assert.Equal(["includes"], read.Eval.Scorers!.Select(s => s.Name));

        var output = TempPath("scored.json");
        var appended = await ScoreLogs.ScoreAsync(path, [Constant("bonus", 1.0)], options: new ScoreLogOptions { OutputPath = output });
        Assert.Equal(output, appended.Location);
        Assert.Equal(["includes", "bonus"], EvalLogWriter.Read(output).Results!.Scores.Select(s => s.Name));
        Assert.Equal(["includes"], EvalLogWriter.Read(path).Results!.Scores.Select(s => s.Name));
    }

    [Fact]
    public async Task a_python_written_log_is_rescored_and_written_back_as_json()
    {
        var path = TempPath("python_eval_log.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(FixturePath("python_eval_log.json"), path);

        var scored = await ScoreLogs.ScoreAsync(path, [Scorers.Includes()], ScoreAction.Overwrite);

        Assert.Equal(["includes"], scored.Results!.Scores.Select(s => s.Name));
        var byId = scored.Samples!.ToDictionary(s => s.Id.ToString()!, s => s);
        Assert.Equal("C", byId["1"].Scores!["includes"].Text);
        Assert.Equal("I", byId["s2"].Scores!["includes"].Text);
        Assert.Equal(2, scored.Results.TotalSamples);
        Assert.Equal(1, scored.Results.CompletedSamples);
        Assert.Equal(["1", "s2"], Assert.Single(scored.Reductions!).Samples.Select(s => s.SampleId!.ToString()));
        // the fixture had no scorers span: the new one is appended after the sample's events
        Assert.Equal(ScoreLogs.ScorersSpanName, LastScorersSpan(byId["1"].Events)!.Name);
        Assert.Equal("score", byId["1"].Events.OfType<ScoreEvent>().Last().Event);
        var read = EvalLogWriter.Read(path);
        Assert.Equal(Json(scored.Results), Json(read.Results));
        Assert.Equal(Json(scored.Samples!.Select(s => s.Scores)), Json(read.Samples!.Select(s => s.Scores)));
    }

    [PythonFact]
    public async Task python_reads_the_rescored_log_and_finds_the_scorers_span()
    {
        var path = TempPath("python_eval_log.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(FixturePath("python_eval_log.json"), path);
        await ScoreLogs.ScoreAsync(path, [Scorers.Includes()], ScoreAction.Overwrite, [Reducers.Max()]);

        var output = PythonReference.Run($$"""
            import json
            from inspect_ai.log import read_eval_log
            from inspect_ai.event._tree import event_tree
            from inspect_ai.log._score import _find_scorers_span
            log = read_eval_log('{{path}}')
            print(json.dumps({
                "scores": [[s.name, s.reducer, sorted(s.metrics)] for s in log.results.scores],
                "sample_scores": {str(s.id): {k: v.value for k, v in (s.scores or {}).items()} for s in log.samples},
                "reductions": [[r.scorer, r.reducer, [str(x.sample_id) for x in r.samples]] for r in log.reductions or []],
                "scorers_span": [_find_scorers_span(event_tree(s.events)) is not None for s in log.samples],
                "score_events": [[e.scorer for e in s.events if e.event == "score" and not e.intermediate] for s in log.samples],
                "headline": log.results.headline.model_dump(exclude_none=True),
                "eval_scorers": [s.name for s in log.eval.scorers],
                "epochs_reducer": log.eval.config.epochs_reducer,
            }))
            """);
        var result = JsonNode.Parse(output)!;

        Assert.Equal("""[["includes","max",["accuracy","stderr"]]]""", result["scores"]!.ToJsonString());
        Assert.Equal("""{"1":{"includes":"C"},"s2":{"includes":"I"}}""", result["sample_scores"]!.ToJsonString());
        Assert.Equal("""[["includes","max",["1","s2"]]]""", result["reductions"]!.ToJsonString());
        Assert.Equal("[true,true]", result["scorers_span"]!.ToJsonString());
        // the fixture has no scorers span, so _get_updated_events appends rather than replaces: sample 1 keeps its legacy "match" score event
        Assert.Equal("""[["match","includes"],["includes"]]""", result["score_events"]!.ToJsonString());
        Assert.Equal("""{"scorer":"includes","score":"includes","metric":"accuracy","reducer":"max"}""", result["headline"]!.ToJsonString());
        Assert.Equal("""["includes"]""", result["eval_scorers"]!.ToJsonString());
        Assert.Equal("""["max"]""", result["epochs_reducer"]!.ToJsonString());
    }

    [PythonFact]
    public void results_and_reductions_match_python_eval_results()
    {
        var output = PythonReference.Run("""
            import json
            from inspect_ai._eval.task.results import eval_results
            from inspect_ai.scorer import Scorer, Target, accuracy, scorer, stderr
            from inspect_ai.scorer._metric import SampleScore, Score
            from inspect_ai.scorer._reducer import max_score, mean_score
            from inspect_ai.solver import TaskState

            @scorer(metrics=[accuracy(), stderr()])
            def s1() -> Scorer:
                async def score(state: TaskState, target: Target) -> Score:
                    return Score(value=1)
                return score

            data = [(1, 1.0), (1, 0.0), (2, 1.0), (2, 1.0), (3, float("nan")), (3, 0.0), ("x", 1.0)]
            scores = [{"s1": SampleScore(score=Score(value=v), sample_id=sid, scorer="s1")} for sid, v in data]
            results, reductions = eval_results(7, scores, [mean_score(), max_score()], [s1()], None, ["s1"], completed_samples=6)
            print(json.dumps({
                "scores": [[s.name, s.reducer, s.scored_samples, s.unscored_samples, {k: m.value for k, m in s.metrics.items()}] for s in results.scores],
                "reductions": [[r.scorer, r.reducer, [[str(ss.sample_id), ss.value] for ss in r.samples]] for r in reductions],
                "headline": results.headline.model_dump(exclude_none=True),
                "completed": results.completed_samples,
            }))
            """);
        var expected = JsonNode.Parse(output)!;

        var data = new (object Id, double Value)[] { (1, 1.0), (1, 0.0), (2, 1.0), (2, 1.0), (3, double.NaN), (3, 0.0), ("x", 1.0) };
        var scores = data.Select(d => (IReadOnlyDictionary<string, SampleScore>)new Dictionary<string, SampleScore> { ["s1"] = new(new Score(d.Value), d.Id, null, "s1") }).ToList();
        var s1 = Scorers.Custom("s1", (_, _, _) => Task.FromResult(new Score(1.0)), Metrics.Accuracy(), Metrics.Stderr());
        var computed = EvalResultsBuilder.ComputeResults(7, scores, [s1], ["s1"], [Reducers.Mean(), Reducers.Max()], null, completedSamples: 6);

        var expectedScores = expected["scores"]!.AsArray();
        Assert.Equal(expectedScores.Count, computed.Results.Scores.Count);
        foreach (var (wanted, actual) in expectedScores.Zip(computed.Results.Scores))
        {
            Assert.Equal(wanted![0]!.GetValue<string>(), actual.Name);
            Assert.Equal(wanted[1]!.GetValue<string>(), actual.Reducer);
            Assert.Equal(wanted[2]!.GetValue<int>(), actual.ScoredSamples);
            Assert.Equal(wanted[3]!.GetValue<int>(), actual.UnscoredSamples);
            foreach (var (metric, value) in wanted[4]!.AsObject())
            {
                Assert.Equal(value!.GetValue<double>(), actual.Metrics[metric].Value, 1e-9);
            }
        }

        var expectedReductions = expected["reductions"]!.AsArray();
        Assert.Equal(expectedReductions.Count, computed.Reductions!.Count);
        foreach (var (wanted, actual) in expectedReductions.Zip(computed.Reductions))
        {
            Assert.Equal(wanted![0]!.GetValue<string>(), actual.Scorer);
            Assert.Equal(wanted[1]!.GetValue<string>(), actual.Reducer);
            Assert.Equal(wanted[2]!.AsArray().Select(s => s![0]!.GetValue<string>()), actual.Samples.Select(s => Convert.ToString(s.SampleId, CultureInfo.InvariantCulture)));
            Assert.Equal(wanted[2]!.AsArray().Select(s => s![1]!.GetValue<double>()), actual.Samples.Select(s => s.Score.AsFloat()));
        }

        Assert.Equal(expected["headline"]!.ToJsonString(), JsonNode.Parse(Json(computed.Results.Headline))!.ToJsonString());
        Assert.Equal(6, computed.Results.CompletedSamples);
        Assert.Equal(7, computed.Results.TotalSamples);
    }
}
