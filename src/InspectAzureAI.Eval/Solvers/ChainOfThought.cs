using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Solvers;

/// <summary>Port of the <c>chain_of_thought</c> and <c>assistant_message</c> solvers of <c>solver/_prompt.py</c>.</summary>
public static partial class Solvers
{
    /// <summary><c>DEFAULT_COT_TEMPLATE</c>, verbatim (including the leading and trailing newline).</summary>
    public const string DefaultCotTemplate =
        "\n{prompt}\n\nBefore answering, reason in a step-by-step manner as to get the right answer. Provide your answer at the end on its own line in the form \"ANSWER: $ANSWER\" (without quotes) where $ANSWER is the answer to the question.\n";

    /// <summary>
    /// Port of <c>chain_of_thought(template)</c>: rewrites the user prompt through <paramref name="template"/>, whose
    /// single variable is <c>{prompt}</c> (Python <c>str.format</c>, so any other placeholder is an error).
    /// </summary>
    public static Solver ChainOfThought(string template = DefaultCotTemplate)
    {
        ArgumentNullException.ThrowIfNull(template);
        return (state, _, _) =>
        {
            var prompt = state.UserPrompt;
            var variables = new Dictionary<string, object?>(StringComparer.Ordinal) { ["prompt"] = prompt.Text };
            SetUserPromptText(state, PythonFormat.Format(template, variables));
            return Task.FromResult(state);
        };
    }

    /// <summary>
    /// Port of <c>assistant_message()</c>: appends the formatted template as an assistant message attributed to the
    /// state's model. Template variables come from <paramref name="parameters"/>, the state's metadata and the store
    /// (parameters win), like <see cref="UserMessage"/>.
    /// </summary>
    public static Solver AssistantMessage(string template, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        return (state, _, _) =>
        {
            var content = TemplateFormatter.Format(template, TemplateVariables(state, parameters));
            state.Messages.Add(new ChatMessageAssistant(content, model: state.Model));
            return Task.FromResult(state);
        };
    }
}
