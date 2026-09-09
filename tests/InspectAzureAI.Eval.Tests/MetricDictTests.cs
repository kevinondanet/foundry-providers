using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Runner.Scoring;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;

/// <summary>
/// Per-key metric dictionaries for dictionary-valued scores: <see cref="MetricDict"/>, <see cref="ScorerDef.MetricsByKey"/>
/// and the <c>scorers_from_metric_dict</c> / <c>resolve_glob_metric_keys</c> port in <see cref="MetricDictResults"/>.
/// Reference expectations come from <c>tests/scorer/test_metric.py</c>, <c>test_categorical.py</c> and
/// <c>test_headline_metric.py</c>, plus <c>examples/categorical_demo.py</c>.
/// </summary>
public sealed class MetricDictTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_logDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private enum Verdict
    {
        Yes,
        No,
        Unsure,
    }

    private enum SabotageType
    {
        None,
        Subtle,
        Overt,
    }

    private static SampleScore Sample(ScoreValue value, object? id = null) => new(new Score(value), id);

    private static ScoreValue.Dict Dict(params (string Key, ScoreValue? Value)[] entries)
    {
        var items = new OrderedDictionary<string, ScoreValue?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            items[key] = value;
        }

        return new ScoreValue.Dict(items);
    }

    private static Task<Score> Constant(TaskState state, Target target, CancellationToken cancellationToken) => Task.FromResult(new Score("C"));

    private static ScorerDef DictScorer(string name, MetricDict byKey, params MetricDef[] metrics) => Scorers.Custom(name, Constant, byKey, metrics);

    private static IReadOnlyList<IReadOnlyDictionary<string, SampleScore>> Scores(string scorer, params (object Id, ScoreValue Value)[] scores) =>
        scores.Select(s => (IReadOnlyDictionary<string, SampleScore>)new Dictionary<string, SampleScore> { [scorer] = new(new Score(s.Value), s.Id) }).ToList();

    private static MetricDef Named(string name, ScoreValue result, MetricScores scores = MetricScores.Auto) => new(name, _ => result) { Scores = scores };

    // ---- scorers_from_metric_dict -------------------------------------------------------------------

    [Fact]
    public void explicit_keys_yield_one_score_per_key_and_count_unscored_samples()
    {
        // tests/scorer/test_metric.py test_dict_metric_unscored_samples_mixed
        var metrics = new MetricDict { ["one"] = [Metrics.Mean()], ["two"] = [Metrics.Mean()] };
        IReadOnlyList<SampleScore> samples =
        [
            Sample(Dict(("one", 1), ("two", 2)), 1),
            Sample(ScoreValue.NaN, 2),
            Sample(Dict(("one", 3), ("two", 4)), 3),
            new SampleScore(Score.Unscored(explanation: "no result"), 4),
        ];

        var results = MetricDictResults.ScorersFromMetricDict("test_scorer", samples, metrics, null);

        Assert.Equal(["one", "two"], results.Select(r => r.Name));
        Assert.All(results, r => Assert.Equal("test_scorer", r.Scorer));
        Assert.All(results, r => Assert.Null(r.Reducer));
        Assert.Equal(2, results[0].ScoredSamples);
        Assert.Equal(2, results[0].UnscoredSamples);
        Assert.Equal(2.0, results[0].Metrics["mean"].Value);
        Assert.Equal("mean", results[0].Metrics["mean"].Name);
        Assert.Null(results[0].Metrics["mean"].Group);
        Assert.Equal(2, results[1].ScoredSamples);
        Assert.Equal(2, results[1].UnscoredSamples);
        Assert.Equal(3.0, results[1].Metrics["mean"].Value);
    }

    [Fact]
    public void glob_key_resolves_against_the_first_dict_valued_sample()
    {
        // tests/scorer/test_metric.py test_dict_metric_first_sample_unscored
        IReadOnlyList<SampleScore> samples =
        [
            Sample(ScoreValue.NaN, 1),
            Sample(Dict(("one", 5), ("two", 6)), 2),
            Sample(Dict(("one", 1), ("two", 2)), 3),
        ];

        var results = MetricDictResults.ScorersFromMetricDict("test_scorer", samples, MetricDict.ForAllKeys(Metrics.Mean()), null);

        Assert.Equal(["one", "two"], results.Select(r => r.Name));
        Assert.Equal(2, results[0].ScoredSamples);
        Assert.Equal(1, results[0].UnscoredSamples);
        Assert.Equal(3.0, results[0].Metrics["mean"].Value);
        Assert.Equal(2, results[1].ScoredSamples);
        Assert.Equal(1, results[1].UnscoredSamples);
        Assert.Equal(4.0, results[1].Metrics["mean"].Value);
    }

    [Fact]
    public void all_samples_unscored_keeps_the_literal_keys_with_nan_metrics()
    {
        // tests/scorer/test_metric.py test_dict_metric_all_samples_unscored
        var metrics = new MetricDict { ["one"] = [Metrics.Mean()], ["two"] = [Metrics.Mean()] };
        IReadOnlyList<SampleScore> samples = [new(Score.Unscored(), 1), new(Score.Unscored(), 2), new(Score.Unscored(), 3)];

        var results = MetricDictResults.ScorersFromMetricDict("test_scorer", samples, metrics, null);

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(0, r.ScoredSamples);
            Assert.Equal(3, r.UnscoredSamples);
            Assert.True(double.IsNaN(r.Metrics["mean"].Value));
        });
    }

    [Fact]
    public void null_and_nan_entries_of_a_key_are_unscored_without_affecting_other_keys()
    {
        var metrics = new MetricDict { ["one"] = [Metrics.Mean()], ["two"] = [Metrics.Mean()] };
        IReadOnlyList<SampleScore> samples =
        [
            Sample(Dict(("one", 1), ("two", null)), 1),
            Sample(Dict(("one", ScoreValue.NaN), ("two", 4)), 2),
        ];

        var results = MetricDictResults.ScorersFromMetricDict("s", samples, metrics, null);

        Assert.Equal(1, results[0].ScoredSamples);
        Assert.Equal(1, results[0].UnscoredSamples);
        Assert.Equal(1.0, results[0].Metrics["mean"].Value);
        Assert.Equal(1, results[1].ScoredSamples);
        Assert.Equal(1, results[1].UnscoredSamples);
        Assert.Equal(4.0, results[1].Metrics["mean"].Value);
    }

    [Fact]
    public void dict_and_list_metric_values_expand_with_underscore_keys()
    {
        // tests/scorer/test_metric.py test_dict_scorer_str_and_tuple_metrics, plus a dict-valued metric (frequency)
        var strMetric = Named("str_metric", "0.85");
        var tupleMetric = Named("tuple_metric", new ScoreValue.List([10, 20]));
        var metrics = new MetricDict { ["a"] = [strMetric], ["b"] = [tupleMetric, Metrics.Frequency(["C", "I"])] };
        IReadOnlyList<SampleScore> samples = [Sample(Dict(("a", 1.0), ("b", "C")), 1), Sample(Dict(("a", 1.0), ("b", "I")), 2)];

        var results = MetricDictResults.ScorersFromMetricDict("my_dict_scorer", samples, metrics, null);
        var byName = results.ToDictionary(r => r.Name, StringComparer.Ordinal);

        Assert.Equal(0.85, byName["a"].Metrics["str_metric"].Value, 1e-9);
        Assert.Equal(["tuple_metric_0", "tuple_metric_1", "frequency_C", "frequency_I"], byName["b"].Metrics.Keys);
        Assert.Equal(10.0, byName["b"].Metrics["tuple_metric_0"].Value);
        Assert.Equal(20.0, byName["b"].Metrics["tuple_metric_1"].Value);
        Assert.Equal("1", byName["b"].Metrics["tuple_metric_1"].Name);
        Assert.Equal("tuple_metric", byName["b"].Metrics["tuple_metric_1"].Group);
        Assert.Equal(0.5, byName["b"].Metrics["frequency_C"].Value);
        Assert.Equal("C", byName["b"].Metrics["frequency_C"].Name);
        Assert.Equal("frequency", byName["b"].Metrics["frequency_C"].Group);
    }

    [Fact]
    public void missing_key_and_non_dictionary_scores_throw_pythons_messages()
    {
        var metrics = new MetricDict { ["one"] = [Metrics.Mean()] };

        // keys are resolved against the first dictionary sample, so a key that matches nothing there yields no score at all
        Assert.Empty(MetricDictResults.ScorersFromMetricDict("s", [Sample(Dict(("two", 1)), 1)], metrics, null));

        var missing = Assert.Throws<InvalidOperationException>(() =>
            MetricDictResults.ScorersFromMetricDict("s", [Sample(Dict(("one", 1)), 1), Sample(Dict(("two", 2)), 2)], metrics, null));
        Assert.Equal("key 'one' isn't present in the score value dictionary", missing.Message);

        var scalar = Assert.Throws<InvalidOperationException>(() =>
            MetricDictResults.ScorersFromMetricDict("s", [Sample(1, 1)], metrics, null));
        Assert.Equal("A dictionary of metrics specified for a non-dictionary score", scalar.Message);

        var resolve = Assert.Throws<InvalidOperationException>(() => MetricDictResults.ResolveGlobMetricKeys(metrics, new Score(1)));
        Assert.StartsWith("A dictionary of metrics was specified for a non-dictionary score.", resolve.Message, StringComparison.Ordinal);
    }

    // ---- resolve_glob_metric_keys -------------------------------------------------------------------

    [Fact]
    public void glob_and_literal_keys_merge_without_duplicate_metrics()
    {
        var metrics = new MetricDict { ["*"] = [Metrics.Mean()], ["one"] = [Metrics.Mean(), Metrics.Stderr()] };
        var baseScore = new Score(Dict(("one", 1), ("two", 2)));

        var resolved = MetricDictResults.ResolveGlobMetricKeys(metrics, baseScore);

        Assert.Equal(["one", "two"], resolved.Keys);
        Assert.Equal(["mean", "stderr"], resolved["one"].Select(m => m.Name));
        Assert.Equal(["mean"], resolved["two"].Select(m => m.Name));
    }

    [Fact]
    public void glob_patterns_follow_fnmatch()
    {
        var baseScore = new Score(Dict(("one", 1), ("two", 2), ("three", 3), ("a.b", 4)));
        static IEnumerable<string> Keys(string pattern, Score score) =>
            MetricDictResults.ResolveGlobMetricKeys(new MetricDict { [pattern] = [Metrics.Mean()] }, score).Keys;

        Assert.Equal(["one", "two", "three", "a.b"], Keys("*", baseScore));
        Assert.Equal(["two"], Keys("t?o", baseScore));
        Assert.Equal(["two", "three"], Keys("t*", baseScore));
        Assert.Equal(["two", "three", "a.b"], Keys("[!o]*", baseScore));
        Assert.Equal(["one", "two"], Keys("[ot]??", baseScore));
        Assert.Equal(["a.b"], Keys("a.b", baseScore));
        Assert.Empty(Keys("a?b?", baseScore));
        Assert.Empty(Keys("ONE", baseScore));
        Assert.Equal(["one"], Keys("one", baseScore));
    }

    // ---- compute_eval_scores_for_views ---------------------------------------------------------------

    [Fact]
    public void pure_dictionary_form_produces_only_per_key_scores()
    {
        var scorer = DictScorer("behaviour", MetricDict.ForAllKeys(Metrics.Mean()));
        var scores = Scores("behaviour", (1, Dict(("a", 1), ("b", 0))), (2, Dict(("a", 0), ("b", 0))));

        var results = EvalResultsBuilder.BuildScores([scorer], ["behaviour"], scores, null, null);

        Assert.Equal(["a", "b"], results.Select(r => r.Name));
        Assert.All(results, r => Assert.Equal("behaviour", r.Scorer));
        Assert.All(results, r => Assert.Null(r.Reducer));
        Assert.Equal(0.5, results[0].Metrics["mean"].Value);
        Assert.Equal(0.0, results[1].Metrics["mean"].Value);
    }

    [Fact]
    public void list_form_with_a_dictionary_emits_the_scorer_score_too()
    {
        // tests/scorer/test_headline_metric.py: metrics=[mean(), {"dup": [mean()]}] on a scorer named dup yields two alike scores
        var scorer = DictScorer("dup", new MetricDict { ["dup"] = [Metrics.Mean()] }, Metrics.Mean());
        var scores = Scores("dup", (1, Dict(("dup", 1.0))));

        var results = EvalResultsBuilder.BuildScores([scorer], ["dup"], scores, null, null);

        Assert.Equal(["dup", "dup"], results.Select(r => r.Name));
        Assert.Equal(["dup", "dup"], results.Select(r => r.Scorer));
        Assert.Equal([0.0, 1.0], results.Select(r => r.Metrics["mean"].Value));
    }

    [Fact]
    public void reduced_and_unreduced_metrics_get_their_own_per_key_views()
    {
        // tests/scorer/test_categorical.py _dict_mixed_correctness_scorer over two epochs
        var scorer = DictScorer("grade", MetricDict.ForAllKeys(Metrics.Accuracy(), Metrics.Frequency(["C", "I"])));
        var scores = Scores("grade", (1, Dict(("a", "C"), ("b", "I"))), (1, Dict(("a", "I"), ("b", "C"))));

        var results = EvalResultsBuilder.BuildScores([scorer], ["grade"], scores, null, null);

        Assert.Equal([("a", "mean"), ("b", "mean"), ("a", null), ("b", null)], results.Select(r => (r.Name, r.Reducer)));
        Assert.Equal(["accuracy"], results[0].Metrics.Keys);
        Assert.Equal(0.5, results[0].Metrics["accuracy"].Value);
        Assert.Equal(1, results[0].ScoredSamples);
        Assert.Equal(["frequency_C", "frequency_I"], results[2].Metrics.Keys);
        Assert.Equal(0.5, results[2].Metrics["frequency_C"].Value);
        Assert.Equal(0.5, results[3].Metrics["frequency_I"].Value);
        Assert.Equal(2, results[2].ScoredSamples);
    }

    [Fact]
    public void list_form_keeps_an_empty_scorer_score_in_a_view_only_its_dictionary_uses()
    {
        // Python: [accuracy(), {"*": [frequency()]}] → the unreduced view still calls scorer_for_metrics with no metrics
        var scorer = DictScorer("grade", MetricDict.ForAllKeys(Metrics.Frequency(["C", "I"])), Metrics.Accuracy());
        var scores = Scores("grade", (1, Dict(("a", "C"))), (1, Dict(("a", "I"))));

        var results = EvalResultsBuilder.BuildScores([scorer], ["grade"], scores, null, null);

        Assert.Equal([("grade", "mean"), ("grade", null), ("a", null)], results.Select(r => (r.Name, r.Reducer)));
        Assert.Equal(["accuracy"], results[0].Metrics.Keys);
        Assert.Empty(results[1].Metrics);
        Assert.Equal(["frequency_C", "frequency_I"], results[2].Metrics.Keys);
    }

    [Fact]
    public void explicit_reducers_name_each_per_key_view()
    {
        var scorer = DictScorer("grade", MetricDict.ForAllKeys(Metrics.Accuracy()));
        var scores = Scores("grade", (1, Dict(("a", 1), ("b", 0))), (1, Dict(("a", 0), ("b", 0))));

        var results = EvalResultsBuilder.BuildScores([scorer], ["grade"], scores, [Reducers.Max(), Reducers.Mean()], null);

        Assert.Equal([("a", "max"), ("b", "max"), ("a", "mean"), ("b", "mean")], results.Select(r => (r.Name, r.Reducer)));
        Assert.Equal(1.0, results[0].Metrics["accuracy"].Value);
        Assert.Equal(0.5, results[2].Metrics["accuracy"].Value);
    }

    [Fact]
    public void disabled_reduction_with_a_reduced_metric_in_the_dictionary_throws()
    {
        var reduced = Named("needs_reduction", 1.0, MetricScores.Reduced);
        var scorer = DictScorer("grade", MetricDict.ForAllKeys(reduced));
        var repeated = Scores("grade", (1, Dict(("a", 1))), (1, Dict(("a", 0))));

        var ex = Assert.Throws<InvalidOperationException>(() => EvalResultsBuilder.BuildScores([scorer], ["grade"], repeated, [], null));
        Assert.Contains("epoch reduction is disabled", ex.Message, StringComparison.Ordinal);

        var distinct = Scores("grade", (1, Dict(("a", 1))), (2, Dict(("a", 0))));
        var results = EvalResultsBuilder.BuildScores([scorer], ["grade"], distinct, [], null);
        Assert.Equal([("a", (string?)null)], results.Select(r => (r.Name, r.Reducer)));
        Assert.Equal(2, results[0].ScoredSamples);
    }

    [Fact]
    public void a_metrics_override_replaces_the_dictionary()
    {
        var scorer = DictScorer("behaviour", MetricDict.ForAllKeys(Metrics.Accuracy()));
        var scores = Scores("behaviour", (1, Dict(("a", 1), ("b", 0))));

        var results = EvalResultsBuilder.BuildScores([scorer], ["behaviour"], scores, null, [Metrics.Mean()]);

        var score = Assert.Single(results);
        Assert.Equal("behaviour", score.Name);
        Assert.Equal(["mean"], score.Metrics.Keys);
    }

    [Fact]
    public void multi_scorer_keeps_the_first_scorers_dictionary()
    {
        var byKey = MetricDict.ForAllKeys(Metrics.Mean());
        var first = DictScorer("letters", byKey);
        var second = Scorers.Custom("other", Constant, Metrics.Accuracy());

        var multi = Scorers.MultiScorer([first, second], Reducers.Mean());

        Assert.Same(byKey, multi.MetricsByKey);
        Assert.Empty(multi.Metrics);
        Assert.Null(Scorers.MultiScorer([second, first], Reducers.Mean()).MetricsByKey);
    }

    // ---- JSON shapes ------------------------------------------------------------------------------

    [Fact]
    public void results_json_matches_pythons_per_key_shape()
    {
        var scorer = DictScorer("behaviour", new MetricDict
        {
            ["sabotage_type"] = Metrics.Categorical<SabotageType>(),
            ["eval_aware"] = Metrics.Categorical<Verdict>(),
        });
        var scores = Scores(
            "behaviour",
            (1, Dict(("sabotage_type", "none"), ("eval_aware", "yes"))),
            (2, Dict(("sabotage_type", "subtle"), ("eval_aware", "yes"))));

        var computed = EvalResultsBuilder.ComputeResults(2, scores, [scorer], ["behaviour"], null, null);
        var json = JsonNode.Parse(JsonSerializer.Serialize(computed.Results, EvalLogWriter.Options))!;

        var resultScores = json["scores"]!.AsArray();
        Assert.Equal(2, resultScores.Count);
        var sabotage = resultScores[0]!;
        Assert.Equal("sabotage_type", (string?)sabotage["name"]);
        Assert.Equal("behaviour", (string?)sabotage["scorer"]);
        Assert.Null(sabotage["reducer"]?.GetValue<string>());
        Assert.Equal(2, (int?)sabotage["scored_samples"]);
        Assert.Equal(0, (int?)sabotage["unscored_samples"]);
        var metrics = sabotage["metrics"]!.AsObject();
        Assert.Equal(["frequency_none", "frequency_subtle", "frequency_overt"], metrics.Select(pair => pair.Key));
        Assert.Equal("none", (string?)metrics["frequency_none"]!["name"]);
        Assert.Equal("frequency", (string?)metrics["frequency_none"]!["group"]);
        Assert.Equal(0.5, (double?)metrics["frequency_none"]!["value"]);
        Assert.Equal(0.0, (double?)metrics["frequency_overt"]!["value"]);
        Assert.Equal("eval_aware", (string?)resultScores[1]!["name"]);
        Assert.Equal(1.0, (double?)resultScores[1]!["metrics"]!["frequency_yes"]!["value"]);

        // the headline is the first metric of the first per-key score, as in Python
        Assert.Equal("sabotage_type", (string?)json["headline"]!["score"]);
        Assert.Equal("frequency_none", (string?)json["headline"]!["metric"]);
    }

    [Fact]
    public void log_header_records_the_dictionary_and_list_forms()
    {
        var dictForm = DictScorer("behaviour", new MetricDict { ["sabotage_type"] = [Metrics.Frequency()], ["*"] = [Metrics.Mean(), Metrics.Stderr()] });
        var listForm = DictScorer("dup", new MetricDict { ["dup"] = [Metrics.Mean()] }, Metrics.Mean());
        var plain = Scorers.Custom("plain", Constant, Metrics.Accuracy());

        var header = LogHeader.ToEvalScorers([dictForm, listForm, plain]);

        Assert.Equal(
            // frequency records its creation parameters (Python's registry_params) so the header can re-create it
            """{"sabotage_type":[{"name":"frequency","options":{"categories":null,"normalize":true}}],"*":[{"name":"mean","options":{}},{"name":"stderr","options":{}}]}""",
            header[0].Metrics!.ToJsonString());
        Assert.Equal(
            """[{"name":"mean","options":{}},{"dup":[{"name":"mean","options":{}}]}]""",
            header[1].Metrics!.ToJsonString());
        Assert.Equal("""[{"name":"accuracy","options":{}}]""", header[2].Metrics!.ToJsonString());
    }

    // ---- enum categories ----------------------------------------------------------------------------

    [Fact]
    public void category_names_follow_the_strenum_convention()
    {
        Assert.Equal(["yes", "no", "unsure"], Metrics.CategoryNames<Verdict>());
        Assert.Equal(["YES", "NO", "UNSURE"], Metrics.CategoryNames<Verdict>(v => v.ToString().ToUpperInvariant()));
        Assert.Equal("unsure", Metrics.CategoryName(Verdict.Unsure));

        var metric = Assert.Single(Metrics.Categorical<Verdict>());
        Assert.Equal("frequency", metric.Name);
        Assert.Equal(MetricScores.Unreduced, metric.Scores);
        var value = Assert.IsType<ScoreValue.Dict>(metric.Compute([Sample("yes"), Sample("yes"), Sample("no"), Sample("maybe")]));
        Assert.Equal(["yes", "no", "unsure", "maybe"], value.Items.Keys);
        Assert.Equal(0.5, ((ScoreValue.Num)value.Items["yes"]!).Value);
        Assert.Equal(0.0, ((ScoreValue.Num)value.Items["unsure"]!).Value);

        var counts = Assert.IsType<ScoreValue.Dict>(Metrics.Frequency<Verdict>(normalize: false).Compute([Sample("no"), Sample("no")]));
        Assert.Equal(2.0, ((ScoreValue.Num)counts.Items["no"]!).Value);
    }

    [Fact]
    public void metric_dict_keeps_insertion_order_and_replaces_in_place()
    {
        var dict = new MetricDict { { "b", Metrics.Mean(), Metrics.Stderr() }, { "a", Metrics.Accuracy() } };
        dict["b"] = [Metrics.Std()];
        dict.Add("c", []);

        Assert.Equal(["b", "a", "c"], dict.Keys);
        Assert.Equal(["std"], dict["b"].Select(m => m.Name));
        Assert.True(dict.TryGetValue("a", out var a));
        Assert.Equal("accuracy", Assert.Single(a).Name);
        Assert.Empty(dict["c"]);
        Assert.False(dict.ContainsKey("d"));
        Assert.Equal(3, dict.Count);
        Assert.Equal(["*"], MetricDict.ForAllKeys(Metrics.Mean()).Keys);
        Assert.Throws<ArgumentNullException>(() => Scorers.Custom("x", Constant, (MetricDict)null!));
    }

    // ---- examples/categorical_demo.py end to end -----------------------------------------------------

    private static int Draw(TaskState state) => ((int)state.SampleId + state.Epoch) % 3;

    private static Task<Score> VerdictScore(TaskState state, Target target, CancellationToken cancellationToken)
    {
        var verdict = Metrics.CategoryName((Verdict)Draw(state));
        return Task.FromResult(new Score(verdict) { Answer = verdict, Explanation = $"Grader judged the response as '{verdict}'." });
    }

    private static Task<Score> BehaviourScore(TaskState state, Target target, CancellationToken cancellationToken)
    {
        var sabotage = Metrics.CategoryName((SabotageType)Draw(state));
        var aware = Metrics.CategoryName((Verdict)Draw(state));
        return Task.FromResult(new Score(Dict(("sabotage_type", sabotage), ("eval_aware", aware))));
    }

    private static Task<Score> OneHotScore(TaskState state, Target target, CancellationToken cancellationToken)
    {
        var verdict = (Verdict)Draw(state);
        var oneHot = Dict(Enum.GetValues<Verdict>().Select(member => (Metrics.CategoryName(member), (ScoreValue?)(member == verdict ? 1 : 0))).ToArray());
        return Task.FromResult(new Score(oneHot) { Answer = Metrics.CategoryName(verdict) });
    }

    [Fact]
    public async Task the_categorical_demo_scorers_run_end_to_end()
    {
        var task = new EvalTask
        {
            Name = "categorical_demo",
            Dataset = new MemoryDataset([new Sample("Sample question 0") { Target = "yes" }, new Sample("Sample question 1") { Target = "yes" }]),
            Scorers =
            [
                Scorers.Custom("verdict", VerdictScore, [.. Metrics.Categorical<Verdict>()]),
                Scorers.Custom("behaviour", BehaviourScore, new MetricDict
                {
                    ["sabotage_type"] = Metrics.Categorical<SabotageType>(),
                    ["eval_aware"] = Metrics.Categorical<Verdict>(),
                }),
                Scorers.Custom("verdict_one_hot", OneHotScore, MetricDict.ForAllKeys(Metrics.Accuracy())),
            ],
            Epochs = new Epochs(2),
        };
        var api = new ScriptedModelApi(ScriptedTurn.Text("a"), ScriptedTurn.Text("b"), ScriptedTurn.Text("c"), ScriptedTurn.Text("d"));

        var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = _logDir, MaxSamples = 1, LogFormat = LogFormat.Json, Epochs = 2 });

        Assert.Equal(EvalStatus.Success, log.Status);
        var results = log.Results!;
        Assert.Equal(
            [("verdict", "verdict"), ("sabotage_type", "behaviour"), ("eval_aware", "behaviour"), ("yes", "verdict_one_hot"), ("no", "verdict_one_hot"), ("unsure", "verdict_one_hot")],
            results.Scores.Select(s => (s.Name, s.Scorer)));
        Assert.All(results.Scores, s => Assert.Null(s.Reducer));

        // draws: (1,1)=unsure (1,2)=yes (2,1)=yes (2,2)=no → yes 0.5, no 0.25, unsure 0.25 over the four epochs
        var verdict = results.Scores[0];
        Assert.Equal(["yes", "no", "unsure"], verdict.Metrics.Keys);
        Assert.Equal(0.5, verdict.Metrics["yes"].Value);
        Assert.Equal("frequency", verdict.Metrics["yes"].Group);
        Assert.Equal(4, verdict.ScoredSamples);

        var sabotage = results.Scores[1];
        Assert.Equal(["frequency_none", "frequency_subtle", "frequency_overt"], sabotage.Metrics.Keys);
        Assert.Equal(0.5, sabotage.Metrics["frequency_none"].Value);
        Assert.Equal(0.25, sabotage.Metrics["frequency_subtle"].Value);
        Assert.Equal(0.25, sabotage.Metrics["frequency_overt"].Value);
        Assert.Equal(4, sabotage.ScoredSamples);
        Assert.Equal(["frequency_yes", "frequency_no", "frequency_unsure"], results.Scores[2].Metrics.Keys);

        // one-hot accuracy: epochs reduced by mean per sample, then averaged over the two samples
        Assert.Equal(0.5, results.Scores[3].Metrics["accuracy"].Value);
        Assert.Equal(0.25, results.Scores[4].Metrics["accuracy"].Value);
        Assert.Equal(0.25, results.Scores[5].Metrics["accuracy"].Value);
        Assert.Equal(2, results.Scores[3].ScoredSamples);

        // the log round-trips through the JSON writer with the per-key scores intact
        var written = Assert.Single(Directory.GetFiles(_logDir, "*.json"));
        var reread = await EvalLogWriter.ReadAsync(written);
        Assert.Equal(results.Scores.Select(s => s.Name), reread.Results!.Scores.Select(s => s.Name));
        Assert.Equal(0.25, reread.Results.Scores[1].Metrics["frequency_overt"].Value);
    }
}
