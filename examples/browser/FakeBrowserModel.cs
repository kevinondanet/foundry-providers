using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Browser;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The model behind <c>--fake</c> for <c>examples/browser/browser.py</c>: a <see cref="ScriptedModelApi"/> whose
/// turns are computed from the conversation. It navigates to https://www.aisi.gov.uk/ with <c>web_browser_go</c>,
/// reads the accessibility tree the tool returns, clicks the first link whose name contains "About" with
/// <c>web_browser_click</c> (falling back to the first link on the page), then answers with a two-paragraph
/// summary built from the main content of the page it landed on.
/// </summary>
internal static partial class FakeBrowserModel
{
    public const string ModelName = "browser-scripted";

    /// <summary>The script needs three turns; the budget leaves room for a retry or an extra page.</summary>
    private const int TurnBudget = 12;

    public static Model Create() => new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From(Respond), TurnBudget), ModelName));

    /// <summary>One turn: go, click, or summarise, depending on how many assistant turns have gone before.</summary>
    internal static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools)
    {
        var step = messages.Count(message => message is ChatMessageAssistant);
        var results = messages.OfType<ChatMessageTool>().ToList();
        var output = step switch
        {
            0 => ScriptedTurn.ToolCall(WebBrowser.GoName, new { url = FakeBrowserSandbox.HomeUrl }, text: "I'll open the AISI website first.").Output!,
            1 when LinkId(results.LastOrDefault()?.Text ?? "") is { } id =>
                ScriptedTurn.ToolCall(WebBrowser.ClickName, new { element_id = id }, text: "The About page should describe the institute's work; clicking it.").Output!,
            _ => ModelOutput.FromContent(ModelName, Summary(results.LastOrDefault()?.Text ?? "")),
        };
        return output with { Model = ModelName };
    }

    /// <summary>The id of the first link named "About..." in an accessibility tree, else the first link, else null.</summary>
    internal static int? LinkId(string tree)
    {
        var links = LinkPattern().Matches(tree).Select(match => (Id: int.Parse(match.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture), Name: match.Groups["name"].Value)).ToList();
        var about = links.FirstOrDefault(link => link.Name.Contains("About", StringComparison.OrdinalIgnoreCase));
        return about != default ? about.Id : links.Count > 0 ? links[0].Id : null;
    }

    /// <summary>Two paragraphs: the page's main content (or its static text) and a closing paragraph.</summary>
    internal static string Summary(string toolResult)
    {
        var main = MainContentPattern().Match(toolResult);
        var body = main.Success
            ? main.Groups["content"].Value.Trim()
            : string.Join(" ", StaticTextPattern().Matches(toolResult).Select(match => match.Groups["text"].Value));
        if (body.Length == 0)
        {
            body = "The page did not expose a readable description of the institute's work.";
        }

        return $"The UK AI Security Institute describes its work as follows. {body}\n\n"
            + "In short, the institute evaluates frontier AI systems before and after deployment, studies the risks they pose and the safeguards that mitigate them, and shares that work with developers, governments and researchers.";
    }

    [GeneratedRegex(@"^\s*\[(?<id>\d+)\] link ""(?<name>[^""]*)""", RegexOptions.Multiline)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"main content:\n(?<content>.*?)\n\naccessibility tree:", RegexOptions.Singleline)]
    private static partial Regex MainContentPattern();

    [GeneratedRegex(@"\[\d+\] StaticText ""(?<text>[^""]*)""")]
    private static partial Regex StaticTextPattern();
}
