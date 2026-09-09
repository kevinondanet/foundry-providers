using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.ScorerDemo;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c> for <c>examples/scorer.py</c>: plays both roles the task gives the model. Asked to solve
/// a problem (the <c>PROMPT_TEMPLATE</c> turn) it looks the problem up in the canned MATH-500 rows and answers with a short
/// solution ending in <c>ANSWER: ...</c> — the right answer for most rows, a wrong one for the third row and no
/// <c>ANSWER:</c> line at all for the fourth, so the scorer's three outcomes all appear. Asked to judge equivalence (the
/// <c>EQUIVALENCE_TEMPLATE</c> turn) it answers <c>Yes</c> when expression 1 (the reference solution) contains expression 2
/// (the extracted answer) and <c>No</c> otherwise. Every turn is computed from the conversation, so concurrent samples stay
/// deterministic.
/// </summary>
internal sealed class FakeMathModel
{
    public const string ModelName = "math-scripted";

    /// <summary>The first line of <c>EQUIVALENCE_TEMPLATE</c>, which tells a judge turn from a solve turn.</summary>
    public const string JudgePromptMarker = "Look at the following two expressions";

    /// <summary>The reply for a row that gets no <c>ANSWER:</c> line.</summary>
    public const string NoAnswerReply = "After several attempts I could not reduce this problem to a single final expression.";

    /// <summary>Turns available before the scripted api reports itself exhausted (two per sample).</summary>
    private const int TurnBudget = 10_000;

    /// <summary>What the model does with each canned row, by row index (rows past the table are answered correctly).</summary>
    private static readonly Behaviour[] Script = [Behaviour.Correct, Behaviour.Correct, Behaviour.WrongAnswer, Behaviour.NoAnswerLine, Behaviour.Correct];

    private readonly IReadOnlyList<(string Problem, string Answer)> _rows;

    private FakeMathModel(IReadOnlyList<(string Problem, string Answer)> rows)
    {
        _rows = rows;
    }

    /// <summary>What the scripted model does with one canned row: a correct <c>ANSWER:</c> line, a wrong one, or no <c>ANSWER:</c> line at all.</summary>
    public enum Behaviour
    {
        Correct,
        WrongAnswer,
        NoAnswerLine,
    }

    /// <summary>The scripted model over the canned rows of <paramref name="rowsPath"/> (a JSONL file with <c>problem</c> and <c>answer</c> columns).</summary>
    public static Model Create(string rowsPath)
    {
        var rows = CannedHfHub.LoadRows(rowsPath)
            .Select(row => (Problem: row["problem"]?.GetValue<string>() ?? "", Answer: row["answer"]?.GetValue<string>() ?? ""))
            .ToList();
        var model = new FakeMathModel(rows);
        return new Model(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(model.Respond), TurnBudget), ModelName));
    }

    /// <summary>The behaviour scripted for canned row <paramref name="index"/>.</summary>
    public static Behaviour BehaviourFor(int index) => index >= 0 && index < Script.Length ? Script[index] : Behaviour.Correct;

    /// <summary>The port's equivalence judge: Yes when expression 1 contains expression 2 (the reference solution boxes the answer), else No.</summary>
    public static string Judge(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        const string first = "Expression 1: ";
        const string second = "Expression 2: ";
        var secondAt = prompt.LastIndexOf(second, StringComparison.Ordinal);
        var firstAt = secondAt < 0 ? -1 : prompt.LastIndexOf(first, secondAt, StringComparison.Ordinal);
        if (firstAt < 0)
        {
            return "No";
        }

        var expression1 = prompt[(firstAt + first.Length)..secondAt].Trim();
        var expression2 = prompt[(secondAt + second.Length)..].Trim();
        return expression2.Length > 0 && expression1.Contains(expression2, StringComparison.Ordinal) ? "Yes" : "No";
    }

    /// <summary>The scripted solution for <paramref name="answer"/> under <paramref name="behaviour"/>.</summary>
    public static string Solve(string answer, Behaviour behaviour) => behaviour switch
    {
        Behaviour.NoAnswerLine => NoAnswerReply,
        Behaviour.WrongAnswer => Solution($"{answer} + 1"),
        _ => Solution(answer),
    };

    private static string Solution(string answer) => $"Working through the problem step by step leads to the result {answer}.\n\nANSWER: {answer}";

    private ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        var prompt = messages.LastOrDefault(message => message is ChatMessageUser)?.Text ?? "";
        string reply;
        if (prompt.Contains(JudgePromptMarker, StringComparison.Ordinal))
        {
            reply = Judge(prompt);
        }
        else
        {
            var index = IndexOfProblem(prompt);
            reply = index < 0 ? Solution("unknown") : Solve(_rows[index].Answer, BehaviourFor(index));
        }

        return ModelOutput.FromContent(ModelName, reply);
    }

    private int IndexOfProblem(string prompt)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Problem.Length > 0 && prompt.Contains(_rows[i].Problem, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
