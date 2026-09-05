using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>
/// Ports of <c>tests/solver/test_multiple_choice.py</c>, <c>tests/scorer/test_choice.py</c> and
/// <c>tests/scorer/test_answer.py</c>, plus the <c>chain_of_thought</c>, <c>assistant_message</c>,
/// <c>self_critique</c>, <c>fork</c> and <c>shuffle_choices</c> ports of the same area.
/// </summary>
public sealed class MultipleChoiceTests : IDisposable
{
    private const string C = ScoreConstants.Correct;
    private const string I = ScoreConstants.Incorrect;
    private const string Question = "What's the answer?";

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

    /// <summary>Port of <c>simple_task_state</c>: a state with the given choices, a single input user prompt and a canned completion.</summary>
    private static TaskState State(IReadOnlyList<string>? choices = null, string output = "", IEnumerable<ChatMessage>? messages = null, string input = Question) =>
        new("scripted", 0, 0, input, messages ?? [new ChatMessageUser(input) { Source = "input" }], choices: choices, output: ModelOutput.FromContent("model", output));

    /// <summary>Runs <paramref name="solver"/> with the real generate loop over a scripted model replying <paramref name="replies"/> in turn.</summary>
    private static async Task<TaskState> Solve(Solver solver, TaskState state, params string[] replies)
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(replies.Select(r => ScriptedTurn.Text(r)).ToArray()));
        return await solver(state, GenerateLoop.Create(scope.Model), CancellationToken.None);
    }

    private static string Expected(string template, string letters, string choices) =>
        template.Replace("{letters}", letters).Replace("{question}", Question).Replace("{choices}", choices);

    private static HashSet<string> MarkedCorrect(Choices choices) => choices.Where(c => c.Correct == true).Select(c => c.Value).ToHashSet();

    private static Task<Score> Score(ScorerDef scorer, TaskState state, Target target) => scorer.Score(state, target, CancellationToken.None);

    /// <summary>A <see cref="Random"/> replaying fixed <c>randbelow</c> draws, so Python's <c>Random(seed)</c> orders can be pinned exactly.</summary>
    private sealed class ScriptedRandom(params int[] draws) : Random
    {
        private int _next;

        public override int Next(int maxValue)
        {
            var value = draws[_next++];
            Assert.InRange(value, 0, maxValue - 1);
            return value;
        }
    }

    // ---- Choices ------------------------------------------------------------------------------------------------

    [Fact]
    public void choices_wrap_strings_with_their_original_positions_and_no_mark()
    {
        var choices = new Choices(["a", "b", "c"]);

        Assert.Equal([new Choice("a", null, 0), new Choice("b", null, 1), new Choice("c", null, 2)], choices);
        choices.MarkChoice(1, true);
        choices.MarkChoice(2, false);
        Assert.Equal([null, true, false], choices.Select(c => c.Correct));
    }

    [Fact]
    public void shuffle_uses_pythons_fisher_yates_from_the_end()
    {
        // Python: Random(4).shuffle(range(4)) draws randbelow 1, 1, 0 and yields [2, 0, 3, 1]
        var choices = new Choices(["a", "b", "c", "d"]);

        choices.Shuffle(new ScriptedRandom(1, 1, 0));

        Assert.Equal(["c", "a", "d", "b"], choices.Select(c => c.Value));
        Assert.Equal([2, 0, 3, 1], choices.Select(c => c.OriginalPosition));
    }

    [Fact]
    public void choices_multiple_shuffles_preserve_original_positions()
    {
        var choices = new Choices(["A", "B", "C", "D"]);
        Assert.Equal([0, 1, 2, 3], choices.Select(c => c.OriginalPosition));

        choices.Shuffle(new Random(42));
        Assert.Equal(new Dictionary<string, int> { ["A"] = 0, ["B"] = 1, ["C"] = 2, ["D"] = 3 }, choices.ToDictionary(c => c.Value, c => c.OriginalPosition));

        choices.Shuffle(new Random(123));
        Assert.Equal(new Dictionary<string, int> { ["A"] = 0, ["B"] = 1, ["C"] = 2, ["D"] = 3 }, choices.ToDictionary(c => c.Value, c => c.OriginalPosition));
    }

    [Fact]
    public void choices_prompt_formats_the_template()
    {
        var choices = new Choices(["x", "y"]);

        Assert.Equal("Q: A) x\nB) y [A,B]", choices.Prompt("Q", "{question}: {choices} [{letters}]"));
    }

    [Fact]
    public void task_state_copy_is_independent_but_keeps_the_uuid()
    {
        var state = State(["a", "b"]);
        state.Choices.MarkChoice(0, true);
        state.Metadata["k"] = "v";
        state.Store.Set("s", 1);

        var copy = state.Copy();
        copy.Messages.Add(new ChatMessageUser("more"));
        copy.Metadata["k"] = "changed";
        copy.Store.Set("s", 2);
        copy.Choices.MarkChoice(0, false);

        Assert.Equal(state.Uuid, copy.Uuid);
        Assert.Single(state.Messages);
        Assert.Equal("v", state.Metadata["k"]);
        Assert.Equal(1, state.Store.Get("s"));
        Assert.True(state.Choices[0].Correct);
        Assert.False(copy.Choices[0].Correct);
    }

    // ---- multiple_choice ------------------------------------------------------------------------------------------

    [Fact]
    public async Task raises_if_no_choices()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Solve(Solvers.MultipleChoice(), State(), "ANSWER: A"));

        Assert.Equal("The multiple_choice solver requires samples with choices", ex.Message);
    }

    [Fact]
    public async Task single_multiple_choice()
    {
        var state = await Solve(Solvers.MultipleChoice(), State(["only one choice here"]), "ANSWER: A");

        Assert.Equal("ANSWER: A", state.Output.Completion);
        Assert.Equal(Expected(MultipleChoiceTemplate.SingleAnswer, "A", "A) only one choice here"), state.UserPrompt.Text);
        Assert.True(state.Choices[0].Correct);
    }

    [Fact]
    public async Task maps_choices_without_shuffling()
    {
        var state = await Solve(Solvers.MultipleChoice(), State(["choice 1", "choice 2", "choice 3"]), "ANSWER: A");

        Assert.Equal("ANSWER: A", state.Messages[^1].Text);
        Assert.Equal(Expected(MultipleChoiceTemplate.SingleAnswer, "A,B,C", "A) choice 1\nB) choice 2\nC) choice 3"), state.UserPrompt.Text);
        Assert.Equal([true, false, false], state.Choices.Select(c => c.Correct));
    }

    [Fact]
    public async Task custom_template()
    {
        var state = await Solve(Solvers.MultipleChoice(template: "Do this thing: {question} {choices}"), State(["choice 1", "choice 2", "choice 3"]), "ANSWER: A");

        Assert.Equal("ANSWER: A", state.Messages[^1].Text);
        Assert.Equal("Do this thing: What's the answer? A) choice 1\nB) choice 2\nC) choice 3", state.UserPrompt.Text);
    }

    [Fact]
    public void custom_template_raises_with_missing_fields()
    {
        var ex = Assert.Throws<ArgumentException>(() => Solvers.MultipleChoice(template: "This template lacks substance"));

        Assert.StartsWith("The template must contain '{question}' and '{choices}' placeholders for string substitution.", ex.Message);
    }

    [Fact]
    public async Task custom_template_with_an_unknown_placeholder_fails_at_solve_time()
    {
        var solver = Solvers.MultipleChoice(template: "{question} {choices} {bogus}");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Solve(solver, State(["a"]), "ANSWER: A"));
    }

    [Fact]
    public async Task trailing_punctuation_whitespace()
    {
        var state = await Solve(Solvers.MultipleChoice(), State(["choice 1", "choice 2", "choice 3"]), "ANSWER: A. \n");

        Assert.Equal("ANSWER: A. \n", state.Output.Completion);
        Assert.Equal(["choice 1"], MarkedCorrect(state.Choices));
    }

    [Theory]
    [InlineData("ANSWER: A, C. \n")]
    [InlineData("ANSWER: A and C. \n")]
    [InlineData("ANSWER: AC")]
    [InlineData("ANSWER: a, c")]
    [InlineData("ANSWER: A, C,")]
    public async Task multiple_correct_accepts_separators_case_and_trailing_punctuation(string reply)
    {
        var state = await Solve(Solvers.MultipleChoice(multipleCorrect: true), State(["choice 1", "choice 2", "choice 3"]), reply);

        Assert.Equal(["choice 1", "choice 3"], MarkedCorrect(state.Choices).Order());
    }

    [Theory]
    [InlineData("ANSWER: A, B and C")]
    [InlineData("ANSWER: A, B, and C")]
    public async Task multiple_correct_natural_language_lists(string reply)
    {
        var state = await Solve(Solvers.MultipleChoice(multipleCorrect: true), State(["choice 1", "choice 2", "choice 3", "choice 4"]), reply);

        Assert.Equal(["choice 1", "choice 2", "choice 3"], MarkedCorrect(state.Choices).Order());
    }

    [Fact]
    public async Task multiple_correct()
    {
        var state = await Solve(Solvers.MultipleChoice(multipleCorrect: true), State(["choice 1", "choice 2", "choice 3"]), "ANSWER: AB");

        Assert.Equal("ANSWER: AB", state.Output.Completion);
        Assert.Equal(Expected(MultipleChoiceTemplate.MultipleAnswer, "A,B,C", "A) choice 1\nB) choice 2\nC) choice 3"), state.UserPrompt.Text);
        Assert.Equal([new Choice("choice 1", true, 0), new Choice("choice 2", true, 1), new Choice("choice 3", false, 2)], state.Choices);
    }

    [Fact]
    public async Task multiple_correct_model_generated_commas()
    {
        var state = await Solve(Solvers.MultipleChoice(multipleCorrect: true), State(["choice 1", "choice 2", "choice 3"]), "ANSWER: B, C");

        Assert.Equal("ANSWER: B, C", state.Output.Completion);
        Assert.Equal([new Choice("choice 1", false, 0), new Choice("choice 2", true, 1), new Choice("choice 3", true, 2)], state.Choices);
    }

    [Theory]
    [InlineData("ANSWER: b", false)]
    [InlineData("ANSWER: A,", false)]
    [InlineData("Let me think step by step.\nAnswer: this question asks which option is correct\nWorking through the options...\nANSWER: B", true)]
    public async Task single_answer_parsing_is_case_insensitive_tolerates_a_stray_comma_and_takes_the_last_answer_line(string reply, bool cot)
    {
        var state = await Solve(Solvers.MultipleChoice(cot: cot), State(["choice 1", "choice 2", "choice 3"]), reply);

        var expected = reply.StartsWith("ANSWER: A", StringComparison.Ordinal) ? "choice 1" : "choice 2";
        Assert.Equal([expected], MarkedCorrect(state.Choices));
        if (cot)
        {
            Assert.StartsWith(MultipleChoiceTemplate.SingleAnswerCot[..40], state.UserPrompt.Text);
        }
    }

    [Theory]
    [InlineData(4, "ANSWER: None of the above")]
    [InlineData(4, "ANSWER: Don't know")]
    [InlineData(9, "ANSWER: I don't know")]
    [InlineData(4, "I refuse to answer.")]
    public async Task unparseable_answers_do_not_mark_choices_and_score_incorrect(int count, string reply)
    {
        var choices = Enumerable.Range(0, count).Select(i => $"Option {(char)('A' + i)}").ToList();

        var state = await Solve(Solvers.MultipleChoice(), State(choices), reply);

        Assert.Empty(MarkedCorrect(state.Choices));
        Assert.All(state.Choices, c => Assert.Null(c.Correct));
        var result = await Score(Scorers.Choice(), state, "D");
        Assert.Equal(I, result.Text);
    }

    [Fact]
    public async Task cot_complex_text_scores_correct()
    {
        const string cotComplex = """
            Let's approach this step-by-step:

            1. Option B can be eliminated immediately because it talks about sperm binding to the egg.

            4. Now we're left with options A and D. Let's consider each:

               A) Epistatic interactions refer to how genes interact with each other.

               D) Chromosomal incompatibilities leading to failure of meiosis is more likely.

            Therefore, the most likely main cause of zygote mortality in this scenario would be chromosomal incompatibilities.

            ANSWER: D
            """;

        var state = await Solve(Solvers.MultipleChoice(), State(["A", "B", "C", "D"]), cotComplex);
        var result = await Score(Scorers.Choice(), state, "D");

        Assert.Equal(C, result.Text);
        Assert.Equal("D", result.Answer);
    }

    [Fact]
    public async Task can_shuffle_choices_when_calling_the_model()
    {
        // Python: Random(4).shuffle(range(3)) draws randbelow 0, 1 and yields [2, 1, 0]
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("ANSWER: A")));
        var solver = Solvers.MultipleChoice(shuffle: new ScriptedRandom(0, 1));

        var state = await solver(State(["choice 1", "choice 2", "choice 3"]), GenerateLoop.Create(scope.Model), CancellationToken.None);

        var sent = Assert.Single(scope.Api.Requests).Input.OfType<ChatMessageUser>().Last().Text;
        Assert.Equal(Expected(MultipleChoiceTemplate.SingleAnswer, "A,B,C", "A) choice 3\nB) choice 2\nC) choice 1"), sent);
        Assert.Equal("ANSWER: C", state.Output.Completion);
        Assert.Equal("ANSWER: C", state.Output.Message.Text);
        Assert.Equal("ANSWER: C", state.Messages[^1].Text);
        Assert.Equal(Expected(MultipleChoiceTemplate.SingleAnswer, "A,B,C", "A) choice 1\nB) choice 2\nC) choice 3"), state.UserPrompt.Text);
        Assert.Equal([new Choice("choice 3", true, 2), new Choice("choice 2", false, 1), new Choice("choice 1", false, 0)], state.Choices);
    }

    [Fact]
    public async Task multiple_shuffled_answers_one_answer()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("ANSWER: C")));
        var solver = Solvers.MultipleChoice(multipleCorrect: true, shuffle: new ScriptedRandom(0, 1));

        var state = await solver(State(["choice 1", "choice 2", "choice 3"]), GenerateLoop.Create(scope.Model), CancellationToken.None);

        Assert.Contains("A) choice 3\nB) choice 2\nC) choice 1", scope.Api.Requests[0].Input[^1].Text);
        Assert.Equal("ANSWER: A", state.Output.Completion);
        Assert.Equal("ANSWER: A", state.Messages[^1].Text);
        Assert.Equal(Expected(MultipleChoiceTemplate.MultipleAnswer, "A,B,C", "A) choice 1\nB) choice 2\nC) choice 3"), state.UserPrompt.Text);
        Assert.Equal([new Choice("choice 3", false, 2), new Choice("choice 2", false, 1), new Choice("choice 1", true, 0)], state.Choices);
    }

    [Fact]
    public async Task multiple_shuffled_answers_more()
    {
        // Python: Random(4).shuffle(range(4)) yields [2, 0, 3, 1]; the model's "A, D" are original positions 2 and 1
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("ANSWER: A, D")));
        var solver = Solvers.MultipleChoice(multipleCorrect: true, shuffle: new ScriptedRandom(1, 1, 0));

        var state = await solver(State(["choice 1", "choice 2", "choice 3", "choice 4"]), GenerateLoop.Create(scope.Model), CancellationToken.None);

        Assert.Contains("A) choice 3\nB) choice 1\nC) choice 4\nD) choice 2", scope.Api.Requests[0].Input[^1].Text);
        Assert.Equal("ANSWER: B, C", state.Output.Completion);
        Assert.Equal("ANSWER: B, C", state.Messages[^1].Text);
        Assert.Equal(Expected(MultipleChoiceTemplate.MultipleAnswer, "A,B,C,D", "A) choice 1\nB) choice 2\nC) choice 3\nD) choice 4"), state.UserPrompt.Text);
        Assert.Equal(
            [new Choice("choice 3", true, 2), new Choice("choice 1", false, 0), new Choice("choice 4", false, 3), new Choice("choice 2", true, 1)],
            state.Choices);
    }

    [Fact]
    public async Task shuffled_history_is_left_alone_when_no_answer_was_parsed()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("no idea")));
        var solver = Solvers.MultipleChoice(shuffle: new ScriptedRandom(0, 1));

        var state = await solver(State(["choice 1", "choice 2", "choice 3"]), GenerateLoop.Create(scope.Model), CancellationToken.None);

        Assert.Equal("no idea", state.Output.Completion);
        Assert.Contains("A) choice 3", state.UserPrompt.Text);
        Assert.All(state.Choices, c => Assert.Null(c.Correct));
    }

    [Fact]
    public async Task max_tokens_is_passed_to_generate_and_the_prompt_keeps_its_message_identity()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("ANSWER: B")));
        var state = State(["a", "b"]);
        var promptId = state.UserPrompt.Id;

        await Solvers.MultipleChoice(maxTokens: 7)(state, GenerateLoop.Create(scope.Model), CancellationToken.None);

        Assert.Equal(7, Assert.Single(scope.Api.Requests).Config.MaxTokens);
        Assert.Equal(promptId, state.UserPrompt.Id);
        Assert.Equal([false, true], state.Choices.Select(c => c.Correct));
    }

    [Fact]
    public async Task more_than_26_choices_end_to_end_with_shuffled_dataset()
    {
        var dataset = new MemoryDataset(
        [
            new Sample("Please make the right choice")
            {
                Choices = [.. Enumerable.Range('A', 26).Select(c => ((char)c).ToString()), .. Enumerable.Range(1, 9).Select(i => i.ToString())],
                Target = "5",
            },
        ]);
        dataset.ShuffleChoices(seed: 42);
        var shuffledTarget = dataset[0].Target.Text;
        Assert.Equal("5", dataset[0].Choices![AnswerIndex(shuffledTarget)]);
        var api = new ScriptedModelApi(ScriptedTurn.Text($"ANSWER: {shuffledTarget}"));
        var task = new EvalTask { Name = "mc", Dataset = dataset, Solver = Solvers.MultipleChoice(), Scorers = [Scorers.Choice()] };

        var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(api), LogDir = _logDir, MaxSamples = 1 });

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal(1.0, Assert.Single(log.Results!.Scores).Metrics["accuracy"].Value);
        Assert.Equal(C, log.Samples![0].Scores!["choice"].Text);
    }

    private static int AnswerIndex(string label) => label.Length == 1 && char.IsLetter(label[0]) ? label[0] - 'A' : 25 + int.Parse(label);

    // ---- shuffle_choices ------------------------------------------------------------------------------------------

    [Fact]
    public void shuffle_choices_permutes_choices_and_remaps_the_target()
    {
        var dataset = new MemoryDataset(
        [
            new Sample("q1") { Choices = ["a", "b", "c", "d"], Target = "C" },
            new Sample("q2") { Choices = ["w", "x", "y", "z"], Target = new Target(["A", "D"]) },
            new Sample("q3") { Target = "free text" },
        ]);

        dataset.ShuffleChoices(seed: 7);

        Assert.Equal(["a", "b", "c", "d"], dataset[0].Choices!.Order());
        Assert.Equal("c", dataset[0].Choices![AnswerIndex(dataset[0].Target.Text)]);
        Assert.Equal(2, dataset[1].Target.Count);
        Assert.Equal(["w", "z"], dataset[1].Target.Values.Select(letter => dataset[1].Choices![AnswerIndex(letter)]));
        Assert.Null(dataset[2].Choices);
        Assert.Equal("free text", dataset[2].Target.Text);

        var again = new MemoryDataset([new Sample("q1") { Choices = ["a", "b", "c", "d"], Target = "C" }]);
        again.ShuffleChoices(seed: 7);
        Assert.Equal(dataset[0].Choices, again[0].Choices);
    }

    [Theory]
    [InlineData("10")]
    [InlineData(",")]
    [InlineData("")]
    public void shuffle_choices_rejects_a_target_that_is_not_a_choice_label(string target)
    {
        var dataset = new MemoryDataset([new Sample("q") { Choices = ["a", "b"], Target = target }]);

        Assert.Throws<ArgumentException>(() => dataset.ShuffleChoices(seed: 1));
    }

    // ---- choice scorer --------------------------------------------------------------------------------------------

    [Fact]
    public async Task choice_scores_single_letter()
    {
        var state = State(["choice 1", "choice 2"], "ANSWER: A");
        state.Choices.MarkChoice(0, true);
        state.Choices.MarkChoice(1, false);

        var result = await Score(Scorers.Choice(), state, "A");

        Assert.Equal(C, result.Text);
        Assert.Equal("A", result.Answer);
        Assert.Equal("ANSWER: A", result.Explanation);
        Assert.Equal("choice", Scorers.Choice().Name);
    }

    [Fact]
    public async Task choice_scores_multiple_letters()
    {
        var state = State(["choice 1", "choice 2"], "ANSWER: A, B");
        state.Choices.MarkChoice(0, true);
        state.Choices.MarkChoice(1, true);

        var result = await Score(Scorers.Choice(), state, new Target(["A", "B"]));

        Assert.Equal(C, result.Text);
        Assert.Equal("A, B", result.Answer);
        Assert.Equal("ANSWER: A, B", result.Explanation);
    }

    [Theory]
    [InlineData("A,B")]
    [InlineData("A, B")]
    [InlineData("AB")]
    public async Task choice_scores_multiple_letters_with_separators(string target)
    {
        var state = State(["choice 1", "choice 2", "choice 3"], "ANSWER: A, B");
        state.Choices.MarkChoice(0, true);
        state.Choices.MarkChoice(1, true);
        state.Choices.MarkChoice(2, false);

        var result = await Score(Scorers.Choice(), state, target);

        Assert.Equal(C, result.Text);
        Assert.Equal("A, B", result.Answer);
    }

    [Theory]
    [InlineData(36, "10", new[] { 35 })]
    [InlineData(40, "10, 12", new[] { 35, 37 })]
    [InlineData(36, "A,10", new[] { 0, 35 })]
    public async Task choice_scores_multi_digit_labels(int count, string target, int[] marked)
    {
        var state = State(Enumerable.Range(0, count).Select(i => $"choice {i}").ToList(), $"ANSWER: {target}");
        for (var i = 0; i < count; i++)
        {
            state.Choices.MarkChoice(i, marked.Contains(i));
        }

        var result = await Score(Scorers.Choice(), state, target);

        Assert.Equal(C, result.Text);
    }

    [Fact]
    public async Task choice_target_beyond_choices_raises()
    {
        var state = State(Enumerable.Range(0, 30).Select(i => $"choice {i}").ToList(), "ANSWER: 10");
        for (var i = 0; i < 30; i++)
        {
            state.Choices.MarkChoice(i, i == 0);
        }

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Score(Scorers.Choice(), state, "10"));

        Assert.Contains("beyond the task's 30 choices: 10", ex.Message);
    }

    [Theory]
    [InlineData("No")]
    [InlineData("The answer is 42.")]
    [InlineData("")]
    public async Task choice_with_no_choices_scores_incorrect_without_parsing_the_target(string target)
    {
        var result = await Score(Scorers.Choice(), State([], "No"), target);

        Assert.Equal(I, result.Text);
        Assert.Equal("", result.Answer);
        Assert.Equal("No", result.Explanation);
    }

    [Fact]
    public async Task choice_is_incorrect_on_false_positives_and_missed_answers()
    {
        var extra = State(["choice 1", "choice 2"], "ANSWER: A, B");
        extra.Choices.MarkChoice(0, true);
        extra.Choices.MarkChoice(1, true);
        var missed = State(["choice 1", "choice 2"], "ANSWER: A");
        missed.Choices.MarkChoice(0, true);
        missed.Choices.MarkChoice(1, false);

        var falsePositive = await Score(Scorers.Choice(), extra, "A");
        var missedAnswer = await Score(Scorers.Choice(), missed, new Target(["A", "B"]));

        Assert.Equal(I, falsePositive.Text);
        Assert.Equal("A, B", falsePositive.Answer);
        Assert.Equal("ANSWER: A, B", falsePositive.Explanation);
        Assert.Equal(I, missedAnswer.Text);
        Assert.Equal("A", missedAnswer.Answer);
    }

    [Fact]
    public async Task choice_unshuffles_before_scoring_and_explains_what_the_model_saw()
    {
        var state = State(["choice 1", "choice 2", "choice 3"], "ANSWER: A");
        state.Choices.Shuffle(new ScriptedRandom(0, 1));
        state.Choices.MarkChoice(0, true);
        state.Choices.MarkChoice(1, false);
        state.Choices.MarkChoice(2, false);

        var result = await Score(Scorers.Choice(), state, new Target(["C"]));

        Assert.Equal(C, result.Text);
        Assert.Equal("C", result.Answer);
        Assert.Equal(
            "Choices were shuffled before generating a response, the following was sent to the model:\n\nA) choice 3\nB) choice 2\nC) choice 1\nShuffled answer:\nANSWER: A",
            result.Explanation);
    }

    [Fact]
    public async Task choice_empty_target_matches_no_selection()
    {
        var single = State(["A"], "ANSWERS: A");
        single.Choices.MarkChoice(0, true);
        var none = State(["A", "B"], "ANSWERS: ");
        none.Choices.MarkChoice(0, false);
        none.Choices.MarkChoice(1, false);

        var correct = await Score(Scorers.Choice(), single, new Target(["A"]));
        var nothing = await Score(Scorers.Choice(), none, "");

        Assert.Equal(C, correct.Text);
        Assert.Equal("A", correct.Answer);
        Assert.Equal("ANSWERS: A", correct.Explanation);
        Assert.Equal(C, nothing.Text);
        Assert.Equal("", nothing.Answer);
        Assert.Equal("ANSWERS: ", nothing.Explanation);
    }

    // ---- answer scorer --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("letter", "ANSWER: B", "B", C)]
    [InlineData("letter", "ANSWER: B", "C", I)]
    [InlineData("word", "ANSWER: Yes", "Yes", C)]
    [InlineData("word", "ANSWER: ☆", "☆", C)]
    [InlineData("word", "ANSWER: ○", "○", C)]
    [InlineData("word", "ANSWER: ◎", "◎", C)]
    [InlineData("word", "ANSWER: Yes.", "Yes", C)]
    [InlineData("word", "ANSWER: 42,\n", "42", C)]
    [InlineData("word", "ANSWER: correct!", "correct", C)]
    [InlineData("word", "ANSWER: 3.14", "3.14", C)]
    [InlineData("line", "ANSWER:\nThis is a whole new line", "This is a whole new line", C)]
    [InlineData("line", "ANSWER:\nThis is a whole new line", "This doesn't match does it?", I)]
    [InlineData("line", "What if I submitted this as the answer: some stuff we don't want to be the answer\n\nNo, that seems wrong.\n\nANSWER: 0.1\n", "0.1", C)]
    public async Task answer_extracts_letters_words_and_lines(string pattern, string output, string target, string expected)
    {
        var result = await Score(Scorers.Answer(pattern), State(output: output), target);

        Assert.Equal(expected, result.Text);
        Assert.Equal("answer", Scorers.Answer(pattern).Name);
    }

    [Theory]
    [InlineData("letter", "ANSWER: Yes", "No")]
    [InlineData("word", "ANSWER: Yes then more text", "Yes")]
    [InlineData("word", "ANSWER: No, because reasons", "No")]
    public async Task answer_format_violations_are_incorrect_with_a_reason(string pattern, string output, string target)
    {
        var result = await Score(Scorers.Answer(pattern), State(output: output), target);

        Assert.Equal(I, result.Text);
        Assert.Equal("invalid_response_format", result.Reason);
    }

    [Theory]
    [InlineData("letter", "Let me think. ANSWER: A\nWait, that is wrong.\nANSWER: B", "B")]
    [InlineData("word", "ANSWER: No\nActually, on reflection...\nANSWER: Yes", "Yes")]
    public async Task answer_last_occurrence_wins(string pattern, string output, string target)
    {
        var result = await Score(Scorers.Answer(pattern), State(output: output), target);

        Assert.Equal(target, result.Answer);
        Assert.Equal(C, result.Text);
    }

    [Fact]
    public void answer_rejects_an_unknown_pattern_name()
    {
        Assert.Throws<ArgumentException>(() => Scorers.Answer("paragraph"));
    }

    // ---- chain_of_thought / assistant_message ---------------------------------------------------------------------

    [Fact]
    public async Task chain_of_thought_rewrites_the_prompt_with_the_default_template()
    {
        var state = State(messages: [new ChatMessageSystem("sys"), new ChatMessageUser("What is 2+2?")]);
        var generate = GenerateLoop.Create(new Model(new ScriptedModelApi()));

        await Solvers.ChainOfThought()(state, generate, CancellationToken.None);

        Assert.Equal(Solvers.DefaultCotTemplate.Replace("{prompt}", "What is 2+2?"), state.UserPrompt.Text);
        Assert.StartsWith("\nWhat is 2+2?\n\nBefore answering, reason in a step-by-step manner", state.UserPrompt.Text);
        Assert.Equal("sys", state.Messages[0].Text);
        Assert.Equal(2, state.Messages.Count);
    }

    [Fact]
    public async Task chain_of_thought_custom_template_is_strict_like_str_format()
    {
        var generate = GenerateLoop.Create(new Model(new ScriptedModelApi()));

        var state = State(messages: [new ChatMessageUser("q")]);
        await Solvers.ChainOfThought("{{literal}} {prompt}")(state, generate, CancellationToken.None);
        Assert.Equal("{literal} q", state.UserPrompt.Text);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Solvers.ChainOfThought("{prompt} {unknown}")(State(), generate, CancellationToken.None));
        await Assert.ThrowsAsync<FormatException>(() => Solvers.ChainOfThought("{prompt} }")(State(), generate, CancellationToken.None));
    }

    [Fact]
    public async Task assistant_message_appends_a_formatted_assistant_turn_for_the_state_model()
    {
        var state = State();
        state.Metadata["topic"] = "cats";
        var generate = GenerateLoop.Create(new Model(new ScriptedModelApi()));

        await Solvers.AssistantMessage("Did you mean {topic} or {other}?", new Dictionary<string, object?> { ["other"] = "dogs" })(state, generate, CancellationToken.None);

        var message = Assert.IsType<ChatMessageAssistant>(state.Messages[^1]);
        Assert.Equal("Did you mean cats or dogs?", message.Text);
        Assert.Equal("scripted", message.Model);
        Assert.Equal(2, state.Messages.Count);
    }

    // ---- self_critique --------------------------------------------------------------------------------------------

    [Fact]
    public async Task self_critique_asks_for_a_critique_plays_it_back_and_regenerates()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("Be more polite."), ScriptedTurn.Text("Hello there!")));
        var state = State(messages: [new ChatMessageUser("Say hello"), new ChatMessageAssistant("hi")], output: "hi", input: "Say hello");

        var result = await Solvers.SelfCritique()(state, GenerateLoop.Create(scope.Model), CancellationToken.None);

        Assert.Equal(2, scope.Api.Requests.Count);
        var critiqueRequest = Assert.Single(scope.Api.Requests[0].Input);
        Assert.Equal(Solvers.DefaultCritiqueTemplate.Replace("{question}", "Say hello").Replace("{completion}", "hi"), critiqueRequest.Text);
        Assert.Equal("user", critiqueRequest.Role);

        var playback = scope.Api.Requests[1].Input[^1];
        Assert.Equal("user", playback.Role);
        Assert.Equal(
            Solvers.DefaultCritiqueCompletionTemplate.Replace("{question}", "Say hello").Replace("{completion}", "hi").Replace("{critique}", "Be more polite."),
            playback.Text);
        Assert.Equal("Hello there!", result.Output.Completion);
        Assert.Equal(["user", "assistant", "user", "assistant"], result.Messages.Select(m => m.Role));
    }

    [Fact]
    public async Task self_critique_templates_see_metadata_but_not_its_reserved_keys()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("crit"), ScriptedTurn.Text("final")));
        var state = State(messages: [new ChatMessageUser("Say hello")], output: "hi", input: "Say hello");
        state.Metadata["question"] = "shadowed";
        state.Metadata["style"] = "formal";

        await Solvers.SelfCritique("{question}|{completion}|{style}", "{critique}|{question}|{style}")(state, GenerateLoop.Create(scope.Model), CancellationToken.None);

        Assert.Equal("Say hello|hi|formal", scope.Api.Requests[0].Input[0].Text);
        Assert.Equal("crit|Say hello|formal", scope.Api.Requests[1].Input[^1].Text);
    }

    [Fact]
    public async Task self_critique_can_use_an_alternate_model_for_the_critique()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("final")));
        var critic = new ScriptedModelApi(ScriptedTurn.Text("crit"));
        var state = State(messages: [new ChatMessageUser("Say hello")], output: "hi", input: "Say hello");

        await Solvers.SelfCritique(model: new Model(critic))(state, GenerateLoop.Create(scope.Model), CancellationToken.None);

        Assert.Single(critic.Requests);
        Assert.Single(scope.Api.Requests);
        Assert.Contains("[Critique]: crit", scope.Api.Requests[0].Input[^1].Text);
    }

    [Fact]
    public async Task self_critique_without_a_model_or_sample_context_fails()
    {
        var generate = GenerateLoop.Create(new Model(new ScriptedModelApi()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Solvers.SelfCritique()(State(), generate, CancellationToken.None));
    }

    // ---- fork -----------------------------------------------------------------------------------------------------

    private static Solver Forked(string cookie) => async (state, generate, cancellationToken) =>
    {
        SampleContext.Require().Store.Set("cookie", cookie);
        state.Store.Set("monster", cookie);
        state.Metadata["cookie"] = cookie;
        return await generate(state, cancellationToken: cancellationToken);
    };

    private static void CheckState(TaskState state, string cookie)
    {
        Assert.Equal(cookie, state.Store.Get("cookie"));
        Assert.Equal(cookie, state.Store.Get("monster"));
        Assert.Equal(cookie, state.Metadata["cookie"]);
    }

    [Fact]
    public async Task fork_runs_solvers_on_independent_copies_of_the_state_and_store()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("r1"), ScriptedTurn.Text("r2"), ScriptedTurn.Text("r3"), ScriptedTurn.Text("r4")));
        var generate = GenerateLoop.Create(scope.Model);
        var state = State();

        var results = await Solvers.Fork(state, [Forked("foo"), Forked("bar")], generate);
        CheckState(results[0], "foo");
        CheckState(results[1], "bar");

        var single = await Solvers.Fork(state, Forked("foo"), generate);
        CheckState(single, "foo");
        Assert.NotSame(state, single);

        var chained = await Solvers.Fork(state, Solvers.Chain(Forked("a"), Forked("b"), Forked("c")), generate);
        CheckState(chained, "c");

        // the parent state and the ambient store never see a branch's changes
        Assert.Single(state.Messages);
        Assert.False(state.Metadata.ContainsKey("cookie"));
        Assert.Null(state.Store.Get("monster"));
        Assert.Null(scope.Context.Store.Get("cookie"));
        Assert.Equal(["r1", "r2"], results.Select(r => r.Output.Completion).Order());
    }

    [Fact]
    public async Task fork_records_subtask_spans_and_ends_them_when_a_branch_fails_or_is_cancelled()
    {
        using var scope = new SampleContextScope();
        var generate = GenerateLoop.Create(scope.Model);
        Solver failing = (_, _, _) => throw new InvalidOperationException("boom");
        Solver waiting = async (state, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return state;
        };

        await Solvers.Fork(State(), [Forked("x"), Solvers.Chain(Forked("y"))], generate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Solvers.Fork(State(), failing, generate));
        using var cts = new CancellationTokenSource();
        var pending = Solvers.Fork(State(), [waiting], generate, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var begins = scope.Transcript.Events.OfType<SpanBeginEvent>().Where(e => e.Type == "subtask").ToList();
        Assert.Equal(["fork", "chain", "fork", "fork"], begins.Select(e => e.Name));
        Assert.Equal(begins.Select(e => e.Id).Order(), scope.Transcript.Events.OfType<SpanEndEvent>().Select(e => e.Id).Order());
        Assert.Null(scope.Transcript.CurrentSpanId);
    }

    [Fact]
    public async Task fork_works_without_a_sample_context()
    {
        var generate = GenerateLoop.Create(new Model(new ScriptedModelApi(ScriptedTurn.Text("reply"))));
        Solver marker = (state, _, _) =>
        {
            state.Messages.Add(new ChatMessageUser("branch"));
            return Task.FromResult(state);
        };
        var state = State();

        var result = await Solvers.Fork(state, marker, generate);

        Assert.Equal(["user", "user"], result.Messages.Select(m => m.Role));
        Assert.Single(state.Messages);
    }
}
