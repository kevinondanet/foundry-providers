using System.Globalization;
using InspectAzureAI.Eval.Concurrency;
using InspectAzureAI.Eval.Log;

namespace InspectAzureAI.Eval.Runner;

/// <summary>The runner's progress hooks (the port's stand-in for <c>_display</c>): sample start and completion plus free-form messages.</summary>
public interface IEvalReporter
{
    void SampleStarted(object id, int epoch);

    void SampleCompleted(EvalSample sample);

    void Message(string text);

    /// <summary>
    /// The live counters Python's display footer shows (sample and connection concurrency, throughput, HTTP
    /// retries), called after each sample starts or completes. A default no-op so existing reporters keep compiling.
    /// </summary>
    void Stats(EvalRunStats stats)
    {
    }
}

/// <summary>
/// Port of the plain display (<c>_display/plain/display.py</c>): one line per sample start and completion (id,
/// epoch, scores, tokens, time) and one per message, written to <paramref name="writer"/> (Console.Out by default).
/// </summary>
public sealed class ConsoleEvalReporter(TextWriter? writer = null) : IEvalReporter
{
    private readonly TextWriter _writer = writer ?? Console.Out;

    // samples complete concurrently and a TextWriter is not thread safe
    private readonly object _sync = new();

    private string? _lastStats;

    public void SampleStarted(object id, int epoch) => Write($"sample {id} (epoch {epoch}) started");

    public void SampleCompleted(EvalSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var tokens = sample.ModelUsage.Values.Sum(usage => usage.TotalTokens);
        var seconds = (sample.TotalTime ?? 0).ToString("F1", CultureInfo.InvariantCulture);
        var outcome = sample.Error is { } error
            ? $"error: {FirstLine(error.Message)}"
            : sample.Scores is { Count: > 0 } scores
                ? string.Join(", ", scores.Select(pair => $"{pair.Key}={pair.Value.Text}"))
                : "no scores";
        if (sample.Limit is { } limit)
        {
            outcome += $" [{limit.Type} limit]";
        }

        Write($"sample {sample.Id} (epoch {sample.Epoch}) completed: {outcome} ({tokens} tokens, {seconds}s)");
    }

    public void Message(string text) => Write(text);

    /// <summary>
    /// Port of the footer's <c>HTTP retries: N  out tok/s: R</c> counters plus the connection status entries:
    /// written only once a retry has occurred (as Python gates the footer) and only when the line changed.
    /// </summary>
    public void Stats(EvalRunStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        if (stats.HttpRetries == 0)
        {
            return;
        }

        var line = $"HTTP retries: {stats.HttpRetries}";
        if (stats.OutputTokensPerSecond is { } rate)
        {
            line += $"  out tok/s: {rate.ToString("F0", CultureInfo.InvariantCulture)}";
        }

        if (stats.Connections.Count > 0)
        {
            line += "  " + string.Join("  ", stats.Connections.Select(pair => $"{pair.Key}: {pair.Value.InUse}/{pair.Value.Concurrency}"));
        }

        lock (_sync)
        {
            if (line == _lastStats)
            {
                return;
            }

            _lastStats = line;
            _writer.WriteLine(line);
        }
    }

    private void Write(string line)
    {
        lock (_sync)
        {
            _writer.WriteLine(line);
        }
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? text : text[..index];
    }
}
