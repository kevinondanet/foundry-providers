using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Solvers;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>_eval/task/generate.py</c> <c>task_generate</c>: the <see cref="Generate"/> delegate the runner
/// hands to solvers. Calls the model, appends the assistant message, sets <see cref="TaskState.Output"/> and
/// (per <see cref="ToolCallsMode"/>) executes tool calls through <see cref="ToolExecutor"/> in a loop.
/// </summary>
public static class GenerateLoop
{
    /// <summary>
    /// Builds the <see cref="Generate"/> delegate for <paramref name="model"/>. <paramref name="maxToolOutput"/>
    /// stands in for Python's <c>config.max_tool_output</c>, which this port's <see cref="GenerateConfig"/> lacks.
    /// </summary>
    public static Generate Create(Model model, int? maxToolOutput = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        return (state, toolCalls, config, cancellationToken) => RunAsync(model, state, toolCalls, config, maxToolOutput, cancellationToken);
    }

    /// <summary>
    /// Port of the sample-level checks of <c>TaskState.message_limit</c>: raised before a generate when the
    /// state's own limit is reached (the ambient <see cref="Limits"/> is checked separately by the model).
    /// </summary>
    internal static void CheckMessageLimit(TaskState state)
    {
        if (state.MessageLimitNode is not null)
        {
            // the scoped MessageLimit is checked (with its SampleLimitEvent) by Model.GenerateAsync
            return;
        }

        if (state.MessageLimit is { } limit)
        {
            new Limits { MessageLimit = limit }.CheckMessageLimit(state.Messages.Count);
        }
    }

    /// <summary>Port of the <c>TaskState.token_limit</c> check: raised after a generate once the sample's usage exceeds the state's own limit.</summary>
    internal static void CheckTokenLimit(TaskState state)
    {
        if (state.TokenLimitNode is not null)
        {
            // the scoped TokenLimit is checked (with its SampleLimitEvent) by Model.GenerateAsync
            return;
        }

        if (state.TokenLimit is not { } limit)
        {
            return;
        }

        var total = state.TokenUsage;
        if (total > limit)
        {
            var limitStr = LimitExceededException.FormatLimit(limit);
            var message = $"Token limit exceeded. value: {LimitExceededException.FormatLimit(total)}; limit: {limitStr}";
            throw new LimitExceededException("token", limitStr, total, message);
        }
    }

    private static async Task<TaskState> RunAsync(
        Model model,
        TaskState state,
        ToolCallsMode toolCalls,
        GenerateConfig? config,
        int? maxToolOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        // A forced tool choice applies to the first call only, otherwise the model would be forced over and over.
        var toolChoice = state.ToolChoice;
        while (true)
        {
            CheckMessageLimit(state);

            // Snapshots: the model records its input on the transcript, and the state's lists keep mutating.
            var output = await model.GenerateAsync(state.Messages.ToArray(), state.Tools.ToArray(), toolChoice, config, cancellationToken: cancellationToken).ConfigureAwait(false);
            CheckTokenLimit(state);
            state.Output = output;

            var message = output.Message;
            state.Messages.Add(message);
            if (state.Completed)
            {
                return state;
            }

            if (toolCalls == ToolCallsMode.None || message.ToolCalls is not { Count: > 0 })
            {
                return state;
            }

            var result = await ToolExecutor.ExecuteToolsAsync(state.Messages, state.Tools, maxToolOutput, cancellationToken).ConfigureAwait(false);
            state.Messages.AddRange(result.Messages);
            if (result.Output is { } toolOutput)
            {
                state.Output = toolOutput;
            }

            if (state.Completed || toolCalls == ToolCallsMode.Single)
            {
                return state;
            }

            if (toolChoice is ToolFunction)
            {
                toolChoice = ToolChoice.Auto;
            }
        }
    }
}
