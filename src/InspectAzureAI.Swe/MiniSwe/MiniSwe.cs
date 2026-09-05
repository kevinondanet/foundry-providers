using InspectAzureAI.Eval.Agents;

namespace InspectAzureAI.Swe.MiniSwe;

/// <summary>Port of the inspect_swe <c>mini_swe_agent()</c> factory: a named <see cref="AgentDef"/> running <see cref="MiniSweAgent"/>.</summary>
public static class MiniSwe
{
    public static AgentDef Agent(MiniSweAgentOptions? options = null)
    {
        var resolved = options ?? new MiniSweAgentOptions();
        return new AgentDef(resolved.Name, resolved.Description, (state, cancellationToken) => new MiniSweAgent(resolved).ExecuteAsync(state, cancellationToken));
    }
}
