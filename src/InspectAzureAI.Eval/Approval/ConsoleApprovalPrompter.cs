using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;

namespace InspectAzureAI.Eval.Approval;

/// <summary>
/// Port of <c>approval/_human/console.py</c> <c>console_approval</c>: prints the "Approve Tool" panel (assistant
/// message, view context and call, with <c>{{param}}</c> placeholders substituted from the arguments) and reads a
/// one-letter decision. As with rich's <c>Prompt.ask</c>, an unknown answer re-prompts and an empty line takes the
/// default <c>a</c> (approve) even when approve is not among the offered choices. End of input is an
/// <see cref="EndOfStreamException"/>. The console cannot express <see cref="ApprovalDecision.Modify"/>.
/// </summary>
public sealed class ConsoleApprovalPrompter(TextReader? input = null, TextWriter? output = null) : IApprovalPrompter
{
    public const string Title = "Approve Tool";

    public const string InvalidChoice = "Please select one of the available options";

    public async Task<ApprovalDecision> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Choices.Contains(ApprovalDecision.Modify))
        {
            throw new NotSupportedException("Console approval cannot offer 'modify'; choose among approve, reject, terminate and escalate.");
        }

        var reader = input ?? Console.In;
        var writer = output ?? Console.Out;
        await writer.WriteLineAsync(Render(request.Message, request.View, request.Call.Arguments)).ConfigureAwait(false);

        var letters = new Dictionary<string, ApprovalDecision>(StringComparer.Ordinal);
        var labels = new List<string>();
        foreach (var choice in request.Choices)
        {
            var name = choice.ToPython();
            var letter = name[..1];
            letters[letter] = choice;
            labels.Add($"{Capitalize(name)} ({letter})");
        }

        var prompt = $"{string.Join(", ", labels.Take(labels.Count - 1))}, or {labels[^1]}";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync($"{prompt} [{string.Join('/', letters.Keys)}] (a): ").ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("Console approval input ended before a decision was entered.");
            var value = line.Trim();
            ApprovalDecision decision;
            if (value.Length == 0)
            {
                decision = ApprovalDecision.Approve;
            }
            else if (!letters.TryGetValue(value, out decision))
            {
                await writer.WriteLineAsync(InvalidChoice).ConfigureAwait(false);
                continue;
            }

            await writer.WriteLineAsync($"Decision: {Capitalize(decision.ToPython())}").ConfigureAwait(false);
            return decision;
        }
    }

    /// <summary>Port of <c>render_tool_approval</c> as plain text: title, the assistant message, the view's context and call.</summary>
    public static string Render(string message, ToolCallView view, JsonObject? arguments)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(view);
        if (arguments is not null)
        {
            view = ToolCallViews.Substitute(view, arguments);
        }

        var text = new StringBuilder();
        text.AppendLine($"── {Title} ──");
        message = message.Trim();
        if (message.Length > 0)
        {
            text.AppendLine("Assistant");
            text.AppendLine(message);
        }

        if (view.Context is { } context)
        {
            text.AppendLine();
            AppendContent(text, context);
            text.AppendLine();
        }

        if (view.Call is { } call)
        {
            if (message.Length > 0 || view.Context is not null)
            {
                text.AppendLine("․․․․․․․․․․");
            }

            text.AppendLine();
            AppendContent(text, call);
        }

        return text.ToString().TrimEnd();
    }

    private static void AppendContent(StringBuilder text, ToolCallContent content)
    {
        if (!string.IsNullOrEmpty(content.Title))
        {
            text.AppendLine(content.Title);
        }

        text.AppendLine(content.Content);
    }

    private static string Capitalize(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
