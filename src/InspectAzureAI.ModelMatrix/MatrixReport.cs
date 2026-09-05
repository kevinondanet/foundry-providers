using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Log;

namespace InspectAzureAI.ModelMatrix;

/// <summary>One (sample, epoch) of a deployment's eval.</summary>
internal sealed record MatrixSampleResult(object Id, int Epoch, string? Score, int Tokens, double Seconds, string? Error, string? Limit);

/// <summary>One deployment's row in the matrix.</summary>
internal sealed record MatrixRow
{
    public const string Ok = "ok";

    public const string Partial = "partial";

    public const string Incorrect = "incorrect";

    public const string Unscored = "unscored";

    public const string Error = "error";

    public const string Skipped = "skipped";

    public required string Deployment { get; init; }

    public required string Model { get; init; }

    public required string Format { get; init; }

    public required string Route { get; init; }

    /// <summary><see cref="Ok"/>, <see cref="Partial"/>, <see cref="Incorrect"/>, <see cref="Unscored"/>, <see cref="Error"/> or <see cref="Skipped"/>.</summary>
    public required string Status { get; init; }

    public string? SkipReason { get; init; }

    /// <summary>The first scorer's <c>accuracy</c> metric, when the eval produced one.</summary>
    public double? Accuracy { get; init; }

    public int Tokens { get; init; }

    /// <summary>Wall-clock seconds for the deployment's eval, including sandbox setup.</summary>
    public double Seconds { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>The eval log the row was read from.</summary>
    public string? Log { get; init; }

    public IReadOnlyList<MatrixSampleResult> Samples { get; init; } = [];

    public bool IsError => Status == Error;

    public static MatrixRow SkippedRow(SelectedDeployment entry) => new()
    {
        Deployment = entry.Deployment.Name,
        Model = entry.Deployment.Model,
        Format = entry.Deployment.Format,
        Route = entry.Route,
        Status = Skipped,
        SkipReason = entry.SkipReason,
    };

    public static MatrixRow Failed(SelectedDeployment entry, string error, double seconds) => new()
    {
        Deployment = entry.Deployment.Name,
        Model = entry.Deployment.Model,
        Format = entry.Deployment.Format,
        Route = entry.Route,
        Status = Error,
        ErrorMessage = error,
        Seconds = seconds,
    };

    public static MatrixRow FromLog(SelectedDeployment entry, EvalLog log, double seconds)
    {
        var samples = (log.Samples ?? []).Select(sample => new MatrixSampleResult(
            sample.Id,
            sample.Epoch,
            sample.Scores is { Count: > 0 } ? string.Join(",", sample.Scores.Select(p => $"{p.Key}={p.Value.Text}")) : null,
            sample.ModelUsage.Values.Sum(u => u.TotalTokens),
            sample.TotalTime ?? 0,
            sample.Error?.Message,
            sample.Limit?.Type)).ToList();

        double? accuracy = null;
        var score = log.Results?.Scores.FirstOrDefault();
        if (score is not null && score.Metrics.TryGetValue("accuracy", out var metric) && !double.IsNaN(metric.Value))
        {
            accuracy = metric.Value;
        }

        var error = log.Error?.Message ?? samples.FirstOrDefault(s => s.Error is not null)?.Error;
        var status = log.Status == EvalStatus.Error || error is not null ? Error
            : accuracy is null ? Unscored
            : accuracy >= 1 ? Ok
            : accuracy > 0 ? Partial
            : Incorrect;

        return new MatrixRow
        {
            Deployment = entry.Deployment.Name,
            Model = entry.Deployment.Model,
            Format = entry.Deployment.Format,
            Route = entry.Route,
            Status = status,
            Accuracy = accuracy,
            Tokens = samples.Sum(s => s.Tokens),
            Seconds = seconds,
            ErrorMessage = error,
            Log = log.Location,
            Samples = samples,
        };
    }

    /// <summary>The note column: the skip reason, the first line of the error, a hit sample limit, or nothing.</summary>
    public string Note => SkipReason ?? FirstLine(ErrorMessage) ?? LimitNote ?? "";

    private string? LimitNote
    {
        get
        {
            var limits = Samples.Where(s => s.Limit is not null).Select(s => s.Limit!).Distinct().ToList();
            return limits.Count == 0 ? null : $"limit={string.Join(",", limits)}";
        }
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 72 ? line[..71] + "…" : line;
    }
}

internal sealed record MatrixResource(string Name, string ResourceGroup, string Location, string Kind);

internal sealed record MatrixSummary(int Deployments, int Ran, int Ok, int Partial, int Incorrect, int Unscored, int Errored, int Skipped, int Tokens, double Seconds);

/// <summary>The whole run: what was asked, what the resource holds, one row per deployment.</summary>
internal sealed record MatrixReport
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    public MatrixResource? Resource { get; init; }

