using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.Tools;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Runner.Scoring;
using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Eval.Tests;

using Scorers = InspectAzureAI.Eval.Scorers.Scorers;

/// <summary>
/// The dictionary part of a task-level metrics override (<c>Task.metrics</c> as <c>list[Metric | dict[str, list[Metric]]]</c>)
/// and the round trip of per-key / frequency metric definitions through the log header.
/// </summary>
public sealed class MetricsByKeyOverrideTests
{
    private static ScorerDef Scorer(string name, params MetricDef[] metrics) => new(name, (_, _, _) => Task.FromResult(new Score("C")), metrics);

    private static ScoreValue Dict(double a, double b) => new ScoreValue.Dict(new Dictionary<string, ScoreValue?> { ["a"] = a, ["b"] = b });

    private static IReadOnlyList<IReadOnlyDictionary<string, SampleScore>> Samples(string scorer, params (object Id, ScoreValue Value)[] scores) =>
        scores.Select(s => (IReadOnlyDictionary<string, SampleScore>)new Dictionary<string, SampleScore> { [scorer] = new(new Score(s.Value), s.Id) }).ToList();

    [Fact]
    public void a_task_level_metrics_by_key_override_replaces_the_scorer_metrics_with_the_dictionary_form()
    {
        // the scorer declares plain metrics; the task overrides with metrics={"*": [mean()]} (dictionary form: only per-key scores)
        var scorer = Scorer("grade", Metrics.Accuracy());
        var samples = Samples("grade", (1, Dict(1, 0)), (2, Dict(0, 0)));
        var byKey = MetricDict.ForAllKeys(Metrics.Mean());

        var scores = EvalResultsBuilder.BuildScores([scorer], ["grade"], samples, null, null, byKey);

        Assert.Equal(["a", "b"], scores.Select(score => score.Name));
        Assert.All(scores, score => Assert.Equal("grade", score.Scorer));
        Assert.Equal(0.5, scores[0].Metrics["mean"].Value);
        Assert.Equal(0.0, scores[1].Metrics["mean"].Value);
    }

    [Fact]
    public void a_task_level_list_override_keeps_dropping_the_scorer_dictionary_metrics()
    {
        // metrics=[accuracy()] at task level replaces the scorer's dictionary metrics too (Python's Metrics union)
        var scorer = Scorer("grade", Metrics.Accuracy()) with { MetricsByKey = MetricDict.ForAllKeys(Metrics.Mean()) };
        var samples = Samples("grade", (1, "C"), (2, "I"));

        var scores = EvalResultsBuilder.BuildScores([scorer], ["grade"], samples, null, [Metrics.Accuracy()]);

        var score = Assert.Single(scores);
        Assert.Equal("grade", score.Name);
        Assert.Equal(["accuracy"], score.Metrics.Keys);
        Assert.Equal(0.5, score.Metrics["accuracy"].Value);
    }

    [Fact]
    public void a_task_level_list_and_dictionary_override_is_the_list_form()
    {
        var scorer = Scorer("grade", Metrics.Accuracy());
        var samples = Samples("grade", (1, Dict(1, 0)), (2, Dict(1, 1)));

        var scores = EvalResultsBuilder.BuildScores([scorer], ["grade"], samples, null, [Metrics.Mean()], MetricDict.ForAllKeys(Metrics.Mean()));

        Assert.Equal(["grade", "a", "b"], scores.Select(score => score.Name));
    }

    [Fact]
    public void header_metrics_round_trip_the_dictionary_and_list_forms()
    {
        var dictionary = Scorers.Custom("cat", (_, _, _) => Task.FromResult(new Score("C")), MetricDict.ForAllKeys(Metrics.Mean(), Metrics.Stderr()));
        var (metrics, byKey) = HeaderScorers.MetricsOf(LogHeader.ToEvalScorers([dictionary])[0]);
        Assert.Empty(metrics);
        Assert.Equal(["mean", "stderr"], Assert.Single(byKey!).Value.Select(m => m.Name));
        Assert.Equal("*", Assert.Single(byKey!.Keys));

        var list = Scorers.Custom("cat", (_, _, _) => Task.FromResult(new Score("C")), new MetricDict { ["a"] = [Metrics.Accuracy()], ["b*"] = [Metrics.Mean()] }, Metrics.Accuracy());
        (metrics, byKey) = HeaderScorers.MetricsOf(LogHeader.ToEvalScorers([list])[0]);
        Assert.Equal(["accuracy"], metrics.Select(m => m.Name));
        Assert.Equal(["a", "b*"], byKey!.Keys);
        Assert.Equal("mean", Assert.Single(byKey["b*"]).Name);

        // a plain list is unchanged, and a missing name is still an error
        (metrics, byKey) = HeaderScorers.MetricsOf(new EvalScorer("plain") { Metrics = new JsonArray(new JsonObject { ["name"] = "accuracy", ["options"] = new JsonObject() }) });
        Assert.Null(byKey);
        Assert.Equal("accuracy", Assert.Single(metrics).Name);
        Assert.Throws<NotSupportedException>(() => HeaderScorers.MetricsOf(new EvalScorer("odd") { Metrics = new JsonArray(new JsonObject { ["*"] = new JsonObject() }) }));
    }

    [Fact]
    public void frequency_records_its_categories_and_normalize_in_the_header_and_is_re_created_from_them()
    {
        var scorer = Scorer("cat", Metrics.Frequency(["yes", "no"], normalize: false));
        var header = LogHeader.ToEvalScorers([scorer])[0];

        Assert.Equal("""[{"name":"frequency","options":{"categories":["yes","no"],"normalize":false}}]""", header.Metrics!.ToJsonString());

        var (metrics, byKey) = HeaderScorers.MetricsOf(header);
        Assert.Null(byKey);
        var frequency = Assert.Single(metrics);
        Assert.Equal("frequency", frequency.Name);
        Assert.Equal(MetricScores.Unreduced, frequency.Scores);

        // zero-count categories come back and counts stay un-normalized
        var result = Assert.IsType<ScoreValue.Dict>(frequency.Compute([new SampleScore(new Score("yes"), 1), new SampleScore(new Score("yes"), 2)]));
        Assert.Equal(["yes", "no"], result.Items.Keys);
        Assert.Equal(2.0, Assert.IsType<ScoreValue.Num>(result.Items["yes"]).Value);
        Assert.Equal(0.0, Assert.IsType<ScoreValue.Num>(result.Items["no"]).Value);

        // the default frequency() records null categories and normalize=true
        var defaults = LogHeader.ToEvalScorers([Scorer("cat", Metrics.Frequency())])[0];
        Assert.Equal("""[{"name":"frequency","options":{"categories":null,"normalize":true}}]""", defaults.Metrics!.ToJsonString());
        Assert.Equal("frequency", LogHeader.MetricFromLog("inspect_ai/frequency", new JsonObject { ["categories"] = null, ["normalize"] = true }).Name);
    }
}
