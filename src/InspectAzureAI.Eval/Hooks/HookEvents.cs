using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Hooks;

/// <summary>Port of <c>hooks/_hooks.py</c> <c>EvalSetStart</c>: eval set start hook event data.</summary>
/// <param name="EvalSetId">The globally unique identifier for the eval set. Stable across multiple invocations of an eval set for the same log directory.</param>
/// <param name="LogDir">The log directory for the eval set.</param>
public sealed record EvalSetStart(string EvalSetId, string LogDir);

/// <summary>Port of <c>EvalSetEnd</c>: eval set end event data.</summary>
/// <param name="EvalSetId">The globally unique identifier for the eval set.</param>
/// <param name="LogDir">The log directory for the eval set.</param>
public sealed record EvalSetEnd(string EvalSetId, string LogDir);

/// <summary>Port of <c>RunStart</c>: run start hook event data. A run is one <c>Eval.RunAsync</c> invocation.</summary>
/// <param name="EvalSetId">The globally unique identifier for the eval set (if any).</param>
/// <param name="RunId">The globally unique identifier for the run.</param>
/// <param name="TaskNames">The names of the tasks which will be used in the run.</param>
public sealed record RunStart(string? EvalSetId, string RunId, IReadOnlyList<string> TaskNames);

/// <summary>Port of <c>RunEnd</c>: run end hook event data.</summary>
/// <param name="EvalSetId">The globally unique identifier for the eval set (if any).</param>
/// <param name="RunId">The globally unique identifier for the run.</param>
/// <param name="Exception">
/// The exception that escaped the run, if any. Null when the run returned normally — a log with status
/// <c>error</c> (fail-on-error) is a normal return, as in Python. A cancelled run carries its
/// <see cref="OperationCanceledException"/> here, since <c>Eval.RunAsync</c> throws it.
/// </param>
/// <param name="Logs">The logs the run produced; empty when the run failed before its task's log was written.</param>
public sealed record RunEnd(string? EvalSetId, string RunId, Exception? Exception, IReadOnlyList<EvalLog> Logs);

/// <summary>Port of <c>TaskStart</c>: task start hook event data.</summary>
/// <param name="EvalSetId">The globally unique identifier for the eval set (if any).</param>
/// <param name="RunId">The globally unique identifier for the run.</param>
/// <param name="EvalId">The globally unique identifier for this task execution.</param>
/// <param name="Spec">Specification of the task (the instance the log is written with — do not mutate).</param>
/// <param name="Plan">The solvers that will be run and the generate config.</param>
public sealed record TaskStart(string? EvalSetId, string RunId, string EvalId, EvalSpec Spec, EvalPlan Plan);

/// <summary>Port of <c>TaskEnd</c>: task end hook event data.</summary>
/// <param name="EvalSetId">The globally unique identifier for the eval set (if any).</param>
/// <param name="RunId">The globally unique identifier for the run.</param>
/// <param name="EvalId">The globally unique identifier for the task execution.</param>
/// <param name="Log">The log generated for the task (the instance returned by <c>Eval.RunAsync</c>).</param>
public sealed record TaskEnd(string? EvalSetId, string RunId, string EvalId, EvalLog Log);

/// <summary>Port of <c>SampleInit</c>: sample init hook event data.</summary>
/// <param name="EvalSetId">The globally unique identifier for the eval set (if any).</param>
/// <param name="RunId">The globally unique identifier for the run.</param>
/// <param name="EvalId">The globally unique identifier for the task execution.</param>
/// <param name="SampleId">The globally unique identifier for the sample execution (the sample uuid, stable across error retries).</param>
/// <param name="Summary">Summary of the sample to be initialized.</param>
public sealed record SampleInit(string? EvalSetId, string RunId, string EvalId, string SampleId, EvalSampleSummary Summary);

/// <summary>Port of <c>SampleStart</c>: sample start hook event data.</summary>
/// <param name="EvalSetId">The globally unique identifier for the eval set (if any).</param>
/// <param name="RunId">The globally unique identifier for the run.</param>
/// <param name="EvalId">The globally unique identifier for the task execution.</param>
/// <param name="SampleId">The globally unique identifier for the sample execution (the sample uuid).</param>
/// <param name="Summary">Summary of the sample to be run.</param>
public sealed record SampleStart(string? EvalSetId, string RunId, string EvalId, string SampleId, EvalSampleSummary Summary);

