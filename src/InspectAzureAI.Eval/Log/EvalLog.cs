using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log.Json;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

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
/// Port of <c>log/_log.py</c> <c>EvalLog</c>. Property order follows the Python model because the log format is
/// read by field position in some tools (Python's header-only reader stops at <c>samples</c>).
/// </summary>
public sealed record EvalLog
{
    /// <summary>Port of <c>LOG_SCHEMA_VERSION</c>: the current log format version.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Log format version (<see cref="SchemaVersion"/>); readers reject newer versions and normalise older ones.</summary>
    public int Version { get; init; } = SchemaVersion;

    public EvalStatus Status { get; init; } = EvalStatus.Started;

    public required EvalSpec Eval { get; init; }

    /// <summary>Port of <c>EvalLog.plan</c>: the solvers and generate config.</summary>
    public EvalPlan Plan { get; init; } = new();

    public EvalResults? Results { get; init; }

    public EvalStats Stats { get; init; } = new();

    public EvalError? Error { get; init; }

    /// <summary>Whether any samples were invalidated.</summary>
    public bool Invalidated { get; init; }

    /// <summary>Post-eval edits to tags and metadata (see <see cref="EvalLogEditing"/>).</summary>
    public IReadOnlyList<LogUpdate>? LogUpdates { get; init; }

    /// <summary>Mid-run configuration changes applied via the control channel.</summary>
    public IReadOnlyList<ConfigUpdate>? ConfigUpdates { get; init; }

    /// <summary>Current tags (eval-time plus edits); recomputed by <see cref="EvalLogEditing.RecomputeTagsAndMetadata"/> on read and write.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Current metadata (eval-time plus edits); recomputed like <see cref="Tags"/>.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public IReadOnlyList<EvalSample>? Samples { get; init; }

    /// <summary>Port of <c>EvalLog.reductions</c>: per-sample scores reduced across epochs.</summary>
    public IReadOnlyList<EvalSampleReductions>? Reductions { get; init; }

    /// <summary>Port of <c>EvalLog.location</c>: the file this log was written to or read from (excluded from the JSON, as in Python).</summary>
    [JsonIgnore]
    public string? Location { get; init; }
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalSpec</c>: task and run identity, dataset, sandbox, model and config.</summary>
public sealed record EvalSpec
{
    public string? EvalSetId { get; init; }

    /// <summary>Port of <c>EvalSpec.eval_id</c>: generated on construction; a log lacking it gets a fresh id on read.</summary>
    public string EvalId { get; init; } = ShortUuid.Generate();

    public string RunId { get; init; } = "";

    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;

    public required string Task { get; init; }

    public string TaskId { get; init; } = "";

    /// <summary>Port of <c>task_version: int | str</c>; digit-only versions are written as numbers.</summary>
    public string TaskVersion { get; init; } = "0";

    public string? TaskFile { get; init; }

    public string? TaskDisplayName { get; init; }

    public string? TaskRegistryName { get; init; }

