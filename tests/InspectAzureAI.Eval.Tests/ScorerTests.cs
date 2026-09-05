using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Scorers = InspectAzureAI.Eval.Scorers.Scorers;

/// <summary>The text scorers (<c>includes</c>, <c>match</c>, <c>exact_match</c>, <c>pattern</c>) and the numeric matching helpers behind them.</summary>
public class ScorerTests
{
    private const string C = ScoreConstants.Correct;
    private const string I = ScoreConstants.Incorrect;

    private static TaskState State(string output) =>
        new("scripted", 1, 1, "question", [new ChatMessageUser("question")], output: ModelOutput.FromContent("scripted", output));

    private static Task<Score> Run(ScorerDef scorer, string output, params string[] targets) =>
        scorer.Score(State(output), new Target(targets), CancellationToken.None);

    [Theory]
    [InlineData("end", "28 + 32 = 60\nThis solves the problem.", "60", C)]
    [InlineData("end", "**ANSWER: 28 + 32 = 60**\nThis solves the problem.", "60", C)]
    [InlineData("end", "28 + 32 = 60%", "60%", C)]
    [InlineData("end", "28 + 32 = 60%", "32", C)]
    [InlineData("end", "The answer is 25", "5", I)]
    [InlineData("end", "the value is 110", "10", I)]
    [InlineData("begin", "50 is the answer", "5", I)]
    [InlineData("begin", "5 is the answer", "5", C)]
    [InlineData("any", "got 25 here", "5", I)]
    [InlineData("any", "first 7 then 25 then done", "25", C)]
    [InlineData("end", "5", "-5", I)]
    [InlineData("end", "the result is -5", "-5", C)]
    [InlineData("end", "the result is -5 exactly", "-5", C)]
    [InlineData("end", "pi is approximately 3.14", "3.14", C)]
    [InlineData("end", "pi is approximately 33.14", "3.14", I)]
    [InlineData("end", "the value is 1000", "1e3", C)]
    [InlineData("end", "2.0", "2", C)]
    [InlineData("end", "result: -5.0", "-5", C)]
    [InlineData("end", "The total is $1,000.", "1000", C)]
    [InlineData("end", "The total is \\$20.", "20", C)]
    [InlineData("exact", "5 some text", "5", I)]
    [InlineData("exact", "5abc", "5", I)]
    [InlineData("exact", "5", "5", C)]
    [InlineData("exact", "(42)", "42", I)]
    [InlineData("end", "(42)", "42", C)]
    [InlineData("end", "answer: −5", "-5", C)]
    [InlineData("end", "the result is ½", "0.5", C)]
    [InlineData("any", "got -½ as the result", "-0.5", C)]
    public async Task numeric_match_compares_numbers_not_digit_strings(string location, string output, string target, string expected)
    {
        var score = await Run(Scorers.Match(location, numeric: true), output, target);

        Assert.Equal(expected, score.Text);
    }

    [Fact]
    public async Task numeric_match_reports_the_matched_number_and_tries_every_target()
    {
        var any = await Run(Scorers.Match("any", numeric: true), "first 7 then 25 then done", "25");
        var percent = await Run(Scorers.Match("any", numeric: true), "28 + 32 = 60%\nThis solves the problem.", "60", "60%");
        var end = await Run(Scorers.Match(numeric: true), "The answer is 42.", "42");

        Assert.Equal(C, any.Text);
        Assert.Equal("25", any.Answer);
        Assert.Equal(C, percent.Text);
        Assert.Equal("42", end.Answer);
        Assert.Equal("The answer is 42.", end.Explanation);
    }

    [Theory]
    [InlineData("end", true, "The answer is Paris.", "paris", C)]
    [InlineData("end", true, "Paris is nice", "paris", I)]
    [InlineData("end", false, "Answer: Paris", "paris", I)]
    [InlineData("end", false, "Answer: Paris", "Paris", C)]
    [InlineData("begin", true, "Paris is the capital", "paris", C)]
    [InlineData("begin", true, "The capital is Paris", "paris", I)]
    [InlineData("any", true, "I think Paris, maybe", "paris", C)]
    [InlineData("exact", true, "Paris", "paris", C)]
    [InlineData("exact", true, "  Paris!  ", "paris", C)]
    [InlineData("exact", true, "Paris, France", "paris", I)]
    public async Task text_match_honours_location_and_case(string location, bool ignoreCase, string output, string target, string expected)
    {
        var score = await Run(Scorers.Match(location, ignoreCase), output, target);

        Assert.Equal(expected, score.Text);
        Assert.Equal(output.Trim(), score.Answer);
        Assert.Equal(output, score.Explanation);
    }

