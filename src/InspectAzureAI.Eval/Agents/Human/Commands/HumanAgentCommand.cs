using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Eval.Agents.Human.Commands;

/// <summary>Port of the <c>contexts</c> literal of <c>HumanAgentCommand</c>: where a command runs.</summary>
public enum HumanAgentCommandContext
{
    /// <summary>The <c>task</c> CLI inside the container.</summary>
    Cli,

    /// <summary>The service handler in the host process.</summary>
    Service,
}

/// <summary>Port of <c>HumanAgentCommand.CLIArg</c>: a positional (or <c>--flag</c>) argument of a <c>task</c> subcommand.</summary>
public sealed record HumanAgentCliArg(string Name, string Description, bool Required = false);

/// <summary>
/// Port of <c>agent/_human/commands/command.py</c> <c>HumanAgentCommand</c>: a <c>task</c> subcommand with a CLI
/// half (Python source that runs inside the container, generated into <c>task.py</c>) and a service half (the
/// handler the sandbox service runs in the host). Deviation: Python lifts the CLI half's source with
/// <c>inspect.getsource</c>; here each command carries it verbatim as <see cref="CliSource"/>, and
/// <see cref="Activity"/> describes a served call for the console view.
/// </summary>
public abstract class HumanAgentCommand
{
    /// <summary>Command name (e.g. 'submit').</summary>
    public abstract string Name { get; }

    /// <summary>Command description.</summary>
    public abstract string Description { get; }

    /// <summary>Display group in the instructions (1: task, 2: clock, 3: help).</summary>
    public virtual int Group => 1;

    /// <summary>Contexts where this command runs (defaults to both cli and service).</summary>
    public virtual IReadOnlyList<HumanAgentCommandContext> Contexts => [HumanAgentCommandContext.Cli, HumanAgentCommandContext.Service];

    /// <summary>Positional command line arguments.</summary>
    public virtual IReadOnlyList<HumanAgentCliArg> CliArgs => [];

    /// <summary>Python source of <c>def NAME(args: Namespace) -> None</c>, the CLI handler generated into <c>task.py</c>. Required for the cli context.</summary>
    public virtual string? CliSource => null;

    /// <summary>Service handler (runs in the host). Required for the service context.</summary>
    public virtual SandboxServiceMethod Service(HumanAgentState state) => static (_, _) => Task.FromResult<JsonNode?>(null);

    /// <summary>A one-message description of a served call for the view, or null for nothing to report.</summary>
    public virtual string? Activity(JsonObject parameters, JsonNode? result) => null;

    /// <summary>Whether the command runs in <paramref name="context"/>.</summary>
    public bool RunsIn(HumanAgentCommandContext context) => Contexts.Contains(context);

    /// <summary>A string parameter of a service call (null when absent, JSON null or not a string).</summary>
    protected static string? StringParam(JsonObject parameters, string name) =>
        parameters.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A string result for the client to print.</summary>
    protected static JsonNode Text(string value) => JsonValue.Create(value);
}

/// <summary>Port of <c>agent/_human/commands/__init__.py</c> <c>human_agent_commands</c>: the command set of one human agent run.</summary>
public static class HumanAgentCommands
{
    /// <summary>
    /// submit, validate and quit; <c>score</c> when <paramref name="intermediateScoring"/>; then note, status,
    /// start, stop and finally instructions (which lists the others).
    /// </summary>
    public static IReadOnlyList<HumanAgentCommand> Create(
        AgentState state,
        ISandboxEnvironment sandbox,
        bool answer,
        string? answerPattern,
        bool intermediateScoring,
        bool recordSession,
        string? instructions)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(sandbox);

        var commands = new List<HumanAgentCommand>
        {
            new SubmitCommand(sandbox, recordSession),
            new ValidateCommand(answer, answerPattern),
            new QuitCommand(sandbox, recordSession),
        };

        if (intermediateScoring)
        {
            commands.Add(new ScoreCommand(state));
        }

        commands.AddRange([new NoteCommand(), new StatusCommand(), new StartCommand(), new StopCommand()]);

        return [.. commands, new InstructionsCommand(commands, instructions)];
    }
}