/// <summary>Port of <c>SampleEvent</c>: sample event hook event data.</summary>
/// <param name="EvalSetId">The globally unique identifier for the eval set (if any).</param>
/// <param name="RunId">The globally unique identifier for the run.</param>
/// <param name="EvalId">The globally unique identifier for the task execution.</param>
/// <param name="SampleId">The globally unique identifier for the sample execution (the sample uuid).</param>
/// <param name="Event">The transcript event (owned by the framework — do not mutate).</param>
public sealed record SampleEvent(string? EvalSetId, string RunId, string EvalId, string SampleId, TranscriptEvent Event);

/// <summary>Port of <c>SampleEnd</c>: sample end hook event data.</summary>
/// <param name="EvalSetId">The globally unique identifier for the eval set (if any).</param>
/// <param name="RunId">The globally unique identifier for the run.</param>
/// <param name="EvalId">The globally unique identifier for the task execution.</param>
/// <param name="SampleId">The globally unique identifier for the sample execution (the sample uuid).</param>
/// <param name="Sample">The sample that has run (owned by the framework — do not mutate).</param>
public sealed record SampleEnd(string? EvalSetId, string RunId, string EvalId, string SampleId, EvalSample Sample);

/// <summary>
/// Port of <c>SampleAttemptStart</c>: fired at the beginning of every attempt (including the first). Unlike
/// <see cref="SampleStart"/>, which fires once per sample, this fires on error retries too.
/// </summary>
/// <param name="EvalSetId">The globally unique identifier for the eval set (if any).</param>
/// <param name="RunId">The globally unique identifier for the run.</param>
/// <param name="EvalId">The globally unique identifier for the task execution.</param>
/// <param name="SampleId">The globally unique identifier for the sample execution (the sample uuid).</param>
/// <param name="Summary">Summary of the sample to be run.</param>
/// <param name="Attempt">1-based attempt number.</param>
public sealed record SampleAttemptStart(string? EvalSetId, string RunId, string EvalId, string SampleId, EvalSampleSummary Summary, int Attempt);

/// <summary>
/// Port of <c>SampleAttemptEnd</c>: fired at the end of every attempt (including the last). Unlike
/// <see cref="SampleEnd"/>, which fires once per sample, this fires on error retries too.
/// </summary>
/// <param name="EvalSetId">The globally unique identifier for the eval set (if any).</param>
/// <param name="RunId">The globally unique identifier for the run.</param>
/// <param name="EvalId">The globally unique identifier for the task execution.</param>
/// <param name="SampleId">The globally unique identifier for the sample execution (the sample uuid).</param>
/// <param name="Summary">Summary of the sample.</param>
/// <param name="Attempt">1-based attempt number.</param>
/// <param name="Error">The error from this attempt, if any.</param>
/// <param name="WillRetry">Whether the sample will be retried after this attempt.</param>
public sealed record SampleAttemptEnd(string? EvalSetId, string RunId, string EvalId, string SampleId, EvalSampleSummary Summary, int Attempt, EvalError? Error, bool WillRetry);

/// <summary>Port of <c>ModelUsageData</c>: model usage hook event data (a successful generate that did not hit Inspect's local cache).</summary>
/// <param name="ModelName">The name of the model that was used.</param>
/// <param name="Usage">The model usage metrics.</param>
/// <param name="CallDuration">
/// The duration of the model call in seconds. If HTTP retries were made, this is the time taken for the
/// successful call; it excludes retry waiting (backoff) time.
/// </param>
public sealed record ModelUsageData(string ModelName, ModelUsage Usage, double CallDuration)
{
    /// <summary>The globally unique identifier for the eval set (if any).</summary>
    public string? EvalSetId { get; init; }

    /// <summary>The globally unique identifier for the run (if any).</summary>
    public string? RunId { get; init; }

    /// <summary>The globally unique identifier for the task execution (if any).</summary>
    public string? EvalId { get; init; }

    /// <summary>The name of the task that generated this usage (if any).</summary>
    public string? TaskName { get; init; }

    /// <summary>The number of HTTP retries made before the successful call.</summary>
    public int Retries { get; init; }
}

/// <summary>
/// Port of <c>ModelCacheUsageData</c>: like <see cref="ModelUsageData"/> but without a call duration, since no
/// external call is made when the cache is hit.
/// </summary>
/// <param name="ModelName">The name of the model that was used.</param>
/// <param name="Usage">The model usage metrics (as recorded when the entry was cached).</param>
public sealed record ModelCacheUsageData(string ModelName, ModelUsage Usage);

