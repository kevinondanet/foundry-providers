using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

/// <summary><c>value_to_float</c> ordering, the built-in metrics over mixed C/I/unscored scores, and the epoch score reducers.</summary>
public class MetricTests
{
    private static readonly ScoreReducer MeanReducer = Reducers.Mean();
    private static readonly ScoreReducer MedianReducer = Reducers.Median();
    private static readonly ScoreReducer ModeReducer = Reducers.Mode();
    private static readonly ScoreReducer MaxReducer = Reducers.Max();
    private static readonly ScoreReducer AtLeast3 = Reducers.AtLeast(3);
    private static readonly ScoreReducer AtLeast4 = Reducers.AtLeast(4);
    private static readonly ScoreReducer AtLeast5Of3 = Reducers.AtLeast(5, 3);
    private static readonly ScoreReducer PassAt2 = Reducers.PassAt(2);
    private static readonly ScoreReducer PassAt3Of2 = Reducers.PassAt(3, 2);
    private static readonly ScoreReducer PassAt5 = Reducers.PassAt(5);
    private static readonly ScoreReducer PassAt5Of2 = Reducers.PassAt(5, 2);

    private static readonly ScoreReducer[] AllReducers = [MeanReducer, MedianReducer, ModeReducer, MaxReducer, AtLeast3, PassAt2];

    private static SampleScore Sample(ScoreValue value) => new(new Score(value));

    private static Score S(ScoreValue value) => new(value);

    private static ScoreValue.List L(params double[] items) => new(items.Select(i => (ScoreValue)i).ToList());

    private static ScoreValue.Dict D(params (string Key, double Value)[] items) =>
        new(items.ToDictionary(i => i.Key, i => (ScoreValue?)(ScoreValue)i.Value, StringComparer.Ordinal));

    private static double Num(Score score) => ((ScoreValue.Num)score.Value).Value;

    private static double Num(ScoreValue value) => ((ScoreValue.Num)value).Value;

    [Fact]
    public void value_to_float_maps_numbers_and_bools()
    {
        var fn = ValueToFloat.Default;

        Assert.Equal(1.0, fn(1));
        Assert.Equal(0.5, fn(0.5));
        Assert.Equal(1.0, fn(true));
        Assert.Equal(0.0, fn(false));
    }

    [Fact]
    public void value_to_float_maps_strings_sentinels_and_yes_no()
    {
        var fn = ValueToFloat.Default;

        Assert.Equal(1.0, fn("1.0"));
        Assert.Equal(0.5, fn("0.5"));
        Assert.Equal(0.0, fn("0"));
        Assert.Equal(1.0, fn("yes"));
        Assert.Equal(0.0, fn("No"));
        Assert.Equal(1.0, fn("TRUE"));
        Assert.Equal(0.0, fn("false"));
        Assert.Equal(1.0, fn(ScoreConstants.Correct));
        Assert.Equal(0.5, fn(ScoreConstants.Partial));
        Assert.Equal(0.0, fn(ScoreConstants.Incorrect));
        Assert.Equal(0.0, fn(ScoreConstants.NoAnswer));
    }

    [Fact]
    public void value_to_float_custom_sentinels_are_checked_before_the_numeric_cast()
    {
        var words = ValueToFloat.Create("correct", "incorrect");
        Assert.Equal(1.0, words("correct"));
        Assert.Equal(0.0, words("incorrect"));

        var numeric = ValueToFloat.Create(2.0, 0.0, 1.0);
        Assert.Equal(1.0, numeric(2.0));
        Assert.Equal(0.5, numeric(1.0));
        Assert.Equal(0.0, numeric(0.0));
        Assert.Equal(0.5, numeric(true));
        Assert.Equal(1.0, numeric(2));

        var signed = ValueToFloat.Create(1, -1);
        Assert.Equal(1.0, signed(1));
        Assert.Equal(0.0, signed(-1));
        Assert.Equal(3.0, signed(3));
        Assert.Equal(0.5, signed(0.5));

        Assert.Equal(0.0, ValueToFloat.Create(1, noanswer: -99)(-99));
        Assert.Equal(0.5, ValueToFloat.Create(partial: true)(true));
    }

    [Fact]
    public void value_to_float_passes_non_finite_numbers_through()
    {
        Assert.True(double.IsNaN(ValueToFloat.Default(double.NaN)));
        Assert.True(double.IsPositiveInfinity(ValueToFloat.Default(double.PositiveInfinity)));
        Assert.True(double.IsNegativeInfinity(ValueToFloat.Default(double.NegativeInfinity)));
        Assert.True(double.IsNaN(ValueToFloat.Create(1, -1)(double.NaN)));
    }

