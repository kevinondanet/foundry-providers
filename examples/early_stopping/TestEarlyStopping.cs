using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Examples.EarlyStopping;

/// <summary>
/// Port of <c>examples/early_stopping.py</c> <c>TestEarlyStopping</c>: an early stopping manager that, before each
/// sample run, halts it with probability one half and, once a sample has been halted, halts all of its remaining
/// epochs. Deviation: takes an optional <see cref="Random"/> (Python uses the global <c>random()</c>) so a run can
/// be reproduced, and guards its list with a lock because the runner schedules samples concurrently.
/// </summary>
public sealed class TestEarlyStopping(Random? random = null) : IEarlyStopping
{
    /// <summary>What <c>start_task</c> returns: the manager's name.</summary>
    public const string ManagerName = "test";

    private readonly List<object> _completedSamples = [];
    private readonly Random _random = random ?? Random.Shared;
    private readonly object _sync = new();

    /// <summary>The ids halted so far (Python's <c>_completed_samples</c>).</summary>
    public IReadOnlyList<object> CompletedSamples
    {
        get
        {
            lock (_sync)
            {
                return _completedSamples.ToArray();
            }
        }
    }

    /// <summary>Called at the beginning of an eval run to register the tasks that will be run..</summary>
    public Task<string> StartTaskAsync(EvalSpec task, IReadOnlyList<Sample> samples, int epochs, CancellationToken cancellationToken) =>
        Task.FromResult(ManagerName);

    /// <summary>Called when a sample is complete.</summary>
    public Task CompleteSampleAsync(object id, int epoch, IReadOnlyDictionary<string, SampleScore> scores, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>Called prior to scheduling a sample (return False to prevent it from running).</summary>
    public Task<EarlyStop?> ScheduleSampleAsync(object id, int epoch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_sync)
        {
            // first check if this sample has no more epochs
            if (_completedSamples.Contains(id))
            {
                return Task.FromResult<EarlyStop?>(new EarlyStop(id, epoch));
            }

            if (_random.NextDouble() < 0.5)
            {
                _completedSamples.Add(id);
                return Task.FromResult<EarlyStop?>(new EarlyStop(id, epoch));
            }

            return Task.FromResult<EarlyStop?>(null);
        }
    }

    /// <summary>Called when the run is complete. Return custom metadata for recording in the log file.</summary>
    public Task<IReadOnlyDictionary<string, object?>> CompleteTaskAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>(StringComparer.Ordinal));
}
