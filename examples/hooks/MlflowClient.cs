using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Examples.Hooks;

/// <summary>A run created on the tracking server (<c>runs/create</c>).</summary>
public sealed record MlflowRun(string RunId, string ExperimentId, string RunName, string ArtifactUri);

/// <summary>A run as returned by <c>runs/search</c>.</summary>
public sealed record MlflowRunInfo(string RunId, string RunName, string Status, IReadOnlyDictionary<string, string> Tags);

/// <summary>A trace as returned by the traces endpoints.</summary>
public sealed record MlflowTraceInfo(string TraceId, string ExperimentId, string State, string? ArtifactLocation, IReadOnlyDictionary<string, string> Tags);

/// <summary>One span of a logged trace, as read back from its <c>traces.json</c> artifact.</summary>
public sealed record MlflowSpanInfo(string SpanId, string? ParentSpanId, string Name, string SpanType);

/// <summary>A non-success answer from the tracking server.</summary>
public sealed class MlflowClientException(HttpStatusCode status, string body) : Exception($"MLflow server answered {(int)status}: {body}")
{
    public HttpStatusCode Status { get; } = status;

    public string Body { get; } = body;
}

/// <summary>
/// The subset of the <c>mlflow</c> Python client the two hooks use, over the MLflow tracking server's REST API
/// (there is no .NET MLflow SDK): <c>set_experiment</c> (experiments/get-by-name, experiments/create),
/// <c>start_run</c>/<c>end_run</c> (runs/create with the <c>mlflow.runName</c> and <c>mlflow.parentRunId</c> tags,
/// runs/update), <c>log_param</c>/<c>log_metric</c> (runs/log-batch), <c>log_artifact</c> (a PUT to the server's
/// proxied artifact store, <c>mlflow-artifacts:/</c>), <c>search_runs</c>, and for the tracing hook the v3 trace
/// endpoints (<c>POST api/3.0/mlflow/traces</c> for the trace info, the spans as the trace's <c>traces.json</c>
/// artifact, <c>traces/search</c>). The <see cref="HttpMessageHandler"/> is injectable so a fake server can stand in.
/// </summary>
public sealed class MlflowClient : IDisposable
{
    private const string Api = "api/2.0/mlflow";

    private const string ApiV3 = "api/3.0/mlflow";

    private const string ArtifactsApi = "api/2.0/mlflow-artifacts/artifacts";

    private const string ArtifactsScheme = "mlflow-artifacts:";

    private readonly HttpClient _http;

