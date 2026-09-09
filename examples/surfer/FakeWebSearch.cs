using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.Surfer;

/// <summary>
/// The <c>--fake</c> stand-in for the web: the real <c>web_search()</c> tool over the Tavily provider, whose HTTP
/// client is given a handler that answers every search with a canned <c>TavilySearchResponse</c> (last night's NHL
/// scores). The provider insists on <c>TAVILY_API_KEY</c> being set, so a placeholder is put in the environment
/// when it is missing.
/// </summary>
public static class FakeWebSearch
{
    /// <summary>The placeholder key (only set when <c>TAVILY_API_KEY</c> is absent).</summary>
    public const string PlaceholderKey = "fake-tavily-key";

    /// <summary>The canned answer.</summary>
    public const string Answer =
        "Last night's NHL results: Boston Bruins 4, Toronto Maple Leafs 2; Edmonton Oilers 3, Calgary Flames 1; New York Rangers 2, Pittsburgh Penguins 1 (OT).";

    /// <summary>The canned search results (title, url, content).</summary>
    public static readonly IReadOnlyList<(string Title, string Url, string Content)> Results =
    [
        ("NHL scores: Bruins top Maple Leafs 4-2", "https://example.com/nhl/bruins-leafs", "Boston beat Toronto 4-2 at TD Garden behind two goals from David Pastrnak."),
        ("Oilers beat Flames 3-1 in Battle of Alberta", "https://example.com/nhl/oilers-flames", "Edmonton downed Calgary 3-1; Connor McDavid had a goal and an assist."),
        ("Rangers edge Penguins 2-1 in overtime", "https://example.com/nhl/rangers-penguins", "New York beat Pittsburgh 2-1 in overtime on a goal by Artemi Panarin."),
    ];

    /// <summary>The <c>web_search("tavily")</c> tool over a <see cref="CannedTavilyHandler"/>.</summary>
    public static ToolDef Tool(CannedTavilyHandler? handler = null)
    {
        FakeSecrets.EnsurePlaceholder("TAVILY_API_KEY", PlaceholderKey);
        return BuiltinTools.WebSearch(["tavily"], handler ?? new CannedTavilyHandler());
    }

    /// <summary>The JSON body the handler answers with, for <paramref name="query"/>.</summary>
    public static JsonObject Response(string query) => new()
    {
        ["query"] = query,
        ["answer"] = Answer,
        ["images"] = new JsonArray(),
        ["results"] = new JsonArray(Results.Select(result => (JsonNode?)new JsonObject
        {
            ["title"] = result.Title,
            ["url"] = result.Url,
            ["content"] = result.Content,
            ["score"] = 0.9,
        }).ToArray()),
        ["response_time"] = 0.12,
    };

    /// <summary>An <see cref="HttpMessageHandler"/> that records each query and answers with <see cref="Response"/>.</summary>
    public sealed class CannedTavilyHandler : HttpMessageHandler
    {
        private readonly List<string> _queries = [];

        /// <summary>The queries searched so far.</summary>
        public IReadOnlyList<string> Queries => _queries;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var query = JsonNode.Parse(body)?["query"]?.GetValue<string>() ?? "";
            lock (_queries)
            {
                _queries.Add(query);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Response(query).ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
