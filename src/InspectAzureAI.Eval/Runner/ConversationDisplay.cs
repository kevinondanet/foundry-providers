using System.Text;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Runner;

using Hooks = InspectAzureAI.Eval.Hooks.Hooks;

/// <summary>
/// Port of the <c>conversation</c> display mode (<c>model/_display.py</c> plus <c>util/_conversation.py</c>
/// <c>conversation_panel</c>): prints the messages of every model turn as it happens — the user/system/tool messages
/// since the previous assistant turn, then the assistant message (text, reasoning and rendered tool calls). Run it
/// by adding it to <c>EvalOptions.Hooks</c> (Python's <c>--display conversation</c>; from the CLI,
/// <c>--hooks ConversationDisplay</c> instantiates it by type name): it listens for <see cref="ModelEvent"/>s. Deviation: rich panels and markdown are rendered as plain text — a <c>── Title ──</c>
/// rule, the content, a blank line — and delivery through the hook channel is asynchronous, so output can lag the
/// event slightly (it stays in order).
/// </summary>
public sealed class ConversationDisplay(TextWriter? writer = null) : Hooks
{
    /// <summary>Python's <c>tool_result_display(output, 50)</c>: tool output lines shown before truncation.</summary>
    public const int MaxToolOutputLines = 50;

    /// <summary>Python's <c>truncate_lines</c> default character cap, applied before the line cap.</summary>
    public const int MaxToolOutputCharacters = 100 * 100;

    private readonly TextWriter _writer = writer ?? Console.Out;

    // samples run concurrently and a TextWriter is not thread safe
    private readonly object _sync = new();

    public override Task OnSampleEventAsync(SampleEvent data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Event is ModelEvent model)
        {
            if (!model.Output.Empty)
            {
                Assistant(model.Input, model.Output.Message);
            }
            else if (model.Error is { } error)
            {
                AssistantError(error);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Port of <c>display_conversation_assistant</c>: the input messages since the last assistant message, then <paramref name="message"/>.</summary>
    public void Assistant(IReadOnlyList<ChatMessage> input, ChatMessageAssistant message)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync)
        {
            foreach (var preceding in MessagesPrecedingAssistant(input))
            {
                Message(preceding);
            }

            AssistantMessage(message);
        }
    }

    /// <summary>Port of <c>display_conversation_assistant_error</c>.</summary>
    public void AssistantError(string error) => Panel("Assistant", error);

