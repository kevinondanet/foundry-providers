using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Scorers;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>scorer/_model.py</c>: the grading templates, instructions and grade regexes of
/// <c>model_graded_qa</c> / <c>model_graded_fact</c>, <c>chat_history</c>, <c>model_scoring_prompt</c>
/// and the delimiter neutralisation that hardens the judge prompt against injection.
/// </summary>
public static class ModelGraded
{
    /// <summary><c>DEFAULT_MODEL_GRADED_QA_TEMPLATE</c>, verbatim (including the leading and trailing newline).</summary>
    public const string DefaultQaTemplate = """

        You are assessing a submitted answer on a given task based on a criterion. Here is the data:

        [BEGIN DATA]
        ***
        [Task]: {question}
        ***
        [Submission]: {answer}
        ***
        [Criterion]: {criterion}
        ***
        [END DATA]

        Does the submission meet the criterion?

        {instructions}

        """;

    /// <summary><c>DEFAULT_MODEL_GRADED_FACT_TEMPLATE</c>, verbatim.</summary>
    public const string DefaultFactTemplate = """

        You are comparing a submitted answer to an expert answer on a given question. Here is the data:

        [BEGIN DATA]
        ************
        [Question]: {question}
        ************
        [Expert]: {criterion}
        ************
        [Submission]: {answer}
        ************
        [END DATA]

        Compare the factual content of the submitted answer with the expert answer. Ignore any differences in style, grammar, or punctuation.

        Does the submission contain the content in the expert answer?

        {instructions}

        """;

    /// <summary>Whitespace plus the zero-width and formatting marks that can surround the verdict separator.</summary>
    private const string GradeSpacing = "[\\s\u200b\u200c\u200d\u200e\u200f\u2060\u2063\ufeff]*";

    /// <summary>
    /// <c>DEFAULT_GRADE_PATTERN</c>: the leading greedy <c>.*</c> binds the search to the <em>last</em>
    /// <c>GRADE: X</c>, so earlier mentions in the chain of thought (or injected via the submission) cannot win.
    /// </summary>
    public static readonly string DefaultGradePattern = $"(?is).*(?<!\\w)GRADE(?!\\w){GradeSpacing}:{GradeSpacing}([CPI])";

    /// <summary>
    /// <c>_PERMISSIVE_GRADE_PATTERN</c>: captures whatever word follows the separator so the verdict can be
    /// validated afterwards; <c>\w*</c> (not <c>+</c>) so the capture never forces backtracking onto an earlier grade.
    /// </summary>
    internal static readonly string PermissiveGradePattern = $"(?is).*(?<!\\w)GRADE(?!\\w){GradeSpacing}:{GradeSpacing}(\\w*)";

