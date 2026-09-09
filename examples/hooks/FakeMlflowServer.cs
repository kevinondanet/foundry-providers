using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Examples.Hooks;

/// <summary>One request the fake server answered.</summary>
public sealed record FakeMlflowRequest(string Method, string Path, string Body);

/// <summary>A metric point logged to a run.</summary>
public sealed record FakeMlflowMetric(string Key, double Value, long Step, long Timestamp);

/// <summary>A run held by the fake server.</summary>
public sealed class FakeMlflowRun
{
    public required string RunId { get; init; }

    public required string ExperimentId { get; init; }

    public required string RunName { get; init; }

    public string Status { get; set; } = "RUNNING";

    public long StartTime { get; init; }

    public long? EndTime { get; set; }

    public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);

    public OrderedDictionary<string, string> Params { get; } = new(StringComparer.Ordinal);

    public List<FakeMlflowMetric> Metrics { get; } = [];

    public string ArtifactUri => $"mlflow-artifacts:/{ExperimentId}/{RunId}/artifacts";

    /// <summary>The parent run id (<c>mlflow.parentRunId</c> tag), if nested.</summary>
    public string? ParentRunId => Tags.GetValueOrDefault("mlflow.parentRunId");

    /// <summary>The last logged value of <paramref name="key"/>, or null.</summary>
    public double? Metric(string key) => Metrics.LastOrDefault(metric => metric.Key == key)?.Value;

    /// <summary>Every value of <paramref name="key"/> in step order.</summary>
    public IReadOnlyList<FakeMlflowMetric> MetricSeries(string key) => Metrics.Where(metric => metric.Key == key).OrderBy(metric => metric.Step).ToList();
}

/// <summary>
/// An in-memory MLflow tracking server behind an <see cref="HttpMessageHandler"/>: answers the REST routes
/// <see cref="MlflowClient"/> uses (experiments, runs, log-batch, update, search, proxied artifacts, v3 traces) from
/// dictionaries, records every request in <see cref="Requests"/>, and exposes what was logged so the offline run can
/// print it and the tests can assert it. Not a general MLflow emulation: unknown routes are 404.
/// </summary>
public sealed class FakeMlflowServer : HttpMessageHandler
{
    private readonly object _sync = new();

    private readonly List<FakeMlflowRequest> _requests = [];

    private readonly OrderedDictionary<string, string> _experiments = new(StringComparer.Ordinal); // name -> id

    private readonly OrderedDictionary<string, FakeMlflowRun> _runs = new(StringComparer.Ordinal);

    private readonly Dictionary<string, byte[]> _artifacts = new(StringComparer.Ordinal);

    private readonly OrderedDictionary<string, JsonObject> _traces = new(StringComparer.Ordinal);

    private int _nextExperiment;

    private int _nextRun;

