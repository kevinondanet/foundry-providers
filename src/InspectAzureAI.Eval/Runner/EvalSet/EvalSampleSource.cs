using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;

namespace InspectAzureAI.Eval.Runner.EvalSet;

/// <summary>
/// What a previous attempt holds for a planned (id, epoch): a clean record to reuse as logged
/// (<see cref="Reusable"/>), or an errored one whose history seeds the re-run's <c>error_retries</c>
/// (<see cref="Errored"/>, Python's <c>PreviousError</c>). A sample the prior attempt never logged, or one it
/// invalidated, has no entry and is re-run fresh.
/// </summary>
public abstract record PreviousSample
{
    private PreviousSample()
    {
    }

    /// <summary>A completed, non-invalidated sample of the previous attempt.</summary>
    public sealed record Reusable(EvalSample Sample) : PreviousSample;

    /// <summary>An errored sample of the previous attempt and the retry history the re-run carries forward.</summary>
    public sealed record Errored(EvalSample Sample, IReadOnlyList<EvalRetryError> ErrorRetries) : PreviousSample;
}

/// <summary>
/// Port of <c>_eval/task/run.py</c> <c>eval_log_sample_source</c> (the in-memory branch; the JSON log is read whole
/// in this port): the previous attempt's samples, keyed by id and epoch, that a retry consults before running each
/// sample. Samples are reusable only when the dataset is the same size as the logged one and, for shuffled
/// datasets, every sample carries an explicit id — otherwise positional ids would not be stable and the source is
/// empty (with a warning). Checkpoint resumption (<c>ResumeCheckpoint</c>) is not ported.
/// </summary>
public sealed class EvalSampleSource
{
    private readonly Dictionary<string, EvalSample> _samples;

    private EvalSampleSource(Dictionary<string, EvalSample> samples)
    {
        _samples = samples;
    }

    /// <summary>A source with nothing to reuse (Python's <c>no_sample_source</c>).</summary>
    public static EvalSampleSource Empty { get; } = new(new Dictionary<string, EvalSample>(StringComparer.Ordinal));

    /// <summary>The number of prior samples this source can answer for.</summary>
    public int Count => _samples.Count;

    /// <summary>
    /// The source for <paramref name="log"/> when its samples can be paired with <paramref name="dataset"/>; the
    /// reasons a log cannot be reused are reported through <paramref name="warn"/> as Python logs them.
    /// </summary>
    public static EvalSampleSource FromLog(EvalLog log, IDataset dataset, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(dataset);
        if (log.Samples is not { Count: > 0 } samples)
        {
            return Empty;
        }

        var samplesHaveIds = dataset.All(sample => sample.Id is not null);
        if ((log.Eval.Dataset.Shuffled == true || dataset.Shuffled) && !samplesHaveIds)
        {
            warn?.Invoke("Unable to re-use samples from retry log file because the dataset was shuffled and some samples in the dataset do not have an 'id' field.");
            return Empty;
        }

        if (log.Eval.Dataset.Samples != dataset.Count)
        {
            warn?.Invoke($"Unable to re-use samples from retry log file because the dataset size changed (log samples {log.Eval.Dataset.Samples}, dataset samples {dataset.Count})");
            return Empty;
        }

        var index = new Dictionary<string, EvalSample>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            // Python's list scan takes the first match; a log with duplicate (id, epoch) records keeps the first too
            index.TryAdd(Key(sample.Id, sample.Epoch), sample);
        }

        return new EvalSampleSource(index);
    }

    /// <summary>Port of <c>read_from_memory</c>: the prior record for (<paramref name="id"/>, <paramref name="epoch"/>), or null to run fresh.</summary>
    public PreviousSample? Lookup(object id, int epoch)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!_samples.TryGetValue(Key(id, epoch), out var sample))
        {
            return null;
        }

        if (sample.Error is null && sample.Invalidation is null)
        {
            return new PreviousSample.Reusable(sample);
        }

        if (sample.Error is not null && SeedErrorRetries(sample) is { Count: > 0 } seed)
        {
            return new PreviousSample.Errored(sample, seed);
        }

        return null;
    }

    /// <summary>Port of <c>error_history_ids</c>: the (id, epoch) pairs that errored in the previous attempt.</summary>
    public IReadOnlyList<(object Id, int Epoch)> ErrorHistoryIds() =>
        _samples.Values.Where(sample => sample.Error is not null).Select(sample => (sample.Id, sample.Epoch)).ToList();

    /// <summary>
    /// Port of <c>_seed_error_retries</c>: the sample's own <c>error_retries</c> plus its terminal error — unless that
    /// error is a cancellation (a sibling's failure tore the task down), which never counts as a retry.
    /// </summary>
    public static IReadOnlyList<EvalRetryError> SeedErrorRetries(EvalSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var seed = new List<EvalRetryError>(sample.ErrorRetries ?? []);
        if (sample.Error is { } error && !IsCancellationError(error))
        {
            seed.Add(RetryErrorFromSample(sample, error));
        }

        return seed;
    }

    /// <summary>
    /// Port of <c>is_cancellation_message</c>, extended to this runtime: Python's <c>CancelledError(</c> /
    /// <c>Cancelled(</c> message prefixes, or a traceback that starts with an <see cref="OperationCanceledException"/>.
    /// </summary>
    public static bool IsCancellationError(EvalError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Message.StartsWith("CancelledError(", StringComparison.Ordinal)
            || error.Message.StartsWith("Cancelled(", StringComparison.Ordinal)
            || error.Traceback.StartsWith(typeof(OperationCanceledException).FullName!, StringComparison.Ordinal)
            || error.Traceback.StartsWith(typeof(TaskCanceledException).FullName!, StringComparison.Ordinal);
    }

    /// <summary>Port of <c>_eval_retry_error_from_sample</c>: the error with the events from the last <see cref="ModelEvent"/> onward, attachments resolved.</summary>
    private static EvalRetryError RetryErrorFromSample(EvalSample sample, EvalError error)
    {
        var resolved = LogAttachments.ResolveSampleAttachments(sample, ResolveAttachments.Full);
        var events = resolved.Events;
        var start = 0;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (events[i] is ModelEvent)
            {
                start = i;
                break;
            }
        }

        return new EvalRetryError(error.Message, error.Traceback, error.TracebackAnsi) { Events = events.Skip(start).ToArray() };
    }

    private static string Key(object? id, int epoch) => $"{Eval.SampleIdKey(id)}#{epoch}";
}
