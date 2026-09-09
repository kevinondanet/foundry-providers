using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model.Cache;

namespace InspectAzureAI.Eval.Dataset;

public static partial class Datasets
{
    /// <summary>
    /// Port of <c>dataset/_sources/hf.py</c> <c>hf_dataset</c>: a split of a Hugging Face Hub dataset, mapped to
    /// samples through <paramref name="sampleFields"/> (or <paramref name="recordToSample"/>, which wins), cached on
    /// disk after the first read, then shuffled, limited and choice-shuffled in Python's order.
    /// <para>
    /// Deviation: there is no <c>datasets</c> package in .NET, so rows come from the Hub's datasets-server REST API
    /// (<c>/splits</c> to resolve the default config, then <c>/rows</c> paginated 100 at a time) — see
    /// <see cref="HfDatasetLoader"/> for what that API cannot serve (script-based datasets, local paths, gated
    /// repos without a token, pinned revisions) and how it is cached.
    /// </para>
    /// </summary>
    /// <param name="path">The Hub repo id, e.g. <c>HuggingFaceH4/MATH-500</c>.</param>
    /// <param name="split">The split to load (<c>train</c>, <c>test</c>, ...).</param>
    /// <param name="name">The dataset config; when null the single (or <c>default</c>) config is used, as <c>load_dataset</c> does.</param>
    /// <param name="sampleFields">Field mapping; null maps records already in sample form (<c>input</c>/<c>target</c> columns).</param>
    /// <param name="recordToSample">Custom mapping returning one or more samples per record; wins over <paramref name="sampleFields"/>.</param>
    /// <param name="dataDir">Python's <c>data_dir</c>. Deviation: only part of the cache key (the REST API has no equivalent).</param>
    /// <param name="revision">Branch, tag or commit to pin. Deviation: the datasets-server always serves the default branch, so
    /// a revision only bypasses the cache (as in Python) and is recorded in the cache key; other revisions are not fetched.</param>
    /// <param name="autoId">Assign 1-based ids in record order (before any shuffle, as #4459 requires).</param>
    /// <param name="shuffle">Shuffle the records (whole record groups for a multi-sample <paramref name="recordToSample"/>).</param>
    /// <param name="seed">Seed for <paramref name="shuffle"/>.</param>
    /// <param name="shuffleChoices">Shuffle each sample's choices (Python's <c>shuffle_choices=True</c>).</param>
    /// <param name="shuffleChoicesSeed">Seed for the choice shuffle (Python's <c>shuffle_choices=&lt;int&gt;</c>); passing a seed implies <paramref name="shuffleChoices"/>.</param>
    /// <param name="limit">Keep only the first <paramref name="limit"/> records (after shuffling).</param>
    /// <param name="cached">Read from the on-disk cache when present; <c>false</c> re-fetches. Ignored when <paramref name="revision"/> is set.</param>
    /// <param name="retry">Retry 429/502/timeouts with exponential backoff (port of <c>_call_with_hf_retry</c>).</param>
    /// <param name="token">Hub token for gated/private datasets; defaults to <c>HF_TOKEN</c> or the <c>huggingface-cli login</c> token file.</param>
    /// <param name="trust">Accepted for signature parity. Deviation: the REST API never runs dataset scripts, so this has no effect.</param>
    /// <param name="handler">HTTP handler override (tests); null uses a default <see cref="HttpClient"/>.</param>
    public static IDataset Hf(
        string path,
        string split,
        string? name = null,
        FieldSpec? sampleFields = null,
        RecordToSample? recordToSample = null,
        string? dataDir = null,
        string? revision = null,
        bool autoId = false,
        bool shuffle = false,
        int? seed = null,
        bool shuffleChoices = false,
        int? shuffleChoicesSeed = null,
        int? limit = null,
        bool cached = true,
        bool retry = true,
        string? token = null,
        bool trust = false,
        HttpMessageHandler? handler = null)
    {
        using var loader = new HfDatasetLoader(handler: handler);
        return loader.LoadAsync(new HfDatasetRequest(path, split)
        {
            Name = name,
            SampleFields = sampleFields,
            RecordToSample = recordToSample,
            DataDir = dataDir,
            Revision = revision,
            AutoId = autoId,
            Shuffle = shuffle,
            Seed = seed,
            ShuffleChoices = shuffleChoices,
            ShuffleChoicesSeed = shuffleChoicesSeed,
            Limit = limit,
            Cached = cached,
            Retry = retry,
            Token = token,
            Trust = trust,
        }).GetAwaiter().GetResult();
    }
}