    /// <summary>Every request, in order.</summary>
    public IReadOnlyList<FakeMlflowRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>Experiment name → id.</summary>
    public IReadOnlyDictionary<string, string> Experiments
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, string>(_experiments, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Runs in creation order.</summary>
    public IReadOnlyList<FakeMlflowRun> Runs
    {
        get
        {
            lock (_sync)
            {
                return _runs.Values.ToArray();
            }
        }
    }

    /// <summary>Artifacts by their server path (<c>&lt;experiment&gt;/&lt;run&gt;/artifacts/&lt;artifact path&gt;/&lt;file&gt;</c>).</summary>
    public IReadOnlyDictionary<string, byte[]> Artifacts
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, byte[]>(_artifacts, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Trace infos in creation order.</summary>
    public IReadOnlyList<JsonObject> Traces
    {
        get
        {
            lock (_sync)
            {
                return _traces.Values.Select(trace => (JsonObject)trace.DeepClone()).ToArray();
            }
        }
    }

    /// <summary>The artifact text at <paramref name="path"/>, or null.</summary>
    public string? ArtifactText(string path)
    {
        lock (_sync)
        {
            return _artifacts.TryGetValue(path.Trim('/'), out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
        }
    }

    /// <summary>The artifact paths that start with <paramref name="prefix"/>.</summary>
    public IReadOnlyList<string> ArtifactPaths(string prefix = "")
    {
        lock (_sync)
        {
            return _artifacts.Keys.Where(key => key.StartsWith(prefix.Trim('/'), StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>The spans of a logged trace (its <c>traces.json</c>), or empty.</summary>
    public IReadOnlyList<JsonObject> TraceSpans(string traceId)
    {
        JsonObject? info;
        lock (_sync)
        {
            if (!_traces.TryGetValue(traceId, out info))
            {
                return [];
            }
        }

        var location = info["tags"]?["mlflow.artifactLocation"]?.GetValue<string>() ?? "";
        var text = ArtifactText(location["mlflow-artifacts:".Length..].Trim('/') + "/traces.json");
        return text is null ? [] : (JsonNode.Parse(text)?["spans"] as JsonArray ?? []).OfType<JsonObject>().ToList();
    }

    /// <summary>Distinct <c>METHOD path</c> lines with their counts, in order of first appearance.</summary>
    public IReadOnlyList<(string Route, int Count)> RouteCounts()
    {
        var counts = new OrderedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var request in Requests)
        {
            var route = $"{request.Method} {request.Path}";
            counts[route] = counts.GetValueOrDefault(route) + 1;
        }

        return counts.Select(pair => (pair.Key, pair.Value)).ToList();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var path = request.RequestUri?.AbsolutePath ?? "/";
        var query = request.RequestUri?.Query ?? "";
        var method = request.Method.Method;
        lock (_sync)
        {
            _requests.Add(new FakeMlflowRequest(method, path, path.Contains("mlflow-artifacts", StringComparison.Ordinal) ? $"<{body.Length} bytes>" : Encoding.UTF8.GetString(body)));
            return Handle(method, path, query, body);
        }
    }

    private HttpResponseMessage Handle(string method, string path, string query, byte[] body)
    {
        const string artifacts = "/api/2.0/mlflow-artifacts/artifacts/";
        if (path.StartsWith(artifacts, StringComparison.Ordinal))
        {
            var artifactPath = path[artifacts.Length..].Trim('/');
            switch (method)
            {
                case "PUT":
                    _artifacts[artifactPath] = body;
                    return Json(HttpStatusCode.OK, new JsonObject());
                case "GET":
                    return _artifacts.TryGetValue(artifactPath, out var bytes)
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                        : Error(HttpStatusCode.NotFound, "RESOURCE_DOES_NOT_EXIST", $"artifact {artifactPath} not found");
                default:
                    return Error(HttpStatusCode.MethodNotAllowed, "BAD_REQUEST", method);
            }
        }

        var json = body.Length == 0 ? new JsonObject() : JsonNode.Parse(body) as JsonObject ?? new JsonObject();
        switch (method, path)
        {
            case ("GET", "/api/2.0/mlflow/experiments/get-by-name"):
                {
                    var name = QueryValue(query, "experiment_name");
                    return name is not null && _experiments.TryGetValue(name, out var id)
                        ? Json(HttpStatusCode.OK, new JsonObject { ["experiment"] = Experiment(name, id) })
                        : Error(HttpStatusCode.NotFound, "RESOURCE_DOES_NOT_EXIST", $"Could not find experiment with name '{name}'");
                }

            case ("POST", "/api/2.0/mlflow/experiments/create"):
                {
                    var name = json["name"]?.GetValue<string>() ?? "";
                    if (_experiments.ContainsKey(name))
                    {
                        return Error(HttpStatusCode.BadRequest, "RESOURCE_ALREADY_EXISTS", $"Experiment '{name}' already exists.");
                    }

                    var id = (++_nextExperiment).ToString(CultureInfo.InvariantCulture);
                    _experiments[name] = id;
                    return Json(HttpStatusCode.OK, new JsonObject { ["experiment_id"] = id });
                }

            case ("POST", "/api/2.0/mlflow/runs/create"):
                {
                    var run = new FakeMlflowRun
                    {
                        RunId = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()),
                        ExperimentId = json["experiment_id"]?.GetValue<string>() ?? "0",
                        RunName = json["run_name"]?.GetValue<string>() ?? $"run-{++_nextRun}",
                        StartTime = json["start_time"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                    foreach (var tag in json["tags"] as JsonArray ?? [])
                    {
                        if (tag?["key"]?.GetValue<string>() is { } key)
                        {
                            run.Tags[key] = tag["value"]?.GetValue<string>() ?? "";
                        }
                    }

                    _runs[run.RunId] = run;
                    return Json(HttpStatusCode.OK, new JsonObject { ["run"] = RunJson(run) });
                }

            case ("POST", "/api/2.0/mlflow/runs/log-batch"):
                {
                    if (!TryRun(json, out var run, out var error))
                    {
                        return error;
                    }

                    foreach (var param in json["params"] as JsonArray ?? [])
                    {
                        if (param?["key"]?.GetValue<string>() is { } key)
                        {
                            var value = param["value"]?.GetValue<string>() ?? "";
                            if (value.Length > 6000)
                            {
                                return Error(HttpStatusCode.BadRequest, "INVALID_PARAMETER_VALUE", $"Param value '{key}' exceeds the length limit");
                            }

                            run.Params[key] = value;
                        }
                    }

                    foreach (var metric in json["metrics"] as JsonArray ?? [])
                    {
                        if (metric?["key"]?.GetValue<string>() is { } key)
                        {
                            run.Metrics.Add(new FakeMlflowMetric(
                                key,
                                metric["value"]?.GetValue<double>() ?? 0,
                                metric["step"]?.GetValue<long>() ?? 0,
                                metric["timestamp"]?.GetValue<long>() ?? 0));
                        }
                    }

                    foreach (var tag in json["tags"] as JsonArray ?? [])
                    {
                        if (tag?["key"]?.GetValue<string>() is { } key)
                        {
                            run.Tags[key] = tag["value"]?.GetValue<string>() ?? "";
                        }
                    }

                    return Json(HttpStatusCode.OK, new JsonObject());
                }

            case ("POST", "/api/2.0/mlflow/runs/update"):
                {
                    if (!TryRun(json, out var run, out var error))
                    {
                        return error;
                    }

                    run.Status = json["status"]?.GetValue<string>() ?? run.Status;
                    run.EndTime = json["end_time"]?.GetValue<long>() ?? run.EndTime;
                    return Json(HttpStatusCode.OK, new JsonObject { ["run_info"] = RunInfoJson(run) });
                }

            case ("POST", "/api/2.0/mlflow/runs/search"):
                {
                    var ids = (json["experiment_ids"] as JsonArray ?? []).Select(id => id?.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                    var runs = new JsonArray();
                    foreach (var run in _runs.Values.Where(run => ids.Count == 0 || ids.Contains(run.ExperimentId)))
                    {
                        runs.Add(RunJson(run));
                    }

                    return Json(HttpStatusCode.OK, new JsonObject { ["runs"] = runs });
                }

            case ("POST", "/api/3.0/mlflow/traces"):
                {
                    var info = json["trace"]?["trace_info"] as JsonObject ?? json["trace_info"] as JsonObject ?? new JsonObject();
                    var traceId = info["trace_id"]?.GetValue<string>() ?? "tr-" + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
                    var experimentId = info["trace_location"]?["mlflow_experiment"]?["experiment_id"]?.GetValue<string>() ?? "0";
                    var stored = (JsonObject)info.DeepClone();
                    stored["trace_id"] = traceId;
                    var tags = stored["tags"] as JsonObject ?? new JsonObject();
                    tags["mlflow.artifactLocation"] = MlflowClient.DefaultTraceArtifactLocation(experimentId, traceId);
                    stored["tags"] = tags;
                    _traces[traceId] = stored;
                    return Json(HttpStatusCode.OK, new JsonObject { ["trace"] = new JsonObject { ["trace_info"] = stored.DeepClone() } });
                }

            case ("POST", "/api/3.0/mlflow/traces/search"):
                {
                    var ids = (json["locations"] as JsonArray ?? [])
                        .Select(location => location?["mlflow_experiment"]?["experiment_id"]?.GetValue<string>())
                        .Where(id => id is not null)
                        .ToHashSet(StringComparer.Ordinal);
                    var traces = new JsonArray();
                    foreach (var trace in _traces.Values)
                    {
                        var experimentId = trace["trace_location"]?["mlflow_experiment"]?["experiment_id"]?.GetValue<string>();
                        if (ids.Count == 0 || (experimentId is not null && ids.Contains(experimentId)))
                        {
                            traces.Add(new JsonObject { ["trace_info"] = trace.DeepClone() });
                        }
                    }

                    return Json(HttpStatusCode.OK, new JsonObject { ["traces"] = traces });
                }

            default:
                if (method == "GET" && path.StartsWith("/api/3.0/mlflow/traces/", StringComparison.Ordinal))
                {
                    var traceId = path["/api/3.0/mlflow/traces/".Length..];
                    return _traces.TryGetValue(traceId, out var trace)
                        ? Json(HttpStatusCode.OK, new JsonObject { ["trace"] = new JsonObject { ["trace_info"] = trace.DeepClone() } })
                        : Error(HttpStatusCode.NotFound, "RESOURCE_DOES_NOT_EXIST", $"Trace with ID '{traceId}' not found");
                }

                return Error(HttpStatusCode.NotFound, "ENDPOINT_NOT_FOUND", $"No API found for '{method} {path}'");
        }
    }

    private bool TryRun(JsonObject json, out FakeMlflowRun run, out HttpResponseMessage error)
    {
        var runId = json["run_id"]?.GetValue<string>() ?? json["run_uuid"]?.GetValue<string>() ?? "";
        if (_runs.TryGetValue(runId, out var found))
        {
            run = found;
            error = null!;
            return true;
        }

        run = null!;
        error = Error(HttpStatusCode.NotFound, "RESOURCE_DOES_NOT_EXIST", $"Run with id={runId} not found");
        return false;
    }

    private static JsonObject Experiment(string name, string id) => new()
    {
        ["experiment_id"] = id,
        ["name"] = name,
        ["artifact_location"] = $"mlflow-artifacts:/{id}",
        ["lifecycle_stage"] = "active",
    };

    private static JsonObject RunInfoJson(FakeMlflowRun run) => new()
    {
        ["run_id"] = run.RunId,
        ["run_uuid"] = run.RunId,
        ["run_name"] = run.RunName,
        ["experiment_id"] = run.ExperimentId,
        ["status"] = run.Status,
        ["start_time"] = run.StartTime,
        ["end_time"] = run.EndTime,
        ["artifact_uri"] = run.ArtifactUri,
        ["lifecycle_stage"] = "active",
    };

    private static JsonObject RunJson(FakeMlflowRun run)
    {
        var tags = new JsonArray();
        foreach (var (key, value) in run.Tags)
        {
            tags.Add(new JsonObject { ["key"] = key, ["value"] = value });
        }

        var parameters = new JsonArray();
        foreach (var (key, value) in run.Params)
        {
            parameters.Add(new JsonObject { ["key"] = key, ["value"] = value });
        }

        var metrics = new JsonArray();
        foreach (var group in run.Metrics.GroupBy(metric => metric.Key))
        {
            var last = group.Last();
            metrics.Add(new JsonObject { ["key"] = last.Key, ["value"] = last.Value, ["timestamp"] = last.Timestamp, ["step"] = last.Step });
        }

        return new JsonObject
        {
            ["info"] = RunInfoJson(run),
            ["data"] = new JsonObject { ["tags"] = tags, ["params"] = parameters, ["metrics"] = metrics },
        };
    }

    private static string? QueryValue(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? pair : pair[..separator];
            if (key == name)
            {
                return Uri.UnescapeDataString(separator < 0 ? "" : pair[(separator + 1)..]);
            }
        }

        return null;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) => new(status)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        Json(status, new JsonObject { ["error_code"] = code, ["message"] = message });
}