    [Fact]
    public void match_rejects_an_unknown_location()
    {
        var ex = Assert.Throws<ArgumentException>(() => Scorers.Match("middle"));

        Assert.Contains("middle", ex.Message);
    }

    [Fact]
    public async Task includes_finds_any_target_in_the_completion()
    {
        var insensitive = await Run(Scorers.Includes(), "The capital is Paris", "paris");
        var sensitive = await Run(Scorers.Includes(ignoreCase: false), "The capital is Paris", "paris");
        var second = await Run(Scorers.Includes(), "The capital is Paris", "london", "paris");
        var none = await Run(Scorers.Includes(), "The capital is Paris", "london", "berlin");

        Assert.Equal(C, insensitive.Text);
        Assert.Equal("the capital is paris", insensitive.Answer);
        Assert.Equal("The capital is Paris", insensitive.Explanation);
        Assert.Equal(I, sensitive.Text);
        Assert.Equal("The capital is Paris", sensitive.Answer);
        Assert.Equal(C, second.Text);
        Assert.Equal(I, none.Text);
    }

    [Fact]
    public async Task exact_match_requires_the_whole_completion_to_equal_a_target()
    {
        var same = await Run(Scorers.ExactMatch(), "Paris", "paris");
        var longer = await Run(Scorers.ExactMatch(), "Paris is", "paris");
        var cased = await Run(Scorers.ExactMatch(ignoreCase: false), "Paris", "paris");

        Assert.Equal(C, same.Text);
        Assert.Equal(I, longer.Text);
        Assert.Equal(I, cased.Text);
    }

    [Theory]
    [InlineData("(foo)", true, false, "foo", "foo", C, "foo", null)]
    [InlineData("(foo)", true, false, "foo", "target doesn't match", I, "foo", null)]
    [InlineData("(foo)", true, false, "model doesn't match", "foo", I, null, "invalid_response_format")]
    [InlineData("(FOO)", true, false, "foo", "foo", C, "foo", null)]
    [InlineData("(foo) (bar)", true, false, "foo bar", "foo", C, "foo", null)]
    [InlineData("(foo) (bar)", true, false, "foo bar", "bar", C, "bar", null)]
    [InlineData("(foo) (foo)", true, true, "foo foo", "foo", C, "foo", null)]
    [InlineData("(foo|bar) (foo|bar)", true, true, "foo bar", "bar", I, null, null)]
    [InlineData("(foo) (bar)", true, false, "foo bar", "target doesn't match", I, null, null)]
    [InlineData("(foo) (bar)", true, false, "model doesn't match", "bar", I, null, "invalid_response_format")]
    [InlineData("(f[oz]o) (b[az]r)", true, false, "foo bzr", "bar", I, null, null)]
    [InlineData("ANSWER: (A|B)", false, false, "ANSWER: A", "B", I, "A", null)]
    [InlineData("ANSWER: (A|B) ALTERNATE_ANSWER: (A|B)", false, false, "ANSWER: A ALTERNATE_ANSWER: A", "B", I, null, null)]
    [InlineData(@"ANSWER:\s*(\d+)?", true, true, "ANSWER: ", "42", I, null, null)]
    [InlineData(@"\d+", true, false, "The answer is 42", "42", C, "42", null)]
    [InlineData(@"\d+", true, false, "The answer is 42", "43", I, "42", null)]
    [InlineData(@"\d+", true, true, "The answer is 42", "42", C, "42", null)]
    public async Task pattern_extracts_groups_and_reports_format_failures(
        string pattern, bool ignoreCase, bool matchAll, string output, string target, string expected, string? answer, string? reason)
    {
        var score = await Run(Scorers.Pattern(pattern, ignoreCase, matchAll), output, target);

        Assert.Equal(expected, score.Text);
        Assert.Equal(answer, score.Answer);
        Assert.Equal(reason, score.Reason);
    }

