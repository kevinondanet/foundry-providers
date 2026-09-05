using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Concurrency;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Log;

/// <summary>Port of <c>log/_log.py</c> <c>EvalPlanStep</c>: one solver of the plan with its parameters.</summary>
public sealed record EvalPlanStep(string Solver)
{
    /// <summary>Parameters the solver ran with (defaults resolved).</summary>
    public IReadOnlyDictionary<string, object?> Params { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Parameters as passed by the caller; null means "same as <see cref="Params"/>" (Python's default), and is written as such.</summary>
    public IReadOnlyDictionary<string, object?>? ParamsPassed { get; init; }
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalPlan</c>: the solvers and generate config of the eval.</summary>
public sealed record EvalPlan
{
    public string Name { get; init; } = "plan";

    public IReadOnlyList<EvalPlanStep> Steps { get; init; } = [];

    public EvalPlanStep? Finish { get; init; }

    public GenerateConfig Config { get; init; } = new();
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalRevision</c>: the source revision the eval ran from (<see cref="Type"/> is "git").</summary>
public sealed record EvalRevision(string Type, string Origin, string Commit)
{
    public bool? Dirty { get; init; }
}

/// <summary>Port of <c>log/_log.py</c> <c>HeadlineMetric</c>: which score and metric summarise the eval.</summary>
public sealed record HeadlineMetric
{
    public string? Scorer { get; init; }

    public string? Score { get; init; }

    public string? Metric { get; init; }

    public string? Reducer { get; init; }
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalSampleScore</c>: a <see cref="Scorers.Score"/> tagged with its sample id (written flattened, as in Python).</summary>
public sealed record EvalSampleScore(Score Score)
{
    /// <summary>The sample id (int or string).</summary>
    public object? SampleId { get; init; }
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalSampleReductions</c>: per-sample scores reduced across epochs.</summary>
public sealed record EvalSampleReductions(
    [property: JsonPropertyOrder(0)] string Scorer,
    [property: JsonPropertyOrder(2)] IReadOnlyList<EvalSampleScore> Samples)
{
    [JsonPropertyOrder(1)]
    public string? Reducer { get; init; }
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalRetryError</c>: an error from an earlier attempt of a retried sample.</summary>
public sealed record EvalRetryError(string Message, string Traceback = "", string TracebackAnsi = "")
{
    public IReadOnlyList<TranscriptEvent>? Events { get; init; }
}

/// <summary>Port of <c>model/_model_output.py</c> <c>ModelFallback</c>: a fallback model that served requests.</summary>
public sealed record ModelFallback(string Model, string FallbackModel)
{
    public int Count { get; init; } = 1;

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>Port of <c>model/_model_config.py</c> <c>ModelConfig</c>: a model role's model, config, base URL and args.</summary>
public sealed record ModelConfig(string Model)
{
    public GenerateConfig Config { get; init; } = new();

    public string? BaseUrl { get; init; }

    public IReadOnlyDictionary<string, object?> Args { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>Port of <c>log/_log.py</c> <c>EvalMetricDefinition</c>.</summary>
public sealed record EvalMetricDefinition(string Name)
{
    public IReadOnlyDictionary<string, object?>? Options { get; init; }
}

/// <summary>
/// Port of <c>log/_log.py</c> <c>EvalScorer</c>. <see cref="Metrics"/> is Python's union of metric definitions,
/// groups of definitions, or a dict of groups, kept as raw JSON.
/// </summary>
public sealed record EvalScorer(string Name)
{
    public IReadOnlyDictionary<string, object?>? Options { get; init; }

    public JsonNode? Metrics { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// Port of <c>log/_log.py</c> <c>ConnectionLimitChange</c> (and the controller's <c>LimitChangeRecord</c> tuple): one
/// adaptive-connections scale change. <see cref="Model"/> is the controller's display name, never the (possibly
/// secret-bearing) connection key; <see cref="Timestamp"/> is Unix seconds, as <c>time.time()</c> reports;
/// <see cref="Reason"/> serializes as "slow_start", "steady_state_up", "rate_limit" or "manual".
/// </summary>
public sealed record ConnectionLimitChange(double Timestamp, string Model, int OldLimit, int NewLimit, LimitChangeReason Reason);

/// <summary>Port of <c>util/_early_stopping.py</c> <c>EarlyStop</c>: a sample stopped early.</summary>
public sealed record EarlyStop(object Id, int Epoch)
{
    public string? Reason { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>Port of <c>util/_early_stopping.py</c> <c>EarlyStoppingSummary</c>.</summary>
public sealed record EarlyStoppingSummary(string Manager, IReadOnlyList<EarlyStop> EarlyStops)
{
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>Port of the <c>tuple[int, int]</c> form of <c>EvalConfig.limit</c>: a start/end slice of the dataset.</summary>
public readonly record struct SampleRange(int Start, int End);