    private static readonly IReadOnlyDictionary<string, string> GradeWordValues = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["c"] = "C", ["correct"] = "C", ["i"] = "I", ["incorrect"] = "I", ["p"] = "P", ["partial"] = "P",
    };

    private static readonly string[] TemplateVariables = ["question", "answer", "criterion", "instructions"];

    // A literal space (not \s) is intentional: \s also matches NBSP, which would let a model pre-neutralise
    // its own output and bypass the mitigation.
    private static readonly Regex StructuralDelimiter = new(@"\[(BEGIN|END) DATA\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Port of <c>default_instructions(partial_credit)</c>, verbatim.</summary>
    public static string DefaultInstructions(bool partialCredit)
    {
        var partialLetter = partialCredit ? "P" : "";
        var partialPrompt = partialCredit ? "\"P\" for partially correct answers," : "";
        return "\n"
            + $"After assessing the submitted answer, reply with 'GRADE: $LETTER' (without quotes) where LETTER is one of C{partialLetter}I.  Please choose ONE option for the grade: either \"C\" for correct answers, {partialPrompt}or \"I\" for incorrect answers.\n"
            + "\n"
            + "For example, after reviewing a correct answer you might write 'GRADE: C' or after reviewing an incorrect answer you might write 'GRADE: I'.\n"
            + "\n"
            + $"First, write out in a step by step manner your reasoning about the criterion to be sure that your conclusion is correct. Avoid simply stating the correct answers at the outset. Then, end with your answer formatted as 'GRADE: $LETTER' (without quotes) where LETTER is one of C{partialLetter}I.\n";
    }

    /// <summary>Port of <c>neutralize_structural_delimiters</c>: <c>[END DATA]</c> becomes <c>[END-DATA]</c> (idempotent).</summary>
    public static string NeutralizeStructuralDelimiters(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return StructuralDelimiter.Replace(text, m => m.Value.Replace(' ', '-'));
    }

    /// <summary>
    /// Port of <c>chat_history(state)</c>: drops system messages, keeps everything through the last assistant
    /// turn and renders <c>User:</c> / <c>Assistant:</c> / <c>Tool (fn):</c> lines after the first message's text.
    /// </summary>
    public static string ChatHistory(TaskState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var messages = state.Messages.Where(m => m is not ChatMessageSystem).ToList();
        var lastAssistant = messages.FindLastIndex(m => m is ChatMessageAssistant);
        if (lastAssistant >= 0)
        {
            messages = messages.GetRange(0, lastAssistant + 1);
        }

        var history = new List<string>();
        if (messages.Count > 0)
        {
            history.Add(messages[0].Text);
            foreach (var message in messages.Skip(1))
            {
                switch (message)
                {
                    case ChatMessageUser user:
                        history.Add($"User: {user.Text}");
                        break;
                    case ChatMessageAssistant assistant:
                        var parts = new List<string>();
                        if (assistant.Text.Length > 0)
                        {
                            parts.Add(assistant.Text);
                        }

                        parts.AddRange((assistant.ToolCalls ?? []).Select(call => FormatFunctionCall(call.Function, call.Arguments)));
                        history.Add("Assistant: " + string.Join("\n\n", parts));
                        break;
                    case ChatMessageTool tool:
                        var error = tool.Error is { } e ? $"type='{e.Type}' message='{e.Message}'" : "";
                        history.Add($"Tool ({tool.Function}): {error}{tool.Text}");
                        break;
                }
            }
        }

        return string.Join("\n\n", history);
    }

    /// <summary>
    /// Port of <c>model_scoring_prompt</c>: neutralises delimiters in every dataset-controlled input, lifts
    /// media out of the model output into attachments, and formats the template.
    /// </summary>
    public static ChatMessageUser ModelScoringPrompt(
        string template,
        string question,
        ModelOutput output,
        string criterion,
        string instructions,
        IReadOnlyDictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(metadata);
        var answer = NeutralizeStructuralDelimiters(output.Completion);
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            variables[key] = SanitizeMetadataValue(value);
        }

        var media = output.Choices.Count > 0 && output.Message.Content.Items is { } items
            ? items.Where(c => c.Type is "image" or "audio" or "video").ToList()
            : [];
        if (media.Count > 0)
        {
            answer = answer.Length > 0 ? $"{answer} (see also attached media)" : "See attached media";
        }

        variables["question"] = NeutralizeStructuralDelimiters(question);
        variables["answer"] = answer;
        variables["criterion"] = NeutralizeStructuralDelimiters(criterion);
        variables["instructions"] = instructions;
        var prompt = FormatTemplate(template, variables);

        return media.Count > 0
            ? new ChatMessageUser(new Content[] { new ContentText(prompt) }.Concat(media).ToList())
            : new ChatMessageUser(prompt);
    }

    /// <summary>Port of <c>_model_graded_qa_single</c>; <paramref name="model"/> null grades with the sample's active model.</summary>
    internal static Scorer Create(string template, string? instructions, string? gradePattern, bool includeHistory, bool partialCredit, Model? model)
    {
        var usingDefaultInstructions = string.IsNullOrEmpty(instructions);
        var resolvedInstructions = usingDefaultInstructions ? DefaultInstructions(partialCredit) : instructions!;
        var defaultGradePattern = gradePattern is null;
        // Only when we wrote the instructions do we know which grades were offered; custom instructions or
        // an explicit pattern are authoritative and keep every grade they match.
        var validateOfferedGrades = defaultGradePattern && usingDefaultInstructions;
        string[] offeredGrades = partialCredit ? ["C", "P", "I"] : ["C", "I"];
        var regex = new Regex(validateOfferedGrades ? PermissiveGradePattern : gradePattern ?? DefaultGradePattern);

        return async (state, target, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentNullException.ThrowIfNull(target);
            var grader = model ?? SampleContext.Require().ActiveModel;
            var metadata = state.Metadata
                .Where(pair => !TemplateVariables.Contains(pair.Key, StringComparer.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var question = includeHistory ? ChatHistory(state) : state.InputText;
            var scoringPrompt = ModelScoringPrompt(template, question, state.Output, target.Text, resolvedInstructions, metadata);

            var result = await grader.GenerateAsync([scoringPrompt], cancellationToken: cancellationToken).ConfigureAwait(false);

            var match = regex.Match(result.Completion);
            var value = match.Success ? match.Groups[1].Value : null;
            if (value is not null && defaultGradePattern)
            {
                // The permissive capture takes the whole word so "GRADE: Correct" still resolves; any other
                // multi-character verdict (e.g. "CI") is a protocol deviation, not a laundered first letter.
                var trimmed = value.Trim();
                value = GradeWordValues.TryGetValue(trimmed.ToLowerInvariant(), out var letter)
                    ? letter
                    : trimmed.Length == 1 ? trimmed.ToUpperInvariant() : null;
                if (validateOfferedGrades && value is not null && !offeredGrades.Contains(value, StringComparer.Ordinal))
                {
                    value = null;
                }
            }

            var grading = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["grading"] = new List<ChatMessage> { scoringPrompt, result.Choices.Count > 0 ? result.Message : new ChatMessageAssistant(result.Completion) },
            };
            return value is not null
                ? new Score(value) { Answer = state.Output.Completion, Explanation = result.Completion, Metadata = grading }
                : Score.Unscored(
                    reason: "grader_failed",
                    answer: state.Output.Completion,
                    explanation: "Grade not found in model output: " + result.Completion,
                    metadata: grading);
        };
    }

    /// <summary>Port of <c>format_function_call</c> (<c>_util/format.py</c>): <c>fn(a='x', b=1)</c>, wrapped over lines past 80 columns.</summary>
    internal static string FormatFunctionCall(string functionName, JsonObject arguments, int indentSpaces = 4, int width = 80)
    {
        var formatted = arguments.Select(pair => $"{pair.Key}={FormatValue(pair.Value)}").ToList();
        var args = string.Join(", ", formatted);
        if (args.Length <= width - 1 - functionName.Length - 2)
        {
            return $"{functionName}({args})";
        }

        var indent = new string(' ', indentSpaces);
        var indented = string.Join("\n", string.Join(",\n", formatted).Split('\n').Select(line => line.Trim().Length == 0 ? line : indent + line));
        return $"{functionName}(\n{indented}\n)";
    }

    /// <summary>Python <c>format_value</c>: strings single-quoted, scalars via <c>str()</c>, containers pretty-printed (JSON here).</summary>
    private static string FormatValue(JsonNode? value) => value switch
    {
        null => "None",
        JsonValue v when v.TryGetValue<string>(out var s) => $"'{s}'",
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "True" : "False",
        JsonValue v => v.ToJsonString(),
        _ => value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
    };

    /// <summary>Port of <c>_sanitize_metadata_value</c>: string leaves are neutralised, container structure is preserved.</summary>
    private static object? SanitizeMetadataValue(object? value) => value switch
    {
        string s => NeutralizeStructuralDelimiters(s),
        IReadOnlyDictionary<string, object?> dict => dict.ToDictionary(pair => pair.Key, pair => SanitizeMetadataValue(pair.Value), StringComparer.Ordinal),
        IEnumerable<object?> list => list.Select(SanitizeMetadataValue).ToList(),
        _ => value,
    };

    /// <summary>
    /// The subset of Python <c>str.format</c> the grading templates use: <c>{name}</c> substitution and
    /// <c>{{</c> / <c>}}</c> escapes. Format specs, conversions and attribute/index access are not supported.
    /// </summary>
    internal static string FormatTemplate(string template, IReadOnlyDictionary<string, object?> variables)
    {
        var result = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var ch = template[i];
            if (ch == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    result.Append('{');
                    i += 2;
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new FormatException("Single '{' encountered in format string");
                }

                var name = template[(i + 1)..close];
                if (!variables.TryGetValue(name, out var value))
                {
                    throw new KeyNotFoundException($"Template variable '{name}' is not defined.");
                }

                result.Append(PythonStr(value));
                i = close + 1;
                continue;
            }

            if (ch == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    result.Append('}');
                    i += 2;
                    continue;
                }

                throw new FormatException("Single '}' encountered in format string");
            }

            result.Append(ch);
            i++;
        }

        return result.ToString();
    }

    /// <summary>Python <c>str(value)</c>: a string as-is, everything else its <c>repr</c>.</summary>
    private static string PythonStr(object? value) => value is string s ? s : PythonRepr(value);

    private static string PythonRepr(object? value) => value switch
    {
        null => "None",
        string s => $"'{s}'",
        bool b => b ? "True" : "False",
        IReadOnlyDictionary<string, object?> dict => "{" + string.Join(", ", dict.Select(pair => $"'{pair.Key}': {PythonRepr(pair.Value)}")) + "}",
        IEnumerable<object?> list => "[" + string.Join(", ", list.Select(PythonRepr)) + "]",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
