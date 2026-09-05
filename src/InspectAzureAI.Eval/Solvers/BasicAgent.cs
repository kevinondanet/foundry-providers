using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model.Compaction;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Solvers;

/// <summary>Port of <c>solver/_basic_agent.py</c> <c>basic_agent</c>: the ReAct tool loop with a <c>submit</c> tool and scored attempts.</summary>
public static partial class Solvers
{
    /// <summary>Port of <c>DEFAULT_SYSTEM_MESSAGE</c>; <c>{submit}</c> is the submit tool's name.</summary>
    public const string BasicAgentSystemMessage =
        "You are a helpful assistant attempting to submit the correct answer. You have several functions available to help with finding the answer. "
        + "Each message may perform one function call. You will see the result of the function right after sending the message. "
        + "If you need to perform multiple actions, you can always send more messages with subsequent function calls. "
        + "Do some reasoning before your actions, describing what function calls you are going to use and how they fit into your plan.\n\n"
        + "When you have completed the task and have an answer, call the {submit}() function to report it.";

    /// <summary>Port of <c>DEFAULT_INCORRECT_MESSAGE</c>.</summary>
    public const string BasicAgentIncorrectMessage = "Your submission was incorrect. Please proceed and attempt to find the correct answer.";

    /// <summary>Port of <c>DEFAULT_CONTINUE_MESSAGE</c>.</summary>
    public const string BasicAgentContinueMessage =
        "Please proceed to the next step using your best judgement. If you believe you have completed the task, please call the `submit()` tool with your final answer.";

    /// <summary>Port of <c>DEFAULT_SUBMIT_NAME</c>.</summary>
    public const string BasicAgentSubmitName = "submit";

    /// <summary>Port of <c>DEFAULT_SUBMIT_DESCRIPTION</c>.</summary>
    public const string BasicAgentSubmitDescription = "Submit an answer for evaluation.";

