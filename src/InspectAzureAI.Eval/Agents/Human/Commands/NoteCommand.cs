using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Eval.Agents.Human.Commands;

/// <summary>
/// Port of <c>agent/_human/commands/note.py</c> <c>NoteCommand</c>: <c>task note</c> reads a multiline markdown
/// note in the container and the service records it as an info event from source <c>human_agent</c>.
/// </summary>
public sealed class NoteCommand : HumanAgentCommand
{
    public override string Name => "note";

    public override string Description => "Record a note in the task transcript.";

    public override int Group => 1;

    public override string CliSource => """
        def note(args: Namespace) -> None:
            print(
                "Enter a multiline markdown note (Press Ctrl+D on a new line to finish):\n"
            )
            lines = ["## Human Agent Note"]
            try:
                while True:
                    line = input()
                    lines.append(line)
            except EOFError:
                pass
            call_human_agent("note", content="\n".join(lines))
        """;

    public override SandboxServiceMethod Service(HumanAgentState state) => (parameters, _) =>
    {
        var content = StringParam(parameters, "content") ?? "";
        SampleContext.Current?.Transcript.Info(ClockCommands.EventSource, content);
        return Task.FromResult<JsonNode?>(null);
    };

    public override string? Activity(JsonObject parameters, JsonNode? result) =>
        $"Note recorded:\n{StringParam(parameters, "content") ?? ""}";
}
