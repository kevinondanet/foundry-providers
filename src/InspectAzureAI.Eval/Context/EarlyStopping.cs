using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Eval.Context;

/// <summary>Port of <c>util/_early_stopping.py</c> <c>EarlyStop</c>: a directive to stop a sample early.</summary>
public sealed record EarlyStop(object Id, int Epoch)
{
    /// <summary>Reason for the early stop.</summary>
    public string? Reason { get; init; }

    /// <summary>Metadata related to the early stop.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>Port of <c>EarlyStoppingSummary</c>: the early stopping applied to a task, recorded on <c>EvalResults</c>.</summary>
public sealed record EarlyStoppingSummary(string Manager, IReadOnlyList<EarlyStop> EarlyStops, IReadOnlyDictionary<string, object?> Metadata);

/// <summary>
/// Port of the <c>EarlyStopping</c> protocol: an early stopping manager consulted by the runner. A sample it
/// halts is completed without running (nothing is logged for it) and is counted against
/// <c>EvalResults.TotalSamples</c> but not <c>CompletedSamples</c>.
/// </summary>
public interface IEarlyStopping
{
    /// <summary>Called at the beginning of an eval run with the task metadata, the samples that will run and the epochs; returns the manager's name.</summary>
    Task<string> StartTaskAsync(EvalSpec task, IReadOnlyList<Sample> samples, int epochs, CancellationToken cancellationToken);

    /// <summary>Called prior to scheduling a sample (every attempt): an <see cref="EarlyStop"/> halts it, null lets it run.</summary>
    Task<EarlyStop?> ScheduleSampleAsync(object id, int epoch, CancellationToken cancellationToken);

    /// <summary>Called when a sample completes with scores (a sample that errored without scores is not reported).</summary>
    Task CompleteSampleAsync(object id, int epoch, IReadOnlyDictionary<string, SampleScore> scores, CancellationToken cancellationToken);

    /// <summary>Called when the task is complete; returns metadata (diagnostics, say) about early stopping.</summary>
    Task<IReadOnlyDictionary<string, object?>> CompleteTaskAsync(CancellationToken cancellationToken);
}
