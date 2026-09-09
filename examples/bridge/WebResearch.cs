using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Bridge;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// What the three <c>examples/bridge/*</c> ports share: the <c>dataset.json</c> the three Python examples carry
/// verbatim, the system prompt they all use, and the <c>web_search</c> tool that stands in for LangChain's
/// <c>TavilySearch</c>, the Agents SDK's <c>WebSearchTool</c> and Pydantic AI's <c>WebSearch</c> capability. Live
/// runs use the engine's <c>web_search</c> tool over an external provider (Tavily by default, <c>-T provider=exa</c>
/// to switch); <c>--fake</c> runs use <see cref="CannedWebSearch"/> so no search key is needed.
/// </summary>
public static class WebResearch
{
    /// <summary>The <c>system_prompt</c> / <c>instructions</c> of the three Python agents, verbatim.</summary>
    public const string Instructions = "You help users find information by searching the web.";

    /// <summary>The <c>-T</c> key naming the external search provider of a live run.</summary>
    public const string ProviderArg = "provider";

    /// <summary>The external provider a live run searches with unless <c>-T provider=...</c> says otherwise.</summary>
    public const string DefaultProvider = "tavily";

    /// <summary>Port of <c>json_dataset("dataset.json")</c>: the three research questions and their ideal answers.</summary>
    public static IDataset Dataset(string path) => Datasets.Json(path);

    /// <summary>The <c>web_search</c> tool for a run: canned under <c>--fake</c>, the engine's tool over an external provider otherwise.</summary>
    public static ToolDef SearchTool(ExampleContext ctx, int? maxResults = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.Fake ? CannedWebSearch.Tool() : LiveSearchTool(ctx.TaskArg(ProviderArg, DefaultProvider)!, maxResults);
    }

    /// <summary>
    /// The engine's <c>web_search</c> over <paramref name="provider"/> ("tavily", "exa"; "perplexity" for the .NET
    /// HTTP fallback), with the provider's <c>max_results</c> option when <paramref name="maxResults"/> is given
    /// (LangChain's <c>TavilySearch(max_results=...)</c>).
    /// </summary>
    public static ToolDef LiveSearchTool(string provider, int? maxResults = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        return maxResults is { } max
            ? BuiltinTools.WebSearch(new WebSearchProviders { [provider] = new JsonObject { ["max_results"] = max } })
            : BuiltinTools.WebSearch(provider);
    }
}

/// <summary>
/// A <c>web_search</c> tool with the engine tool's name, description and parameters that answers from a small
/// table of canned results (one per question of <c>dataset.json</c>) instead of calling a search API: what the
/// bridge examples search with under <c>--fake</c>. An unknown query gets the engine's own "no results" answer.
/// </summary>
public static class CannedWebSearch
{
    /// <summary>Canned answers keyed on a word of the question; the text carries the facts of the dataset's ideal answers.</summary>
    public static readonly IReadOnlyList<(string Keyword, string Answer)> Results =
    [
        ("pickleball",
            "Tennis and pickleball are similar racket sports with several differences. A pickleball court is about half the size of a tennis court. "
            + "Pickleball is played with a perforated plastic ball that resembles a whiffle ball, and with solid paddles rather than strung rackets. "
            + "The scoring is different too: games are played to 11 points and a point can only be scored by the serving side."),
        ("mercury",
            "Fish with the lowest mercury levels include salmon, flounder, Atlantic mackerel, anchovies, pollock, catfish, sardines and trout, "
            + "as well as shellfish such as clams, scallops, shrimp and mussels. Large predatory fish (shark, swordfish, king mackerel, bigeye tuna) have the highest levels."),
        ("Game of Thrones",
            "Game of Thrones season 6 (2016) has ten episodes, in broadcast order: The Red Woman, Home, Oathbreaker, Book of the Stranger, The Door, "
            + "Blood of My Blood, The Broken Man, No One, Battle of the Bastards, The Winds of Winter."),
    ];

    /// <summary>The canned answer for <paramref name="query"/>, or null when no keyword matches.</summary>
    public static string? Answer(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        foreach (var (keyword, answer) in Results)
        {
            if (query.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return answer;
            }
        }

        return null;
    }

    /// <summary>The tool: <c>web_search(query)</c> with the engine's description and schema.</summary>
    public static ToolDef Tool()
    {
        var parameters = new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["query"] = ToolParam.Of("string", "Search query.") },
            Required = ["query"],
        };
        return new ToolDef("web_search", BuiltinTools.WebSearchDescription, parameters, (arguments, _) =>
        {
            var query = arguments["query"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
            return Task.FromResult<ToolResult>(Answer(query) ?? BuiltinTools.WebSearchNoResults);
        });
    }
}

/// <summary>
/// The model behind <c>--fake</c> for the bridge examples: a <see cref="ScriptedModelApi"/> whose every turn is
/// computed from the conversation, so the three samples (which run concurrently) each get the same script
/// whatever the scheduling order. As the research agent it searches once (<c>web_search</c> with the question as
/// the query) and then answers from the canned result; as the <c>model_graded_fact</c> grader (recognised by the
/// grading template's <c>[BEGIN DATA]</c> block) it grades every submission <c>C</c>.
/// </summary>
public static class FakeResearchModel
{
    public const string ModelName = "bridge-scripted";

    /// <summary>More turns than the three samples need (a search, an answer and a grade each).</summary>
    private const int TurnBudget = 64;

    /// <summary>The grader's reply: the reasoning the template asks for, then the verdict.</summary>
    public const string GradeReply = "The submission states the facts of the expert answer in its own words.\n\nGRADE: C";

    /// <summary>
    /// A fake model whose final answer for a question is <paramref name="answer"/>(question) (the canned search
    /// result by default; the Pydantic AI port wraps it in the typed output's JSON).
    /// </summary>
    public static Model Create(Func<string, string>? answer = null)
    {
        var respond = answer ?? (question => CannedWebSearch.Answer(question) ?? "I could not find an answer.");
        return new Model(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.From((messages, tools) => Respond(messages, tools, respond)), TurnBudget), ModelName));
    }

    /// <summary>Whether <paramref name="messages"/> is a <c>model_graded_fact</c> grading prompt rather than the agent's conversation.</summary>
    public static bool IsGradingPrompt(IReadOnlyList<ChatMessage> messages) =>
        messages.Count > 0 && messages[^1] is ChatMessageUser user && user.Text.Contains("[BEGIN DATA]", StringComparison.Ordinal);

    private static ModelOutput Respond(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools, Func<string, string> answer)
    {
        if (IsGradingPrompt(messages))
        {
            return ScriptedTurn.Text(GradeReply).Output!;
        }

        var question = messages.OfType<ChatMessageUser>().FirstOrDefault()?.Text ?? "";
        var searched = messages.OfType<ChatMessageTool>().Any(message => message.Function == "web_search");
        if (!searched && tools.Any(tool => tool.Name == "web_search"))
        {
            return ScriptedTurn.ToolCall("web_search", new { query = question }).Output!;
        }

        return ScriptedTurn.Text(answer(question)).Output!;
    }
}