    /// <summary>
    /// Port of <c>basic_agent()</c>: a chain of <paramref name="init"/> (default: the ReAct system message),
    /// the tools, the submit tool and the agent loop. The loop generates with the active model, executes tool
    /// calls, urges the model on with <paramref name="continueMessage"/> when it makes none, and on a
    /// submission replaces the output completion with the answer; with <paramref name="maxAttempts"/> &gt; 1
    /// the submission is scored through <see cref="SampleContext.Scorer"/> and an incorrect one gets
    /// <paramref name="incorrectMessage"/>. The final accepted submission also marks the state completed.
    /// Message and token limits are those of the state (a default message limit of 50 applies when neither
    /// is set); a <see cref="LimitExceededException"/> propagates to the runner like Python's.
    /// With <paramref name="compaction"/> (see <see cref="Compaction.Hook"/>) the loop compacts its input when
    /// the conversation nears the context window and, after a <c>model_length</c> stop, recovers by forced
    /// compaction before giving up, as the Python react agent does.
    /// </summary>
    public static Solver BasicAgent(
        Solver? init = null,
        IReadOnlyList<ToolDef>? tools = null,
        int? messageLimit = null,
        int? tokenLimit = null,
        int maxAttempts = 1,
        string submitName = BasicAgentSubmitName,
        string submitDescription = BasicAgentSubmitDescription,
        string? incorrectMessage = null,
        string? continueMessage = null,
        Func<ScoreValue, double>? scoreValue = null,
        int? maxToolOutput = null,
        bool submitAppend = false,
        CompactionHook? compaction = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(submitName);
        ArgumentNullException.ThrowIfNull(submitDescription);
        var incorrect = incorrectMessage ?? BasicAgentIncorrectMessage;
        var proceed = continueMessage ?? BasicAgentContinueMessage;
        var scoreValueFn = scoreValue ?? DefaultScoreValue;
        init ??= SystemMessage(BasicAgentSystemMessage, new Dictionary<string, object?>(StringComparer.Ordinal) { ["submit"] = submitName });

        // max_output=0: the submitted answer becomes the completion, so truncating it would score a truncation notice.
        var submit = new ToolDef(
            submitName,
            submitDescription,
            new ToolParams
            {
                Properties = new Dictionary<string, ToolParam> { ["answer"] = ToolParam.Of("string", "Submitted answer") },
                Required = ["answer"],
            },
            (arguments, _) => Task.FromResult<ToolResult>(arguments["answer"] is JsonValue value && value.TryGetValue<string>(out var answer) ? answer : arguments["answer"]?.ToJsonString() ?? ""))
        { MaxOutput = 0 };

        Solver submitTool = (state, _, _) =>
        {
            state.Tools.Add(submit);
            return Task.FromResult(state);
        };

        async Task<TaskState> LoopAsync(TaskState state, Generate generate, CancellationToken cancellationToken)
        {
            // Prefer the parameter, then the task's limit; with neither limit at all, 50 messages keeps a
            // never-submitting model from running forever.
            state.MessageLimit = messageLimit ?? state.MessageLimit;
            if (state.MessageLimit is null && tokenLimit is null)
            {
                state.MessageLimit = 50;
            }

            var context = SampleContext.Require();
            var model = context.ActiveModel;
            var compact = compaction?.Invoke(state.Messages.ToArray(), state.Tools.Select(t => t.ToInfo()).ToArray(), model);
            var loopStartTokens = state.TokenUsage;
            var attempts = 0;
            while (!state.Completed)
            {
                GenerateLoop.CheckMessageLimit(state);
                IReadOnlyList<ChatMessage> input = state.Messages.ToArray();
                if (compact is not null)
                {
                    var compacted = await compact.CompactInputAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
                    input = compacted.Input;
                    if (compacted.Message is { } supplemental)
                    {
                        state.Messages.Add(supplemental);
                    }
                }

                var output = await model.GenerateAsync(input, state.Tools.ToArray(), cancellationToken: cancellationToken).ConfigureAwait(false);
                GenerateLoop.CheckTokenLimit(state);
                CheckLoopTokenLimit(tokenLimit, state.TokenUsage - loopStartTokens);
                state.Output = output;
                state.Messages.Add(output.Message);
                if (compact is not null)
                {
                    await compact.RecordOutputAsync(input, output, cancellationToken).ConfigureAwait(false);
                }

                if (output.StopReason == StopReason.ModelLength)
                {
                    if (compact is not null
                        && await Compaction.TryRecoverOverflowAsync(compact, state.Messages.Take(state.Messages.Count - 1).ToArray(), cancellationToken).ConfigureAwait(false) is { } recovered)
                    {
                        state.Messages = recovered.ToList();
                        continue;
                    }

                    context.Transcript.Add(new InfoEvent(null, JsonValue.Create("Agent terminated: model context window exceeded")));
                    break;
                }

                if (output.Message.ToolCalls is not { Count: > 0 })
                {
                    state.Messages.Add(new ChatMessageUser(proceed));
                    continue;
                }

                var results = await ToolExecutor.ExecuteToolsAsync([output.Message], state.Tools, maxToolOutput, cancellationToken).ConfigureAwait(false);
                state.Messages.AddRange(results.Messages);

                var submission = results.Messages.OfType<ChatMessageTool>().FirstOrDefault(m => m.Function == submitName)?.Text;
                if (submission is null)
                {
                    continue;
                }

                state.Output = state.Output with
                {
                    Completion = submitAppend ? $"{state.Output.Completion}\n\n{submission}".Trim() : submission,
                };

                attempts++;
                if (attempts >= maxAttempts)
                {
                    state.Completed = true;
                    break;
                }

                var scores = await ScoreAsync(context, state).ConfigureAwait(false);
                if (scoreValueFn(scores[0].Value) == 1.0)
                {
                    state.Completed = true;
                    break;
                }

                state.Messages.Add(new ChatMessageUser(incorrect));
            }

            return state;
        }

        return Chain(init, UseTools(tools ?? [], append: true), submitTool, LoopAsync);
    }

    /// <summary>Port of <c>score(state)</c>: the task scorers must be available, and at least one score must come back.</summary>
    private static async Task<IReadOnlyList<Score>> ScoreAsync(SampleContext context, TaskState state)
    {
        var scorer = context.Scorer
            ?? throw new InvalidOperationException("The score() function can only be called while executing a task with a scorer.");
        var scores = await scorer(state).ConfigureAwait(false);
        return scores.Count > 0 ? scores : throw new InvalidOperationException("The task scorer returned no scores for the submission.");
    }

    /// <summary>Port of the loop-scoped <c>token_limit(...)</c> context: counts only the tokens used since the loop started.</summary>
    private static void CheckLoopTokenLimit(int? tokenLimit, int used)
    {
        if (tokenLimit is { } limit && used > limit)
        {
            var limitStr = LimitExceededException.FormatLimit(limit);
            var message = $"Token limit exceeded. value: {LimitExceededException.FormatLimit(used)}; limit: {limitStr}";
            throw new LimitExceededException("token", limitStr, used, message);
        }
    }

    /// <summary>Port of the <c>score_value</c> default, <c>value_to_float()</c> with the standard sentinels.</summary>
    internal static double DefaultScoreValue(ScoreValue value) => ValueToFloat.Default(value);
}
