using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents.Bridge;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>agent/_bridge/types.py</c> <c>AgentBridge</c> (main-thread tracking of the generations a scaffold
/// makes through the bridge) together with the request-side helpers of <c>agent/_bridge/util.py</c>:
/// <c>resolve_inspect_model</c>, <c>resolve_generate_config</c>, <c>clear_generation_params</c>,
/// <c>apply_message_ids</c> and the refusal-retry loop of <c>bridge_generate</c>.
/// </summary>
public sealed class AgentBridge
{
    // Sandbox bridge handlers run concurrently (a scaffold fires side calls in parallel with its main loop),
    // so the tracking and id-allocation state is guarded; Python relies on its single event loop instead.
    private readonly object _sync = new();

    private readonly List<MessageFingerprint> _initialFps;

    private readonly List<MessageFingerprint> _initialFpsCondensed;

    private readonly List<string> _initialTexts;

    private readonly Dictionary<string, List<string>> _messageIds = new(StringComparer.Ordinal);

    private List<MessageFingerprint>? _trackedFps;

    private int _trackedCalls;

    private Descent? _trackedDescends;

    private List<MessageFingerprint>? _candidateFps;

    private int _lastMessageCount;

    public AgentBridge(
        AgentState state,
        Model model,
        IReadOnlyDictionary<string, Model>? modelAliases = null,
        int? retryRefusals = null,
        bool forwardGenerationConfig = false,
        IModelEventSink? modelEventSink = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(model);
        State = state;
        Model = model;
        ModelAliases = modelAliases ?? new Dictionary<string, Model>(StringComparer.Ordinal);
        RetryRefusals = retryRefusals;
        ForwardGenerationConfig = forwardGenerationConfig;
        ModelEventSink = modelEventSink;

        var initialMessages = state.Messages.Where(m => m.Role != "system").ToList();
        _initialFps = initialMessages.Select(MessageFingerprint.Of).ToList();
        _initialFpsCondensed = _initialFps.Select(fp => fp.Condensed()).ToList();
        _initialTexts = initialMessages.Select(m => m.Text.Trim()).ToList();
    }

    /// <summary>State updated from messages travelling over the bridge.</summary>
    public AgentState State { get; }

    /// <summary>The model serving requests that name no alias (Python's fallback <c>model</c> and <c>inspect</c> resolution collapsed into one instance).</summary>
    public Model Model { get; }

    /// <summary>Port of <c>model_aliases</c>: request names served by another model; checked before the default.</summary>
    public IReadOnlyDictionary<string, Model> ModelAliases { get; }

    /// <summary>Port of <c>retry_refusals</c>: how many <c>content_filter</c> stops are regenerated before one is returned to the scaffold.</summary>
    public int? RetryRefusals { get; }

    /// <summary>
    /// Port of <c>forward_generation_config</c>: when false (the default) generation-tuning parameters of the
    /// request are dropped so the served model's own config and provider defaults govern generation — a
    /// scaffold computes them for the model it thinks it is talking to, not the one serving the request.
    /// </summary>
    public bool ForwardGenerationConfig { get; }

    /// <summary>Port of <c>model_event_sink</c>: installed around every bridged generation.</summary>
    public IModelEventSink? ModelEventSink { get; }

    /// <summary>
    /// Port of <c>resolve_inspect_model</c>, simplified to what this port can express: an alias resolves to its
    /// model; <c>inspect</c>, <c>inspect/&lt;name&gt;</c>, the served model's own name and any unknown name all
    /// resolve to <see cref="Model"/> (Python's fallback-model, model-role and active-model branches collapse
    /// onto the single bridged instance, which also keeps the eval's config on it).
    /// </summary>
    public Model ResolveModel(string requestedModel)
    {
        ArgumentNullException.ThrowIfNull(requestedModel);
        return ModelAliases.TryGetValue(requestedModel, out var alias) ? alias : Model;
    }

    /// <summary>Port of <c>resolve_generate_config</c>: config built into the model instance wins over the bridged request's.</summary>
    public static GenerateConfig ResolveGenerateConfig(Model model, GenerateConfig requestConfig)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(requestConfig);
        return requestConfig.Merge(model.Config);
    }

    /// <summary>Port of <c>clear_generation_params</c>: the generation-tuning fields a bridged request may not impose (structural fields such as stop sequences are kept).</summary>
    public static GenerateConfig ClearGenerationParams(GenerateConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config with
        {
            MaxTokens = null,
            Temperature = null,
            TopP = null,
            TopK = null,
            FrequencyPenalty = null,
            PresencePenalty = null,
            NumChoices = null,
            Logprobs = null,
            TopLogprobs = null,
            ReasoningEffort = null,
            ReasoningTokens = null,
        };
    }

    /// <summary>Resolve the requested model name (alias → Model; "inspect" or "inspect/&lt;x&gt;" or unknown → the default model), apply config precedence, generate (retrying refusals up to retryRefusals), then track state.</summary>
    public async Task<ModelOutput> GenerateAsync(
        string requestedModel,
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        ToolChoice toolChoice,
        GenerateConfig requestConfig,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedModel);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(toolChoice);
        ArgumentNullException.ThrowIfNull(requestConfig);

        var model = ResolveModel(requestedModel);
        var config = ResolveGenerateConfig(model, ForwardGenerationConfig ? requestConfig : ClearGenerationParams(requestConfig));
        var messages = ApplyMessageIds(input);

        var refusals = 0;
        ModelOutput output;
        while (true)
        {
            using var sinkScope = ModelEventSink is null ? null : ModelEventSinks.Install(ModelEventSink);
            output = await model.GenerateAsync(messages, tools, toolChoice, config, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!output.Empty && output.StopReason == StopReason.ContentFilter && RetryRefusals is { } limit && refusals < limit)
            {
                refusals++;
                continue;
            }

            break;
        }

        TrackState(messages, output);
        return output;
    }

    /// <summary>
    /// Port of <c>apply_message_ids</c> / <c>_id_for_message</c>: ids are allocated from message content so the
    /// same message carries the same id across the scaffold's successive requests, while a repeated identical
    /// message within one conversation still gets its own id.
    /// </summary>
    internal IReadOnlyList<ChatMessage> ApplyMessageIds(IReadOnlyList<ChatMessage> messages)
    {
        lock (_sync)
        {
            var conversationIds = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<ChatMessage>(messages.Count);
            foreach (var message in messages)
            {
                var key = MessageKey(message);
                if (!_messageIds.TryGetValue(key, out var ids))
                {
                    ids = [];
                    _messageIds[key] = ids;
                }

                var id = ids.FirstOrDefault(existing => !conversationIds.Contains(existing));
                if (id is null)
                {
                    id = ShortUuid.Generate();
                    ids.Add(id);
                }

                conversationIds.Add(id);
                result.Add(message with { Id = id });
            }

            return result;
        }
    }

    /// <summary>
    /// Port of <c>_track_state</c>: surfaces the main conversation as <see cref="State"/> by thread identity
    /// rather than message count. A call extending the tracked thread (by role + text prefix) always updates
    /// it; otherwise the new thread's graded descent from the initial input decides displacement, equal
    /// verdicts fall back to the legacy length heuristic, and an unadopted thread is remembered as a candidate
    /// that is promoted if the next call extends it (which is how tracking recovers after scaffold-side
    /// compaction). See the Python docstring for the full rationale.
    /// </summary>
    internal void TrackState(IReadOnlyList<ChatMessage> input, ModelOutput output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        lock (_sync)
        {
            var messages = new List<ChatMessage>(input.Count + 1);
            messages.AddRange(input);
            messages.Add(output.Message);
            var fps = messages.Select(MessageFingerprint.Of).ToList();

            if (_trackedFps is null)
            {
                // first observed call: best information available so far (a side call is displaced later)
                AdoptThread(messages, output, fps, calls: 1);
            }
            else if (ThreadTracking.Extends(_trackedFps, fps))
            {
                AdoptThread(messages, output, fps, calls: _trackedCalls + 1);
            }
            else if (_candidateFps is not null && ThreadTracking.Extends(_candidateFps, fps))
            {
                // the candidate got continued so it is a live agent loop (e.g. post-compaction): promote it
                AdoptThread(messages, output, fps, calls: 2);
            }
            else
            {
                var descends = DescendsFromInitial(messages, fps);
                if (descends is { } verdict
                    && _trackedDescends is { } tracked
                    && verdict > tracked
                    && (_trackedCalls == 1 || messages.Count > _trackedFps.Count))
                {
                    // the real conversation displacing a weaker-anchored thread: a one-shot side call that
                    // landed first, or (when longer) a promoted multi-call sub-agent loop; a short stray
                    // descending one-shot still can't displace an established thread (flapping guard)
                    AdoptThread(messages, output, fps, calls: 1);
                }
                else if (descends == _trackedDescends
                    && messages.Count > (descends is { } and not Descent.No ? _trackedFps.Count : _lastMessageCount))
                {
                    // legacy length heuristic: against the tracked thread when both descend (a parked side
                    // call can't lower the bar), against the previous call otherwise (a scaffold rewriting
                    // message text every call recovers from compaction only through it)
                    AdoptThread(messages, output, fps, calls: 1);
                }
                else
                {
                    _candidateFps = fps;
                }
            }

            _lastMessageCount = messages.Count;
        }
    }

    /// <summary>Port of <c>_adopt_thread</c>: make <paramref name="messages"/> the tracked main thread; <paramref name="calls"/> is the number of bridge calls attributed to it.</summary>
    private void AdoptThread(List<ChatMessage> messages, ModelOutput output, List<MessageFingerprint> fps, int calls)
    {
        State.Messages = messages;
        State.Output = output;
        _trackedFps = fps;
        _trackedCalls = calls;
        _trackedDescends = DescendsFromInitial(messages, fps);
        _candidateFps = null;
    }

    /// <summary>
    /// Port of <c>_descends_from_initial</c>: how a thread's non-system messages anchor on the initial input,
    /// graded per aligned position and aggregated weakness-first (any contained position caps the thread at
    /// <c>Contained</c>, otherwise any quoted position grades it <c>Quoted</c>, otherwise <c>Exact</c>). Null when
    /// there is no initial input to anchor on.
    /// </summary>
    private Descent? DescendsFromInitial(IReadOnlyList<ChatMessage> messages, IReadOnlyList<MessageFingerprint> fps)
    {
        if (_initialFps.Count == 0)
        {
            return null;
        }

        var nonSystem = new List<(ChatMessage Message, MessageFingerprint Fp)>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (fps[i].Role != "system")
            {
                nonSystem.Add((messages[i], fps[i]));
            }
        }

        if (nonSystem.Count < _initialFps.Count)
        {
            return Descent.No;
        }

        var quoted = false;
        var contained = false;
        for (var i = 0; i < _initialFps.Count; i++)
        {
            var (message, fp) = nonSystem[i];
            var position = ThreadTracking.PositionDescent(message, fp, _initialFps[i], _initialFpsCondensed[i], _initialTexts[i]);
            if (position == Descent.No)
            {
                return Descent.No;
            }

            quoted = quoted || position == Descent.Quoted;
            contained = contained || position == Descent.Contained;
        }

        if (contained)
        {
            return Descent.Contained;
        }

        return quoted ? Descent.Quoted : Descent.Exact;
    }

    /// <summary>The content identity <c>_id_for_message</c> keys on (Python hashes the message JSON; ids are excluded by construction).</summary>
    private static string MessageKey(ChatMessage message)
    {
        var snapshot = new JsonObject
        {
            ["role"] = message.Role,
            ["content"] = message.Content.IsString
                ? JsonValue.Create(message.Content.Text)
                : new JsonArray(message.ContentList.Select(c => (JsonNode?)JsonValue.Create(c.ToString())).ToArray()),
        };
        switch (message)
        {
            case ChatMessageAssistant assistant when assistant.ToolCalls is { Count: > 0 } calls:
                snapshot["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode?)new JsonObject
                {
                    ["id"] = c.Id,
                    ["function"] = c.Function,
                    ["arguments"] = c.Arguments.DeepClone(),
                }).ToArray());
                break;
            case ChatMessageTool tool:
                snapshot["tool_call_id"] = tool.ToolCallId;
                snapshot["function"] = tool.Function;
                snapshot["error"] = tool.Error is null ? null : $"{tool.Error.Type}:{tool.Error.Message}";
                break;
        }

        return Mm3Hash.Hash(PythonJson.Dumps(snapshot));
    }
}