/// <summary>The arguments of <c>hf_dataset</c> as a record, for <see cref="HfDatasetLoader.LoadAsync"/>.</summary>
public sealed record HfDatasetRequest(string Path, string Split)
{
    public string? Name { get; init; }

    /// <summary>Python's <c>data_dir</c>: only part of the cache key here (the REST API has no equivalent).</summary>
    public string? DataDir { get; init; }

    public FieldSpec? SampleFields { get; init; }

    public RecordToSample? RecordToSample { get; init; }

    public string? Revision { get; init; }

    public bool AutoId { get; init; }

    public bool Shuffle { get; init; }

    public int? Seed { get; init; }

    public bool ShuffleChoices { get; init; }

    public int? ShuffleChoicesSeed { get; init; }

    public int? Limit { get; init; }

    public bool Cached { get; init; } = true;

    public bool Retry { get; init; } = true;

    public string? Token { get; init; }

    public bool Trust { get; init; }
}

/// <summary>
/// Port of the loading half of <c>dataset/_sources/hf.py</c> over the Hugging Face datasets-server REST API
/// (<see href="https://datasets-server.huggingface.co"/>) instead of the <c>datasets</c> package:
/// <list type="bullet">
/// <item><c>GET /splits?dataset=</c> lists configs and splits; the config is <c>name</c>, else the only config, else
/// <c>default</c>, else an error naming the available configs (as <c>load_dataset</c> reports it).</item>
/// <item><c>GET /rows?dataset=&amp;config=&amp;split=&amp;offset=&amp;length=100</c> pages through the split. A page that
/// reports <c>truncated_cells</c> is refetched with a smaller page size down to one row; a single row the server still
/// truncates is an <see cref="InvalidDataException"/>.</item>
/// </list>
/// The fetched rows are cached as <c>rows.jsonl</c> under <c>$INSPECT_CACHE_DIR/hf_datasets</c> (or the inspect_ai user
/// cache directory, <see cref="AppDirs.InspectCacheDir"/>) in a directory named <c>{safe_filename(path)}-{mm3_hash(path+name+data_dir+split+revision+"{}")}</c>,
/// the same name Python derives, so the layouts coexist (Python stores Arrow files there; this port looks only for its own <c>rows.jsonl</c>).
/// <para>
/// Deviations: the datasets-server does not serve script-based datasets (<c>trust</c> is therefore a no-op), local dataset
/// directories or non-default revisions, and gated or private repos need a token (<c>HF_TOKEN</c>, the
/// <c>huggingface-cli login</c> token file, or <see cref="HfDatasetRequest.Token"/>); parquet download is not implemented
/// because it would need a parquet reader. The retry policy is Python's (429/502/timeouts, 3 attempts — 5 under <c>CI</c> —
/// random exponential waits from 60s capped at 5 minutes) and the shuffle permutation is <see cref="MemoryDataset.Shuffle"/>'s,
/// not <c>datasets.Dataset.shuffle</c>'s.
/// </para>
/// </summary>
public sealed class HfDatasetLoader : IDisposable
{
    /// <summary>Default datasets-server endpoint (override with <c>HF_DATASETS_SERVER</c> or the constructor).</summary>
    public const string DefaultEndpoint = "https://datasets-server.huggingface.co";

    /// <summary>The environment variable naming the cache root, shared with the prompt cache.</summary>
    public const string CacheDirVar = CacheOps.CacheDirVar;

    /// <summary>Largest page the <c>/rows</c> endpoint serves.</summary>
    public const int PageLength = 100;

    /// <summary>Python's <c>_INITIAL_WAIT_SECS</c>.</summary>
    public static readonly TimeSpan InitialWait = TimeSpan.FromSeconds(60);

    /// <summary>Python's <c>_HF_RATE_LIMIT_WINDOW_SECS</c>.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    private const string RowsFileName = "rows.jsonl";

    private static readonly HashSet<int> TransientStatuses = [429, 502];

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string? _cacheDir;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Random _random;

