using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

/// <summary>Port of <c>model/_model.py</c> <c>Model.generate_loop</c> (the rest of the class is in <c>Model.cs</c>).</summary>
public sealed partial class Model
{
    /// <summary>
    /// Port of <c>Model.generate_loop</c> for a string input, which becomes a single <see cref="ChatMessageUser"/>.
    /// See <see cref="GenerateLoopAsync(IReadOnlyList{ChatMessage}, IReadOnlyList{IToolSource}?, GenerateConfig?, CachePolicy?, StreamHandler?, CancellationToken)"/>.
    /// </summary>
    public Task<(IReadOnlyList<ChatMessage> Messages, ModelOutput Output)> GenerateLoopAsync(
        string input,
        IReadOnlyList<IToolSource>? tools = null,
        GenerateConfig? config = null,
        CachePolicy? cache = null,
        StreamHandler? onStream = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return GenerateLoopAsync([new ChatMessageUser(input)], tools, config, cache, onStream, cancellationToken);
    }

    /// <summary>
    /// Port of <c>Model.generate_loop</c>: generates, executing the model's tool calls (through
    /// <see cref="ToolExecutor"/>, with <see cref="GenerateConfig.MaxToolOutput"/> from <paramref name="config"/>
    /// or this model's config) and generating again, until the model stops calling tools. Returns the messages
    /// added to the conversation (the assistant and tool messages after <paramref name="input"/>) and the final
    /// <see cref="ModelOutput"/>. <paramref name="tools"/> holds <see cref="ToolDef"/>s and tool sources (Python's
    /// <c>Sequence[Tool | ToolDef | ToolSource]</c>; a plain tool list converts covariantly); like Python's
    /// <c>generate</c>, the sources are resolved on every turn, and any MCP servers behind them are connected per
    /// call unless the caller holds an <see cref="McpConnection"/>. <paramref name="config"/>,
    /// <paramref name="cache"/> and <paramref name="onStream"/> are passed to every generate (retry attempt numbers
    /// in stream events are per call, not cumulative).
    /// </summary>
    public async Task<(IReadOnlyList<ChatMessage> Messages, ModelOutput Output)> GenerateLoopAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<IToolSource>? tools = null,
        GenerateConfig? config = null,
        CachePolicy? cache = null,
        StreamHandler? onStream = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sources = tools ?? [];
        var maxToolOutput = config?.MaxToolOutput ?? Config.MaxToolOutput;

        // initialise messages (a copy: the caller's list is left untouched)
        var messages = new List<ChatMessage>(input);
        while (true)
        {
            var resolvedTools = await ToolSources.ResolveAsync(sources, cancellationToken).ConfigureAwait(false);

            // call model (on a snapshot: the model records its input and the list keeps growing)
            var output = await GenerateAsync(messages.ToArray(), resolvedTools, null, config, cache, onStream, cancellationToken).ConfigureAwait(false);

            // append to new messages
            messages.Add(output.Message);

            // make tool calls or terminate if there are none
            if (output.Message.ToolCalls is { Count: > 0 })
            {
                var result = await ToolExecutor.ExecuteToolsAsync(messages, resolvedTools, maxToolOutput, cancellationToken).ConfigureAwait(false);
                messages.AddRange(result.Messages);
                if (result.Output is { } toolsOutput)
                {
                    output = toolsOutput;
                }
            }
            else
            {
                return (messages.GetRange(input.Count, messages.Count - input.Count), output);
            }
        }
    }
}
