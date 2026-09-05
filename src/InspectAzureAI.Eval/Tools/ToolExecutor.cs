using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tools;

/// <summary>Port of <c>model/_call_tools.py</c> <c>ExecuteToolsResult</c>.</summary>
public sealed record ExecuteToolsResult(IReadOnlyList<ChatMessage> Messages, ModelOutput? Output = null);

/// <summary>
/// Port of <c>model/_call_tools.py</c> <c>execute_tools</c>: runs the tool calls of the last assistant
/// message in ordered stages (consecutive parallel-safe calls concurrently, a serial call as a barrier),
/// maps tool failures to <see cref="ToolCallError"/>s, truncates text output and records a
/// <see cref="ToolEvent"/> per call on the current transcript.
/// </summary>
public static class ToolExecutor
{
    public const int DefaultMaxOutput = 16 * 1024;

    /// <summary>
    /// Ports execute_tools: acts only when messages[^1] is a ChatMessageAssistant with tool calls. Returns the
    /// ChatMessageTool messages (one per call, in call order). <paramref name="approval"/> (Python's <c>approval</c>
    /// parameter) temporarily replaces the ambient approval policies for the duration of the call; null or empty
    /// leaves them as they are.
    /// </summary>
    public static async Task<ExecuteToolsResult> ExecuteToolsAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDef> tools,
        int? maxOutput = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ApprovalPolicy>? approval = null)
    {
        if (messages.Count == 0 || messages[^1] is not ChatMessageAssistant { ToolCalls: { Count: > 0 } toolCalls })
        {
            return new ExecuteToolsResult([]);
        }

        using var approvalScope = ToolApproval.BeginIfAny(approval);

        var results = new ChatMessageTool[toolCalls.Count];
        var extras = new IReadOnlyList<ChatMessage>?[toolCalls.Count];
        ModelOutput? resultOutput = null;
        foreach (var stage in Stages(toolCalls, tools))
        {
            var tasks = stage.Select(index => RunOneAsync(toolCalls[index], tools, messages, maxOutput, cancellationToken)).ToArray();
            var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
            for (var i = 0; i < stage.Count; i++)
            {
                results[stage[i]] = outcomes[i].Message;
                extras[stage[i]] = outcomes[i].Extra;
                // Like Python, the last handoff output in declared order wins.
                resultOutput = outcomes[i].Output ?? resultOutput;
            }

            // Anything that is not a ToolError (or one of the mapped system errors) is fatal to the sample,
            // as in Python; the first failure in declared order wins once the stage has settled.
            var fatal = outcomes.Select(o => o.Fatal).FirstOrDefault(e => e is not null);
            if (fatal is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fatal).Throw();
            }
        }

        var all = new List<ChatMessage>(results.Length);
        for (var i = 0; i < results.Length; i++)
        {
            all.Add(results[i]);
            if (extras[i] is { } extra)
            {
                all.AddRange(extra);
            }
        }

        return new ExecuteToolsResult(all, resultOutput);
    }

    /// <summary>Port of the stage partitioning: unknown tools are serial.</summary>
    internal static List<List<int>> Stages(IReadOnlyList<ToolCall> calls, IReadOnlyList<ToolDef> tools)
    {
        var flags = calls.Select(c => tools.FirstOrDefault(t => t.Name == c.Function)?.Parallel ?? false).ToArray();
        var stages = new List<List<int>>();
        var i = 0;
        while (i < flags.Length)
        {
            if (flags[i])
            {
                var stage = new List<int>();
                while (i < flags.Length && flags[i])
                {
                    stage.Add(i++);
                }

                stages.Add(stage);
            }
            else
            {
                stages.Add([i++]);
            }
        }

        return stages;
    }

    /// <summary><paramref name="Extra"/> and <paramref name="Output"/> are a handoff's messages (appended after the tool message) and the agent's output.</summary>
    private sealed record Outcome(ChatMessageTool Message, Exception? Fatal, IReadOnlyList<ChatMessage>? Extra = null, ModelOutput? Output = null);

    private static async Task<Outcome> RunOneAsync(ToolCall call, IReadOnlyList<ToolDef> tools, IReadOnlyList<ChatMessage> conversation, int? maxOutput, CancellationToken cancellationToken)
    {
        var transcript = SampleContext.Current?.Transcript;
        // Python encloses a handoff's tool span in a "handoff" span named after the agent.
        using var handoffSpan = tools.FirstOrDefault(t => t.Name == call.Function)?.Handoff is { } handoffAgent ? transcript?.Span(handoffAgent.Agent.Name, "handoff") : null;
        using var span = transcript?.Span(call.Function, "tool");
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var tool = tools.FirstOrDefault(t => t.Name == call.Function);
        ToolResult result = ToolResult.Empty;
        ToolCallError? error = null;
        Exception? fatal = null;
        IReadOnlyList<ChatMessage>? extra = null;
        ModelOutput? output = null;
        string? agent = null;
        try
        {
            if (call.ParseError is not null)
            {
                throw new ToolParsingError(call.ParseError);
            }

            if (tool is null)
            {
                error = new ToolCallError("unknown", $"Tool {call.Function} not found");
            }
            else
            {
                // Python's call_tool applies the approver before validating the arguments; a "modify" decision
                // rebinds only the call the tool receives, the tool message and event keep the model's own call.
                var assistantText = conversation[^1] is ChatMessageAssistant assistant ? assistant.Text : "";
                var (approved, approval) = await ToolApproval.ApplyAsync(assistantText, call, tool.Viewer, conversation, cancellationToken).ConfigureAwait(false);
                if (!approved)
                {
                    throw approval?.Decision == ApprovalDecision.Terminate
                        ? new TerminateSampleException("Tool call approver requested termination.")
                        : new ToolApprovalError(approval?.Explanation);
                }

                var executeCall = approval?.Modified ?? call;
                foreach (var required in tool.Parameters.Required)
                {
                    if (!executeCall.Arguments.ContainsKey(required))
                    {
                        throw new ToolParsingError($"Required parameter {required} not provided to tool call.");
                    }
                }

                if (tool.Handoff is { } handoff)
                {
                    var handoffResult = await Agents.Agents.ExecuteHandoffAsync(handoff, executeCall, conversation, cancellationToken).ConfigureAwait(false);
                    result = handoffResult.Result;
                    extra = handoffResult.Messages;
                    output = handoffResult.Output;
                    agent = handoffResult.AgentName;
                }
                else
                {
                    result = await tool.Execute(executeCall.Arguments, cancellationToken).ConfigureAwait(false) ?? ToolResult.Empty;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SandboxTimeoutException ex)
        {
            error = new ToolCallError("timeout", "Command timed out before completing.");
            if (ex.TruncatedOutput.Length > 0)
            {
                result = ex.TruncatedOutput;
            }
        }
        catch (TimeoutException)
        {
            error = new ToolCallError("timeout", "Command timed out before completing.");
        }
        catch (SandboxUnavailableException ex)
        {
            error = new ToolCallError("sandbox_unavailable", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            error = new ToolCallError("permission", WithPeriod(ex.Message));
        }
        catch (FileNotFoundException ex)
        {
            error = new ToolCallError("file_not_found", ex.FileName is { } file ? $"File '{file}' was not found." : ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            error = new ToolCallError("file_not_found", ex.Message);
        }
        catch (IOException ex) when (ex.Message.Contains("is a directory", StringComparison.OrdinalIgnoreCase))
        {
            error = new ToolCallError("is_a_directory", WithPeriod(ex.Message));
        }
        catch (DecoderFallbackException ex)
        {
            error = new ToolCallError("unicode_decode", $"Error decoding bytes to utf-8: {ex.Message}");
        }
        catch (OutputLimitExceededException ex)
        {
            error = new ToolCallError("limit", $"The tool exceeded its output limit of {ex.LimitDescription}.");
            result = ex.TruncatedOutput ?? "";
        }
        catch (LimitExceededException ex)
        {
            error = new ToolCallError("limit", $"The tool exceeded its {ex.Type} limit of {ex.LimitStr}.");
        }
        catch (ToolParsingError ex)
        {
            error = new ToolCallError("parsing", ex.Message);
        }
        catch (ToolApprovalError ex)
        {
            error = new ToolCallError("approval", ex.Message);
        }
        catch (ToolError ex)
        {
            error = new ToolCallError("unknown", ex.Message);
        }
        catch (Exception ex)
        {
            fatal = ex;
        }

        MessageContent content;
        ToolTruncation? truncation = null;
        string eventResult;
        if (result.Contents is { } contents)
        {
            content = MessageContent.FromItems(contents);
            eventResult = result.AsText();
        }
        else
        {
            var text = result.Text ?? "";
            var truncated = TruncateToolOutput(call.Function, text, tool?.MaxOutput ?? maxOutput ?? DefaultMaxOutput);
            if (truncated is not null)
            {
                text = truncated.Output;
                truncation = new ToolTruncation(truncated.RawBytes, truncated.TruncatedBytes);
            }

            content = text;
            // Python builds the event from `content` after truncation, so the log carries what the model saw.
            eventResult = text;
        }

        var message = new ChatMessageTool(content, toolCallId: call.Id, function: call.Function, error: error);
        // Python creates the event when the call starts and _set_result stamps completed/message_id on every call;
        // failed marks only the unhandled-exception path (a ToolCallError is not a failure).
        transcript?.Add(new ToolEvent(call.Id, call.Function, call.Arguments, eventResult, error, truncation, stopwatch.Elapsed)
        {
            Timestamp = started,
            Completed = DateTimeOffset.UtcNow,
            Agent = agent,
            Failed = fatal is not null ? true : null,
            MessageId = message.Id,
        });
        return new Outcome(message, fatal, extra, output);
    }

    internal sealed record TruncatedToolOutput(string Output, int RawBytes, int TruncatedBytes);

    /// <summary>
    /// Port of <c>truncate_tool_output</c>: keeps the tail of the output within <paramref name="maxOutput"/>
    /// UTF-8 bytes (0 or negative disables truncation) and wraps it in Python's exact template.
    /// </summary>
    internal static TruncatedToolOutput? TruncateToolOutput(string toolName, string output, int maxOutput)
    {
        if (output.Length == 0 || maxOutput <= 0)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(output);
        if (bytes.Length <= maxOutput)
        {
            return null;
        }

        var start = bytes.Length - maxOutput;
        while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
        {
            start++;
        }

        var tail = Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        var wrapped = "\nThe output of your call to " + toolName + " was too long to be displayed.\n"
            + "Here is a truncated version:\n"
            + "<START_TOOL_OUTPUT>\n"
            + tail + "\n"
            + "<END_TOOL_OUTPUT>\n";
        return new TruncatedToolOutput(wrapped, bytes.Length, maxOutput);
    }

    private static string WithPeriod(string message) => message.EndsWith('.') ? message : message + ".";
}