    /// <param name="handler">HTTP handler (a fake in tests); null uses a default handler.</param>
    /// <param name="cacheDir">Cache root override; null resolves <c>$INSPECT_CACHE_DIR/hf_datasets</c> or the user cache directory.</param>
    /// <param name="endpoint">datasets-server base URL; null uses <c>HF_DATASETS_SERVER</c> or <see cref="DefaultEndpoint"/>.</param>
    /// <param name="delay">Retry sleep (tests substitute a no-op); null uses <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    /// <param name="random">Source of the retry jitter; null uses <see cref="Random.Shared"/>.</param>
    public HfDatasetLoader(HttpMessageHandler? handler = null, string? cacheDir = null, string? endpoint = null, Func<TimeSpan, CancellationToken, Task>? delay = null, Random? random = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(100);
        _endpoint = (endpoint ?? (Environment.GetEnvironmentVariable("HF_DATASETS_SERVER") is { Length: > 0 } env ? env : DefaultEndpoint)).TrimEnd('/');
        _cacheDir = cacheDir;
        _delay = delay ?? Task.Delay;
        _random = random ?? Random.Shared;
    }

    /// <summary>Requests made through this loader (method and URL), oldest first; empty on a cache hit.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Port of <c>hf_dataset</c> given resolved rows: fetch (or read the cache), map, id, shuffle, limit, shuffle choices.</summary>
    public async Task<IDataset> LoadAsync(HfDatasetRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Path);
        ArgumentException.ThrowIfNullOrEmpty(request.Split);

        var records = await FetchRecordsAsync(request, cancellationToken).ConfigureAwait(false);
        var mapper = request.RecordToSample ?? SampleRecords.Mapper(request.SampleFields ?? new FieldSpec());

        // ids track records, not shuffled positions (#4459): number the groups before permuting them
        var groups = records.Select(record => mapper(record).ToList()).ToList();
        if (request.AutoId)
        {
            var nextId = 1;
            for (var g = 0; g < groups.Count; g++)
            {
                groups[g] = groups[g].Select(sample => sample with { Id = nextId++ }).ToList();
            }
        }

        if (request.Shuffle)
        {
            var random = request.Seed is { } seed ? new Random(seed) : Random.Shared;
            for (var i = groups.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (groups[i], groups[j]) = (groups[j], groups[i]);
            }
        }

        if (request.Limit is { } limit)
        {
            groups = groups.Take(Math.Max(0, limit)).ToList();
        }

        var dataset = new MemoryDataset(groups.SelectMany(group => group), name: request.Path, location: request.Path, shuffled: request.Shuffle);
        if (request.ShuffleChoices || request.ShuffleChoicesSeed is not null)
        {
            dataset.ShuffleChoices(request.ShuffleChoicesSeed);
        }

