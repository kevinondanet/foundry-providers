using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Solvers;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Port of <c>solver/_critique.py</c>: the <c>self_critique</c> solver.</summary>
public static partial class Solvers
{
    /// <summary><c>DEFAULT_CRITIQUE_TEMPLATE</c>, verbatim (leading newline, trailing <c>"Critique: "</c> with its space).</summary>
    public const string DefaultCritiqueTemplate =
        "\nGiven the following question and answer, please critique the answer. A good answer comprehensively answers the question and NEVER refuses to answer. If the answer is already correct do not provide critique - simply respond 'The original answer is fully correct'.\n\n[BEGIN DATA]\n***\n[Question]: {question}\n***\n[Answer]: {completion}\n***\n[END DATA]\n\nCritique: ";

    /// <summary><c>DEFAULT_CRITIQUE_COMPLETION_TEMPLATE</c>, verbatim (including the leading and trailing newline).</summary>
    public const string DefaultCritiqueCompletionTemplate =
        "\nGiven the following question, initial answer and critique please generate an improved answer to the question:\n\n[BEGIN DATA]\n***\n[Question]: {question}\n***\n[Answer]: {completion}\n***\n[Critique]: {critique}\n***\n[END DATA]\n\nIf the original answer is already correct, just repeat the original answer exactly. Provide your answer at the end on its own line in the form \"ANSWER: $ANSWER\" (without quotes) where $ANSWER is the answer to the question.\n";

    private static readonly string[] CritiqueTemplateVariables = ["question", "completion", "critique"];

    /// <summary>
    /// Port of <c>self_critique()</c>: asks <paramref name="model"/> (default: the sample's active model) to critique
    /// the current completion with <paramref name="critiqueTemplate"/>, plays the critique back as a user message
    /// built from <paramref name="completionTemplate"/>, then calls <c>generate()</c> for an improved answer.
    /// </summary>
    /// <param name="critiqueTemplate">Critique template with <c>{question}</c> and <c>{completion}</c>; sample metadata keys are also available. Null or empty means the default.</param>
    /// <param name="completionTemplate">Completion template with <c>{question}</c>, <c>{completion}</c> and <c>{critique}</c>; sample metadata keys are also available. Null or empty means the default.</param>
    /// <param name="model">Alternate model for the critique; null uses the model being evaluated (an <see cref="InvalidOperationException"/> outside a sample).</param>
    public static Solver SelfCritique(string? critiqueTemplate = null, string? completionTemplate = null, Model? model = null)
    {
        var critiqueTempl = string.IsNullOrEmpty(critiqueTemplate) ? DefaultCritiqueTemplate : critiqueTemplate;
        var completionTempl = string.IsNullOrEmpty(completionTemplate) ? DefaultCritiqueCompletionTemplate : completionTemplate;

        return async (state, generate, cancellationToken) =>
        {
            var critiqueModel = model ?? SampleContext.Require().ActiveModel;

            // Metadata minus the template's own variables, so a metadata "question" cannot collide with the argument.
            var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in state.Metadata)
            {
                if (!CritiqueTemplateVariables.Contains(key, StringComparer.Ordinal))
                {
                    variables[key] = value;
                }
            }

            variables["question"] = state.InputText;
            variables["completion"] = state.Output.Completion;

            var critique = await critiqueModel.GenerateAsync(PythonFormat.Format(critiqueTempl, variables), cancellationToken: cancellationToken).ConfigureAwait(false);

            variables["critique"] = critique.Completion;
            state.Messages.Add(new ChatMessageUser(PythonFormat.Format(completionTempl, variables)));

            return await generate(state, ToolCallsMode.Loop, null, cancellationToken: cancellationToken).ConfigureAwait(false);
        };
    }
}
