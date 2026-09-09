using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Hooks;

using Hooks = InspectAzureAI.Eval.Hooks.Hooks;

/// <summary>Port of the <c>trackio.Trace(messages=..., metadata=...)</c> record <c>TrackioHooks.on_sample_end</c> builds for a sample.</summary>
public sealed record TrackioTrace(IReadOnlyList<TrackioMessage> Messages, JsonObject Metadata);

/// <summary>One <c>{"role": ..., "content": ...}</c> entry of a Trackio trace.</summary>
public sealed record TrackioMessage(string Role, string Content);

/// <summary>
/// Port of <c>examples/hooks/trackio_tracking.py</c> <c>TrackioHooks</c> (<c>@hooks(name="trackio_tracking",
/// description="Log each Inspect AI sample to Trackio as a trackio.Trace.")</c>). Deviation: <b>not portable</b> —
/// Trackio (<c>https://github.com/gradio-app/trackio</c>) is a Python-only, local-first tracker (a Gradio dashboard
/// over a local Hugging Face datasets store, optionally synced to a Space) with no HTTP API a .NET process could
/// target, so this hook is always disabled (<see cref="Enabled"/> is false, where Python's checks
/// <c>TRACKIO_PROJECT</c>) and logs nothing. What <em>is</em> ported is the data shaping — <see cref="ContentToText"/>,
/// <see cref="MessagesToTraceMessages"/>, <see cref="ScoresToMetadata"/> and <see cref="BuildTrace"/> produce exactly
/// the <c>trackio.Trace</c> record the Python hook logs — so a sink can be attached if one ever exists.
/// </summary>
public sealed class TrackioHooks : Hooks
{
    public const string HookName = "trackio_tracking";

    public const string HookDescription = "Log each Inspect AI sample to Trackio as a trackio.Trace.";

    public const string ProjectVariable = "TRACKIO_PROJECT";

    public const string SpaceIdVariable = "TRACKIO_SPACE_ID";

    /// <summary>Why the hook does nothing (printed by the example).</summary>
    public const string NotPortable = "trackio is a Python-only local library (Gradio dashboard over a local datasets store); it has no HTTP API to port to, so the hook is disabled";

    private int _step;

    /// <summary>Always false: there is no Trackio client to log to (Python: <c>TRACKIO_PROJECT</c> is set).</summary>
    public override bool Enabled => false;

    /// <summary>Whether the Python hook would be enabled in this environment (<c>TRACKIO_PROJECT</c> set).</summary>
    public static bool WouldBeEnabledInPython => Environment.GetEnvironmentVariable(ProjectVariable) is not null;

    /// <summary>Port of <c>_content_to_text</c>: a string as is; a list joined by newlines from each item's text (or its string form).</summary>
    public static string ContentToText(MessageContent? content)
    {
        if (content is null)
        {
            return "";
        }

        if (content.IsString)
        {
            return content.Text ?? "";
        }

        return string.Join("\n", (content.Items ?? []).Select(item => item switch
        {
            ContentText text => text.Text,
            ContentReasoning reasoning => reasoning.Reasoning,
            _ => item.ToString() ?? "",
        }));
    }

    /// <summary>Port of <c>_messages_to_trace_messages</c>.</summary>
    public static IReadOnlyList<TrackioMessage> MessagesToTraceMessages(IEnumerable<ChatMessage>? messages) =>
        (messages ?? []).Select(message => new TrackioMessage(string.IsNullOrEmpty(message.Role) ? "user" : message.Role, ContentToText(message.Content))).ToList();

    /// <summary>Port of <c>_scores_to_metadata</c>: <c>score/&lt;name&gt;</c> values and <c>score/&lt;name&gt;/explanation</c> when present.</summary>
    public static JsonObject ScoresToMetadata(IReadOnlyDictionary<string, Score>? scores)
    {
        var flat = new JsonObject();
        foreach (var (name, score) in scores ?? new Dictionary<string, Score>())
        {
            flat[$"score/{name}"] = score.Value.ToJson();
            if (!string.IsNullOrEmpty(score.Explanation))
            {
                flat[$"score/{name}/explanation"] = score.Explanation;
            }
        }

        return flat;
    }

    /// <summary>
    /// Port of the body of <c>on_sample_end</c>: the sample's messages (or its input when it has none) plus the
    /// completion as a trailing assistant message, and the sample id, epoch, target, eval/run ids and scores as
    /// metadata.
    /// </summary>
    public static TrackioTrace BuildTrace(SampleEnd data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var sample = data.Sample;
        var messages = MessagesToTraceMessages(sample.Messages).ToList();
        if (messages.Count == 0)
        {
            messages.AddRange(sample.Input.IsText
                ? [new TrackioMessage("user", sample.Input.Text ?? "")]
                : MessagesToTraceMessages(sample.Input.Messages));
        }

        var completion = sample.Output.Choices.Count > 0 ? sample.Output.Completion : "";
        if (!string.IsNullOrEmpty(completion))
        {
            messages.Add(new TrackioMessage("assistant", completion));
        }

        var metadata = new JsonObject
        {
            ["sample_id"] = sample.Id.ToString() ?? "",
            ["epoch"] = sample.Epoch,
            ["target"] = sample.Target.Count == 1 ? sample.Target.Text : new JsonArray(sample.Target.Values.Select(v => (JsonNode?)v).ToArray()),
            ["eval_id"] = data.EvalId,
            ["run_id"] = data.RunId,
        };
        foreach (var (key, value) in ScoresToMetadata(sample.Scores))
        {
            metadata[key] = value?.DeepClone();
        }

        return new TrackioTrace(messages, metadata);
    }

    /// <summary>The step the next trace would be logged at (Python's <c>self._step</c>).</summary>
    public int NextStep() => Interlocked.Increment(ref _step) - 1;
}