    public MlflowClient(string trackingUri, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackingUri);
        TrackingUri = trackingUri.TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(TrackingUri + "/");
    }

    public string TrackingUri { get; }

    /// <summary>Port of <c>mlflow.set_experiment(name)</c>: the experiment's id, creating it when absent.</summary>
    public async Task<string> GetOrCreateExperimentAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var response = await _http.GetAsync($"{Api}/experiments/get-by-name?experiment_name={Uri.EscapeDataString(name)}", cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var found = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return found["experiment"]?["experiment_id"]?.GetValue<string>() ?? throw new InvalidOperationException("experiments/get-by-name returned no experiment_id");
        }

        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            throw new MlflowClientException(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        var created = await PostAsync($"{Api}/experiments/create", new JsonObject { ["name"] = name }, cancellationToken).ConfigureAwait(false);
        return created["experiment_id"]?.GetValue<string>() ?? throw new InvalidOperationException("experiments/create returned no experiment_id");
    }

    /// <summary>Port of <c>mlflow.start_run(run_name=..., nested=..., tags=...)</c>: a run (nested under <paramref name="parentRunId"/> when given).</summary>
    public async Task<MlflowRun> CreateRunAsync(string experimentId, string runName, IReadOnlyDictionary<string, string>? tags = null, string? parentRunId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentNullException.ThrowIfNull(runName);
        var tagList = new JsonArray { Tag("mlflow.runName", runName) };
        if (parentRunId is not null)
        {
            tagList.Add(Tag("mlflow.parentRunId", parentRunId));
        }

        foreach (var (key, value) in tags ?? new Dictionary<string, string>())
        {
            tagList.Add(Tag(key, value));
        }

        var body = new JsonObject
        {
            ["experiment_id"] = experimentId,
            ["run_name"] = runName,
            ["start_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["tags"] = tagList,
        };
        var created = await PostAsync($"{Api}/runs/create", body, cancellationToken).ConfigureAwait(false);
        var info = created["run"]?["info"] ?? throw new InvalidOperationException("runs/create returned no run info");
        var runId = info["run_id"]?.GetValue<string>() ?? throw new InvalidOperationException("runs/create returned no run_id");
        return new MlflowRun(
            runId,
            info["experiment_id"]?.GetValue<string>() ?? experimentId,
            info["run_name"]?.GetValue<string>() ?? runName,
            info["artifact_uri"]?.GetValue<string>() ?? "");
    }

    /// <summary>Port of <c>mlflow.log_param</c>.</summary>
    public Task LogParamAsync(string runId, string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        var body = new JsonObject
        {
            ["run_id"] = runId,
            ["params"] = new JsonArray(new JsonObject { ["key"] = key, ["value"] = value }),
        };
        return PostAsync($"{Api}/runs/log-batch", body, cancellationToken);
    }

    /// <summary>Port of <c>mlflow.log_metric(key, value, step=...)</c>.</summary>
    public Task LogMetricAsync(string runId, string key, double value, long? step = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(key);
        var metric = new JsonObject
        {
            ["key"] = key,
            ["value"] = value,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["step"] = step ?? 0,
        };
        var body = new JsonObject { ["run_id"] = runId, ["metrics"] = new JsonArray(metric) };
        return PostAsync($"{Api}/runs/log-batch", body, cancellationToken);
    }

    /// <summary>Port of <c>mlflow.end_run(status=...)</c>: <paramref name="status"/> is <c>FINISHED</c>, <c>FAILED</c> or <c>KILLED</c>.</summary>
    public Task UpdateRunAsync(string runId, string status, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(status);
        var body = new JsonObject
        {
            ["run_id"] = runId,
            ["status"] = status,
            ["end_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        return PostAsync($"{Api}/runs/update", body, cancellationToken);
    }

    /// <summary>
    /// Port of <c>mlflow.log_artifact(path, artifact_path=...)</c> for a server that proxies its artifact store
    /// (<c>mlflow server --serve-artifacts</c>, the default; run artifact URIs then read <c>mlflow-artifacts:/...</c>).
    /// Any other artifact location is an <see cref="InvalidOperationException"/>: the client cannot write to it.
    /// </summary>
    public async Task LogArtifactAsync(MlflowRun run, string artifactPath, string fileName, byte[] contents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(contents);
        await PutArtifactAsync(ArtifactUrl(run.ArtifactUri, $"{artifactPath.Trim('/')}/{fileName}"), contents, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of <c>mlflow.search_runs(experiment_ids=[...])</c>: the experiment's runs, oldest first.</summary>
    public async Task<IReadOnlyList<MlflowRunInfo>> SearchRunsAsync(string experimentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        var body = new JsonObject
        {
            ["experiment_ids"] = new JsonArray(experimentId),
            ["max_results"] = 100,
            ["order_by"] = new JsonArray("attributes.start_time ASC"),
        };
        var result = await PostAsync($"{Api}/runs/search", body, cancellationToken).ConfigureAwait(false);
        var runs = new List<MlflowRunInfo>();
        foreach (var run in result["runs"] as JsonArray ?? [])
        {
            var info = run?["info"];
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tag in run?["data"]?["tags"] as JsonArray ?? [])
            {
                if (tag?["key"]?.GetValue<string>() is { } key)
                {
                    tags[key] = tag["value"]?.GetValue<string>() ?? "";
                }
            }

            runs.Add(new MlflowRunInfo(
                info?["run_id"]?.GetValue<string>() ?? "",
                info?["run_name"]?.GetValue<string>() ?? tags.GetValueOrDefault("mlflow.runName", "unnamed"),
                info?["status"]?.GetValue<string>() ?? "unknown",
                tags));
        }

        return runs;
    }

    /// <summary>
    /// Logs a completed trace the way the Python client's exporter does when the root span ends: the trace info to
    /// <c>POST api/3.0/mlflow/traces</c>, then the spans as the trace's <c>traces.json</c> artifact.
    /// </summary>
    public async Task<MlflowTraceInfo> LogTraceAsync(MlflowTrace trace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var created = await PostAsync($"{ApiV3}/traces", new JsonObject { ["trace"] = new JsonObject { ["trace_info"] = trace.TraceInfoJson() } }, cancellationToken).ConfigureAwait(false);
        var info = ParseTraceInfo(created["trace"]?["trace_info"] ?? created["trace_info"] ?? created) with { ExperimentId = trace.ExperimentId };
        if (string.IsNullOrEmpty(info.TraceId))
        {
            info = info with { TraceId = trace.TraceId };
        }

        var location = info.ArtifactLocation ?? DefaultTraceArtifactLocation(trace.ExperimentId, info.TraceId);
        var spans = Encoding.UTF8.GetBytes(trace.TraceDataJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        await PutArtifactAsync(ArtifactUrl(location, "traces.json"), spans, cancellationToken).ConfigureAwait(false);
        return info with { ArtifactLocation = location };
    }

    /// <summary>Port of <c>mlflow.search_traces(experiment_ids=[...])</c>.</summary>
    public async Task<IReadOnlyList<MlflowTraceInfo>> SearchTracesAsync(string experimentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        var body = new JsonObject
        {
            ["locations"] = new JsonArray(new JsonObject
            {
                ["type"] = "MLFLOW_EXPERIMENT",
                ["mlflow_experiment"] = new JsonObject { ["experiment_id"] = experimentId },
            }),
            ["max_results"] = 100,
        };
        var result = await PostAsync($"{ApiV3}/traces/search", body, cancellationToken).ConfigureAwait(false);
        var traces = new List<MlflowTraceInfo>();
        foreach (var item in result["traces"] as JsonArray ?? [])
        {
            if (item is not null)
            {
                var info = ParseTraceInfo(item["trace_info"] ?? item);
                traces.Add(string.IsNullOrEmpty(info.ExperimentId) ? info with { ExperimentId = experimentId } : info);
            }
        }

        return traces;
    }

    /// <summary>Port of the span walk of <c>mlflow.get_trace(trace_id).data.spans</c>: the spans of a logged trace, read from its <c>traces.json</c> artifact.</summary>
    public async Task<IReadOnlyList<MlflowSpanInfo>> GetTraceSpansAsync(MlflowTraceInfo trace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var location = trace.ArtifactLocation ?? DefaultTraceArtifactLocation(trace.ExperimentId, trace.TraceId);
        using var response = await _http.GetAsync(ArtifactUrl(location, "traces.json"), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new MlflowClientException(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        var data = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var spans = new List<MlflowSpanInfo>();
        foreach (var span in data["spans"] as JsonArray ?? [])
        {
            if (span is null)
            {
                continue;
            }

            var spanType = "UNKNOWN";
            if (span["attributes"]?["mlflow.spanType"]?.GetValue<string>() is { } encoded)
            {
                try
                {
                    spanType = JsonSerializer.Deserialize<string>(encoded) ?? encoded;
                }
                catch (JsonException)
                {
                    spanType = encoded;
                }
            }

            spans.Add(new MlflowSpanInfo(
                span["span_id"]?.GetValue<string>() ?? "",
                span["parent_span_id"]?.GetValue<string>(),
                span["name"]?.GetValue<string>() ?? "",
                spanType));
        }

        return spans;
    }

    /// <summary>The server URL of an artifact under an <c>mlflow-artifacts:/</c> location (or an absolute http(s) one).</summary>
    public static string ArtifactUrl(string artifactLocation, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(artifactLocation);
        ArgumentNullException.ThrowIfNull(relativePath);
        if (artifactLocation.StartsWith(ArtifactsScheme, StringComparison.Ordinal))
        {
            var path = artifactLocation[ArtifactsScheme.Length..].Trim('/');
            return $"{ArtifactsApi}/{path}/{relativePath.TrimStart('/')}";
        }

        if (artifactLocation.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || artifactLocation.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return $"{artifactLocation.TrimEnd('/')}/{relativePath.TrimStart('/')}";
        }

        throw new InvalidOperationException($"artifact location '{artifactLocation}' is not served by the tracking server (an mlflow-artifacts:/ location is required; start the server with --serve-artifacts)");
    }

    /// <summary>Where the server keeps a trace's artifacts when it does not say (<c>mlflow-artifacts:/&lt;experiment&gt;/traces/&lt;trace&gt;/artifacts</c>).</summary>
    public static string DefaultTraceArtifactLocation(string experimentId, string traceId) => $"{ArtifactsScheme}/{experimentId}/traces/{traceId}/artifacts";

    public void Dispose() => _http.Dispose();

    private static MlflowTraceInfo ParseTraceInfo(JsonNode node)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node["tags"] is JsonObject tagObject)
        {
            foreach (var (key, value) in tagObject)
            {
                tags[key] = value?.GetValue<string>() ?? "";
            }
        }
        else if (node["tags"] is JsonArray tagArray)
        {
            foreach (var tag in tagArray)
            {
                if (tag?["key"]?.GetValue<string>() is { } key)
                {
                    tags[key] = tag["value"]?.GetValue<string>() ?? "";
                }
            }
        }

        return new MlflowTraceInfo(
            node["trace_id"]?.GetValue<string>() ?? node["request_id"]?.GetValue<string>() ?? "",
            node["trace_location"]?["mlflow_experiment"]?["experiment_id"]?.GetValue<string>() ?? node["experiment_id"]?.GetValue<string>() ?? "",
            node["state"]?.GetValue<string>() ?? node["status"]?.GetValue<string>() ?? "",
            tags.GetValueOrDefault("mlflow.artifactLocation"),
            tags);
    }

    private static JsonObject Tag(string key, string value) => new() { ["key"] = key, ["value"] = value };

    private async Task<JsonObject> PostAsync(string path, JsonObject body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new MlflowClientException(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task PutArtifactAsync(string url, byte[] contents, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(contents);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.PutAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new MlflowClientException(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException($"MLflow server returned a non-object body: {text}");
    }

    /// <summary>Python's <c>str()</c> of a number for a param value.</summary>
    public static string Str(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