    [Fact]
    public async Task pattern_explains_a_missing_match_with_the_completion()
    {
        var score = await Run(Scorers.Pattern("ANSWER: (A|B)"), "I do not know", "A");

        Assert.Equal("Scoring pattern not matched in output: I do not know", score.Explanation);
        var matched = await Run(Scorers.Pattern("ANSWER: (A|B)"), "ANSWER: A", "A");
        Assert.Equal("ANSWER: A", matched.Explanation);
    }

    [Fact]
    public async Task pattern_match_all_answers_with_the_target_text()
    {
        var score = await Run(Scorers.Pattern("(A) (B)", matchAll: true), "A B", "A", "B");

        Assert.Equal(C, score.Text);
        Assert.Equal("AB", score.Answer);
    }

    [Fact]
    public void built_in_scorers_carry_their_python_names_and_accuracy_stderr_metrics()
    {
        var scorers = new[] { Scorers.Includes(), Scorers.Match(), Scorers.ExactMatch(), Scorers.Pattern("x"), Scorers.ModelGradedQa(), Scorers.ModelGradedFact() };

        Assert.Equal(["includes", "match", "exact_match", "pattern", "model_graded_qa", "model_graded_fact"], scorers.Select(s => s.Name));
        Assert.All(scorers, s => Assert.Equal(["accuracy", "stderr"], s.Metrics.Select(m => m.Name)));
    }

    [Fact]
    public async Task custom_wraps_a_scorer_with_a_name_and_metrics()
    {
        var scorer = Scorers.Custom("mine", (state, target, _) => Task.FromResult(new Score(state.Output.Completion.Length) { Answer = target.Text }), Metrics.Mean(), Metrics.Std());

        var score = await Run(scorer, "abcd", "t");

        Assert.Equal("mine", scorer.Name);
        Assert.Equal(["mean", "std"], scorer.Metrics.Select(m => m.Name));
        Assert.Equal(4.0, score.AsFloat());
        Assert.Equal("t", score.Answer);
        Assert.Throws<ArgumentException>(() => Scorers.Custom("", scorer.Score));
    }

    [Theory]
    [InlineData("60", "60")]
    [InlineData("1000", "1000")]
    [InlineData("100000", "1e+05")]
    [InlineData("3.14159265", "3.1416")]
    [InlineData("0.00001", "1e-05")]
    [InlineData("-5.0", "-5")]
    [InlineData("(42)", "42")]
    [InlineData("42!", "42")]
    [InlineData("<42", "<42")]
    [InlineData("nan", "nan")]
    [InlineData("inf", "inf")]
    [InlineData("hello", "hello")]
    public void normalize_number_formats_like_python_5g(string input, string expected)
    {
        Assert.Equal(expected, MatchScorers.NormalizeNumber(input));
    }

    [Fact]
    public void numeric_punctuation_stripping_matches_python()
    {
        Assert.Equal("1000", MatchScorers.StripNumericPunctuation("\\$1,000."));
        Assert.Equal("2.5 and 3", MatchScorers.StripNumericPunctuation("**2.5** and 3."));
        Assert.Equal("60%", MatchScorers.StripNumericPunctuation("60%"));
        Assert.Equal("hello", MatchScorers.StripPunctuation("  hello!! "));
        Assert.Equal("(42)", MatchScorers.NormalizeNumber("(42)", trimPunctuation: false));
    }

    [Theory]
    [InlineData("２½", 2.5)]
    [InlineData("½", 0.5)]
    [InlineData("−½", -0.5)]
    [InlineData("3²", 9)]
    [InlineData("-2²", 4)]
    [InlineData("2⁻¹", 0.5)]
    [InlineData("１２", 12)]
    [InlineData("十二", 12)]
    [InlineData("二千零五", 2005)]
    [InlineData("三点五", 3.5)]
    [InlineData("负三", -3)]
    [InlineData("Ⅻ", 12)]
    [InlineData("²", 2)]
    [InlineData("1,234.5", 1234.5)]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("1 234", 1234)]
    [InlineData("1,5", 1.5)]
    [InlineData("1e3", 1000)]
    [InlineData("٣٤", 34)]
    public void unicode_numbers_parse_like_the_python_parser(string input, double expected)
    {
        Assert.True(UnicodeNumber.TryParse(input, out var value));
        Assert.Equal(expected, value, 10);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("5abc")]
    [InlineData("1.2.3")]
    [InlineData("½½")]
    public void unicode_parser_rejects_non_numbers(string input)
    {
        Assert.False(UnicodeNumber.TryParse(input, out _));
    }
}
