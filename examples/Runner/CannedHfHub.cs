using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Dataset;

namespace InspectAzureAI.Examples.Runner;

/// <summary>
/// The <c>--fake</c> stand-in for the Hugging Face datasets-server behind <c>hf_dataset</c>: an <see cref="HttpMessageHandler"/>
/// (the seam <see cref="HfDatasetLoader"/> takes) that answers <c>GET /splits</c> and the paged <c>GET /rows</c> for one
/// dataset split from a JSONL file of rows copied from the real server, so the offline run never touches the network
/// and the real <c>hf_dataset</c> code path (config resolution, paging, field mapping, shuffle) still runs. Shared by
/// the HF-backed examples (scorer, simpleqa); <see cref="CreateLoader"/> gives a loader over this hub with a private
/// cache directory, so an offline run never writes the user's <c>hf_datasets</c> cache.
/// </summary>
public sealed class CannedHfHub : HttpMessageHandler
{
    /// <summary>The cache root of the offline loaders: a folder under the temp directory, outside the user's Hub cache.</summary>
    public static string PrivateCacheDir { get; } = Path.Combine(Path.GetTempPath(), "inspect-examples", "hf-fake");

    public CannedHfHub(string dataset, string split, string rowsPath, string config = "default")
    {
        ArgumentException.ThrowIfNullOrEmpty(dataset);
        ArgumentException.ThrowIfNullOrEmpty(split);
        ArgumentException.ThrowIfNullOrEmpty(rowsPath);
        Dataset = dataset;
        Split = split;
        Config = config;
        Rows = LoadRows(rowsPath);
    }

    public string Dataset { get; }

    public string Split { get; }

    public string Config { get; }

    /// <summary>The canned rows, in file order.</summary>
    public IReadOnlyList<JsonObject> Rows { get; }

    /// <summary>Every request answered (method and URL), oldest first.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>An <see cref="HfDatasetLoader"/> over this hub caching under <see cref="PrivateCacheDir"/> (pair it with <c>Cached = false</c> on the request).</summary>
    public HfDatasetLoader CreateLoader() => new(handler: this, cacheDir: PrivateCacheDir);

    /// <summary>Reads a JSONL file of row objects (one per line, blank lines skipped).</summary>
    public static List<JsonObject> LoadRows(string rowsPath)
    {
        var rows = new List<JsonObject>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(rowsPath, Encoding.UTF8))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            rows.Add(JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException($"{rowsPath} line {lineNumber} is not a JSON object."));
        }

        return rows;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri ?? throw new InvalidOperationException("request without a URI");
        lock (Requests)
        {
            Requests.Add($"{request.Method} {uri}");
        }

        var query = ParseQuery(uri.Query);
        if (query.GetValueOrDefault("dataset") != Dataset)
        {
            return Task.FromResult(Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = "The dataset does not exist on the Hub." }));
        }

        switch (uri.AbsolutePath)
        {
            case "/splits":
                return Task.FromResult(Json(HttpStatusCode.OK, new JsonObject
                {
                    ["splits"] = new JsonArray(new JsonObject { ["dataset"] = Dataset, ["config"] = Config, ["split"] = Split }),
                    ["pending"] = new JsonArray(),
                    ["failed"] = new JsonArray(),
                }));
            case "/rows":
                if (query.GetValueOrDefault("config") != Config || query.GetValueOrDefault("split") != Split)
                {
                    return Task.FromResult(Json(HttpStatusCode.UnprocessableEntity, new JsonObject { ["error"] = $"Parameters 'config' and 'split' are required and must name {Config}/{Split}" }));
                }

                var offset = int.Parse(query.GetValueOrDefault("offset") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                var length = int.Parse(query.GetValueOrDefault("length") ?? HfDatasetLoader.PageLength.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
                var page = Rows.Skip(offset).Take(length).Select((row, i) => (JsonNode)new JsonObject
                {
                    ["row_idx"] = offset + i,
                    ["row"] = row.DeepClone(),
                    ["truncated_cells"] = new JsonArray(),
                }).ToArray();
                return Task.FromResult(Json(HttpStatusCode.OK, new JsonObject
                {
                    ["features"] = new JsonArray(),
                    ["rows"] = new JsonArray(page),
                    ["num_rows_total"] = Rows.Count,
                    ["num_rows_per_page"] = HfDatasetLoader.PageLength,
                    ["partial"] = false,
                }));
            default:
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? "" : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }
}