    [Fact]
    public void value_to_float_warns_and_returns_zero_for_unconvertible_values()
    {
        var before = ProviderLogger.Warnings.Count;

        Assert.Equal(0.0, ValueToFloat.Default("foo"));
        Assert.Equal(0.0, ValueToFloat.Default("nan"));
        Assert.Equal(0.0, ValueToFloat.Default("NaN"));
        Assert.Equal(0.0, ValueToFloat.Default("inf"));
        Assert.Equal(0.0, ValueToFloat.Default("-inf"));
        Assert.Equal(0.0, ValueToFloat.Default(L(1, 2)));
        Assert.Equal(0.0, ValueToFloat.Default(D(("a", 1))));

        var warnings = ProviderLogger.Warnings.Skip(before).ToList();
        Assert.Equal(7, warnings.Count);
        Assert.Equal("Unable to convert value to float: foo", warnings[0]);
        Assert.Equal("Unable to convert value to float: [1,2]", warnings[5]);
    }

    [Fact]
    public void accuracy_and_mean_skip_unscored_samples()
    {
        SampleScore[] scores = [Sample("C"), Sample("I"), Sample("C"), new(Score.Unscored(reason: "refusal"))];

        Assert.Equal(2.0 / 3.0, Num(Metrics.Accuracy().Compute(scores)), 10);
        Assert.Equal(2.0 / 3.0, Num(Metrics.Mean().Compute(scores)), 10);
        Assert.Equal(0.0, Num(Metrics.Accuracy().Compute([])));
        Assert.Equal(0.0, Num(Metrics.Mean().Compute([new(Score.Unscored())])));
        Assert.Equal(2.0, Num(Metrics.Accuracy(_ => 2.0).Compute(scores)));
        Assert.Equal("accuracy", Metrics.Accuracy().Name);
        Assert.Equal("mean", Metrics.Mean().Name);
    }

