using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Log;

/// <summary>Port of the <c>resolve_attachments: bool | Literal["full", "core"]</c> argument of the log readers.</summary>
public enum ResolveAttachments
{
    /// <summary>Leave <c>attachment://</c> references as they are (Python <c>False</c>).</summary>
    None,

    /// <summary>Resolve everything except <c>ModelEvent.call</c>, retaining the attachments it still references (Python <c>"core"</c>).</summary>
    Core,

    /// <summary>Resolve every reference and clear <c>attachments</c> (Python <c>"full"</c> / <c>True</c>).</summary>
    Full,
}

/// <summary>
/// Port of the attachment half of <c>log/_condense.py</c>: <see cref="CondenseSample"/> moves long event text
/// (over 100 characters) and base64 data URIs out of the sample into <c>attachments</c>, keyed by their
/// <see cref="MurmurHash3"/> hash and referenced as <c>attachment://&lt;hash&gt;</c>; <see cref="ResolveSampleAttachments"/>
/// puts the content back. The message and call pools of condensed logs (<c>events_data</c>, <c>input_refs</c>,
/// <c>call_refs</c>) are not built or resolved by this port; the pools' own strings are still walked.
/// </summary>
public static partial class LogAttachments
{
    /// <summary>Port of <c>ATTACHMENT_PROTOCOL</c>.</summary>
    public const string AttachmentProtocol = "attachment://";

    /// <summary>Port of <c>BASE_64_DATA_REMOVED</c>: what a data URI becomes when images are not logged.</summary>
    public const string Base64DataRemoved = "<base64-data-removed>";

    /// <summary>Port of <c>MAX_JSON_VALUE_DEPTH</c>: containers nested deeper than this are replaced by <see cref="JsonValueMaxDepthExceeded"/>.</summary>
    public const int MaxJsonValueDepth = 240;

    /// <summary>Port of <c>JSON_VALUE_MAX_DEPTH_EXCEEDED</c>.</summary>
    public const string JsonValueMaxDepthExceeded = "<max nesting depth exceeded>";

    private const string LegacyContentProtocol = "tc://";

    /// <summary>Text longer than this in events becomes an attachment (Python's <c>len(text) &gt; 100</c>).</summary>
    private const int EventTextThreshold = 100;

    /// <summary>Port of <c>_util/url.py</c> <c>is_data_uri</c>: a <c>data:</c> URI with a base64 payload (the media type is optional).</summary>
    public static bool IsDataUri(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return DataUriRegex().IsMatch(url);
    }

