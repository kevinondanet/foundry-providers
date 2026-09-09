using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Simpleqa;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c> for <c>examples/simpleqa.py</c>: asked a question it looks it up in the canned
/// SimpleQA-Verified rows and answers with the reference answer, except for the third and fifth rows, which get a wrong
/// answer so both grades appear; asked to grade (the <c>model_graded_qa</c> prompt, recognised by its <c>[BEGIN DATA]</c>
/// block) it answers <c>GRADE: C</c> when the submission contains the criterion and <c>GRADE: I</c> otherwise. Every turn
/// is computed from the conversation, so concurrent samples stay deterministic.
/// </summary>
internal sealed class FakeSimpleqaModel
{
    public const string ModelName = "simpleqa-scripted";

    /// <summary>The line of <c>DEFAULT_MODEL_GRADED_QA_TEMPLATE</c> that tells a grading turn from an answer turn.</summary>
    public const string GraderPromptMarker = "[BEGIN DATA]";

    /// <summary>Turns available before the scripted api reports itself exhausted (two per sample).</summary>
    private const int TurnBudget = 10_000;

    /// <summary>The wrong answers, by canned row index (every other row is answered with its reference answer).</summary>
    private static readonly IReadOnlyDictionary<int, string> WrongAnswers = new Dictionary<int, string>
    {
        [2] = "Omar Abdullah",
        [4] = "The Environmental Protection Agency",
    };

    private readonly IReadOnlyList<(string Problem, string Answer)> _rows;

    private FakeSimpleqaModel(IReadOnlyList<(string Problem, string Answer)> rows)
    {
        _rows = rows;
    }

    /// <summary>The scripted model over the canned rows of <paramref name="rowsPath"/> (a JSONL file with <c>problem</c> and <c>answer</c> columns).</summary>
    public static Model Create(string rowsPath)
    {
        var rows = CannedHfHub.LoadRows(rowsPath)
            .Select(row => (Problem: row["problem"]?.GetValue<string>() ?? "", Answer: row["answer"]?.GetValue<string>() ?? ""))
            .ToList();
        var model = new FakeSimpleqaModel(rows);
        return new Model(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(model.Respond), TurnBudget), ModelName));
    }

    /// <summary>Whether canned row <paramref name="index"/> is answered wrongly.</summary>
    public static bool AnswersWrongly(int index) => WrongAnswers.ContainsKey(index);

    /// <summary>The scripted answer for canned row <paramref name="index"/> whose reference answer is <paramref name="answer"/>.</summary>
    public static string Answer(int index, string answer) => WrongAnswers.TryGetValue(index, out var wrong) ? wrong : answer;

    /// <summary>The port's grader: C when the <c>[Submission]</c> contains the <c>[Criterion]</c> (case-insensitive), else I.</summary>
    public static string Grade(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var submission = Section(prompt, "[Submission]: ", "[Criterion]: ");
        var criterion = Section(prompt, "[Criterion]: ", "[END DATA]");
        var correct = criterion.Length > 0 && submission.Contains(criterion, StringComparison.OrdinalIgnoreCase);
        return correct
            ? "The submission states the expected answer.\n\nGRADE: C"
            : "The submission does not state the expected answer.\n\nGRADE: I";
    }

    private static string Section(string prompt, string start, string end)
    {
        var from = prompt.IndexOf(start, StringComparison.Ordinal);
        if (from < 0)
        {
            return "";
        }

        from += start.Length;
        var to = prompt.IndexOf(end, from, StringComparison.Ordinal);
        var text = to < 0 ? prompt[from..] : prompt[from..to];
        return text.Replace("***", "", StringComparison.Ordinal).Trim();
    }

    private ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        var prompt = messages.LastOrDefault(message => message is ChatMessageUser)?.Text ?? "";
        string reply;
        if (prompt.Contains(GraderPromptMarker, StringComparison.Ordinal))
        {
            reply = Grade(prompt);
        }
        else
        {
            var index = IndexOfProblem(prompt);
            reply = index < 0 ? "I don't know." : Answer(index, _rows[index].Answer);
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
