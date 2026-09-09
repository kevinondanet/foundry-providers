using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Agents;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Port of <c>agent/_react.py</c> <c>react</c> and <c>react_no_submit</c>.</summary>
public static partial class Agents
{
    /// <summary>Port of the default submit tool's name.</summary>
    public const string DefaultSubmitName = "submit";

    /// <summary>Port of the default submit tool's description.</summary>
    public const string DefaultSubmitDescription = "Submit an answer for evaluation.";

    /// <summary>The name a react agent carries when none is given (Python's registry name).</summary>
    public const string ReactName = "react";

    /// <summary>
    /// Port of <c>react(...)</c>: the extensible ReAct agent. The agent runs a tool loop until the model submits
    /// an answer with the submit tool (or, with <see cref="AgentSubmit.Disabled"/>, until it makes no tool call).
    /// </summary>
    /// <param name="name">Agent name (needed with <see cref="Handoff"/> / <see cref="AsTool"/>); defaults to "react".</param>
    /// <param name="description">Agent description (needed with <see cref="Handoff"/> / <see cref="AsTool"/>).</param>
    /// <param name="prompt">
    /// System prompt pieces; null is Python's default <see cref="AgentPrompt"/>, <see cref="AgentPrompt.None"/>
    /// is Python's <c>prompt=None</c> (no system message) and a string is Python's <c>prompt="instructions"</c>.
    /// </param>
    /// <param name="tools">
    /// Tools and tool sources available to the agent (Python's <c>Sequence[Tool | ToolDef | ToolSource]</c>; a plain
    /// tool list converts covariantly): <see cref="ToolDef"/>s, handoff tools, and <see cref="IToolSource"/>s such as
    /// <see cref="Mcp.McpTools"/> or an <see cref="McpServer"/>. Sources are resolved on every turn and the MCP
    /// servers behind them stay connected for the whole loop (Python's <c>mcp_connection(tools)</c>).
    /// </param>
    /// <param name="model">Model to generate with (defaults to the sample's active model).</param>
    /// <param name="modelAgent">Python's <c>model=Agent</c>: a generation agent used in place of <paramref name="model"/>; compaction is then that agent's business.</param>
    /// <param name="attempts">Multiple scored attempts (defaults to one, unscored).</param>
    /// <param name="submit">Submit tool configuration; null is the default tool, <see cref="AgentSubmit.Disabled"/> is Python's <c>submit=False</c>.</param>
    /// <param name="onContinue">Python's <c>on_continue: str</c>: the message played back when the model makes no tool call (<c>{submit}</c> is the submit tool's name).</param>
    /// <param name="onContinueFn">Python's <c>on_continue: AgentContinue</c>: called on every iteration to decide whether and how to continue.</param>
    /// <param name="retryRefusals">How many times a <c>content_filter</c> stop is retried within one generate (the refusal is then dropped).</param>
    /// <param name="compaction">Python's <c>compaction</c> strategy, as a factory for the compaction seam (see <see cref="CreateAgentCompaction"/>).</param>
    /// <param name="truncation">
    /// Python's <c>truncation</c>: null is "disabled"; <see cref="MessageFilters.TrimMessages"/> is "auto"; any
    /// other filter is applied to the conversation on a context window overflow.
    /// </param>
    /// <param name="approval">Python's <c>approval</c>: approval policies for tool calls within this agent, temporarily replacing any active policies for the duration of each tool execution.</param>
    /// <returns>The ReAct agent.</returns>
    public static AgentDef React(
        string? name = null,
        string? description = null,
        AgentPrompt? prompt = null,
        IReadOnlyList<IToolSource>? tools = null,
        Model? model = null,
        AgentModel? modelAgent = null,
        AgentAttempts? attempts = null,
        AgentSubmit? submit = null,
        string? onContinue = null,
        AgentContinue? onContinueFn = null,
        int? retryRefusals = null,
        CreateAgentCompaction? compaction = null,
        MessageFilter? truncation = null,
        IReadOnlyList<ApprovalPolicy>? approval = null)
    {
        if (model is not null && modelAgent is not null)
        {
            throw new ArgumentException("Pass either model or modelAgent, not both.", nameof(modelAgent));
        }

        if (onContinue is not null && onContinueFn is not null)
        {
            throw new ArgumentException("Pass either onContinue or onContinueFn, not both.", nameof(onContinueFn));
        }

        if (retryRefusals is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryRefusals), retryRefusals, "retryRefusals must be non-negative.");
        }

        var resolvedPrompt = prompt ?? AgentPrompt.Default;
        var resolvedSubmit = submit ?? AgentSubmit.Default;
        var resolvedAttempts = attempts ?? new AgentAttempts();
        if (resolvedAttempts.Attempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempts), resolvedAttempts.Attempts, "attempts must be at least 1.");
        }

        if (!resolvedSubmit.Enabled && onContinue is not null)
        {
            throw new ArgumentException(
                "Passing a string to on_continue with no submit tool is not permitted, "
                + "because in this case the agent will always terminate when no tool calls are made.",
                nameof(onContinue));
        }

        var agentTools = (tools ?? []).ToList();
        ToolDef? submitTool = null;
        if (resolvedSubmit.Enabled)
        {
            submitTool = ResolveSubmitTool(resolvedSubmit);
            agentTools.Add(submitTool);
        }

        // Python's has_handoff looks at the direct entries only; tool sources are not resolved for the prompt.
        var systemMessage = PromptToSystemMessage(resolvedPrompt, agentTools.OfType<ToolDef>().ToArray(), submitTool?.Name);
        var loop = new ReactLoop(agentTools, systemMessage, model, modelAgent, resolvedAttempts, resolvedSubmit, submitTool, onContinue, onContinueFn, retryRefusals, compaction, truncation, approval);
        return new AgentDef(name ?? ReactName, description ?? "", loop.ExecuteAsync);
    }

    /// <summary>
    /// Port of <c>_prompt_to_system_message</c>: instructions, the handoff prompt (only when a handoff tool is
    /// present) and the assistant prompt, joined by blank lines. The submit prompt is appended to the assistant
    /// prompt unless that already mentions <c>{submit}</c>; <c>{submit}</c> is replaced by the submit tool's name
    /// ("submit" when there is none). A <see cref="AgentPrompt.None"/> prompt yields no message.
    /// </summary>
    public static ChatMessageSystem? PromptToSystemMessage(AgentPrompt? prompt, IReadOnlyList<ToolDef> tools, string? submitTool)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (prompt is null || ReferenceEquals(prompt, AgentPrompt.None))
        {
            return null;
        }

        var lines = new List<string>();
        if (!string.IsNullOrEmpty(prompt.Instructions))
        {
            lines.Add(prompt.Instructions);
        }

        if (!string.IsNullOrEmpty(prompt.HandoffPrompt) && HasHandoff(tools))
        {
            lines.Add(prompt.HandoffPrompt);
        }

        if (!string.IsNullOrEmpty(prompt.AssistantPrompt))
        {
            string assistantPrompt;
            if (submitTool is not null && !prompt.AssistantPrompt.Contains("{submit}", StringComparison.Ordinal) && !string.IsNullOrEmpty(prompt.SubmitPrompt))
            {
                assistantPrompt = $"{prompt.AssistantPrompt}\n{prompt.SubmitPrompt.Replace("{submit}", submitTool, StringComparison.Ordinal)}";
            }
            else
            {
                assistantPrompt = prompt.AssistantPrompt.Replace("{submit}", submitTool ?? DefaultSubmitName, StringComparison.Ordinal);
            }

            lines.Add(assistantPrompt);
        }

        return new ChatMessageSystem(string.Join("\n\n", lines));
    }

    /// <summary>
    /// Port of <c>_remove_submit_tool</c>: drops the submit tool's result messages and its calls from assistant
    /// messages; when a call is removed the last reasoning item is removed too (some providers reject the
    /// reasoning that led to a call that is no longer there).
    /// </summary>
    public static List<ChatMessage> RemoveSubmitTool(IReadOnlyList<ChatMessage> messages, string submitName)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(submitName);
        var filtered = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (message is ChatMessageTool tool && tool.Function == submitName)
            {
                continue;
            }

            if (message is ChatMessageAssistant { ToolCalls: { Count: > 0 } toolCalls } assistant)
            {
                var remaining = toolCalls.Where(call => call.Function != submitName).ToList();
                if (remaining.Count < toolCalls.Count)
                {
                    var content = assistant.Content;
                    if (!content.IsString)
                    {
                        var items = content.Items!.ToList();
                        var lastReasoning = items.FindLastIndex(item => item is ContentReasoning);
                        if (lastReasoning >= 0)
                        {
                            items.RemoveAt(lastReasoning);
                        }

                        content = MessageContent.FromItems(items);
                    }

                    filtered.Add(assistant with { ToolCalls = remaining, Content = content });
                    continue;
                }
            }

            filtered.Add(message);
        }

        return filtered;
    }

    /// <summary>Port of the submit tool resolution: the caller's tool (copied) or the default one, with name/description overrides and a default <c>max_output</c> of 0.</summary>
    private static ToolDef ResolveSubmitTool(AgentSubmit submit)
    {
        var tool = submit.Tool ?? DefaultSubmitTool();
        // The submit result becomes the completion, so truncating it would score a truncation notice in place of
        // the model's answer; an explicit max_output on a caller's submit tool is their call.
        return tool with
        {
            Name = submit.Name ?? tool.Name,
            Description = submit.Description ?? tool.Description,
            MaxOutput = tool.MaxOutput ?? 0,
        };
    }

    /// <summary>Port of <c>default_submit_tool</c>: <c>submit(answer: str)</c> returning the answer.</summary>
    private static ToolDef DefaultSubmitTool() => new(
        DefaultSubmitName,
        DefaultSubmitDescription,
        new ToolParams
        {
            Properties = new Dictionary<string, ToolParam> { ["answer"] = ToolParam.Of("string", "Submitted answer") },
            Required = ["answer"],
        },
        (arguments, _) => Task.FromResult<ToolResult>(
            arguments["answer"] is JsonValue value && value.TryGetValue<string>(out var answer) ? answer : arguments["answer"]?.ToJsonString() ?? ""));

    /// <summary>The state of one <c>react()</c> configuration; <see cref="ExecuteAsync"/> is the agent.</summary>
    private sealed class ReactLoop(
        IReadOnlyList<IToolSource> tools,
        ChatMessageSystem? systemMessage,
        Model? model,
        AgentModel? modelAgent,
        AgentAttempts attempts,
        AgentSubmit submit,
        ToolDef? submitTool,
        string? onContinue,
        AgentContinue? onContinueFn,
        int? retryRefusals,
        CreateAgentCompaction? compaction,
        MessageFilter? truncation,
        IReadOnlyList<ApprovalPolicy>? approval)
    {
        private const int MaxConsecutiveContentFilter = 3;

        public async Task<AgentState> ExecuteAsync(AgentState state, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(state);
            if (systemMessage is not null)
            {
                state.Messages.Insert(0, systemMessage);
            }

            var generator = modelAgent is null ? model ?? SampleContext.Require().ActiveModel : null;

            // Python: async with mcp_connection(tools) -- the servers behind the tool sources stay connected for the
            // whole loop, so a stateful MCP server keeps its state from one turn to the next.
            await using var connection = await McpConnection.ConnectAsync(tools, cancellationToken).ConfigureAwait(false);
            var compact = compaction is null ? null : CreateCompaction(state.Messages, await ResolveToolsAsync(cancellationToken).ConfigureAwait(false));
            var attemptCount = 0;
            var consecutiveContentFilter = 0;
            var submitName = submitTool?.Name;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Python resolves the tool sources on every turn (an MCP server may change the tools it offers).
                var resolvedTools = await ResolveToolsAsync(cancellationToken).ConfigureAwait(false);
                state = await GenerateAsync(generator, state, resolvedTools, compact, cancellationToken).ConfigureAwait(false);

                if (!state.Output.Empty && state.Output.StopReason == StopReason.ModelLength)
                {
                    var (recovered, handled) = await HandleOverflowAsync(state, compact, cancellationToken).ConfigureAwait(false);
                    state = recovered;
                    if (handled)
                    {
                        continue;
                    }

                    break;
                }

                if (!state.Output.Empty && state.Output.StopReason == StopReason.ContentFilter)
                {
                    consecutiveContentFilter++;
                    if (consecutiveContentFilter >= MaxConsecutiveContentFilter)
                    {
                        break;
                    }
                }
                else
                {
                    consecutiveContentFilter = 0;
                }

                if (HasToolCalls(state))
                {
                    var results = await ToolExecutor.ExecuteToolsAsync(state.Messages, resolvedTools, cancellationToken: cancellationToken, approval: approval).ConfigureAwait(false);
                    state.Messages.AddRange(results.Messages);
                    if (results.Output is { } output)
                    {
                        state.Output = output;
                    }

                    if (submitName is not null && Submission(results.Messages, submitName) is { } answer)
                    {
                        RecordSubmission(state, answer);
                        attemptCount++;
                        if (attemptCount >= attempts.Attempts)
                        {
                            break;
                        }

                        var scores = await ScoreAsync(state, cancellationToken).ConfigureAwait(false);
                        if (scores.Count == 0)
                        {
                            throw new InvalidOperationException("The task scorer returned no scores for the submission.");
                        }

                        var scoreValue = attempts.ScoreValue ?? ValueToFloat.Default;
                        if (scoreValue(scores[0].Value) == 1.0)
                        {
                            break;
                        }

                        var response = attempts.IncorrectMessageFn is { } incorrect
                            ? await incorrect(state, scores, cancellationToken).ConfigureAwait(false)
                            : attempts.IncorrectMessage;
                        state.Messages.Add(new ChatMessageUser(response));
                    }
                }

                if (onContinueFn is not null)
                {
                    var decision = await onContinueFn(state, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The on_continue function returned null.");
                    if (decision is AgentContinueResult.StopResult)
                    {
                        break;
                    }

                    ApplyContinue(state, decision, submitName);
                }
                else if (!HasToolCalls(state))
                {
                    if (submitName is null)
                    {
                        break;
                    }

                    var continueMessage = onContinue ?? AgentPrompt.DefaultContinuePrompt;
                    state.Messages.Add(new ChatMessageUser(continueMessage.Replace("{submit}", submitName, StringComparison.Ordinal)));
                }
            }

            if (submitName is not null && !submit.KeepInMessages)
            {
                // Submit calls would confuse parent agents watching for their own submit tools.
                state.Messages = RemoveSubmitTool(state.Messages, submitName);
            }

            return state;
        }

        private static bool HasToolCalls(AgentState state) => !state.Output.Empty && state.Output.Message.ToolCalls is { Count: > 0 };

        /// <summary>Port of the tool resolution of <c>_agent_generate</c>: the tools the agent's sources currently provide, in list order.</summary>
        private Task<IReadOnlyList<ToolDef>> ResolveToolsAsync(CancellationToken cancellationToken) => ToolSources.ResolveAsync(tools, cancellationToken);

        /// <summary>Port of <c>submission()</c>: the text of the first error-free submit tool result.</summary>
        private static string? Submission(IReadOnlyList<ChatMessage> toolResults, string submitName) =>
            toolResults.OfType<ChatMessageTool>().FirstOrDefault(result => result.Function == submitName && result.Error is null)?.Text;

        /// <summary>
        /// Sets the completion to the answer (alone, or appended to what the model wrote with the call) and, unless
        /// the submit call is kept, also appends the answer to the assistant message itself, in the output and in
        /// the conversation, since the call is removed at the end.
        /// </summary>
        private void RecordSubmission(AgentState state, string answer)
        {
            var completion = submit.AnswerOnly ? answer : $"{state.Output.Completion}{submit.AnswerDelimiter}{answer}".Trim();
            state.Output = state.Output with { Completion = completion };
            if (submit.KeepInMessages || state.Output.Choices.Count == 0)
            {
                return;
            }

            var choice = state.Output.Choices[0];
            var message = choice.Message;
            var updated = message.Content.IsString
                ? message with { Content = $"{message.Content.Text}{submit.AnswerDelimiter}{answer}".Trim() }
                : message with { Content = MessageContent.FromItems([.. message.Content.Items!, new ContentText(answer)]) };
            state.Output = state.Output with { Choices = [choice with { Message = updated }, .. state.Output.Choices.Skip(1)] };
            var index = state.Messages.FindLastIndex(m => ReferenceEquals(m, message) || (m is ChatMessageAssistant && m.Id is not null && m.Id == message.Id));
            if (index >= 0)
            {
                state.Messages[index] = updated;
            }
        }

        /// <summary>Port of the <c>on_continue</c> callable branch for a non-stop decision.</summary>
        private static void ApplyContinue(AgentState state, AgentContinueResult decision, string? submitName)
        {
            switch (decision)
            {
                case AgentContinueResult.ContinueResult:
                    if (!HasToolCalls(state))
                    {
                        state.Messages.Add(new ChatMessageUser(submitName is null
                            ? AgentPrompt.DefaultContinuePromptNoSubmit
                            : AgentPrompt.DefaultContinuePrompt.Replace("{submit}", submitName, StringComparison.Ordinal)));
                    }

                    break;
                case AgentContinueResult.MessageResult message:
                    state.Messages.Add(new ChatMessageUser(submitName is null ? message.Text : message.Text.Replace("{submit}", submitName, StringComparison.Ordinal)));
                    break;
                case AgentContinueResult.StateResult replacement:
                    state.Messages = replacement.State.Messages;
                    state.Output = replacement.State.Output;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected on_continue result {decision.GetType().Name}.");
            }
        }

        /// <summary>Port of <c>_agent_compact</c>: the compaction for this run, preserving the system messages and sample input.</summary>
        private AgentCompaction? CreateCompaction(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDef> resolvedTools)
        {
            if (compaction is null)
            {
                return null;
            }

            if (modelAgent is not null)
            {
                ProviderLogger.WarnOnce("react() agent: compaction has been enabled along with a custom agent as the model. Ignoring compaction strategy (the agent needs to handle compaction directly).");
                return null;
            }

            var partitioned = MessageFilters.PartitionMessages(messages);
            return compaction([.. partitioned.System, .. partitioned.Input], resolvedTools, model);
        }

        /// <summary>Port of <c>_agent_generate</c> / <c>_model_generate</c>: one assistant turn, with input compaction and refusal retries.</summary>
        private async Task<AgentState> GenerateAsync(Model? generator, AgentState state, IReadOnlyList<ToolDef> resolvedTools, AgentCompaction? compact, CancellationToken cancellationToken)
        {
            if (modelAgent is not null)
            {
                return await modelAgent(state, resolvedTools, cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<ChatMessage> input;
            if (compact is not null)
            {
                var compacted = await compact.CompactInput(state.Messages.ToArray(), false, cancellationToken).ConfigureAwait(false);
                input = compacted.Messages;
                if (compacted.Message is { } notice)
                {
                    state.Messages.Add(notice);
                }
            }
            else
            {
                input = state.Messages.ToArray();
            }

            var refusals = 0;
            while (true)
            {
                var output = await generator!.GenerateAsync(input, resolvedTools, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!output.Empty && output.StopReason == StopReason.ContentFilter && retryRefusals is { } limit && refusals < limit)
                {
                    refusals++;
                    continue;
                }

                state.Output = output;
                state.Messages.Add(output.Message);
                if (compact?.RecordOutput is { } record)
                {
                    await record(input, output, cancellationToken).ConfigureAwait(false);
                }

                return state;
            }
        }

        /// <summary>
        /// Port of <c>_handle_overflow</c>: drops the failed assistant turn, then tries forced compaction, then
        /// the truncation filter (continuing only if it shortened the conversation); otherwise the agent stops.
        /// </summary>
        private async Task<(AgentState State, bool Handled)> HandleOverflowAsync(AgentState state, AgentCompaction? compact, CancellationToken cancellationToken)
        {
            var transcript = SampleContext.Current?.Transcript;
            var previous = state.Messages.Take(state.Messages.Count - 1).ToList();

            if (compact is not null)
            {
                try
                {
                    var compacted = await compact.CompactInput(previous, true, cancellationToken).ConfigureAwait(false);
                    state.Messages = compacted.Messages.ToList();
                    if (compacted.Message is { } notice && (state.Messages.Count == 0 || !ReferenceEquals(state.Messages[^1], notice)))
                    {
                        state.Messages.Add(notice);
                    }

                    return (state, true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Falling back from configured compaction to the lossy overflow filter is a real degradation.
                    ProviderLogger.Warning($"Forced compaction failed during overflow recovery: {ex.Message}; falling back to overflow filter.");
                }
            }

            if (truncation is not null)
            {
                var trimmed = await truncation(previous, cancellationToken).ConfigureAwait(false);
                state.Messages = trimmed.ToList();
                if (trimmed.Count < previous.Count)
                {
                    transcript?.Add(new InfoEvent(null, JsonValue.Create("Agent exceeded model context window, truncating messages and continuing.")));
                    return (state, true);
                }
            }

            transcript?.Add(new InfoEvent(null, JsonValue.Create("Agent terminated: model context window exceeded")));
            return (state, false);
        }
    }
}
