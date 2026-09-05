using System.Text.Json;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Scorers = InspectAzureAI.Eval.Scorers.Scorers;

/// <summary>The extra scorers ported from <c>scorer/</c>: <c>f1</c>, <c>exact</c>, <c>cascade</c>, <c>multi_scorer</c>, <c>precomputed_scores</c> and <c>math</c>.</summary>
public class ScorerPortTests : IDisposable
{
    private const string C = ScoreConstants.Correct;
    private const string I = ScoreConstants.Incorrect;

    private readonly string _directory = Directory.CreateTempSubdirectory("scorer-port-tests").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static TaskState State(string output, object? sampleId = null, int epoch = 1) =>
        new("scripted", sampleId ?? 1, epoch, "question", [new ChatMessageUser("question")], output: ModelOutput.FromContent("scripted", output));

    private static Task<Score> Run(ScorerDef scorer, string output, params string[] targets) =>
        scorer.Score(State(output), new Target(targets), CancellationToken.None);

    private static double Num(Score score) => ((ScoreValue.Num)score.Value).Value;

    private static Scorer Stage(string name, Score? result, List<string> calls) =>
        (_, _, _) =>
        {
            calls.Add(name);
            return Task.FromResult(result!);
        };

    private static ScorerDef Fixed(string name, ScoreValue value, params MetricDef[] metrics) =>
        new(name, (_, _, _) => Task.FromResult(new Score(value)), metrics);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteRecords(string name, params object[] records) => Write(name, JsonSerializer.Serialize(records));

    // ---- f1 / exact ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("foo", new[] { "foo" }, 1.0)]
    [InlineData("foo1", new[] { "foo" }, 0.0)]
    [InlineData("Paris", new[] { "Paris, Texas", "Paris" }, 1.0)]
    [InlineData("Paris", new[] { "Paris, Texas" }, 0.67)]
    [InlineData("(3.14)", new[] { "3.14" }, 1.0)]
    [InlineData("The answer is 3.14.", new[] { "3.14" }, 0.5)]
    [InlineData("the quick brown fox", new[] { "a quick brown dog" }, 0.67)]
    [InlineData("New-York city", new[] { "new york" }, 0.8)]
    [InlineData("It's 1e3 dollars", new[] { "1000.0 dollars" }, 0.8)]
    [InlineData("hello", new[] { " hello" }, 1.0)]
    [InlineData("hello", new[] { "" }, 0.0)]
    [InlineData("hello", new[] { "", "hello" }, 1.0)]
    public async Task f1_matches_python(string output, string[] targets, double expected)
    {
        var score = await Run(Scorers.F1(), output, targets);

        Assert.Equal(expected, Num(score), 1e-9);
        Assert.Equal(output, score.Answer);
    }

    [Fact]
    public async Task f1_stop_words_answer_fn_and_metrics()
    {
        Assert.Equal(0.0, Num(await Run(Scorers.F1(stopWords: ["Paris"]), "Paris", "Paris, Texas")));
        Assert.Equal(1.0, Num(await Run(Scorers.F1(stopWords: ["Texas"]), "Paris", "Paris, Texas")));
        var extracted = await Run(Scorers.F1(answerFn: completion => completion.Split(':')[^1]), "ANSWER: Paris", "Paris");
        Assert.Equal(1.0, Num(extracted));
        Assert.Equal(" Paris", extracted.Answer);
        Assert.Equal("f1", Scorers.F1().Name);
        Assert.Equal(["mean", "stderr"], Scorers.F1().Metrics.Select(m => m.Name));
        Assert.Equal(0.6666666666666666, Classification.ComputeF1("Paris", "Paris, Texas"), 1e-9);
    }