        return dataset;
    }

    /// <summary>The cache directory for a request: <c>{root}/{safe_filename(path)}-{mm3_hash(...)}</c>, as Python names it.</summary>
    public string CacheDirectory(HfDatasetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = $"{request.Path}{request.Name ?? "None"}{request.DataDir ?? "None"}{request.Split}{request.Revision ?? "None"}{{}}";
        return Path.Combine(CacheRoot(), $"{SafeFilename(request.Path)}-{MurmurHash3.Hash(key)}");
    }

    private string CacheRoot()
    {
        if (_cacheDir is not null)
        {
            Directory.CreateDirectory(_cacheDir);
            return _cacheDir;
        }

        var env = Environment.GetEnvironmentVariable(CacheDirVar);
        if (!string.IsNullOrEmpty(env))
        {
            var root = Path.Combine(env, "hf_datasets");
            Directory.CreateDirectory(root);
            return root;
        }

        return AppDirs.InspectCacheDir("hf_datasets");
    }

    private async Task<List<JsonObject>> FetchRecordsAsync(HfDatasetRequest request, CancellationToken cancellationToken)
    {
        var directory = CacheDirectory(request);
        var rowsFile = Path.Combine(directory, RowsFileName);
        if (request.Cached && request.Revision is null && File.Exists(rowsFile))
        {
            return ReadRows(rowsFile);
        }

        var token = ResolveToken(request.Token);
        var config = await ResolveConfigAsync(request, token, cancellationToken).ConfigureAwait(false);
        var records = await ReadSplitAsync(request, config, token, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"{RowsFileName}.{Guid.NewGuid():N}.tmp");
        await using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
        {
            foreach (var record in records)
            {
                await writer.WriteLineAsync(record.ToJsonString()).ConfigureAwait(false);
            }
        }

        File.Move(temp, rowsFile, overwrite: true);
        return records;
    }

    private static List<JsonObject> ReadRows(string rowsFile)
    {
        var records = new List<JsonObject>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(rowsFile, Encoding.UTF8))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            records.Add(JsonNode.Parse(line) as JsonObject ?? throw new InvalidDataException($"{rowsFile} line {lineNumber} is not a JSON object."));
        }

        return records;
    }

    /// <summary>
    /// Resolves the config to read: <c>name</c>, else the only config, else one literally named <c>default</c>, else the
    /// "Config name is missing" error <c>load_dataset</c> raises. Deviation: <c>load_dataset</c> picks the config flagged
    /// <c>default: true</c> in the README YAML (whatever its name); the <c>/splits</c> response carries no such flag, so a
    /// multi-config repo whose flagged default is not named <c>default</c> needs an explicit <see cref="HfDatasetRequest.Name"/>.
    /// </summary>
    private async Task<string> ResolveConfigAsync(HfDatasetRequest request, string? token, CancellationToken cancellationToken)
    {
        var url = $"{_endpoint}/splits?dataset={Uri.EscapeDataString(request.Path)}";
        var body = await GetJsonAsync(url, token, request.Retry, cancellationToken).ConfigureAwait(false);
        var splits = (body["splits"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(item => (Config: item["config"]?.GetValue<string>() ?? "", Split: item["split"]?.GetValue<string>() ?? ""))
            .ToList();
        var configs = splits.Select(s => s.Config).Distinct(StringComparer.Ordinal).ToList();

        string config;
        if (request.Name is { } name)
        {
            config = name;
            if (configs.Count > 0 && !configs.Contains(config, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"BuilderConfig '{name}' not found. Available: [{string.Join(", ", configs.Select(c => $"'{c}'"))}]");
            }
        }
        else if (configs.Count == 1)
        {
            config = configs[0];
        }
        else if (configs.Contains("default", StringComparer.Ordinal))
        {
            config = "default";
        }
        else if (configs.Count == 0)
        {
            throw new InvalidDataException($"Dataset '{request.Path}' has no configs listed by the datasets-server (it may still be processing, be script-based, or not exist).");
        }
        else
        {
            throw new InvalidDataException($"Config name is missing.\nPlease pick one among the available configs: [{string.Join(", ", configs.Select(c => $"'{c}'"))}]\nExample of usage:\n\t`Datasets.Hf(\"{request.Path}\", \"{request.Split}\", name: \"{configs[0]}\")`");
        }

        var available = splits.Where(s => s.Config == config).Select(s => s.Split).ToList();
        if (available.Count > 0 && !available.Contains(request.Split, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Unknown split \"{request.Split}\". Should be one of [{string.Join(", ", available.Select(s => $"'{s}'"))}].");
        }

        return config;
    }

    private async Task<List<JsonObject>> ReadSplitAsync(HfDatasetRequest request, string config, string? token, CancellationToken cancellationToken)
    {
        var records = new List<JsonObject>();
        var offset = 0;
        var length = PageLength;
        long? total = null;
        while (total is null || offset < total)
        {
            var url = $"{_endpoint}/rows?dataset={Uri.EscapeDataString(request.Path)}&config={Uri.EscapeDataString(config)}&split={Uri.EscapeDataString(request.Split)}&offset={offset}&length={length}";
            var body = await GetJsonAsync(url, token, request.Retry, cancellationToken).ConfigureAwait(false);
            var rows = (body["rows"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            total ??= body["num_rows_total"]?.GetValue<long>() ?? rows.Count;

            if (rows.Any(row => row["truncated_cells"] is JsonArray { Count: > 0 }))
            {
                if (length > 1)
                {
                    length = Math.Max(1, length / 2);
                    continue;
                }

                var truncated = rows.First(row => row["truncated_cells"] is JsonArray { Count: > 0 });
                throw new InvalidDataException(
                    $"Row {truncated["row_idx"]?.ToJsonString() ?? offset.ToString(CultureInfo.InvariantCulture)} of {request.Path}/{config}/{request.Split} exceeds the datasets-server row size limit " +
                    $"(truncated cells: {truncated["truncated_cells"]!.ToJsonString()}); this dataset cannot be read through the REST API.");
            }

            foreach (var row in rows)
            {
                records.Add(row["row"]?.DeepClone() as JsonObject ?? throw new InvalidDataException($"Row {records.Count} of {request.Path} is not a JSON object."));
            }

            if (rows.Count == 0)
            {
                break;
            }

            offset += rows.Count;
        }

        return records;
    }

    private async Task<JsonObject> GetJsonAsync(string url, string? token, bool retry, CancellationToken cancellationToken)
    {
        var maxTries = retry ? MaxTries() : 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await GetJsonOnceAsync(url, token, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxTries && ShouldRetry(ex))
            {
                // tenacity wait_random_exponential(multiplier=60, max=300)
                var upper = Math.Min(MaxWait.TotalSeconds, InitialWait.TotalSeconds * Math.Pow(2, attempt - 1));
                var wait = TimeSpan.FromSeconds(_random.NextDouble() * upper);
                await _delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<JsonObject> GetJsonOnceAsync(string url, string? token, CancellationToken cancellationToken)
    {
        Requests.Add($"GET {url}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TryReadError(body) ?? body;
            throw new HfHubHttpException((int)response.StatusCode, $"Hugging Face datasets-server returned {(int)response.StatusCode} for {url}: {detail}");
        }

        try
        {
            return JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException($"Hugging Face datasets-server returned a response that is not a JSON object for {url}.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Hugging Face datasets-server returned invalid JSON for {url}: {ex.Message}", ex);
        }
    }

    private static string? TryReadError(string body)
    {
        try
        {
            return (JsonNode.Parse(body) as JsonObject)?["error"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Port of <c>_should_retry_hf_error</c>: 429/502, transport failures and timeouts are transient.</summary>
    private static bool ShouldRetry(Exception ex) => ex switch
    {
        HfHubHttpException http => TransientStatuses.Contains(http.Status),
        HttpRequestException => true,
        // HttpClient reports its timeout as a TaskCanceledException wrapping a TimeoutException (with the token cancelled),
        // so a timeout is matched by shape; a plain cancellation whose token was not requested by the caller is also a timeout.
        TaskCanceledException { InnerException: TimeoutException } => true,
        TaskCanceledException { CancellationToken.IsCancellationRequested: false } => true,
        _ => false,
    };

    /// <summary>Port of <c>_hf_max_tries</c>: 5 attempts when <c>CI</c> is set, else 3.</summary>
    private static int MaxTries() => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ? 3 : 5;

    /// <summary>
    /// The token to send: the explicit one, then <c>HF_TOKEN</c>, then the file <c>huggingface-cli login</c> writes
    /// (<c>$HF_TOKEN_PATH</c>, <c>$HF_HOME/token</c> or <c>~/.cache/huggingface/token</c>), as <c>huggingface_hub</c> resolves it.
    /// </summary>
    public static string? ResolveToken(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token.Trim();
        }

        var env = Environment.GetEnvironmentVariable("HF_TOKEN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        var tokenPath = Environment.GetEnvironmentVariable("HF_TOKEN_PATH");
        if (string.IsNullOrWhiteSpace(tokenPath))
        {
            var home = Environment.GetEnvironmentVariable("HF_HOME");
            if (string.IsNullOrWhiteSpace(home))
            {
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface");
            }

            tokenPath = Path.Combine(home, "token");
        }

        try
        {
            if (File.Exists(tokenPath))
            {
                var text = File.ReadAllText(tokenPath).Trim();
                return text.Length > 0 ? text : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unreadable token file is the same as none
        }

        return null;
    }

    /// <summary>Port of <c>_util/file.py</c> <c>safe_filename</c>: ASCII letters, digits, <c>.-_</c> only, runs of underscores collapsed.</summary>
    public static string SafeFilename(string text, int maxLength = 255)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (ch > 127)
            {
                continue; // encode("ASCII", "ignore")
            }

            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        }

        var safe = Regex.Replace(builder.ToString(), "_+", "_").Trim('.', '_');
        if (safe.Length == 0)
        {
            safe = "untitled";
        }

        if (safe.Length > maxLength)
        {
            var dot = safe.LastIndexOf('.');
            if (dot > 0 && safe.Length - dot <= 10)
            {
                var extension = safe[dot..];
                safe = safe[..(maxLength - extension.Length)] + extension;
            }
            else
            {
                safe = safe[..maxLength];
            }
        }

        return safe;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Port of <c>huggingface_hub.errors.HfHubHTTPError</c>: a non-success status from the Hub, carrying the status code.</summary>
public sealed class HfHubHttpException(int statusCode, string message) : HttpRequestException(message, null, (HttpStatusCode)statusCode)
{
    /// <summary>The HTTP status code.</summary>
    public int Status { get; } = statusCode;
}
