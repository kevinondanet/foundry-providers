using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Swe.CopilotCli;

/// <summary>
/// Folds the Copilot CLI's <c>--output-format json</c> lines into the transcript, the part of inspect_swe's
/// <c>_claude_code/_events/live_consumer.py</c> the Claude Code port left out. Two line shapes exist (the 1.0.83
/// probe and Harbor's parser): the namespaced session stream (<c>{type, data, id, timestamp, parentId[, ephemeral]}</c>
/// with <c>assistant.message</c>, <c>tool.execution_start</c>, <c>tool.execution_complete</c>, <c>session.error</c>)
/// and a flat Anthropic-style stream (<c>message</c>, <c>tool_use</c>, <c>tool_result</c>, <c>usage</c>); both end
/// with a <c>result</c> line (<c>sessionId</c>, <c>exitCode</c>, <c>usage</c>). Every line is recorded verbatim as
/// an <c>Info("copilot_cli")</c> event except the streaming-only ephemeral ones (<see cref="IsStreamingLine"/>:
/// per-token deltas, partial tool output, background-task ticks) unless asked for — note that the CLI also flags
/// <c>session.skills_loaded</c> and its other <c>session.*</c> announcements as ephemeral, and those are kept, being
/// the only record of what the plugin offered. Unlike plan 3.4 (and Python's bridge-only tool tracking), a
/// <see cref="ToolEvent"/> is recorded per completed execution so the viewer renders a tool card, carrying the view
/// of the call the bridge returned to the CLI (or, for a call the bridge never saw, the CLI's own record of it); the
/// bridge's <c>ModelEvent</c>s remain the source of truth for the conversation. A completion that reports an
/// <c>error</c> is failed even without <c>success: false</c>. The session id is kept for <c>--resume</c>; unknown
/// shapes are tolerated.
/// </summary>
public sealed class CopilotCliEvents
{
    /// <summary>The <c>source</c> of every info event.</summary>
    public const string Source = "copilot_cli";

    private readonly Transcript _transcript;

    private readonly Func<string, ToolCall?> _bridgedToolCall;

    private readonly Dictionary<string, ToolCall> _cliToolCalls = new(StringComparer.Ordinal);

    private readonly List<ToolEvent> _toolEvents = [];

    private readonly bool _recordStreamingLines;