/// <summary>Port of <c>BeforeModelGenerate</c>: data provided before a model generate call (every attempt of the retry loop).</summary>
/// <param name="ModelName">The name of the model about to be called.</param>
/// <param name="Input">The chat messages about to be sent to the model (system message inserted, user messages collapsed).</param>
/// <param name="Tools">The tools available for the model to call.</param>
/// <param name="ToolChoice">Directives to the model as to which tools to prefer.</param>
/// <param name="Config">The generation configuration.</param>
/// <param name="Cache"><see cref="CacheMode.Write"/> if caching is enabled for the call, null otherwise.</param>
public sealed record BeforeModelGenerate(
    string ModelName,
    IReadOnlyList<ChatMessage> Input,
    IReadOnlyList<ToolInfo> Tools,
    ToolChoice ToolChoice,
    GenerateConfig Config,
    CacheMode? Cache)
{
    /// <summary>The globally unique identifier for the eval set (if any).</summary>
    public string? EvalSetId { get; init; }

    /// <summary>The globally unique identifier for the run (if any).</summary>
    public string? RunId { get; init; }

    /// <summary>The globally unique identifier for the task execution (if any).</summary>
    public string? EvalId { get; init; }

    /// <summary>The globally unique identifier for the sample execution (the sample uuid), if any.</summary>
    public string? SampleId { get; init; }

    /// <summary>The name of the task that triggered this generate call (if any).</summary>
    public string? TaskName { get; init; }
}

/// <summary>Port of <c>ModelRetry</c>: model retry hook event data.</summary>
/// <param name="ModelName">The name of the model whose call is being retried.</param>
/// <param name="Attempt">The number of the attempt that just failed (1 for the first failure).</param>
/// <param name="WaitTime">
/// The time in seconds that will be waited (backoff) before the next attempt: the time attributable to
/// rate limiting and other transient retries.
/// </param>
public sealed record ModelRetry(string ModelName, int Attempt, double WaitTime)
{
    /// <summary>The globally unique identifier for the eval set (if any).</summary>
    public string? EvalSetId { get; init; }

    /// <summary>The globally unique identifier for the run (if any).</summary>
    public string? RunId { get; init; }

    /// <summary>The globally unique identifier for the task execution (if any).</summary>
    public string? EvalId { get; init; }

    /// <summary>The globally unique identifier for the sample execution (the sample uuid), if any.</summary>
    public string? SampleId { get; init; }

    /// <summary>The name of the task whose model call is being retried (if any).</summary>
    public string? TaskName { get; init; }

    /// <summary>The type name of the exception that triggered the retry (e.g. "RequestFailedException"), if known.</summary>
    public string? ExceptionType { get; init; }

    /// <summary>The HTTP status code of the failure that triggered the retry (e.g. 429 or 503), if any.</summary>
    public int? StatusCode { get; init; }
}

/// <summary>Port of <c>_util/retry.py</c> <c>RetryErrorInfo</c>: the exception type name and HTTP status of a retried failure.</summary>
/// <param name="ExceptionType">The type name of the exception, if known.</param>
/// <param name="StatusCode">The HTTP status code carried by the exception, if any.</param>
public readonly record struct RetryErrorInfo(string? ExceptionType, int? StatusCode)
{
    /// <summary>Port of <c>retry_error_type_status</c>: <c>type(ex).__name__</c> and the status code of a <c>RequestFailedException</c>.</summary>
    public static RetryErrorInfo Of(Exception? exception) =>
        exception is null ? default : new RetryErrorInfo(exception.GetType().Name, Provider.Util.HttpRetryUtil.StatusCodeOf(exception));
}

/// <summary>Port of <c>SampleScoring</c>: sample scoring hook event data (fired before the sample is scored).</summary>
/// <param name="EvalSetId">The globally unique identifier for the eval set (if any).</param>
/// <param name="RunId">The globally unique identifier for the run.</param>
/// <param name="EvalId">The globally unique identifier for the task execution.</param>
/// <param name="SampleId">The globally unique identifier for the sample execution (the sample uuid).</param>
public sealed record SampleScoring(string? EvalSetId, string RunId, string EvalId, string SampleId);

/// <summary>Port of <c>ApiKeyOverride</c>: api key override hook event data.</summary>
/// <param name="EnvVarName">The name of the environment var containing the API key (e.g. <c>OPENAI_API_KEY</c>).</param>
/// <param name="Value">The original value of the environment variable (empty when no key exists anywhere).</param>
public sealed record ApiKeyOverride(string EnvVarName, string Value);
