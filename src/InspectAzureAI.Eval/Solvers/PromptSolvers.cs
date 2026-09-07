using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Solvers;

/// <summary>Port of <c>solver/_prompt.py</c>: <c>system_message</c>, <c>prompt_template</c> and <c>user_message</c>.</summary>
public static partial class Solvers
{
    /// <summary>
    /// Port of <c>system_message()</c>: inserts the formatted template after any existing system messages
    /// (<c>append_system_message</c>). Template variables come from <paramref name="parameters"/>, the state's
    /// metadata and the store (parameters win).
    /// </summary>
    public static Solver SystemMessage(string template, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        return (state, _, _) =>
        {
            var content = TemplateFormatter.Format(template, TemplateVariables(state, parameters));
            AppendSystemMessage(state.Messages, new ChatMessageSystem(content));
            return Task.FromResult(state);
        };
    }

    /// <summary>
    /// Port of <c>prompt_template()</c>: rewrites the current user prompt through the template, where
    /// <c>{prompt}</c> is the prompt's text (a <c>prompt</c> parameter overrides it, as in Python).
    /// </summary>
    public static Solver PromptTemplate(string template, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        return (state, _, _) =>
        {
            var prompt = state.UserPrompt;
            var variables = new Dictionary<string, object?>(StringComparer.Ordinal) { ["prompt"] = prompt.Text };
            foreach (var (key, value) in TemplateVariables(state, parameters, omitPrompt: true))
            {
                variables[key] = value;
            }

            var index = state.Messages.FindLastIndex(m => ReferenceEquals(m, prompt));
            state.Messages[index] = prompt with { Content = WithText(prompt.Content, TemplateFormatter.Format(template, variables)) };
            return Task.FromResult(state);
        };
    }

    /// <summary>Port of <c>user_message()</c>: appends the formatted template as a user message.</summary>
    public static Solver UserMessage(string template, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        return (state, _, _) =>
        {
            state.Messages.Add(new ChatMessageUser(TemplateFormatter.Format(template, TemplateVariables(state, parameters))));
            return Task.FromResult(state);
        };
    }

    /// <summary>Port of <c>state.metadata | state.store._data | params</c>; only <c>prompt_template</c> omits a metadata/store <c>prompt</c> key (its own placeholder), <c>system_message</c> and <c>user_message</c> pass everything through.</summary>
    private static Dictionary<string, object?> TemplateVariables(TaskState state, IReadOnlyDictionary<string, object?>? parameters, bool omitPrompt = false)
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var source in new[] { state.Metadata, state.Store.ToDictionary() })
        {
            foreach (var (key, value) in source)
            {
                if (!omitPrompt || key != "prompt")
                {
                    variables[key] = value;
                }
            }
        }

        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
            {
                variables[key] = value;
            }
        }

        return variables;
    }

    /// <summary>Port of <c>solver/_util.py</c> <c>append_system_message</c>.</summary>
    private static void AppendSystemMessage(List<ChatMessage> messages, ChatMessageSystem message)
    {
        var lastIndex = messages.FindLastIndex(m => m is ChatMessageSystem);
        messages.Insert(lastIndex + 1, message);
    }

    /// <summary>Port of the <c>ChatMessageBase.text</c> setter: string content is replaced; a content list keeps its non-text items and gets one text item.</summary>
    private static MessageContent WithText(MessageContent content, string text) =>
        content.IsString ? text : MessageContent.FromItems([.. (content.Items ?? []).Where(c => c is not ContentText), new ContentText(text)]);
}

/// <summary>
/// Port of <c>_util/format.py</c> <c>format_template</c>: Python <c>str.format</c>-style <c>{name}</c>
/// substitution that leaves unknown, null-valued and unformattable placeholders (and lone braces) intact.
/// Only simple field names are resolved; attribute/index access such as <c>{a.b}</c> is left intact.
/// </summary>
internal static class TemplateFormatter
{
    public static string Format(string template, IReadOnlyDictionary<string, object?> variables)
    {
        var result = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    result.Append('{');
                    i += 2;
                    continue;
                }

                var close = FindClosingBrace(template, i + 1);
                if (close < 0)
                {
                    result.Append(template, i, template.Length - i);
                    break;
                }

                var field = template.Substring(i + 1, close - i - 1);
                result.Append(Substitute(field, variables) ?? "{" + field + "}");
                i = close + 1;
            }
            else if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                result.Append('}');
                i += 2;
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }

    private static int FindClosingBrace(string template, int start)
    {
        var depth = 0;
        for (var i = start; i < template.Length; i++)
        {
            switch (template[i])
            {
                case '{':
                    depth++;
                    break;
                case '}' when depth == 0:
                    return i;
                case '}':
                    depth--;
                    break;
            }
        }

        return -1;
    }

    private static string? Substitute(string field, IReadOnlyDictionary<string, object?> variables)
    {
        var nameEnd = field.IndexOfAny([':', '!']);
        var name = nameEnd < 0 ? field : field[..nameEnd];
        var specIndex = field.IndexOf(':');
        var spec = specIndex < 0 ? "" : field[(specIndex + 1)..];
        if (name.Length == 0 || name.IndexOfAny(['.', '[']) >= 0)
        {
            return null;
        }

        if (!variables.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        if (spec.Length == 0)
        {
            return Stringify(value);
        }

        try
        {
            return value is IFormattable formattable ? formattable.ToString(spec, CultureInfo.InvariantCulture) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Python's <c>str(value)</c> for the value types template variables carry.</summary>
    private static string Stringify(object value) => value switch
    {
        string s => s,
        bool b => b ? "True" : "False",
        JsonValue json when json.TryGetValue<string>(out var s) => s,
        JsonValue json when json.TryGetValue<bool>(out var b) => b ? "True" : "False",
        JsonNode json => json.ToJsonString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
