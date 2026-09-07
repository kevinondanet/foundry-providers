using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Agents;

/// <summary>Port of <c>agent/_as_solver.py</c> <c>as_solver</c> (the other <c>inspect_ai.agent</c> functions live in the sibling partial files).</summary>
public static partial class Agents
{
    /// <summary>
    /// Port of <c>as_solver(agent)</c>: runs the agent on an <see cref="AgentState"/> built from the state's
    /// messages inside an "agent" span, and copies the messages and output back even when the agent
    /// fails so the partial conversation is logged and scored.
    /// </summary>
    public static Solver AsSolver(AgentDef agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return async (state, _, cancellationToken) =>
        {
            var agentState = new AgentState(state.Messages);
            try
            {
                using var span = SampleContext.Current?.Transcript.Span(agent.Name, "agent");
                agentState = await agent.Execute(agentState, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                state.Messages = agentState.Messages;
                state.Output = agentState.Output;
            }

            return state;
        };
    }
}
