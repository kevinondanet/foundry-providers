using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Agents;

/// <summary>
/// Port of the <c>run()</c> return of <c>agent/_run.py</c>: the agent's final state and, when one of the
/// limits passed to <c>run()</c> was exceeded, the error that stopped it (Python returns a tuple only when
/// limits are given; here <see cref="LimitError"/> is simply null otherwise).
/// </summary>
public sealed record AgentRunResult(AgentState State, LimitExceededException? LimitError);

/// <summary>Port of <c>agent/_run.py</c> <c>run</c>, <c>agent/_agent.py</c> <c>agent_with</c> / <c>is_agent</c> and <c>scorer/_score.py</c> <c>score(AgentState)</c>.</summary>
public static partial class Agents
{
    /// <summary>Port of <c>run(agent, input: str, ...)</c>: the input becomes a single user message with <c>source="input"</c>.</summary>
    public static Task<AgentRunResult> RunAsync(AgentDef agent, string input, AgentLimits? limits = null, string? name = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return RunAsync(agent, [new ChatMessageUser(input) { Source = "input" }], limits, name, cancellationToken);
    }

    /// <summary>
    /// Port of <c>run(agent, input, limits, name)</c>: copies the input messages (marking them
    /// <c>source="input"</c>), runs the agent on a fresh <see cref="AgentState"/> inside an "agent" span named
    /// <paramref name="name"/> (default: the agent's name) and under <paramref name="limits"/>. A limit of this
    /// call being exceeded is caught and returned as <see cref="AgentRunResult.LimitError"/> alongside the state
    /// the agent got to; any other limit error (the sample's, an enclosing scope's) propagates. The conversation
    /// is available only through the returned state.
    /// </summary>
    public static async Task<AgentRunResult> RunAsync(
        AgentDef agent,
        IReadOnlyList<ChatMessage> input,
        AgentLimits? limits = null,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(input);
        var state = new AgentState(input.Select(message => message with { Source = "input" }));
        using var scope = LimitScope.Apply(limits, cancellationToken);
        using var span = SampleContext.Current?.Transcript.Span(name ?? agent.Name, "agent");
        try
        {
            state = await agent.Execute(state, scope.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (scope.AsLimitError(ex) is { } limitError)
        {
            return new AgentRunResult(state, limitError);
        }

        return new AgentRunResult(state, null);
    }

    /// <summary>
    /// Port of <c>agent_with(agent, name, description)</c>: the agent with either field replaced. Python modifies
    /// the agent in place; <see cref="AgentDef"/> is a record, so a copy is returned instead.
    /// </summary>
    public static AgentDef AgentWith(AgentDef agent, string? name = null, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent with { Name = name ?? agent.Name, Description = description ?? agent.Description };
    }

    /// <summary>Port of <c>is_agent(obj)</c>: whether the object is an <see cref="AgentDef"/>.</summary>
    public static bool IsAgent(object? obj) => obj is AgentDef;

    /// <summary>
    /// Port of <c>score(state: AgentState)</c>: runs the task's scorers over a copy of the sample's own state
    /// carrying this agent state's messages and output, recording intermediate score events. Requires a sample
    /// context with a sample state and a scorer, like Python's <c>RuntimeError</c>s.
    /// </summary>
    public static async Task<IReadOnlyList<Score>> ScoreAsync(AgentState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        var context = SampleContext.Current;
        var sampleState = context?.SampleState
            ?? throw new InvalidOperationException("The score() function can only be called while executing a task");
        var scorer = context.Scorer
            ?? throw new InvalidOperationException("The score() function can only be called while executing a task with a scorer.");
        return await scorer(sampleState.WithMessages(state.Messages, state.Output)).ConfigureAwait(false);
    }
}