    /// <summary>Port of <c>display_conversation_message</c>: a tool message as its output, an assistant message in full, anything else as a panel titled by its role.</summary>
    public void Message(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message)
        {
            case ChatMessageTool tool:
                ToolMessage(tool);
                break;
            case ChatMessageAssistant assistant:
                AssistantMessage(assistant);
                break;
            default:
                Panel(Capitalize(message.Role), message.Text);
                break;
        }
    }

    /// <summary>Port of <c>display_conversation_tool_message</c>: the error message or output, truncated to <see cref="MaxToolOutputLines"/>; nothing when empty.</summary>
    public void ToolMessage(ChatMessageTool message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var output = (message.Error is { } error ? error.Message : message.Text).Trim();
        if (output.Length > 0)
        {
            Panel($"Tool Output: {message.Function}", RenderToolOutput(output));
        }
    }

    /// <summary>Port of <c>display_conversation_assistant_message</c>.</summary>
    public void AssistantMessage(ChatMessageAssistant message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Panel("Assistant", RenderAssistant(message));
    }

    /// <summary>Port of <c>conversation_panel</c>: <c>── title ──</c>, the content (when any) and a blank line.</summary>
    public void Panel(string title, string? content = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        var text = new StringBuilder();
        text.Append("── ").Append(title).Append(" ──").Append('\n');
        if (!string.IsNullOrEmpty(content))
        {
            text.Append(content.TrimEnd()).Append('\n');
        }

        lock (_sync)
        {
            _writer.Write(text.ToString());
            _writer.WriteLine();
            _writer.Flush();
        }
    }

    /// <summary>Port of <c>messages_preceding_assistant</c>: the messages after the last assistant message (all of them when there is none).</summary>
    public static IReadOnlyList<ChatMessage> MessagesPrecedingAssistant(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var start = messages.Count;
        while (start > 0 && messages[start - 1] is not ChatMessageAssistant)
        {
            start--;
        }

        return messages.Skip(start).ToList();
    }

    /// <summary>The assistant message body: text and reasoning blocks, a blank line, then the tool calls as Python calls in a fenced block.</summary>
    public static string RenderAssistant(ChatMessageAssistant message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var blocks = new List<string>();
        if (message.Content.IsString)
        {
            var text = message.Text.Trim();
            if (text.Length > 0)
            {
                blocks.Add(text);
            }
        }
        else
        {
            foreach (var content in message.Content.Items!)
            {
                switch (content)
                {
                    case ContentReasoning reasoning:
                        if (RenderReasoning(reasoning) is { } rendered)
                        {
                            blocks.Add(rendered);
                        }

                        break;
                    case ContentText { Text.Length: > 0 } text:
                        blocks.Add(text.Text.Trim());
                        break;
                }
            }
        }

        if (message.ToolCalls is { Count: > 0 } calls)
        {
            if (blocks.Count > 0)
            {
                blocks.Add("");
            }

            blocks.Add(RenderToolCalls(calls));
        }

        return string.Join("\n", blocks);
    }

    /// <summary>Port of <c>render_tool_calls</c> / <c>transcript_function</c>: each call as <c>function(arg=value, ...)</c> in a <c>```python</c> block.</summary>
    public static string RenderToolCalls(IEnumerable<ToolCall> calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        return string.Join("\n", calls.Select(call => "```python\n" + ModelGraded.FormatFunctionCall(call.Function, call.Arguments) + "\n```"));
    }

    /// <summary>Port of <c>tool_result_display</c>: control characters removed, the text capped at <see cref="MaxToolOutputCharacters"/> and <paramref name="maxLines"/> lines with Python's truncation note.</summary>
    public static string RenderToolOutput(string text, int maxLines = MaxToolOutputLines)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (lines, truncated) = TruncateLines(CleanControlCharacters(text), maxLines);
        return truncated is { } additional ? $"{lines}\n\nOutput truncated ({additional} additional lines)..." : lines;
    }

    /// <summary>Port of <c>transcript_reasoning</c> as plain text: the reasoning (or the provider's summary, or the redaction notice) between <c>&lt;think&gt;</c> markers, capped at 50 lines.</summary>
    private static string? RenderReasoning(ContentReasoning reasoning)
    {
        var text = (reasoning.Redacted
            ? reasoning.Summary is { Length: > 0 } ? reasoning.Summary : "Reasoning encrypted by model provider."
            : reasoning.Reasoning).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var (lines, truncated) = TruncateLines(text, 50, maxCharacters: null);
        if (truncated is { } additional)
        {
            lines += $"\n\n_Content truncated ({additional} additional lines)..._";
        }

        return $"<think>\n{lines}\n</think>";
    }

    /// <summary>Port of <c>_util/text.py</c> <c>truncate_lines</c>.</summary>
    private static (string Text, int? Truncated) TruncateLines(string text, int maxLines, int? maxCharacters = MaxToolOutputCharacters)
    {
        if (maxCharacters is { } cap && text.Length > cap)
        {
            text = text[..(cap - 3)] + "...";
        }

        var lines = text.Split('\n');
        if (lines.Length > maxLines)
        {
            return (string.Join("\n", lines.Take(maxLines)), lines.Length - maxLines);
        }

        return (text, null);
    }

    private static string CleanControlCharacters(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            // Python keeps newlines and tabs and drops the Cc (control) and Cf (format) categories
            if (c is '\n' or '\t' || char.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format))
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static string Capitalize(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
