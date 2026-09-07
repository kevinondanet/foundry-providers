using System.Globalization;
using System.Text;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Log;

namespace InspectAzureAI.SweShowcase;

/// <summary>One <c>--hooks</c> entry: a built-in hook name, optionally with a destination file (<c>sample-log=hooks.log</c>).</summary>
internal sealed record HookChoice(string Name, string? Path)
{
    public static readonly IReadOnlyList<string> Names = [SampleLoggingHooks.Name];

    public static HookChoice Parse(string spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var eq = spec.IndexOf('=', StringComparison.Ordinal);
        var name = (eq < 0 ? spec : spec[..eq]).Trim().ToLowerInvariant();
        var path = eq < 0 ? null : spec[(eq + 1)..].Trim();
        if (!Names.Contains(name, StringComparer.Ordinal))
        {
            throw new UsageError($"--hooks expects {string.Join("|", Names)}[=file], got '{spec}'");
        }

        if (path is not null && path.Length == 0)
        {
            throw new UsageError($"--hooks {name}= expects a file path");
        }

        return new HookChoice(name, path);
    }

    /// <summary>Creates the hook of a single run; a file destination is opened here (truncated) and closed when the hook is disposed.</summary>
    public Hooks Create(TextWriter console)
    {
        ArgumentNullException.ThrowIfNull(console);
        return Path is null ? new SampleLoggingHooks(console) : new SampleLoggingHooks(HookFiles.OpenFile(Path), ownsWriter: true);
    }

    /// <summary>
    /// Creates the hook over a writer the caller owns: the console, or the writer a <see cref="HookFiles"/> shared by
    /// several runs opened once for <see cref="Path"/>. The lines the caller prefixes (the matrix's deployment name)
    /// reach the destination as they are, and runs in flight together never truncate each other's file.
    /// </summary>
    public Hooks CreateOn(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return new SampleLoggingHooks(writer);
    }

    public override string ToString() => Path is null ? Name : $"{Name}={Path}";
}

/// <summary>
/// The files of the <c>--hooks name=FILE</c> destinations of runs that share a process: each path is opened once
/// (truncated) on first use and shared by every hook that names it, so the matrix's parallel deployments append whole
/// lines to one file instead of each re-creating it. Writers are synchronized and flush every line; dispose after the
/// last run has ended.
/// </summary>
internal sealed class HookFiles : IDisposable
{
    /// <summary>Guards <see cref="_writers"/>: parallel deployments open their hooks from thread-pool threads.</summary>
    private readonly Lock _gate = new();

    private readonly Dictionary<string, TextWriter> _writers = new(StringComparer.Ordinal);

    /// <summary>The shared writer of <paramref name="path"/>, opened on the first call.</summary>
    public TextWriter Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var key = System.IO.Path.GetFullPath(path);
        lock (_gate)
        {
            if (!_writers.TryGetValue(key, out var writer))
            {
                writer = TextWriter.Synchronized(OpenFile(path));
                _writers[key] = writer;
            }

            return writer;
        }
    }

    /// <summary>Opens <paramref name="path"/> for writing (truncated, its directory created), flushing every line so the file can be followed while a run is in flight.</summary>
    public static StreamWriter OpenFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var writer in _writers.Values)
            {
                writer.Dispose();
            }

            _writers.Clear();
        }
    }
}

/// <summary>
/// The showcase's built-in lifecycle hook (<c>--hooks sample-log</c>): one line per run, task, sample and model
/// event, in the order Inspect emits them — run start, task start, sample init / start / scoring / attempt end /
/// end (scores, tokens, cost, limit, error), model usage per generation, cache hits, retries, task end and run end.
/// Sample transcript events are counted per sample rather than printed. Lines are prefixed <c>[hook]</c> so they
/// are easy to tell apart from the runner's own progress lines.
/// </summary>
internal sealed class SampleLoggingHooks : Hooks, IDisposable
{
    public const string Name = "sample-log";

    private readonly TextWriter _writer;

    private readonly bool _ownsWriter;

    private readonly Lock _gate = new();

    private readonly Dictionary<string, int> _eventCounts = new(StringComparer.Ordinal);

