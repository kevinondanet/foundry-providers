using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Log;

namespace InspectAzureAI.Examples.Hooks;

using Hooks = InspectAzureAI.Eval.Hooks.Hooks;

/// <summary>
/// Port of <c>examples/hooks/wandb_weave.py</c> <c>WeaveHooks</c> (<c>@hooks(name="weave_hooks", description="Weights &amp;
/// Biases Weave")</c>): a Weave thread per sample and a <c>sample_complete</c> call carrying the sample's inputs and
/// outputs. Deviation: <b>a stub</b> — the Python module drives the <c>weave</c> client library (<c>weave.init</c>,
/// <c>weave.thread</c>, <c>WeaveClient.create_call</c>/<c>finish_call</c>), whose trace-server HTTP protocol is
/// documented in neither this repository nor the inspect_ai one, so no transport was written against it (the rule for
/// this port: only a plainly documented HTTP API gets a client). The hook is therefore always disabled
/// (<see cref="Enabled"/> is false, where Python's checks <c>WANDB_PROJECT_ID</c> and <c>WANDB_API_KEY</c>) and logs
/// nothing; the data shaping is ported — <see cref="ThreadId"/>, <see cref="SampleCompleteInputs"/> and
/// <see cref="SampleCompleteOutput"/> build the exact thread id and call payloads — so a client can be attached later.
/// </summary>
public sealed class WeaveHooks : Hooks
{
    public const string HookName = "weave_hooks";

    public const string HookDescription = "Weights & Biases Weave";

    public const string ProjectIdVariable = "WANDB_PROJECT_ID";

    public const string ApiKeyVariable = "WANDB_API_KEY";

    /// <summary>The op name of the per-sample call.</summary>
    public const string SampleCompleteOp = "sample_complete";

    /// <summary>Why the hook does nothing (printed by the example).</summary>
    public const string NotPorted = "the weave client's trace-server HTTP protocol is not documented in this repository or in inspect_ai, so no transport was written; the hook is disabled";

    private readonly object _sync = new();

    private readonly Dictionary<string, EvalSpec> _tasks = new(StringComparer.Ordinal);

    /// <summary>Always false: there is no Weave client (Python: <c>WANDB_PROJECT_ID</c> and <c>WANDB_API_KEY</c> are set).</summary>
    public override bool Enabled => false;

    /// <summary>Whether the Python hook would be enabled in this environment.</summary>
    public static bool WouldBeEnabledInPython =>
        Environment.GetEnvironmentVariable(ProjectIdVariable) is not null && Environment.GetEnvironmentVariable(ApiKeyVariable) is not null;

    /// <summary>The task specs of the tasks in flight (what <c>on_task_start</c>/<c>on_task_end</c> maintain).</summary>
    public IReadOnlyDictionary<string, EvalSpec> Tasks
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, EvalSpec>(_tasks, StringComparer.Ordinal);
            }
        }
    }

    public override Task OnTaskStartAsync(TaskStart data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_sync)
        {
            _tasks[data.EvalId] = data.Spec;
        }

        return Task.CompletedTask;
    }

    public override Task OnTaskEndAsync(TaskEnd data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_sync)
        {
            _tasks.Remove(data.EvalId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Port of the thread id of <c>on_sample_start</c>: <c>{task}-{dataset id}[{epoch}]-{model}-{sample id}</c> with
    /// slashes replaced by dashes (Weave forbids them); <c>task</c> and no model when the spec is unknown.
    /// </summary>
    public static string ThreadId(EvalSpec? spec, object datasetId, int epoch, string sampleId)
    {
        ArgumentNullException.ThrowIfNull(datasetId);
        ArgumentNullException.ThrowIfNull(sampleId);
        var taskName = spec?.Task ?? "task";
        var model = spec is null ? "" : $"-{spec.Model}";
        return $"{taskName}-{datasetId}[{epoch}]{model}-{sampleId}".Replace('/', '-');
    }

    /// <summary>Port of the <c>inputs</c> of the <c>sample_complete</c> call: id, epoch, model, input (as JSON) and metadata.</summary>
    public static JsonObject SampleCompleteInputs(EvalSample sample, EvalSpec? spec)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return new JsonObject
        {
            ["id"] = MlflowTrackingHooks.IdNode(sample.Id),
            ["epoch"] = sample.Epoch,
            ["model"] = spec?.Model,
            ["input"] = ToJsonable(sample.Input),
            ["metadata"] = ToJsonable(sample.Metadata),
        };
    }

    /// <summary>Port of the <c>output</c> of the <c>sample_complete</c> call: output, scores, error, total and working time.</summary>
    public static JsonObject SampleCompleteOutput(EvalSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return new JsonObject
        {
            ["output"] = ToJsonable(sample.Output),
            ["scores"] = ToJsonable(sample.Scores),
            ["error"] = ToJsonable(sample.Error),
            ["total_time"] = sample.TotalTime,
            ["working_time"] = sample.WorkingTime,
        };
    }

    /// <summary>Port of <c>pydantic_core.to_jsonable_python</c> through the eval log's serializer.</summary>
    private static JsonNode? ToJsonable<T>(T? value) => value is null ? null : JsonSerializer.SerializeToNode(value, EvalLogWriter.Options);
}
