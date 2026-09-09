using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.BiologyQa;

/// <summary>
/// The Tavily service behind <c>--fake</c> for <c>examples/biology_qa.py</c>: an <see cref="HttpMessageHandler"/>
/// handed to <c>BuiltinTools.WebSearch(providers, handler)</c> that answers every
/// <c>POST https://api.tavily.com/search</c> with a canned response in Tavily's shape, whose <c>answer</c> is the
/// bundled dataset's target for the queried question (a query that is not a dataset question gets no answer) and
/// whose single result cites <c>https://example.com/biology/&lt;question id&gt;</c>. The real
/// <c>TavilySearchProvider</c> still builds the request, so the bearer header, the body and the citation parsing
/// are exercised offline. Every query is recorded in <see cref="Queries"/>.
/// </summary>
internal sealed class FakeTavilyHandler : HttpMessageHandler
{
    /// <summary>The value <see cref="EnsureApiKey"/> gives <c>TAVILY_API_KEY</c> when it is not set.</summary>
    public const string ApiKeyPlaceholder = "fake-tavily-key";

    private readonly object _sync = new();
    private readonly List<string> _queries = [];

    /// <summary>Every query received so far, in order.</summary>
    public IReadOnlyList<string> Queries
    {
        get
        {
            lock (_sync)
            {
                return _queries.ToArray();
            }
        }
    }

    /// <summary>
    /// The Tavily provider reads <c>TAVILY_API_KEY</c> from the environment when it is first used and fails without
    /// one, so the fake run sets a placeholder for this process when the variable is absent (an existing value is
    /// kept: the fake transport never sends it anywhere).
    /// </summary>
    public static void EnsureApiKey()
    {
        FakeSecrets.EnsurePlaceholder(TavilySearchProvider.EnvKey, ApiKeyPlaceholder);
    }

    /// <summary>The canned response body for <paramref name="query"/>, in Tavily's shape.</summary>
    public static JsonObject ResponseFor(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entry = BiologyQaAnswers.Find(query);
        var results = new JsonArray();
        if (entry is { } found)
        {
            results.Add(new JsonObject
            {
                ["title"] = $"Biology trivia: {found.Question}",
                ["url"] = $"https://example.com/biology/{found.Id}",
                ["content"] = $"{found.Question} {found.Answer}.",
                ["score"] = 0.98,
                ["raw_content"] = null,
            });
        }

        return new JsonObject
        {
            ["query"] = query,
            ["answer"] = entry is { } hit ? hit.Answer : null,
            ["follow_up_questions"] = null,
            ["images"] = new JsonArray(),
            ["results"] = results,
            ["response_time"] = 0.01,
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var query = body.Length > 0 ? JsonNode.Parse(body)?["query"]?.GetValue<string>() ?? "" : "";
        lock (_sync)
        {
            _queries.Add(query);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ResponseFor(query).ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>The bundled <c>biology_qa</c> dataset as a lookup from question to answer, shared by the scripted search service and the scripted model.</summary>
internal static class BiologyQaAnswers
{
    private static readonly Lazy<IReadOnlyList<(string Id, string Question, string Answer)>> Entries = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The 20 questions with their ids and answers, in dataset order.</summary>
    public static IReadOnlyList<(string Id, string Question, string Answer)> All => Entries.Value;

    /// <summary>The entry whose question is <paramref name="question"/> (trimmed, case-insensitive), or null.</summary>
    public static (string Id, string Question, string Answer)? Find(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var wanted = question.Trim();
        foreach (var entry in Entries.Value)
        {
            if (string.Equals(entry.Question, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static IReadOnlyList<(string Id, string Question, string Answer)> Load()
    {
        var dataset = Datasets.Example("biology_qa", new FieldSpec(Input: "question", Target: "answer"));
        var entries = new List<(string, string, string)>(dataset.Count);
        foreach (var sample in dataset)
        {
            entries.Add((sample.Id?.ToString() ?? "", sample.Input.ToString(), sample.Target.Text));
        }

        return entries;
    }
}
