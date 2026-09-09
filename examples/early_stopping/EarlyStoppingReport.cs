using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Examples.EarlyStopping;

/// <summary>
/// A display-only addition of this port (not in <c>early_stopping.py</c>): wraps the task's early stopping manager,
/// counts the sample runs it schedules and halts, and prints the tally to <paramref name="output"/> when the task
/// completes (the log records the same in <c>EvalResults.EarlyStopping</c>). Deviation: Python shows the early
/// stops only in the log.
/// </summary>
public sealed class EarlyStoppingReport(IEarlyStopping inner, TextWriter output) : IEarlyStopping
{
    /// <summary>The prefix of the printed line.</summary>
    public const string Prefix = "early stop: ";

    private readonly IEarlyStopping _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
    private readonly object _sync = new();
    private readonly List<EarlyStop> _stops = [];
    private string _manager = "";
    private int _scheduled;

    /// <summary>Sample runs scheduled so far.</summary>
    public int Scheduled
    {
        get
        {
            lock (_sync)
            {
                return _scheduled;
            }
        }
    }

    /// <summary>The runs halted so far, in order.</summary>
    public IReadOnlyList<EarlyStop> Stops
    {
        get
        {
            lock (_sync)
            {
                return _stops.ToArray();
            }
        }
    }

    public async Task<string> StartTaskAsync(EvalSpec task, IReadOnlyList<Sample> samples, int epochs, CancellationToken cancellationToken)
    {
        _manager = await _inner.StartTaskAsync(task, samples, epochs, cancellationToken).ConfigureAwait(false);
        return _manager;
    }

    public async Task<EarlyStop?> ScheduleSampleAsync(object id, int epoch, CancellationToken cancellationToken)
    {
        var stop = await _inner.ScheduleSampleAsync(id, epoch, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _scheduled++;
            if (stop is not null)
            {
                _stops.Add(stop);
            }
        }

        return stop;
    }

    public Task CompleteSampleAsync(object id, int epoch, IReadOnlyDictionary<string, SampleScore> scores, CancellationToken cancellationToken) =>
        _inner.CompleteSampleAsync(id, epoch, scores, cancellationToken);

    public async Task<IReadOnlyDictionary<string, object?>> CompleteTaskAsync(CancellationToken cancellationToken)
    {
        var metadata = await _inner.CompleteTaskAsync(cancellationToken).ConfigureAwait(false);
        _output.WriteLine(Summary());
        return metadata;
    }

    /// <summary>The printed line: halted/scheduled runs, the manager, and the halted sample ids.</summary>
    public string Summary()
    {
        lock (_sync)
        {
            var ids = _stops.Select(stop => stop.Id).Distinct().ToList();
            var shown = string.Join(", ", ids.Take(12).Select(id => id.ToString()));
            if (ids.Count > 12)
            {
                shown += $", … ({ids.Count} ids)";
            }

            return $"{Prefix}manager '{_manager}' halted {_stops.Count} of {_scheduled} sample runs"
                + (ids.Count > 0 ? $"; samples halted (their remaining epochs too): {shown}" : "");
        }
    }
}
