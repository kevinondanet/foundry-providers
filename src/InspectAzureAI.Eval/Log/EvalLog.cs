using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Log;

/// <summary>Port of the <c>EvalStatus</c> literal of <c>log/_log.py</c>; written lower-case.</summary>
public enum EvalStatus
{
    Started,
    Success,
    Cancelled,
    Error,
}

/// <summary>
/// Port of <c>log/_log.py</c> <c>EvalLog</c> (the subset the runner produces). Property order follows the
/// Python model because the log format is read by field position in some tools.
/// </summary>
public sealed record EvalLog
{
    /// <summary>Format version of this port's JSON logs (Python's <c>.eval</c> format is version 2 and not produced here).</summary>
    public int Version { get; init; } = 1;

    public EvalStatus Status { get; init; } = EvalStatus.Started;

    public required EvalSpec Eval { get; init; }

    public EvalResults? Results { get; init; }

    public EvalStats Stats { get; init; } = new();

    public EvalError? Error { get; init; }

    public IReadOnlyList<EvalSample>? Samples { get; init; }

    /// <summary>Port of <c>EvalLog.location</c>: the file this log was written to or read from (excluded from the JSON, as in Python).</summary>
    [JsonIgnore]
    public string? Location { get; init; }
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalSpec</c>: task and run identity, dataset, sandbox, model and config.</summary>
public sealed record EvalSpec
{
    public string RunId { get; init; } = "";

    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;

    public required string Task { get; init; }

    public string TaskId { get; init; } = "";

    public string TaskVersion { get; init; } = "0";

    public required EvalDataset Dataset { get; init; }

    public SandboxSpec? Sandbox { get; init; }

    public required string Model { get; init; }

    public EvalConfig Config { get; init; } = new();
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalDataset</c>.</summary>
public sealed record EvalDataset
{
    public string? Name { get; init; }

    public string? Location { get; init; }

    public int? Samples { get; init; }

    /// <summary>Sample ids (ints or strings) in dataset order.</summary>
    public IReadOnlyList<object>? SampleIds { get; init; }

    public bool? Shuffled { get; init; }
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalConfig</c> (the options this runner honours); <see cref="TimeLimit"/> is seconds.</summary>
public sealed record EvalConfig
{
    public int? Limit { get; init; }

    public IReadOnlyList<object>? SampleId { get; init; }

    public int? Epochs { get; init; }

    /// <summary>Port of <c>fail_on_error</c>: <c>True</c> fails on the first sample error, <c>False</c> never, a number is a fraction (below 1) or count of failed samples.</summary>
    public FailOnError? FailOnError { get; init; }

    /// <summary>Port of <c>continue_on_fail</c>: keep running when the <c>fail_on_error</c> condition is met and only mark the log as failed at the end.</summary>
    public bool? ContinueOnFail { get; init; }

    /// <summary>Port of <c>retry_on_error</c>: number of times a sample that errors is retried.</summary>
    public int? RetryOnError { get; init; }

    public int? MessageLimit { get; init; }

    public int? TokenLimit { get; init; }

    /// <summary>Port of <c>turn_limit</c>: maximum turns (model generations) per sample.</summary>
    public int? TurnLimit { get; init; }

    public int? TimeLimit { get; init; }

    /// <summary>Port of <c>working_limit</c>: maximum working time per sample, in seconds.</summary>
    public int? WorkingLimit { get; init; }

    public int? MaxSamples { get; init; }

    public bool? SandboxCleanup { get; init; }
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalResults</c>.</summary>
public sealed record EvalResults
{
    public int TotalSamples { get; init; }

    public int CompletedSamples { get; init; }

    /// <summary>Port of <c>early_stopping</c>: the summary of an early stopping manager, when the task had one.</summary>
    public EarlyStoppingSummary? EarlyStopping { get; init; }

    public IReadOnlyList<EvalScore> Scores { get; init; } = [];
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalScore</c>: one scorer's metrics (<see cref="Name"/> and <see cref="Scorer"/> are both the scorer name here).</summary>
public sealed record EvalScore(string Name, string Scorer)
{
    public string? Reducer { get; init; }

    public int? ScoredSamples { get; init; }

    public int? UnscoredSamples { get; init; }

    public IReadOnlyDictionary<string, EvalMetric> Metrics { get; init; } = new Dictionary<string, EvalMetric>(StringComparer.Ordinal);
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalMetric</c>. A NaN <see cref="Value"/> is written as JSON <c>null</c> and read back as NaN.</summary>
public sealed record EvalMetric(string Name, double Value);

/// <summary>Port of <c>log/_log.py</c> <c>EvalStats</c>.</summary>
public sealed record EvalStats
{
    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public IReadOnlyDictionary<string, ModelUsage> ModelUsage { get; init; } = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
}

/// <summary>Port of <c>_util/error.py</c> <c>EvalError</c>.</summary>
public sealed record EvalError(string Message, string Traceback = "", string TracebackAnsi = "")
{
    /// <summary>Port of <c>exception_to_error</c>: message plus the .NET stack trace as the traceback.</summary>
    public static EvalError FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new EvalError(exception.Message, exception.ToString());
    }
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalSampleLimit</c>: which limit ended the sample ("message", "token", "time", ...).</summary>
public sealed record EvalSampleLimit(string Type, double Limit, string? Reason = null);

/// <summary>
/// Port of <c>log/_log.py</c> <c>EvalSample</c>: everything recorded for one sample/epoch. <see cref="Files"/>
/// holds destination names only (never contents); <see cref="TotalTime"/> and <see cref="WorkingTime"/> are seconds.
/// </summary>
public sealed record EvalSample
{
    public required object Id { get; init; }

    public required int Epoch { get; init; }

    public required SampleInput Input { get; init; }

    public IReadOnlyList<string>? Choices { get; init; }

    public Target Target { get; init; } = Target.Empty;

    public SandboxSpec? Sandbox { get; init; }

    public IReadOnlyList<string>? Files { get; init; }

    public string? Setup { get; init; }

    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    public ModelOutput Output { get; init; } = new();

    public IReadOnlyDictionary<string, Score>? Scores { get; init; }

    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, object?> Store { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public IReadOnlyList<TranscriptEvent> Events { get; init; } = [];

    public IReadOnlyDictionary<string, ModelUsage> ModelUsage { get; init; } = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public double? TotalTime { get; init; }

    public double? WorkingTime { get; init; }

    public string? Uuid { get; init; }

    public EvalError? Error { get; init; }

    /// <summary>Port of <c>error_retries</c>: the errors of the attempts retried before this one (empty when none).</summary>
    public IReadOnlyList<EvalRetryError>? ErrorRetries { get; init; }

    public EvalSampleLimit? Limit { get; init; }
}

/// <summary>
/// Port of <c>log/_log.py</c> <c>EvalRetryError</c>: the error of a retried sample attempt with the events from
/// that attempt's last <c>ModelEvent</c> onward (all of them when it never generated).
/// </summary>
public sealed record EvalRetryError(string Message, string Traceback = "", string TracebackAnsi = "", IReadOnlyList<TranscriptEvent>? Events = null);
