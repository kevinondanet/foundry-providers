using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Eval.Agents.Human.Commands;

/// <summary>Port of <c>agent/_human/commands/status.py</c> <c>StatusCommand</c>: <c>task status</c> prints the clock and any intermediate scores.</summary>
public sealed class StatusCommand : HumanAgentCommand
{
    public override string Name => "status";

    public override string Description => "Print task status (clock, scoring, etc.)";

    public override int Group => 2;

    public override string CliSource => """
        def status(args: Namespace) -> None:
            print(call_human_agent("status"))
        """;

    public override SandboxServiceMethod Service(HumanAgentState state) => (_, _) =>
        Task.FromResult<JsonNode?>(Text(HumanAgentText.RenderStatus(state)));
}
