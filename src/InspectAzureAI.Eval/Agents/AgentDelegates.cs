namespace InspectAzureAI.Eval.Agents;

/// <summary>Port of the <c>Agent</c> protocol of <c>agent/_agent.py</c>: transforms an <see cref="AgentState"/>.</summary>
public delegate Task<AgentState> Agent(AgentState state, CancellationToken cancellationToken);

/// <summary>Port of the <c>@agent</c> registry entry: a named, described agent.</summary>
public sealed record AgentDef(string Name, string Description, Agent Execute);