    [Fact]
    public void stderr_and_std_use_the_sample_deviation()
    {
        SampleScore[] scores = [Sample("C"), Sample("I"), Sample("C"), Sample("I"), new(Score.Unscored())];
        var std = Math.Sqrt(1.0 / 3.0);

        Assert.Equal(std, Num(Metrics.Std().Compute(scores)), 10);
        Assert.Equal(std / 2, Num(Metrics.Stderr().Compute(scores)), 10);
        Assert.Equal(0.0, Num(Metrics.Stderr().Compute([Sample("C")])));
        Assert.Equal(0.0, Num(Metrics.Std().Compute([])));
        Assert.Equal("stderr", Metrics.Stderr().Name);
        Assert.Equal("std", Metrics.Std().Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void scalar_reducers_match_python(bool includeNan)
    {
        var scores = new List<Score> { S(6), S(0), S(0), S(0), S(8), S(4) };
        if (includeNan)
        {
            scores.Add(S(double.NaN));
        }

        Assert.Equal(3.0, Num(MeanReducer(scores)));
        Assert.Equal(2.0, Num(MedianReducer(scores)));
        Assert.Equal(0.0, Num(ModeReducer(scores)));
        Assert.Equal(8.0, Num(MaxReducer(scores)));
        Assert.Equal(1.0, Num(AtLeast3(scores)));
        Assert.Equal(0.0, Num(AtLeast4(scores)));
        Assert.Equal(0.8, Num(PassAt2(scores)), 10);
        Assert.Equal(0.95, Num(PassAt3Of2(scores)), 10);
        Assert.Equal(1.0, Num(PassAt5(scores)));
        Assert.Equal(1.0, Num(PassAt5Of2(scores)));
    }

    [Fact]
    public void reducers_yield_nan_for_all_unscored_or_empty_input()
    {
        Score[] allNan = [S(double.NaN), S(double.NaN), S(double.NaN)];

        foreach (var reducer in AllReducers.Concat([PassAt3Of2, PassAt5, PassAt5Of2]))
        {
            Assert.True(reducer(allNan).Value.IsNaN);
            Assert.True(reducer([]).Value.IsNaN);
        }
    }

    [Fact]
    public void pass_at_is_undefined_with_fewer_scored_epochs_than_k()
    {
        Assert.True(PassAt5([S(1), S(0), S(double.NaN)]).Value.IsNaN);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void list_reducers_reduce_index_by_index(bool includeNan)
    {
        var scores = new List<Score> { S(L(1, 2)), S(L(4, 3)), S(L(3, 1)), S(L(1, 2)), S(L(1, 2)) };
        if (includeNan)
        {
            scores.Add(S(L(double.NaN, double.NaN)));
        }

        Assert.Equal(L(2, 2), MeanReducer(scores).Value);
        Assert.Equal(L(1, 2), MedianReducer(scores).Value);
        Assert.Equal(L(1, 2), ModeReducer(scores).Value);
        Assert.Equal(L(4, 3), MaxReducer(scores).Value);
        Assert.Equal(L(1, 1), AtLeast3(scores).Value);
        Assert.Equal(L(1, 1), AtLeast4(scores).Value);
        Assert.Equal(L(1, 1), PassAt2(scores).Value);
    }

    [Fact]
    public void list_reducers_skip_nan_at_root_scores()
    {
        Score[] scores = [S(L(1, 2)), S(L(4, 3)), S(double.NaN), S(L(3, 1)), S(L(1, 2)), S(double.NaN), S(L(1, 2))];

        Assert.Equal(L(2, 2), MeanReducer(scores).Value);
        Assert.Equal(L(1, 2), MedianReducer(scores).Value);
        Assert.Equal(L(1, 2), ModeReducer(scores).Value);
        Assert.Equal(L(4, 3), MaxReducer(scores).Value);
        Assert.Equal(L(1, 1), AtLeast3(scores).Value);
        Assert.Equal(L(1, 1), PassAt2(scores).Value);
    }

    [Fact]
    public void list_reducers_yield_nan_elements_when_every_element_is_nan()
    {
        Score[] scores = [S(L(double.NaN, double.NaN)), S(L(double.NaN, double.NaN))];

        foreach (var reducer in AllReducers)
        {
            var list = Assert.IsType<ScoreValue.List>(reducer(scores).Value);
            Assert.Equal(2, list.Items.Count);
            Assert.All(list.Items, item => Assert.True(item.IsNaN));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void dict_reducers_reduce_key_by_key(bool includeNan)
    {
        var scores = new List<Score>
        {
            S(D(("coolness", 5), ("spiciness", 1))),
            S(D(("coolness", 4), ("spiciness", 1))),
            S(D(("coolness", 3), ("spiciness", 1))),
            S(D(("coolness", 2), ("spiciness", 1))),
            S(D(("coolness", 1), ("spiciness", 21))),
        };
        if (includeNan)
        {
            scores.Add(S(D(("coolness", double.NaN), ("spiciness", double.NaN))));
        }

        Assert.Equal(D(("coolness", 3), ("spiciness", 5)), MeanReducer(scores).Value);
        Assert.Equal(D(("coolness", 3), ("spiciness", 1)), MedianReducer(scores).Value);
        Assert.Equal(D(("coolness", 5), ("spiciness", 1)), ModeReducer(scores).Value);
        Assert.Equal(D(("coolness", 5), ("spiciness", 21)), MaxReducer(scores).Value);
        Assert.Equal(D(("coolness", 1), ("spiciness", 1)), AtLeast3(scores).Value);
        Assert.Equal(D(("coolness", 1), ("spiciness", 1)), AtLeast4(scores).Value);
        Assert.Equal(D(("coolness", 0), ("spiciness", 0)), AtLeast5Of3(scores).Value);
        Assert.Equal(D(("coolness", 1), ("spiciness", 1)), PassAt2(scores).Value);
        Assert.Equal(["coolness", "spiciness"], ((ScoreValue.Dict)MeanReducer(scores).Value).Items.Keys);
    }

    [Fact]
    public void dict_reducers_detect_the_shape_from_a_later_score_when_the_first_is_unscored()
    {
        Score[] scores = [S(double.NaN), S(D(("coolness", 4), ("spiciness", 1))), S(D(("coolness", 2), ("spiciness", 1)))];

        Assert.Equal(D(("coolness", 3), ("spiciness", 1)), MeanReducer(scores).Value);
        Assert.Equal(D(("coolness", 4), ("spiciness", 1)), MaxReducer(scores).Value);
    }

    [Fact]
    public void reducers_reject_mismatched_dict_keys_and_list_lengths()
    {
        Score[] dicts = [S(D(("coolness", 5), ("spiciness", 1))), S(D(("coolness", 4)))];
        Score[] lists = [S(L(1, 2, 3)), S(L(1, 2))];
        Score[] mixed = [S(D(("a", 1))), S(L(1))];

        foreach (var reducer in AllReducers)
        {
            Assert.Contains("mismatched keys", Assert.Throws<ArgumentException>(() => reducer(dicts)).Message);
            Assert.Contains("mismatched lengths", Assert.Throws<ArgumentException>(() => reducer(lists)).Message);
            Assert.Contains("non-dictionary", Assert.Throws<ArgumentException>(() => reducer(mixed)).Message);
        }
    }

    [Fact]
    public void reducers_apply_value_to_float_once_per_container_element()
    {
        Score[] halves = [S(D(("x", 4))), S(D(("x", 8)))];
        Assert.Equal(D(("x", 3)), Reducers.Mean(v => Num(v) / 2)(halves).Value);
        Assert.Equal(D(("x", 3)), Reducers.Median(v => Num(v) / 2)(halves).Value);

        var sentinels = ValueToFloat.Create(2.0, 0.0, 1.0);
        Score[] dicts = [S(D(("reward", 1.0), ("steps", 2.0), ("cost", 0.0))), S(D(("reward", 1.0), ("steps", 2.0), ("cost", 0.0)))];
        Score[] lists = [S(L(1.0, 2.0, 0.0)), S(L(1.0, 2.0, 0.0))];
        Assert.Equal(D(("reward", 0.5), ("steps", 1.0), ("cost", 0.0)), Reducers.Mean(sentinels)(dicts).Value);
        Assert.Equal(L(0.5, 1.0, 0.0), Reducers.Mean(sentinels)(lists).Value);
    }

    [Fact]
    public void reducers_keep_answer_explanation_and_reason_only_when_identical()
    {
        var same = new Score(1) { Answer = "1", Explanation = "An explanation", Reason = "no_response", Metadata = new Dictionary<string, object?> { ["foo"] = "bar" } };
        var different = new Score(2) { Answer = "2", Explanation = "Different explanation", Reason = "refusal", Metadata = new Dictionary<string, object?> { ["foo"] = "BAZ" } };
        Score[] all = [same, same, same, same, same, different];

        foreach (var reducer in AllReducers)
        {
            var mixed = reducer(all);
            Assert.Null(mixed.Answer);
            Assert.Null(mixed.Explanation);
            Assert.Null(mixed.Reason);
            Assert.Same(same.Metadata, mixed.Metadata);

            var identical = reducer(all[..^1]);
            Assert.Equal("1", identical.Answer);
            Assert.Equal("An explanation", identical.Explanation);
            Assert.Equal("no_response", identical.Reason);

            var single = reducer([same]);
            Assert.Equal("1", single.Answer);
            Assert.Equal("An explanation", single.Explanation);
            Assert.Equal("no_response", single.Reason);
            Assert.Same(same.Metadata, single.Metadata);
        }
    }

    [Fact]
    public void reducers_keep_the_reason_of_all_unscored_epochs_only_when_identical()
    {
        Score[] sameReason = [Score.Unscored(reason: "no_response"), Score.Unscored(reason: "no_response")];
        Score[] differentReason = [Score.Unscored(reason: "no_response"), Score.Unscored(reason: "refusal")];

        foreach (var reducer in AllReducers)
        {
            var kept = reducer(sameReason);
            Assert.True(kept.Value.IsNaN);
            Assert.Equal("no_response", kept.Reason);
            var dropped = reducer(differentReason);
            Assert.True(dropped.Value.IsNaN);
            Assert.Null(dropped.Reason);
        }
    }

    [Fact]
    public void reducers_treat_string_grades_through_value_to_float()
    {
        Score[] grades = [S("C"), S("I"), S("C"), S("P")];

        Assert.Equal(0.625, Num(MeanReducer(grades)));
        Assert.Equal("C", ModeReducer(grades).Value.Text);
        Assert.Equal("C", MaxReducer(grades).Value.Text);
        Assert.Equal(1.0, Num(Reducers.AtLeast(2)(grades)));
        Assert.Equal(0.0, Num(Reducers.AtLeast(3)(grades)));
    }

    [Fact]
    public void mode_counts_bools_and_equal_numbers_together_and_prefers_the_first_seen()
    {
        Assert.Equal(1.0, Num(ModeReducer([S(1), S(true), S(0)])));
        Assert.Equal(2.0, Num(ModeReducer([S(2), S(3), S(3), S(2)])));
        Assert.Equal("a", ModeReducer([S("a"), S("b")]).Value.Text);
    }
}