    public CopilotCliEvents(Transcript transcript, Func<string, ToolCall?>? bridgedToolCall = null, bool recordStreamingLines = false)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        _transcript = transcript;
        _bridgedToolCall = bridgedToolCall ?? (_ => null);
        _recordStreamingLines = recordStreamingLines;
    }

    /// <summary>
    /// An <c>ephemeral: true</c> line that only streams progress: everything ephemeral except the <c>session.*</c>
    /// announcements (skills, tools, MCP servers, warnings), plus <c>session.background_tasks_changed</c>, which the
    /// 1.0.83 probe saw 26-28 times per short run with nothing in it.
    /// </summary>
    public static bool IsStreamingLine(JsonObject line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var ephemeral = line["ephemeral"] is JsonValue e && e.TryGetValue<bool>(out var flag) && flag;
        if (!ephemeral)
        {
            return false;
        }

        var type = Str(line["type"]) ?? "";
        return type == "session.background_tasks_changed" || !type.StartsWith("session.", StringComparison.Ordinal);
    }

    /// <summary>The session id from <c>session.start</c> (events.jsonl shape) or the final <c>result</c> line, whichever came last.</summary>
    public string? SessionId { get; private set; }

    /// <summary>The final <c>result</c> line of the latest run, if one was seen.</summary>
    public JsonObject? Result { get; private set; }

    /// <summary>The <c>exitCode</c> of <see cref="Result"/>.</summary>
    public int? ResultExitCode { get; private set; }

    /// <summary>The message of the latest <c>session.error</c> line (the CLI reports failures on stdout, not stderr).</summary>
    public string? SessionError { get; private set; }

    /// <summary>The tool events recorded so far (one per completed execution).</summary>
    public IReadOnlyList<ToolEvent> ToolEvents => _toolEvents;

    /// <summary>Clears the per-run state (result, error) before the next launch; the session id and tool calls carry over.</summary>
    public void BeginRun()
    {
        Result = null;
        ResultExitCode = null;
        SessionError = null;
    }

    /// <summary>Records <paramref name="raw"/> as an info event (streaming lines only on request) and folds what it carries.</summary>
    public void Fold(JsonNode raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (_recordStreamingLines || raw is not JsonObject candidate || !IsStreamingLine(candidate))
        {
            _transcript.Info(Source, raw);
        }

        if (raw is not JsonObject line || line["type"] is not JsonValue typeValue || !typeValue.TryGetValue<string>(out var type))
        {
            return;
        }

        var data = line["data"] as JsonObject;
        switch (type)
        {
            case "result":
                Result = line;
                ResultExitCode = Int(line["exitCode"]);
                SessionId = Str(line["sessionId"]) ?? SessionId;
                break;
            case "session.start":
                SessionId = Str(data?["sessionId"]) ?? SessionId;
                break;
            case "session.error":
                SessionError = Str(data?["message"]) ?? SessionError;
                break;
            case "assistant.message":
                foreach (var request in (data?["toolRequests"] as JsonArray) ?? [])
                {
                    if (request is JsonObject r && Str(r["toolCallId"]) is { } requestId && Str(r["name"]) is { } requestName)
                    {
                        Remember(requestId, requestName, r["arguments"]);
                    }
                }

                break;
            case "tool.execution_start":
                if (Str(data?["toolCallId"]) is { } startId && Str(data?["toolName"]) is { } startName)
                {
                    Remember(startId, startName, data?["arguments"]);
                }

                break;
            case "tool.execution_complete":
                if (Str(data?["toolCallId"]) is { } completeId)
                {
                    var success = data?["success"] is JsonValue s && s.TryGetValue<bool>(out var ok) ? ok : (bool?)null;
                    var error = Str(data?["error"]) ?? Str((data?["error"] as JsonObject)?["message"]);
                    CompleteToolCall(completeId, Str((data?["result"] as JsonObject)?["content"]) ?? Str(data?["result"]), success, error);
                }

                break;
            case "tool_use":
                if (Str(line["id"]) is { } useId && Str(line["name"]) is { } useName)
                {
                    Remember(useId, useName, line["input"]);
                }

                break;
            case "tool_result":
                if (Str(line["tool_use_id"]) is { } resultId)
                {
                    var isError = line["is_error"] is JsonValue e && e.TryGetValue<bool>(out var err) && err;
                    CompleteToolCall(resultId, FlatContent(line["content"]), isError ? false : null, null);
                }

                break;
        }
    }

    private void Remember(string id, string name, JsonNode? arguments)
    {
        var args = arguments?.DeepClone() as JsonObject ?? [];
        _cliToolCalls[id] = new ToolCall(id, name, args);
    }

    private void CompleteToolCall(string id, string? result, bool? success, string? error)
    {
        var call = _bridgedToolCall(id) ?? _cliToolCalls.GetValueOrDefault(id);
        if (call is null)
        {
            return;
        }

        var failed = success == false || error is not null;
        var toolEvent = new ToolEvent(call.Id, call.Function, call.Arguments, result, error is not null ? new ToolCallError("unknown", error) : null)
        {
            View = ToolCallViews.Default(call).Call,
            Failed = failed ? true : null,
            Completed = DateTimeOffset.UtcNow,
        };
        _toolEvents.Add(_transcript.Record(toolEvent));
    }

    private static string? FlatContent(JsonNode? content) => content switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        JsonArray blocks => string.Join("\n", blocks.Select(b => Str((b as JsonObject)?["text"]) ?? b?.ToJsonString() ?? "")),
        _ => content.ToJsonString(),
    };

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
