using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Eval.Agents.Human.Commands;

/// <summary>
/// Port of <c>agent/_human/commands/instructions.py</c> <c>InstructionsCommand</c>: <c>task instructions</c>
/// prints the command reference (grouped, including itself) plus any extra instructions, then the task instructions.
/// </summary>
public sealed class InstructionsCommand : HumanAgentCommand
{
    private readonly IReadOnlyList<HumanAgentCommand> _commands;

    private readonly string? _instructions;

    public InstructionsCommand(IReadOnlyList<HumanAgentCommand> commands, string? instructions)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = [.. commands, this];
        _instructions = instructions;
    }

    public override string Name => "instructions";

    public override string Description => "Display task commands and instructions.";

    public override int Group => 3;

    public override string CliSource => """
        def instructions(args: Namespace) -> None:
            print(call_human_agent("instructions", **vars(args)))
        """;

    public override SandboxServiceMethod Service(HumanAgentState state) => (_, _) =>
        Task.FromResult<JsonNode?>(Text(HumanAgentText.RenderInstructions(_commands, _instructions, state.Instructions)));
}