    [Theory]
    [InlineData("foo", new[] { "foo" }, C)]
    [InlineData("foo", new[] { "foobar", "boofar", "foo" }, C)]
    [InlineData("foo1", new[] { "foo" }, I)]
    [InlineData("(3.14)", new[] { "3.14" }, C)]
    [InlineData("3,000", new[] { "3000" }, C)]
    [InlineData("$1,000.", new[] { "1000" }, C)]
    [InlineData("don't", new[] { "dont" }, C)]
    [InlineData("U.S.", new[] { "US" }, C)]
    [InlineData("world hello", new[] { "hello world" }, I)]
    [InlineData("hello hello", new[] { "hello" }, I)]
    [InlineData("hello", new[] { "" }, I)]
    public async Task exact_matches_python(string output, string[] targets, string expected)
    {
        var score = await Run(Scorers.Exact(), output, targets);

        Assert.Equal(expected, score.Text);
        Assert.Equal(output, score.Answer);
        Assert.Equal("exact", Scorers.Exact().Name);
    }

    // ---- cascade ------------------------------------------------------------------------------------

    [Fact]
    public async Task cascade_short_circuits_at_the_first_settling_stage()
    {
        var calls = new List<string>();
        var scorer = Scorers.Cascade(("exact", Stage("exact", new Score(C), calls)), ("grader", Stage("grader", new Score(C), calls)));

        var result = await Run(scorer, "x", "x");

        Assert.Equal(["exact"], calls);
        Assert.Equal(C, result.Text);
        Assert.Equal("exact", result.Metadata!["decided_by"]);
        Assert.Equal("cascade", scorer.Name);
        Assert.Equal(["accuracy", "stderr"], scorer.Metrics.Select(m => m.Name));
    }

    [Fact]
    public async Task cascade_falls_through_skips_unscored_stages_and_reports_the_decider()
    {
        var calls = new List<string>();
        var fallsThrough = await Run(Scorers.Cascade(("exact", Stage("exact", new Score(I), calls)), ("grader", Stage("grader", new Score(C), calls))), "x", "x");
        Assert.Equal(["exact", "grader"], calls);
        Assert.Equal(C, fallsThrough.Text);
        Assert.Equal("grader", fallsThrough.Metadata!["decided_by"]);

        calls.Clear();
        var lastScored = await Run(Scorers.Cascade(("a", Stage("a", new Score(I), calls)), ("b", Stage("b", new Score(I), calls))), "x", "x");
        Assert.Equal(["a", "b"], calls);
        Assert.Equal(I, lastScored.Text);
        Assert.Equal("b", lastScored.Metadata!["decided_by"]);

        calls.Clear();
        var declined = await Run(Scorers.Cascade(("a", Stage("a", null, calls)), ("b", Stage("b", new Score(I), calls))), "x", "x");
        Assert.Equal(["a", "b"], calls);
        Assert.Equal("b", declined.Metadata!["decided_by"]);

        var unscoredStage = await Run(Scorers.Cascade(("a", Stage("a", Score.Unscored(), calls)), ("b", Stage("b", new Score(C), calls))), "x", "x");
        Assert.Equal(C, unscoredStage.Text);
        Assert.Equal("b", unscoredStage.Metadata!["decided_by"]);

        calls.Clear();
        var allDeclined = await Run(Scorers.Cascade(("a", Stage("a", null, calls)), ("b", Stage("b", Score.Unscored(), calls))), "x", "x");
        Assert.Equal(["a", "b"], calls);
        Assert.True(allDeclined.IsUnscored);
        Assert.Equal(ScoreReason.ScoringFailed, allDeclined.Reason);
        Assert.Null(allDeclined.Metadata);
    }