    public SampleLoggingHooks(TextWriter writer, bool ownsWriter = false)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _ownsWriter = ownsWriter;
    }

    public override Task OnRunStartAsync(RunStart data, CancellationToken cancellationToken)
    {
        Write($"run {data.RunId} start: {string.Join(", ", data.TaskNames)}{(data.EvalSetId is null ? "" : $" (eval set {data.EvalSetId})")}");
        return Task.CompletedTask;
    }

    public override Task OnRunEndAsync(RunEnd data, CancellationToken cancellationToken)
    {
        Write($"run {data.RunId} end: {data.Logs.Count} log(s){(data.Exception is null ? "" : $", exception {data.Exception.GetType().Name}: {FirstLine(data.Exception.Message)}")}");
        return Task.CompletedTask;
    }

    public override Task OnTaskStartAsync(TaskStart data, CancellationToken cancellationToken)
    {
        Write($"task {data.Spec.Task} start: model {data.Spec.Model}, {data.Spec.Dataset.Samples?.ToString(CultureInfo.InvariantCulture) ?? "?"} samples, plan {string.Join(" > ", data.Plan.Steps.Select(step => step.Solver))}");
        return Task.CompletedTask;
    }

    public override Task OnTaskEndAsync(TaskEnd data, CancellationToken cancellationToken)
    {
        var log = data.Log;
        var usage = log.Stats.ModelUsage.Values.Aggregate(new Provider.Core.ModelUsage(), (left, right) => left + right);
        Write($"task {log.Eval.Task} end: {log.Status.ToString().ToLowerInvariant()}, {usage.TotalTokens} tokens{Cost(usage.TotalCost)}, log {log.Location}");
        return Task.CompletedTask;
    }

    public override Task OnSampleInitAsync(SampleInit data, CancellationToken cancellationToken)
    {
        Write($"sample {data.Summary.Id} (epoch {data.Summary.Epoch}) init");
        return Task.CompletedTask;
    }

    public override Task OnSampleStartAsync(SampleStart data, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _eventCounts[data.SampleId] = 0;
        }

        Write($"sample {data.Summary.Id} (epoch {data.Summary.Epoch}) start");
        return Task.CompletedTask;
    }

    public override Task OnSampleEventAsync(SampleEvent data, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _eventCounts[data.SampleId] = _eventCounts.GetValueOrDefault(data.SampleId) + 1;
        }

        return Task.CompletedTask;
    }

    public override Task OnSampleScoringAsync(SampleScoring data, CancellationToken cancellationToken)
    {
        Write($"sample {data.SampleId} scoring");
        return Task.CompletedTask;
    }

    public override Task OnSampleAttemptEndAsync(SampleAttemptEnd data, CancellationToken cancellationToken)
    {
        if (data.Error is { } error)
        {
            Write($"sample {data.Summary.Id} (epoch {data.Summary.Epoch}) attempt {data.Attempt} error: {FirstLine(error.Message)}{(data.WillRetry ? " (will retry)" : "")}");
        }

        return Task.CompletedTask;
    }

    public override Task OnSampleEndAsync(SampleEnd data, CancellationToken cancellationToken)
    {
        var sample = data.Sample;
        int events;
        lock (_gate)
        {
            events = _eventCounts.GetValueOrDefault(data.SampleId);
            _eventCounts.Remove(data.SampleId);
        }

        var usage = sample.ModelUsage.Values.Aggregate(new Provider.Core.ModelUsage(), (left, right) => left + right);
        var outcome = sample.Error is { } error
            ? $"error: {FirstLine(error.Message)}"
            : sample.Scores is { Count: > 0 } scores
                ? string.Join(", ", scores.Select(pair => $"{pair.Key}={pair.Value.Text}"))
                : "no scores";
        var limit = sample.Limit is { } hit ? $", {hit.Type} limit" : "";
        Write($"sample {sample.Id} (epoch {sample.Epoch}) end: {outcome} ({usage.TotalTokens} tokens{Cost(usage.TotalCost)}, {events} events{limit})");
        return Task.CompletedTask;
    }

    public override Task OnModelUsageAsync(ModelUsageData data, CancellationToken cancellationToken)
    {
        var seconds = data.CallDuration.ToString("F2", CultureInfo.InvariantCulture);
        Write($"model {data.ModelName}: {data.Usage.InputTokens} in, {data.Usage.OutputTokens} out ({seconds}s{(data.Retries > 0 ? $", {data.Retries} retries" : "")}{Cost(data.Usage.TotalCost)})");
        return Task.CompletedTask;
    }

    public override Task OnModelCacheUsageAsync(ModelCacheUsageData data, CancellationToken cancellationToken)
    {
        Write($"model {data.ModelName}: cache hit ({data.Usage.TotalTokens} tokens)");
        return Task.CompletedTask;
    }

    public override Task OnModelRetryAsync(ModelRetry data, CancellationToken cancellationToken)
    {
        Write($"model {data.ModelName}: retry {data.Attempt} in {data.WaitTime.ToString("F1", CultureInfo.InvariantCulture)}s{(data.ExceptionType is null ? "" : $" after {data.ExceptionType}")}{(data.StatusCode is { } status ? $" (HTTP {status})" : "")}");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_ownsWriter)
        {
            _writer.Dispose();
        }
    }

    private static string Cost(double? cost) => cost is { } value ? $", ${value.ToString("0.000000", CultureInfo.InvariantCulture)}" : "";

    private static string FirstLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? text : text[..index];
    }

    private void Write(string line)
    {
        lock (_gate)
        {
            _writer.WriteLine($"[hook] {line}");
            _writer.Flush();
        }
    }
}