    public IReadOnlyDictionary<string, object?> TaskAttribs { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, object?> TaskArgs { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Task args as passed by the caller; null means "same as <see cref="TaskArgs"/>" (Python's migration for older logs).</summary>
    public IReadOnlyDictionary<string, object?>? TaskArgsPassed { get; init; }

    public string? Solver { get; init; }

    public IReadOnlyDictionary<string, object?>? SolverArgs { get; init; }

    /// <summary>Solver args as passed by the caller; null means "same as <see cref="SolverArgs"/>".</summary>
    public IReadOnlyDictionary<string, object?>? SolverArgsPassed { get; init; }

    /// <summary>Eval-time tags (see <see cref="EvalLog.Tags"/> for the edited view).</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    public required EvalDataset Dataset { get; init; }

    public SandboxSpec? Sandbox { get; init; }

    public required string Model { get; init; }

    public GenerateConfig ModelGenerateConfig { get; init; } = new();

    public string? ModelBaseUrl { get; init; }

    public IReadOnlyDictionary<string, object?> ModelArgs { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Port of <c>model_roles: dict[str, ModelConfig | list[ModelConfig]]</c>; a single-model role is written as one object.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ModelConfig>>? ModelRoles { get; init; }

    public EvalConfig Config { get; init; } = new();

    public EvalRevision? Revision { get; init; }

    public IReadOnlyDictionary<string, string> Packages { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Eval-time metadata (see <see cref="EvalLog.Metadata"/> for the edited view).</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>Port of <c>EvalSpec.viewer</c> (<c>ViewerConfig</c>), kept as raw JSON.</summary>
    public JsonObject? Viewer { get; init; }

    public IReadOnlyList<EvalScorer>? Scorers { get; init; }

    /// <summary>Port of <c>EvalSpec.metrics</c> (a union of metric definitions and groups), kept as raw JSON.</summary>
    public JsonNode? Metrics { get; init; }

    public HeadlineMetric? HeadlineMetric { get; init; }
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

/// <summary>
/// Port of <c>log/_log.py</c> <c>EvalConfig</c>; <see cref="TimeLimit"/> and <see cref="WorkingLimit"/> are seconds.
/// Python's union-typed options are split: <c>limit</c> is <see cref="Limit"/> or <see cref="LimitRange"/>,
/// <c>fail_on_error</c> is <see cref="FailOnError"/> (a bool or a number), <c>sample_shuffle</c> is
/// <see cref="SampleShuffle"/> or <see cref="SampleShuffleSeed"/> (the specific form wins when both are set).
/// </summary>
public sealed record EvalConfig
{
    public int? Limit { get; init; }

    /// <summary>The <c>(start, end)</c> form of <c>limit</c>.</summary>
    public SampleRange? LimitRange { get; init; }

    public IReadOnlyList<object>? SampleId { get; init; }

    public bool? SampleShuffle { get; init; }

    /// <summary>The seed form of <c>sample_shuffle</c>.</summary>
    public int? SampleShuffleSeed { get; init; }

    public int? Epochs { get; init; }

    public IReadOnlyList<string>? EpochsReducer { get; init; }

    /// <summary>Port of <c>ApprovalPolicyConfig</c>, kept as raw JSON.</summary>
    public JsonObject? Approval { get; init; }

    /// <summary>A bool or a notification target string.</summary>
    public object? Notification { get; init; }

    /// <summary>Port of <c>fail_on_error</c>: <c>True</c> fails on the first sample error, <c>False</c> never, a number is a fraction (below 1) or count of failed samples.</summary>
    public FailOnError? FailOnError { get; init; }

    public bool? ContinueOnFail { get; init; }

    public int? RetryOnError { get; init; }

    public bool? ScoreOnError { get; init; }

    public int? MessageLimit { get; init; }

    public int? TokenLimit { get; init; }

    public string? TokenLimitType { get; init; }

    /// <summary>Port of <c>turn_limit</c>: maximum turns (model generations) per sample.</summary>
    public int? TurnLimit { get; init; }

    public int? TimeLimit { get; init; }

    public int? WorkingLimit { get; init; }

    public double? CostLimit { get; init; }

    public int? MaxSamples { get; init; }

    public int? MaxDatasetMemory { get; init; }

    public int? MaxTasks { get; init; }

    public int? MaxSubprocesses { get; init; }

    public int? MaxSandboxes { get; init; }

    public bool? SandboxCleanup { get; init; }

    public bool? SandboxPrebuilt { get; init; }

    public bool? LogSamples { get; init; }

    public bool? LogRealtime { get; init; }

    public bool? LogImages { get; init; }

    public bool? LogModelApi { get; init; }

    public int? LogBuffer { get; init; }

    public int? LogShared { get; init; }

    public bool? ScoreDisplay { get; init; }

    /// <summary>A bool, a port number or a bind address.</summary>
    public object? AcpServer { get; init; }
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalResults</c>.</summary>
public sealed record EvalResults
{
    public int TotalSamples { get; init; }

    public int CompletedSamples { get; init; }

    public EarlyStoppingSummary? EarlyStopping { get; init; }

    public IReadOnlyList<EvalScore> Scores { get; init; } = [];

    /// <summary>The resolved headline metric.</summary>
    public HeadlineMetric? Headline { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalScore</c>: one scorer's metrics (<see cref="Name"/> and <see cref="Scorer"/> are both the scorer name here).</summary>
public sealed record EvalScore(string Name, string Scorer)
{
    public string? Reducer { get; init; }

    public int? ScoredSamples { get; init; }

    public int? UnscoredSamples { get; init; }

    /// <summary>Parameters the scorer was instantiated with.</summary>
    public IReadOnlyDictionary<string, object?> Params { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, EvalMetric> Metrics { get; init; } = new Dictionary<string, EvalMetric>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalMetric</c>. A NaN <see cref="Value"/> is written as the <c>NaN</c> constant, as in Python.</summary>
public sealed record EvalMetric([property: JsonPropertyOrder(0)] string Name, [property: JsonPropertyOrder(2)] double Value)
{
    /// <summary>Metric group (for grouped metrics).</summary>
    [JsonPropertyOrder(1)]
    public string? Group { get; init; }

    [JsonPropertyOrder(3)]
    public IReadOnlyDictionary<string, object?> Params { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    [JsonPropertyOrder(4)]
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalStats</c>.</summary>
public sealed record EvalStats
{
    /// <summary>Written as <c>""</c> when unset, as Python does.</summary>
    [JsonConverter(typeof(EmptyStringDateTimeOffsetConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Written as <c>""</c> when unset, as Python does.</summary>
    [JsonConverter(typeof(EmptyStringDateTimeOffsetConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTimeOffset? CompletedAt { get; init; }

    public IReadOnlyDictionary<string, ModelUsage> ModelUsage { get; init; } = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ModelUsage> RoleUsage { get; init; } = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);

    /// <summary>Port of <c>connection_limit_history</c>: adaptive-connections scale changes (empty unless adaptive connections were active).</summary>
    public IReadOnlyList<ConnectionLimitChange> ConnectionLimitHistory { get; init; } = [];
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

    /// <summary>Port of <c>EvalSample.timelines</c>: custom timeline views over <see cref="Events"/> (nodes reference events by uuid).</summary>
    public IReadOnlyList<Timeline>? Timelines { get; init; }

    public IReadOnlyDictionary<string, ModelUsage> ModelUsage { get; init; } = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ModelUsage> RoleUsage { get; init; } = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);

    public IReadOnlyList<ModelFallback>? ModelFallbacks { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public double? TotalTime { get; init; }

    public double? WorkingTime { get; init; }

    public string? Uuid { get; init; }

    /// <summary>Provenance of an invalidation of this sample.</summary>
    public ProvenanceData? Invalidation { get; init; }

    public EvalError? Error { get; init; }

    public IReadOnlyList<EvalRetryError>? ErrorRetries { get; init; }

    /// <summary>Port of <c>EvalSample.attachments</c>: content referenced from events by <c>attachment://</c> hash (not resolved by this port).</summary>
    public IReadOnlyDictionary<string, string> Attachments { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Port of <c>EvalSample.events_data</c>: pooled messages and calls of condensed logs, kept as raw JSON.</summary>
    public JsonObject? EventsData { get; init; }

    public EvalSampleLimit? Limit { get; init; }

    public int? TurnCount { get; init; }

    public int? TokenLimit { get; init; }

    public string? TokenLimitType { get; init; }

    public int? TokenLimitUsage { get; init; }

    public int? MessageLimit { get; init; }

    public int? TimeLimit { get; init; }
}