    [Fact]
    public async Task cascade_threshold_metadata_and_immutability()
    {
        var calls = new List<string>();
        var partialSettles = await Run(Scorers.Cascade(0.5, ("a", Stage("a", new Score(ScoreConstants.Partial), calls)), ("b", Stage("b", new Score(C), calls))), "x", "x");
        Assert.Equal(["a"], calls);
        Assert.Equal(ScoreConstants.Partial, partialSettles.Text);
        Assert.Equal("a", partialSettles.Metadata!["decided_by"]);

        calls.Clear();
        var partialDoesNot = await Run(Scorers.Cascade(("a", Stage("a", new Score(ScoreConstants.Partial), calls)), ("b", Stage("b", new Score(C), calls))), "x", "x");
        Assert.Equal(["a", "b"], calls);
        Assert.Equal("b", partialDoesNot.Metadata!["decided_by"]);

        var original = new Score(C) { Metadata = new Dictionary<string, object?> { ["foo"] = "bar" } };
        var preserved = await Run(Scorers.Cascade(("a", Stage("a", original, calls))), "x", "x");
        Assert.Equal("bar", preserved.Metadata!["foo"]);
        Assert.Equal("a", preserved.Metadata["decided_by"]);
        Assert.False(original.Metadata!.ContainsKey("decided_by"));

        var bare = new Score(C);
        var decorated = await Run(Scorers.Cascade(("a", Stage("a", bare, calls))), "x", "x");
        Assert.Null(bare.Metadata);
        Assert.Equal("a", decorated.Metadata!["decided_by"]);

        var named = await Run(Scorers.Cascade([Fixed("exact", I), Fixed("grader", C)]), "x", "x");
        Assert.Equal("grader", named.Metadata!["decided_by"]);
        Assert.Contains("threshold", Assert.Throws<ArgumentException>(() => Scorers.Cascade(("threshold", Stage("t", null, calls)))).Message);
    }

    // ---- multi_scorer -------------------------------------------------------------------------------

    [Fact]
    public async Task multi_scorer_runs_every_scorer_and_reduces()
    {
        var scorer = Scorers.MultiScorer([Fixed("a", 1, Metrics.Mean()), Fixed("b", 0), Fixed("c", 1)], Reducers.Mean());

        var result = await Run(scorer, "x", "x");

        Assert.Equal(2.0 / 3.0, Num(result), 1e-9);
        Assert.Equal("multi_scorer", scorer.Name);
        Assert.Equal(["mean"], scorer.Metrics.Select(m => m.Name));

        var majority = await Run(Scorers.MultiScorer([Fixed("a", C), Fixed("b", C), Fixed("c", I)], "majority"), "x", "x");
        Assert.Equal(C, majority.Text);
        Assert.Equal(3, Assert.IsType<Dictionary<string, object?>>(majority.Metadata!["panel"])["size"]);

        var collected = await Run(Scorers.MultiScorer([Fixed("a", C), Fixed("b", I)], Reducers.Collect()), "x", "x");
        Assert.Equal(new ScoreValue.List([C, I]), collected.Value);

        var withUnscored = await Run(Scorers.MultiScorer([Fixed("a", 1), Fixed("b", double.NaN)], Reducers.Mean()), "x", "x");
        Assert.Equal(1.0, Num(withUnscored));

        var empty = await Run(Scorers.MultiScorer([], Reducers.Mean()), "x", "x");
        Assert.True(empty.IsUnscored);
        Assert.Equal(ScoreReason.ScoringFailed, empty.Reason);
        Assert.Empty(Scorers.MultiScorer([], Reducers.Mean()).Metrics);
    }

