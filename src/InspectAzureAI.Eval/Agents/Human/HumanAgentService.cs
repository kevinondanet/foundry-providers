using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents.Human.Commands;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Agents.Human;

/// <summary>
/// Port of <c>agent/_human/service.py</c> <c>run_human_agent_service</c>: binds a <see cref="HumanAgentState"/>
/// to the sample store (instructions = the state's messages joined by blank lines), records the initial
/// <c>stop</c> clock event, publishes every service-context command as a method of the <c>human_agent</c>
/// sandbox service and serves it until an answer arrives (refreshing the view on every poll), then sets the
/// agent's output to a <c>human_agent</c> completion carrying the answer.
/// </summary>
public static class HumanAgentService
{
    /// <summary>The sandbox service name (and the module the generated <c>task.py</c> imports).</summary>
    public const string ServiceName = "human_agent";

    public static async Task<AgentState> RunAsync(
        ISandboxEnvironment sandbox,
        string? user,
        AgentState state,
        IReadOnlyList<HumanAgentCommand> commands,
        IHumanAgentView? view,
        TimeSpan? pollingInterval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(commands);

        // initialise agent state
        var instructions = string.Join("\n\n", state.Messages.Select(message => message.Text)).Trim();
        var store = SampleContext.Current?.Store ?? new Store();
        var agentState = store.As<HumanAgentState>();
        agentState.Instructions = instructions;

        // record that clock is stopped
        ClockCommands.ClockActionEvent("stop", agentState);

        // extract service methods from commands
        var methods = new Dictionary<string, SandboxServiceMethod>(StringComparer.Ordinal);
        foreach (var command in commands.Where(command => command.RunsIn(HumanAgentCommandContext.Service)))
        {
            methods[command.Name] = WithActivity(command, command.Service(agentState), view);
        }

        // callback to check if task is completed (use this to periodically update the view with the current state)
        bool TaskIsCompleted()
        {
            view?.UpdateState(agentState);
            return agentState.Answer is not null;
        }

        // run the service
        await SandboxService.RunAsync(
            ServiceName,
            methods,
            TaskIsCompleted,
            sandbox,
            user,
            pollingInterval: pollingInterval,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // set the answer if we have one
        if (agentState.Answer is { } answer)
        {
            state.Output = ModelOutput.FromContent(HumanCli.ModelName, answer);
        }

        return state;
    }

    private static SandboxServiceMethod WithActivity(HumanAgentCommand command, SandboxServiceMethod method, IHumanAgentView? view)
    {
        if (view is null)
        {
            return method;
        }

        return async (parameters, cancellationToken) =>
        {
            var result = await method(parameters, cancellationToken).ConfigureAwait(false);
            if (command.Activity(parameters, result) is { } message)
            {
                view.Activity(message);
            }

            return result;
        };
    }
}
