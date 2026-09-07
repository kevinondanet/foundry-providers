using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MafChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace InspectAzureAI.Maf;

/// <summary>
/// A Microsoft Agent Framework agent as an Inspect agent: the framework runs its own tool loop while every model
/// call goes through an <see cref="InspectChatClient"/>, so the bridge's state is the resulting conversation. The
/// in-process analogue of Python's <c>agent_bridge()</c>, plus <c>react</c>-style submit and scored attempts.
/// </summary>
public static class AgentFramework
{
    public static AgentDef Agent(MafAgentOptions? options = null)
    {
        var resolved = options ?? new MafAgentOptions();
        if (resolved.Attempts.Attempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolved.Attempts.Attempts, "attempts must be at least 1.");
        }

        if (resolved.RetryRefusals is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolved.RetryRefusals, "retryRefusals cannot be negative.");
        }

        if (resolved.MaxToolIterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), resolved.MaxToolIterations, "maxToolIterations must be at least 1.");
        }

        // the framework resolves a call by name, and the run classifies tools by name too
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in resolved.Tools)
        {
            if (!names.Add(tool.Name))
            {
                throw new ArgumentException($"Duplicate tool name '{tool.Name}'.", nameof(options));
            }
        }

        if (resolved.Submit && names.Contains(resolved.SubmitName))
        {
            throw new ArgumentException($"A tool named '{resolved.SubmitName}' collides with the submit tool; rename it, set SubmitName, or disable Submit.", nameof(options));
        }

        return new AgentDef(resolved.Name, resolved.Description, (state, cancellationToken) => new MafAgentLoop(resolved).ExecuteAsync(state, cancellationToken));
    }
}

internal sealed class MafAgentLoop(MafAgentOptions options)
{
    private string? _submitted;

    /// <summary>The framework iteration in which the submit tool ran; the loop ends after that iteration's last call.</summary>
    private int? _submitIteration;

    /// <summary>Names of the Inspect tools (the framework wraps functions, so the instance cannot be recognised by type).</summary>
    private readonly HashSet<string> _inspectTools = new(StringComparer.Ordinal);

    /// <summary>A limit or termination raised inside a framework-side tool; the framework would fold it into a tool error, so it is re-thrown after the run.</summary>
    private Exception? _fatal;

    public async Task<AgentState> ExecuteAsync(AgentState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var context = SampleContext.Require();
        var model = options.Model ?? context.ActiveModel;
        var bridge = new AgentBridge(state, model, retryRefusals: options.RetryRefusals, approval: options.Approval, cache: options.Cache);
        using var inspectClient = new InspectChatClient(bridge);
        // Our own function-invocation layer rather than the agent's default one: Inspect's limits bound the run, and
        // tool errors are reported to the model for as long as it keeps calling, as the native tool loop does (an
        // unhandled exception in an Inspect tool still fails the sample: see InvokeToolAsync).
        using var client = new FunctionInvokingChatClient(inspectClient)
        {
            MaximumIterationsPerRequest = options.MaxToolIterations,
            MaximumConsecutiveErrorsPerRequest = int.MaxValue,
            IncludeDetailedErrors = true,
        };

        var tools = new List<AITool>(options.Tools);
        if (options.Submit)
        {
            tools.Add(new ToolDefFunction(SubmitTool()));
        }

        _inspectTools.UnionWith(tools.OfType<ToolDefFunction>().Select(tool => tool.Name));

        var instructions = options.Instructions?.Replace("{submit}", options.SubmitName, StringComparison.Ordinal);
        var agent = options.AgentFactory is { } factory
            ? factory(client, tools)
            : new ChatClientAgent(client, instructions, options.Name, options.Description, tools);
        agent = agent.AsBuilder().Use(InvokeToolAsync).Build();
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<MafChatMessage> input = MafConversion.ToMafMessages(state.Messages);
        var attemptCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _submitted = null;
            _submitIteration = null;
            var response = await agent.RunAsync(input, session, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (_fatal is { } fatal)
            {
                ExceptionDispatchInfo.Capture(fatal).Throw();
            }

            var uninvoked = UninvokedCalls(response);
            if (uninvoked.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Agent Framework returned {uninvoked.Count} tool call(s) it did not invoke ({string.Join(", ", uninvoked)}); "
                    + "the conversation cannot continue with unanswered calls (an approval-gated function, or a custom agent that bypasses function invocation).");
            }

            AppendUntrackedToolResults(bridge.State, response);

            if (_submitted is { } answer)
            {
                bridge.State.Output = bridge.State.Output with { Completion = answer };
                attemptCount++;
                if (attemptCount >= options.Attempts.Attempts)
                {
                    break;
                }

                var scores = await Agents.ScoreAsync(bridge.State, cancellationToken).ConfigureAwait(false);
                if (scores.Count == 0)
                {
                    throw new InvalidOperationException("The task scorer returned no scores for the submission.");
                }

                var scoreValue = options.Attempts.ScoreValue ?? ValueToFloat.Default;
                if (scoreValue(scores[0].Value) == 1.0)
                {
                    break;
                }

                var incorrect = options.Attempts.IncorrectMessageFn is { } incorrectFn
                    ? await incorrectFn(bridge.State, scores, cancellationToken).ConfigureAwait(false)
                    : options.Attempts.IncorrectMessage;
                input = [new MafChatMessage(ChatRole.User, incorrect)];
                continue;
            }

            if (!options.Submit)
            {
                break;
            }

            input = [new MafChatMessage(ChatRole.User, options.ContinueMessage.Replace("{submit}", options.SubmitName, StringComparison.Ordinal))];
        }