    public required string Endpoint { get; init; }

    public required string Task { get; init; }

    public required string Scorer { get; init; }

    public required string Agent { get; init; }

    public required string Sandbox { get; init; }

    public required IReadOnlyDictionary<string, object?> Config { get; init; }

    public required IReadOnlyList<MatrixRow> Rows { get; init; }

    public MatrixSummary Summary => new(
        Rows.Count,
        Rows.Count(r => r.Status != MatrixRow.Skipped),
        Rows.Count(r => r.Status == MatrixRow.Ok),
        Rows.Count(r => r.Status == MatrixRow.Partial),
        Rows.Count(r => r.Status == MatrixRow.Incorrect),
        Rows.Count(r => r.Status == MatrixRow.Unscored),
        Rows.Count(r => r.IsError),
        Rows.Count(r => r.Status == MatrixRow.Skipped),
        Rows.Sum(r => r.Tokens),
        Rows.Sum(r => r.Seconds));

    public bool HasErrors => Rows.Any(r => r.IsError);

    /// <summary>
    /// This run's rows on top of <paramref name="previous"/>: a deployment run this time replaces its old row, every
    /// other old row is kept. Everything else (resource, config, time) describes this run.
    /// </summary>
    public MatrixReport MergedInto(MatrixReport previous)
    {
        var current = Rows.ToDictionary(r => r.Deployment, StringComparer.OrdinalIgnoreCase);
        var kept = previous.Rows.Where(r => !current.ContainsKey(r.Deployment));
        return this with { Rows = kept.Concat(Rows).OrderBy(r => r.Deployment, StringComparer.OrdinalIgnoreCase).ToList() };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static MatrixReport FromJson(string json) =>
        JsonSerializer.Deserialize<MatrixReport>(json, JsonOptions) ?? throw new JsonException("empty matrix report");

    private static readonly string[] Headers = ["deployment", "format", "route", "status", "accuracy", "tokens", "time", "note"];

    private IEnumerable<string[]> Cells() => Rows.Select(r => new[]
    {
        r.Deployment,
        r.Format,
        r.Route,
        r.Status,
        r.Accuracy is { } a ? a.ToString("0.000", CultureInfo.InvariantCulture) : "-",
        r.Status == MatrixRow.Skipped ? "-" : r.Tokens.ToString(CultureInfo.InvariantCulture),
        r.Status == MatrixRow.Skipped ? "-" : r.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s",
        r.Note,
    });

    /// <summary>The console table: fixed columns sized to their content.</summary>
    public string RenderTable()
    {
        var rows = Cells().ToList();
        var widths = Headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        var text = new StringBuilder();
        text.AppendLine(Line(Headers, widths));
        foreach (var row in rows)
        {
            text.AppendLine(Line(row, widths));
        }

        var s = Summary;
        text.AppendLine();
        text.AppendLine($"{s.Ran} run ({s.Ok} ok, {s.Partial} partial, {s.Incorrect} incorrect, {s.Unscored} unscored, {s.Errored} errored), {s.Skipped} skipped; {s.Tokens} tokens");
        return text.ToString();

        static string Line(string[] cells, int[] widths) =>
            string.Join("  ", cells.Select((c, i) => i is 4 or 5 or 6 ? c.PadLeft(widths[i]) : c.PadRight(widths[i]))).TrimEnd();
    }

    /// <summary>The same table as GitHub-flavoured Markdown, preceded by the run's context.</summary>
    public string RenderMarkdown()
    {
        var text = new StringBuilder();
        text.AppendLine($"# {Task} × {Agent}");
        text.AppendLine();
        text.AppendLine($"{(Resource is null ? Endpoint : $"`{Resource.Name}` ({Resource.ResourceGroup}, {Resource.Location})")} · {CapturedAt:yyyy-MM-dd HH:mm} UTC · sandbox {Sandbox}");
        text.AppendLine();
        text.AppendLine("| " + string.Join(" | ", Headers) + " |");
        text.AppendLine("|" + string.Join("|", Headers.Select((_, i) => i is 4 or 5 or 6 ? "---:" : "---")) + "|");
        foreach (var row in Cells())
        {
            text.AppendLine("| " + string.Join(" | ", row.Select(c => c.Replace("|", "\\|"))) + " |");
        }

        var s = Summary;
        text.AppendLine();
        text.AppendLine($"{s.Ran} run ({s.Ok} ok, {s.Partial} partial, {s.Incorrect} incorrect, {s.Unscored} unscored, {s.Errored} errored), {s.Skipped} skipped; {s.Tokens} tokens.");
        return text.ToString();
    }
}
