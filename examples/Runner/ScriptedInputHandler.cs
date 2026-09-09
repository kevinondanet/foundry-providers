using InspectAzureAI.Eval.Context.Input;

namespace InspectAzureAI.Examples.Runner;

/// <summary>
/// The operator behind <c>--fake</c>: an <see cref="IInputHandler"/> that answers each <c>ask_user</c> question from a
/// script instead of the Textual "Question" tab of <c>examples/ask_user/demo.py</c> (or the console prompt Python
/// falls back to), so an offline run never waits on the terminal. Answers are served in order; once they run out
/// every further question is declined (the <c>User declined to answer the question.</c> tool error path). Each
/// question and its scripted answer are printed, and every request is kept in <see cref="Requests"/> for tests.
/// Shared by the ask_user and inline_cards examples; <see cref="ExampleRunner"/> installs a declining one as
/// <see cref="InputHandlers.Default"/> for every <c>--fake</c> run, so a plain <c>ask_user()</c> never blocks either.
/// </summary>
public sealed class ScriptedInputHandler(IEnumerable<InputResult> answers, TextWriter? output = null) : IInputHandler
{
    private readonly object _sync = new();
    private readonly Queue<InputResult> _answers = new(answers ?? throw new ArgumentNullException(nameof(answers)));
    private readonly List<InputRequest> _requests = [];
    private readonly TextWriter _output = output ?? Console.Out;

    /// <summary>Every question asked so far, in order.</summary>
    public IReadOnlyList<InputRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>A handler answering every question with <paramref name="content"/> (the same accepted answer each time).</summary>
    public static ScriptedInputHandler Accepting(IReadOnlyDictionary<string, object?> content, TextWriter? output = null, int times = 1) =>
        new(Enumerable.Repeat(InputResult.Accepted(content), times), output);

    /// <summary>A handler declining every question.</summary>
    public static ScriptedInputHandler Declining(TextWriter? output = null) => new([], output);

    public Task<InputResult> RequestAsync(InputRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        InputResult result;
        lock (_sync)
        {
            _requests.Add(request);
            result = _answers.Count > 0 ? _answers.Dequeue() : InputResult.Declined();
        }

        var fields = string.Join(", ", request.Schema.Properties.Select(pair => $"{pair.Key}:{pair.Value.Type}{(request.Schema.IsRequired(pair.Key) ? "" : "?")}"));
        _output.WriteLine($"[ask_user] {request.Message}");
        _output.WriteLine($"[ask_user]   fields: {fields}");
        _output.WriteLine(result.Outcome == InputOutcome.Accepted
            ? $"[ask_user]   -> accepted {result.ContentJson()} (scripted)"
            : $"[ask_user]   -> {result.Outcome.ToPython()} (scripted)");
        return Task.FromResult(result);
    }
}