    /// <summary>The attachment hashes referenced from <paramref name="text"/> (JSON or plain), in first-seen order.</summary>
    public static IReadOnlySet<string> AttachmentRefs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in AttachmentRefRegex().Matches(text))
        {
            refs.Add(match.Groups[1].Value);
        }

        return refs;
    }

    /// <summary>
    /// Port of <c>condense_sample</c>: events get long text and (when <paramref name="logImages"/>) data URIs
    /// replaced by attachment references, messages only their data URIs; with <paramref name="logImages"/> false
    /// data URIs become <see cref="Base64DataRemoved"/> instead (a one-way change). Already-condensed content is
    /// re-evaluated against the policy, and attachments nothing references any more are dropped.
    /// </summary>
    public static EvalSample CondenseSample(EvalSample sample, bool logImages = true)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var existing = new Dictionary<string, string>(sample.Attachments, StringComparer.Ordinal);
        var attachments = new Dictionary<string, string>(existing, StringComparer.Ordinal);
        var rewritten = new HashSet<string>(StringComparer.Ordinal);
        var eventsFn = ExistingAttachmentsFn(existing, rewritten, EventsAttachmentFn(attachments, logImages));
        var messagesFn = ExistingAttachmentsFn(existing, rewritten, MessagesAttachmentFn(attachments, logImages));

        var condensed = sample with
        {
            Input = WalkInput(sample.Input, messagesFn),
            Messages = WalkMessages(sample.Messages, messagesFn),
            Events = WalkEvents(sample.Events, eventsFn, onlyCore: false),
            ErrorRetries = WalkRetries(sample.ErrorRetries, eventsFn, onlyCore: false),
            EventsData = WalkEventsData(sample.EventsData, eventsFn, onlyCore: false),
            Attachments = attachments,
        };

        // liveness is decided only after every field is condensed, so no surviving reference is orphaned
        var referenced = AttachmentRefs(JsonSerializer.Serialize(condensed with { Attachments = new Dictionary<string, string>() }, EvalLogWriter.Options));
        return condensed with
        {
            Attachments = attachments
                .Where(pair => !rewritten.Contains(pair.Key) || referenced.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>Port of <c>condense_event</c>: one event condensed into <paramref name="attachments"/>.</summary>
    public static TranscriptEvent CondenseEvent(TranscriptEvent e, IDictionary<string, string> attachments, bool logImages = true)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(attachments);
        return WalkEvent(e, EventsAttachmentFn(attachments, logImages), onlyCore: false);
    }

    /// <summary>
    /// Port of <c>resolve_sample_attachments</c>: <c>attachment://</c> (and legacy <c>tc://</c>) references are
    /// replaced by their content. <see cref="ResolveAttachments.Core"/> leaves <c>ModelEvent.call</c> condensed
    /// and keeps the attachments it still references; <see cref="ResolveAttachments.Full"/> clears them all.
    /// </summary>
    public static EvalSample ResolveSampleAttachments(EvalSample sample, ResolveAttachments mode = ResolveAttachments.Core)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (mode == ResolveAttachments.None)
        {
            return sample;
        }

        var fn = ResolveFn(sample.Attachments);
        var onlyCore = mode == ResolveAttachments.Core;
        var resolved = sample with
        {
            Input = WalkInput(sample.Input, fn),
            Messages = WalkMessages(sample.Messages, fn),
            Events = WalkEvents(sample.Events, fn, onlyCore),
            ErrorRetries = WalkRetries(sample.ErrorRetries, fn, onlyCore),
            EventsData = WalkEventsData(sample.EventsData, fn, onlyCore),
            Attachments = new Dictionary<string, string>(StringComparer.Ordinal),
        };

        if (!onlyCore || sample.Attachments.Count == 0)
        {
            return resolved;
        }

        var referenced = AttachmentRefs(JsonSerializer.Serialize(resolved, EvalLogWriter.Options));
        if (referenced.Count == 0)
        {
            return resolved;
        }

        return resolved with
        {
            Attachments = sample.Attachments
                .Where(pair => referenced.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>Port of <c>resolve_events_attachments</c>: references in <paramref name="events"/> resolved from <paramref name="attachments"/>.</summary>
    public static IReadOnlyList<TranscriptEvent> ResolveEventsAttachments(IReadOnlyList<TranscriptEvent> events, IReadOnlyDictionary<string, string> attachments, ResolveAttachments mode = ResolveAttachments.Core)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(attachments);
        return mode == ResolveAttachments.None ? events : WalkEvents(events, ResolveFn(attachments), mode == ResolveAttachments.Core);
    }

    private static Func<string, string> ResolveFn(IReadOnlyDictionary<string, string> attachments) => text =>
    {
        if (text.StartsWith(LegacyContentProtocol, StringComparison.Ordinal))
        {
            text = AttachmentProtocol + text[LegacyContentProtocol.Length..];
        }

        return text.StartsWith(AttachmentProtocol, StringComparison.Ordinal) && attachments.TryGetValue(text[AttachmentProtocol.Length..], out var content)
            ? content
            : text;
    };

    private static Func<string, string> EventsAttachmentFn(IDictionary<string, string> attachments, bool logImages) => text =>
    {
        if (!logImages && IsDataUri(text))
        {
            return Base64DataRemoved;
        }

        return text.Length > EventTextThreshold ? CreateAttachment(attachments, text) : text;
    };

    private static Func<string, string> MessagesAttachmentFn(IDictionary<string, string> attachments, bool logImages) => text =>
        IsDataUri(text) ? logImages ? CreateAttachment(attachments, text) : Base64DataRemoved : text;

    /// <summary>Port of <c>_existing_attachments_content_fn</c>: applies the policy to the content behind an existing reference.</summary>
    private static Func<string, string> ExistingAttachmentsFn(IReadOnlyDictionary<string, string> existing, ISet<string> rewritten, Func<string, string> contentFn) => text =>
    {
        if (text.StartsWith(AttachmentProtocol, StringComparison.Ordinal) && existing.TryGetValue(text[AttachmentProtocol.Length..], out var value))
        {
            var result = contentFn(value);
            if (result != text)
            {
                rewritten.Add(text[AttachmentProtocol.Length..]);
            }

            return result;
        }

        return contentFn(text);
    };

    private static string CreateAttachment(IDictionary<string, string> attachments, string text)
    {
        var hash = MurmurHash3.Hash(text);
        attachments[hash] = text;
        return AttachmentProtocol + hash;
    }

    private static IReadOnlyList<EvalRetryError>? WalkRetries(IReadOnlyList<EvalRetryError>? retries, Func<string, string> fn, bool onlyCore) =>
        retries?.Select(retry => retry.Events is null ? retry : retry with { Events = WalkEvents(retry.Events, fn, onlyCore) }).ToList();

    /// <summary>The pooled messages are walked as messages, the pooled calls as JSON (skipped for core-only resolution, like <c>ModelEvent.call</c>).</summary>
    private static JsonObject? WalkEventsData(JsonObject? eventsData, Func<string, string> fn, bool onlyCore)
    {
        if (eventsData is null)
        {
            return null;
        }

        var result = new JsonObject();
        foreach (var (key, value) in eventsData)
        {
            switch (key)
            {
                case "messages" when value is JsonArray:
                    var messages = value.Deserialize<List<ChatMessage>>(EvalLogWriter.Options) ?? [];
                    result[key] = JsonSerializer.SerializeToNode(WalkMessages(messages, fn), EvalLogWriter.Options);
                    break;
                case "calls" when value is JsonArray calls && !onlyCore:
                    result[key] = new JsonArray(calls.Select(call => WalkJson(call, fn, 0)).ToArray());
                    break;
                default:
                    result[key] = value?.DeepClone();
                    break;
            }
        }

        return result;
    }

    private static IReadOnlyList<TranscriptEvent> WalkEvents(IReadOnlyList<TranscriptEvent> events, Func<string, string> fn, bool onlyCore) =>
        events.Select(e => WalkEvent(e, fn, onlyCore)).ToList();

    private static TranscriptEvent WalkEvent(TranscriptEvent e, Func<string, string> fn, bool onlyCore) => e switch
    {
        SampleInitEvent init => init with { Sample = WalkSample(init.Sample, fn), State = WalkJson(init.State, fn, 0) },
        ModelEvent model => model with
        {
            Tools = model.Tools.Select(tool => tool with { Description = fn(tool.Description) }).ToList(),
            Input = WalkMessages(model.Input, fn),
            Output = WalkOutput(model.Output, fn),
            Call = onlyCore ? model.Call : WalkCall(model.Call, fn),
        },
        StateEvent state => state with { Changes = WalkChanges(state.Changes, fn) },
        StoreEvent store => store with { Changes = WalkChanges(store.Changes, fn) },
        SubtaskEvent subtask => subtask with { Events = WalkEvents(subtask.Events, fn, onlyCore) },
        ToolEvent tool => tool with { Arguments = WalkJsonObject(tool.Arguments, fn, 0), Events = WalkEvents(tool.Events, fn, onlyCore) },
        InfoEvent info => info with { Data = WalkJson(info.Data, fn, 0) },
        _ => e,
    };

    private static Sample WalkSample(Sample sample, Func<string, string> fn) =>
        sample.Input.IsText
            ? sample with { Input = fn(sample.Input.Text ?? "") }
            : sample with { Input = WalkMessages(sample.Input.Messages!, fn).ToArray() };

    /// <summary>Port of <c>walk_input</c>: a text input is left as is (only <c>SampleInitEvent</c>'s copy is walked).</summary>
    private static SampleInput WalkInput(SampleInput input, Func<string, string> fn) =>
        input.IsText ? input : WalkMessages(input.Messages!, fn).ToArray();

    private static IReadOnlyList<ChatMessage> WalkMessages(IReadOnlyList<ChatMessage> messages, Func<string, string> fn) =>
        messages.Select(message => WalkMessage(message, fn)).ToList();

    private static ChatMessage WalkMessage(ChatMessage message, Func<string, string> fn)
    {
        var content = message.Content.IsString
            ? MessageContent.FromString(fn(message.Content.Text!))
            : MessageContent.FromItems(message.Content.Items!.Select(item => WalkContent(item, fn)));
        return message switch
        {
            ChatMessageAssistant assistant => assistant with
            {
                Content = content,
                ToolCalls = assistant.ToolCalls?.Select(call => call with { Arguments = WalkJsonObject(call.Arguments, fn, 0) }).ToList(),
            },
            _ => message with { Content = content },
        };
    }

    private static Content WalkContent(Content content, Func<string, string> fn) => content switch
    {
        ContentText text => text with { Text = fn(text.Text) },
        ContentImage image => image with { Image = fn(image.Image) },
        ContentAudio audio => audio with { Audio = fn(audio.Audio) },
        ContentVideo video => video with { Video = fn(video.Video) },
        ContentReasoning reasoning => reasoning with { Reasoning = fn(reasoning.Reasoning) },
        _ => content,
    };

    private static ModelOutput WalkOutput(ModelOutput output, Func<string, string> fn) =>
        output with { Choices = output.Choices.Select(choice => choice with { Message = (ChatMessageAssistant)WalkMessage(choice.Message, fn) }).ToList() };

    private static ModelCall? WalkCall(ModelCall? call, Func<string, string> fn)
    {
        if (call is null)
        {
            return null;
        }

        var walked = ModelCall.Create(WalkJsonObject(call.Request, fn, 0));
        if (call.Error == true)
        {
            walked.SetError(WalkJson(call.Response, fn, 0), call.Time);
        }
        else if (call.Response is not null || call.Time is not null)
        {
            walked.SetResponse(WalkJson(call.Response, fn, 0), call.Time);
        }

        walked.CallRefs = call.CallRefs;
        walked.CallKey = call.CallKey;
        return walked;
    }

    /// <summary>Port of <c>walk_state_json_change</c>: only each change's <c>value</c> is walked.</summary>
    private static JsonElement WalkChanges(JsonElement changes, Func<string, string> fn)
    {
        if (changes.ValueKind != JsonValueKind.Array)
        {
            return changes;
        }

        var array = (JsonArray)JsonNode.Parse(changes.GetRawText())!;
        foreach (var change in array.OfType<JsonObject>())
        {
            if (change.ContainsKey("value"))
            {
                var value = change["value"];
                change.Remove("value");
                change["value"] = WalkJson(value, fn, 0);
            }
        }

        return TranscriptEventJson.ToElement(array);
    }

    private static JsonObject WalkJsonObject(JsonObject value, Func<string, string> fn, int depth)
    {
        var result = new JsonObject();
        foreach (var (key, item) in value)
        {
            result[key] = WalkJson(item, fn, depth + 1);
        }

        return result;
    }

    /// <summary>Port of <c>walk_json_value</c>: strings through <paramref name="fn"/>, containers recursively up to <see cref="MaxJsonValueDepth"/>.</summary>
    private static JsonNode? WalkJson(JsonNode? value, Func<string, string> fn, int depth)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonObject or JsonArray when depth >= MaxJsonValueDepth:
                return JsonValue.Create(JsonValueMaxDepthExceeded);
            case JsonObject obj:
                return WalkJsonObject(obj, fn, depth);
            case JsonArray array:
                return new JsonArray(array.Select(item => WalkJson(item, fn, depth + 1)).ToArray());
            case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                return JsonValue.Create(fn(text));
            case JsonValue scalar when scalar.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.String:
                return JsonValue.Create(fn(element.GetString()!));
            default:
                return value.DeepClone();
        }
    }

    [GeneratedRegex("^data:[^,]*;base64,")]
    private static partial Regex DataUriRegex();

    [GeneratedRegex("attachment://([0-9a-f]{32})")]
    private static partial Regex AttachmentRefRegex();
}