        return bridge.State;
    }

    /// <summary>
    /// Function-invocation middleware. For a framework-side function it records the transcript tool event (an
    /// Inspect tool records its own through the executor) and captures a limit or termination the function
    /// raised, so it can be re-thrown from the run rather than becoming a tool error. For every function it ends
    /// the framework's loop once the iteration that ran the submit tool has finished its last call: the framework
    /// would otherwise call the model again with the submission, and ending on the submit call itself could skip
    /// its siblings.
    /// </summary>
    private async ValueTask<object?> InvokeToolAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var transcript = _inspectTools.Contains(context.Function.Name) || !options.RecordToolEvents ? null : SampleContext.Current?.Transcript;
        using var span = transcript?.Span(context.Function.Name, "tool");
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        object? result;
        try
        {
            result = await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is LimitExceededException or TerminateSampleException)
        {
            _fatal = ex;
            context.Terminate = true;
            RecordToolEvent(transcript, context, null, ex, failed: false, started, stopwatch.Elapsed);
            return ErrorResult(context, ex);
        }
        catch (Exception ex)
        {
            RecordToolEvent(transcript, context, null, ex, failed: true, started, stopwatch.Elapsed);
            if (_inspectTools.Contains(context.Function.Name))
            {
                // the executor already reported tool errors as results; what reaches here is unhandled and fails
                // the sample, as it does in the native loop (the framework would fold it into a tool error)
                _fatal = ex;
                context.Terminate = true;
                return ErrorResult(context, ex);
            }

            // the framework ignores Terminate on an exception, so a failure that must end the iteration (a
            // sibling of the submission) is returned as its error text instead
            TerminateAfterSubmission(context);
            if (context.Terminate)
            {
                return ErrorResult(context, ex);
            }

            throw;
        }

        TerminateAfterSubmission(context);
        RecordToolEvent(transcript, context, result, null, failed: false, started, stopwatch.Elapsed);
        return result;
    }

    /// <summary>A failure returned in place of a result so the framework ends the iteration; the tool message keeps the error object (see <see cref="ToolDefFunction"/>).</summary>
    private static ChatMessageTool ErrorResult(FunctionInvocationContext context, Exception ex) =>
        new("", toolCallId: context.CallContent.CallId, function: context.Function.Name, error: new ToolCallError("unknown", ex.Message));

    private void TerminateAfterSubmission(FunctionInvocationContext context)
    {
        if (!options.Submit)
        {
            return;
        }

        if (_submitted is not null && context.Function.Name == options.SubmitName)
        {
            _submitIteration = context.Iteration;
        }

        if (_submitIteration == context.Iteration && context.FunctionCallIndex == context.FunctionCount - 1)
        {
            context.Terminate = true;
        }
    }

    private static void RecordToolEvent(Transcript? transcript, FunctionInvocationContext context, object? result, Exception? failure, bool failed, DateTimeOffset started, TimeSpan elapsed)
    {
        if (transcript is null)
        {
            return;
        }

        var call = context.CallContent;
        var error = failure is null ? null : new ToolCallError("unknown", failure.Message);
        transcript.Add(new ToolEvent(call.CallId, call.Name, MafConversion.ToJsonObject(call.Arguments), failure is null ? MafConversion.ResultText(result) : null, error, null, elapsed)
        {
            Timestamp = started,
            Completed = DateTimeOffset.UtcNow,
            Failed = failed ? true : null,
        });
    }

    /// <summary>Tool calls in the framework's response that have no result: the framework stopped without invoking them.</summary>
    private static List<string> UninvokedCalls(AgentResponse response)
    {
        var answered = response.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId).ToHashSet(StringComparer.Ordinal);
        return response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.CallId).Where(id => !answered.Contains(id)).ToList();
    }

    /// <summary>The submit tool: records the answer for the attempts loop and returns it as the tool result.</summary>
    private ToolDef SubmitTool() => new(
        options.SubmitName,
        options.SubmitDescription,
        new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["answer"] = ToolParam.Of("string", "Submitted answer") },
            Required = ["answer"],
        },
        (arguments, _) =>
        {
            var answer = arguments["answer"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : arguments["answer"]?.ToJsonString() ?? "";
            _submitted = answer;
            return Task.FromResult<ToolResult>(answer);
        });

    /// <summary>
    /// The bridge learns a tool result from the next model request; results of the final round (a submission, or
    /// a terminated loop) never reach a request, so they are appended from the framework's response.
    /// </summary>
    private static void AppendUntrackedToolResults(AgentState state, AgentResponse response)
    {
        var known = new HashSet<string>(state.Messages.OfType<ChatMessageTool>().Select(message => message.ToolCallId).OfType<string>(), StringComparer.Ordinal);
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var call in state.Messages.OfType<ChatMessageAssistant>().SelectMany(message => message.ToolCalls ?? []))
        {
            callNames[call.Id] = call.Function;
        }

        foreach (var call in response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>())
        {
            callNames[call.CallId] = call.Name;
        }

        foreach (var result in response.Messages.Where(message => message.Role == ChatRole.Tool).SelectMany(message => message.Contents).OfType<FunctionResultContent>())
        {
            if (known.Add(result.CallId))
            {
                state.Messages.Add(MafConversion.ToToolResult(result, callNames.GetValueOrDefault(result.CallId)));
            }
        }
    }
}