    [Fact]
    public async Task multi_scorer_runs_scorers_concurrently_and_propagates_cancellation()
    {
        var started = 0;
        var release = new TaskCompletionSource();
        ScorerDef Waiting(string name) => new(name, async (_, _, ct) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                release.SetResult();
            }

            await release.Task.WaitAsync(ct);
            return new Score(1);
        }, []);

        var result = await Scorers.MultiScorer([Waiting("a"), Waiting("b")], Reducers.Mean()).Score(State("x"), new Target("x"), new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        Assert.Equal(1.0, Num(result));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ScorerDef cancelled = new("c", async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new Score(1);
        }, []);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scorers.MultiScorer([cancelled], Reducers.Mean()).Score(State("x"), new Target("x"), cts.Token));
    }

    // ---- precomputed_scores -------------------------------------------------------------------------

    [Fact]
    public async Task precomputed_scores_reads_json_records_by_id()
    {
        var path = WriteRecords("scores.json",
            new { id = "s1", value = 1, answer = "yes", explanation = "rated by human", metadata = new { rater = "alice" } },
            new { id = 3, value = 0 });
        var scorer = Scorers.PrecomputedScores(path);

        var first = await scorer.Score(State("x", "s1"), new Target("a1"), CancellationToken.None);
        Assert.Equal(1.0, Num(first));
        Assert.Equal("yes", first.Answer);
        Assert.Equal("rated by human", first.Explanation);
        Assert.Equal("alice", first.Metadata!["rater"]);

        Assert.Equal(0.0, Num(await scorer.Score(State("x", 3), new Target("a3"), CancellationToken.None)));
        Assert.Equal(0.0, Num(await scorer.Score(State("x", "3"), new Target("a3"), CancellationToken.None)));

        var missing = await scorer.Score(State("x", "s2"), new Target("a2"), CancellationToken.None);
        Assert.True(missing.IsUnscored);
        Assert.Contains("No score record", missing.Explanation);
        Assert.Equal("precomputed_scores", scorer.Name);
        Assert.Equal(["accuracy", "stderr"], scorer.Metrics.Select(m => m.Name));
        Assert.Equal(["mean"], Scorers.PrecomputedScores(path, metrics: [Metrics.Mean()]).Metrics.Select(m => m.Name));
    }

    [Fact]
    public async Task precomputed_scores_reads_jsonl_and_prefers_epoch_specific_records()
    {
        var jsonl = Write("scores.jsonl", "{\"id\": \"s1\", \"value\": 1}\n\n{\"id\": \"s2\", \"value\": 0.5}\n");
        var scorer = Scorers.PrecomputedScores(jsonl);
        Assert.Equal(1.0, Num(await scorer.Score(State("x", "s1"), new Target("a"), CancellationToken.None)));
        Assert.Equal(0.5, Num(await scorer.Score(State("x", "s2"), new Target("a"), CancellationToken.None)));

        var epochs = Scorers.PrecomputedScores(WriteRecords("epochs.json", new { id = "s1", value = 1 }, new { id = "s2", value = 0 }, new { id = "s2", epoch = 2, value = 1 }));
        Assert.Equal(1.0, Num(await epochs.Score(State("x", "s1", 1), new Target("a"), CancellationToken.None)));
        Assert.Equal(1.0, Num(await epochs.Score(State("x", "s1", 2), new Target("a"), CancellationToken.None)));
        Assert.Equal(0.0, Num(await epochs.Score(State("x", "s2", 1), new Target("a"), CancellationToken.None)));
        Assert.Equal(1.0, Num(await epochs.Score(State("x", "s2", 2), new Target("a"), CancellationToken.None)));
        Assert.True((await epochs.Score(State("x", 3, 1), new Target("a"), CancellationToken.None)).IsUnscored);

        var uri = Scorers.PrecomputedScores(new Uri(jsonl).AbsoluteUri);
        Assert.Equal(1.0, Num(await uri.Score(State("x", "s1"), new Target("a"), CancellationToken.None)));
    }

    [Fact]
    public async Task precomputed_scores_on_missing_error_and_validation()
    {
        var path = WriteRecords("one.json", new { id = "s1", value = 1 });

        var strict = Scorers.PrecomputedScores(path, onMissing: "error");
        Assert.Equal(1.0, Num(await strict.Score(State("x", "s1"), new Target("a"), CancellationToken.None)));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => strict.Score(State("x", "s2", 2), new Target("a"), CancellationToken.None));
        Assert.Contains("No score record", ex.Message);
        Assert.Contains("'s2' (epoch 2)", ex.Message);

        Assert.Contains("on_missing", Assert.Throws<ArgumentException>(() => Scorers.PrecomputedScores(path, onMissing: "nope")).Message);
        Assert.Contains("Duplicate", Assert.Throws<ArgumentException>(() => Scorers.PrecomputedScores(WriteRecords("dup.json", new { id = "s1", value = 1 }, new { id = "s1", value = 0 }))).Message);
        Assert.Contains("and epoch 2", Assert.Throws<ArgumentException>(() => Scorers.PrecomputedScores(WriteRecords("dup2.json", new { id = "s1", epoch = 2, value = 1 }, new { id = "s1", epoch = 2, value = 0 }))).Message);
        Assert.Contains("'value'", Assert.Throws<ArgumentException>(() => Scorers.PrecomputedScores(WriteRecords("novalue.json", new { id = "s1" }))).Message);
        Assert.Contains("'id'", Assert.Throws<ArgumentException>(() => Scorers.PrecomputedScores(WriteRecords("noid.json", new { value = 1 }))).Message);
        Assert.Contains("list", Assert.Throws<ArgumentException>(() => Scorers.PrecomputedScores(Write("notalist.json", "{\"s1\": 1}"))).Message);
        Assert.Contains("objects", Assert.Throws<ArgumentException>(() => Scorers.PrecomputedScores(Write("notobjects.json", "[1]"))).Message);
        Assert.Contains("non-integer 'epoch'", Assert.Throws<ArgumentException>(() => Scorers.PrecomputedScores(WriteRecords("badepoch.json", new { id = "s1", epoch = 1.5, value = 1 }))).Message);
        Assert.Throws<NotSupportedException>(() => Scorers.PrecomputedScores("s3://bucket/scores.json"));
        Assert.Throws<FileNotFoundException>(() => Scorers.PrecomputedScores(Path.Combine(_directory, "missing.json")));
    }

    [Fact]
    public async Task precomputed_scores_returns_a_copy_of_the_record()
    {
        var scorer = Scorers.PrecomputedScores(WriteRecords("copy.json", new { id = "s1", value = new { a = 1, b = "x" }, metadata = new { tags = new[] { "t" } } }));

        var first = await scorer.Score(State("x", "s1"), new Target("a"), CancellationToken.None);
        var second = await scorer.Score(State("x", "s1"), new Target("a"), CancellationToken.None);

        Assert.Equal(first.Value, second.Value);
        Assert.NotSame(first.Metadata, second.Metadata);
        ((List<object?>)first.Metadata!["tags"]!).Add("mutated");
        Assert.Single((List<object?>)second.Metadata!["tags"]!);
    }

    // ---- math ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("The answer is \\boxed{42}", "42", C)]
    [InlineData("\\boxed{\\frac{1}{2}}", "0.5", C)]
    [InlineData("\\boxed{42}", "43", I)]
    [InlineData("The answer is 42", "42", C)]
    [InlineData("\\boxed{\\sqrt2}", "$\\sqrt{2}$", C)]
    [InlineData("\\boxed{33.5\\%}", "0.335", C)]
    [InlineData("The answer is $$42$$", "42", C)]
    [InlineData("\\boxed{\\sqrt{\\sqrt{2}}}", "2**(1/4)", C)]
    [InlineData("$\\{1,2,3\\}$", "$\\{3, 2, 1\\}$", C)]
    [InlineData("$\\left(1, 2\\right)$", "$\\left(1, 2\\right)$", C)]
    [InlineData("\\boxed{1.5 \\times 10^3}", "1500", C)]
    [InlineData("-1 + sqrt(3)", "\\sqrt{3} - 1", C)]
    [InlineData("(3*sqrt(5))/7", "\\frac{3\\sqrt{5}}{7}", C)]
    [InlineData("\\left( 3, \\frac{\\pi}{2} \\right)", "\\left(3,\\frac{\\pi}{2}\\right)", C)]
    [InlineData("(-\\infty,-8)\\cup (8,\\infty)", "(-\\infty,-8)\\cup (8,\\infty)", C)]
    [InlineData("odd $n$", "odd $n$", C)]
    [InlineData("$g(x)=\\frac{1}{3} ((2x)^a +(2x)^{-a})$ for some $a>0$", "$g(x)=\\frac{1}{3} ((2x)^a +(2x)^{-a})$ for some $a>0$", C)]
    [InlineData("\\boxed{\\binom{2026}{1013}}", "\\binom{2026}{1013}", C)]
    [InlineData("\\boxed{2024^{4046}}", "2024^{4046}", C)]
    [InlineData("\\boxed{1/2004!}", "1/2004!", C)]
    [InlineData("\\boxed{2^{2025}}", "3^{2025}", I)]
    [InlineData("\\boxed{10\\text{ cm}}", "10", C)]
    [InlineData("\\boxed{\\$15,\\!000}", "15000", C)]
    [InlineData("x=2", "2=x", C)]
    [InlineData("\\boxed{\\;\\angle APC = 74^\\circ\\;}", "74^\\circ", C)]
    [InlineData("\\beginboxed{8\\sqrt{6}}", "8\\sqrt{6}", C)]
    [InlineData("\\boxed{74°}", "74^\\circ", C)]
    [InlineData("\\frac{6\\sqrt{1789-240\\sqrt{21}}+8\\sqrt{1236-240\\sqrt{21}}}{5}", "96-10\\sqrt{21}", C)]
    [InlineData("The total is 1,234.5 dollars", "1234.5", C)]
    [InlineData("Final answer: 3/4", "0.75", C)]
    [InlineData("Result = 12.5%", "\\frac{1}{8}", C)]
    [InlineData("I get 99999999999", "100000000000", I)]
    [InlineData("\\boxed{1\\frac{1}{2}}", "1.5", C)]
    [InlineData("\\boxed{5!}", "120", C)]
    [InlineData("\\boxed{2^{10}}", "1024", C)]
    [InlineData("**42**", "42", C)]
    [InlineData("π", "3.141592653589793", C)]
    [InlineData("\\boxed{\\sqrt{16}}", "4", C)]
    public async Task math_scorer_matches_python_cases(string output, string target, string expected)
    {
        var score = await Run(Scorers.Math(), output, target);

        Assert.Equal(expected, score.Text);
        Assert.Equal(expected == C ? "correct" : "incorrect", score.Metadata!["math_scorer_status"]);
        Assert.Equal(output, score.Explanation);
        Assert.NotNull(score.Answer);
    }

    [Fact]
    public async Task math_scorer_uses_the_last_boxed_answer_even_after_a_flood()
    {
        var scorer = Scorers.Math();

        Assert.Equal(C, (await Run(scorer, "\\boxed{10} and then \\boxed{42}", "42")).Text);
        Assert.Equal(I, (await Run(scorer, "\\boxed{10} and then \\boxed{42}", "\\{10,42\\}")).Text);
        var flood = string.Join(" ", Enumerable.Repeat("\\boxed{9}", 400)) + " \\boxed{42}";
        Assert.Equal(C, (await Run(scorer, flood, "42")).Text);
    }

    [Theory]
    [InlineData("x=2")]
    [InlineData("2=x")]
    [InlineData("2 = y")]
    [InlineData("z = 2")]
    public async Task math_scorer_assignment_targets_match_either_orientation(string target)
    {
        Assert.Equal(C, (await Run(Scorers.Math(), "2", target)).Text);
    }

    [Fact]
    public async Task math_scorer_handles_target_alternatives_and_unparseable_targets()
    {
        var scorer = Scorers.Math();

        Assert.Equal(C, (await Run(scorer, "\\boxed{42}", "__import__('os').system('false')", "42")).Text);

        var unscored = await Run(scorer, "\\boxed{42}", "__import__('os').system('false')");
        Assert.True(unscored.IsUnscored);
        Assert.Equal("target_parse_error", unscored.Metadata!["math_scorer_status"]);
        Assert.Contains("non-mathematical code syntax", unscored.Explanation);

        var empty = await Run(scorer, "\\boxed{42}");
        Assert.True(empty.IsUnscored);
        Assert.Contains("target is empty", empty.Explanation);
        Assert.Equal("math", scorer.Name);
        Assert.Equal(["accuracy", "stderr"], scorer.Metrics.Select(m => m.Name));
    }

    [Fact]
    public async Task math_scorer_reports_answer_parse_and_limit_statuses()
    {
        var scorer = Scorers.Math();

        var unsafeAnswer = await Run(scorer, "\\boxed{__import__('os')}", "42");
        Assert.Equal(I, unsafeAnswer.Text);
        Assert.Equal("answer_parse_error", unsafeAnswer.Metadata!["math_scorer_status"]);
        Assert.Contains("Could not parse mathematical answer", unsafeAnswer.Explanation);
        Assert.Null(unsafeAnswer.Answer);

        var tooLong = await Run(scorer, "\\boxed{" + new string('1', 300) + "}", "1");
        Assert.Equal(I, tooLong.Text);
        Assert.Equal("answer_limit", tooLong.Metadata!["math_scorer_status"]);
        Assert.Contains("complexity limit", tooLong.Explanation);

        var eager = await Run(scorer, "\\boxed{\\det(A)}", "1");
        Assert.Equal("answer_limit", eager.Metadata!["math_scorer_status"]);

        var empty = await Run(scorer, "", "1");
        Assert.Equal("answer_parse_error", empty.Metadata!["math_scorer_status"]);
    }

    [Fact]
    public async Task math_scorer_falls_back_to_a_numeric_candidate_behind_prose()
    {
        var scorer = Scorers.Math();

        var prose = await Run(scorer, "The answer is 42 because of the theorem", "42");
        Assert.Equal(C, prose.Text);
        Assert.Equal("42", prose.Answer);

        var wrong = await Run(scorer, "The answer is 41 because of the theorem", "42");
        Assert.Equal(I, wrong.Text);

        var text = await Run(scorer, "x^2 + 1", "x^2 + 1");
        Assert.Equal(C, text.Text);
    }

    [Theory]
    [InlineData("x^2 + 1", "1 + x^2")]
    [InlineData("\\boxed{x^2 - x^2}", "0")]
    [InlineData("x<2", "2>x")]
    public async Task math_scorer_known_deviation_symbolic_answers_compare_as_text(string output, string target)
    {
        // Python's SymPy path proves these equivalent (CORRECT); the numeric-subset port only compares symbolic
        // answers by normalized text, so they score INCORRECT here. Documented in docs/ports/metrics.md.
        var score = await Run(Scorers.Math(), output, target);

        Assert.Equal(I, score.Text);
        Assert.Equal("incorrect", score.Metadata!["math_scorer_status"]);
    }

    [Fact]
    public void math_answer_candidates_match_python_extraction()
    {
        Assert.Equal(["42", "The answer is 42"], MathAnswer.AnswerCandidates("The answer is 42"));
        Assert.Equal(["7", "So \\boxed{7}"], MathAnswer.AnswerCandidates("So \\boxed{7}."));
        Assert.Equal(["$x + 1$ and done", "The result is $x + 1$ and done", "x + 1", "1"], MathAnswer.AnswerCandidates("The result is $x + 1$ and done"));
        Assert.Equal(["1,234"], MathAnswer.AnswerCandidates("1,234"));
        Assert.Equal(["\\frac{1}{2}", "\\boxed{\\frac{1}{2}}"], MathAnswer.TargetCandidates("\\boxed{\\frac{1}{2}}"));
        Assert.Equal("x + 1", MathAnswer.StripDelimiters("**$x + 1$**"));
        Assert.Equal("**$x + 1$**", MathAnswer.StripDelimiters("**$x + 1$**."));
        Assert.Equal("a  b", MathAnswer.StripDelimiters("\\, a  b \\quad"));
        Assert.Throws<MathLimitException>(() => MathAnswer.AnswerCandidates(new string('a', 1_000_001)));
    }

    [Theory]
    [InlineData("1/3", "0.3333333333", C)]
    [InlineData("\\frac{1}{3}", "0.33333333333333", C)]
    [InlineData("0.1 + 0.2", "0.3", C)]
    [InlineData("\\sqrt{2}", "1.41421356237", C)]
    [InlineData("\\sqrt{2}", "\\sqrt{3}", I)]
    [InlineData("(1, 2)", "(2, 1)", I)]
    [InlineData("\\{1, 2\\}", "\\{2, 1, 1\\}", C)]
    [InlineData("[1, 2]", "(1, 2)", C)]
    [InlineData("10^{-2}", "0.01", C)]
    [InlineData("2e3", "2000", C)]
    [InlineData("-\\frac{3}{4}", "-0.75", C)]
    [InlineData("50 percent", "0.5", C)]
    [InlineData("\\sqrt[3]{27}", "3", C)]
    [InlineData("1 \\cdot 2 \\times 3", "6", C)]
    [InlineData("|-5|", "5", C)]
    public async Task math_scorer_numeric_equivalence_rules(string output, string target, string expected)
    {
        Assert.Equal(expected, (await Run(Scorers.Math(), output, target)).Text);
    }
}
