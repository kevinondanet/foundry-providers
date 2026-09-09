using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Eval.Agents.Human.Commands;

/// <summary>Port of <c>agent/_human/commands/clock.py</c> <c>StartCommand</c>: <c>task start</c> resumes the task clock.</summary>
public sealed class StartCommand : HumanAgentCommand
{
    public override string Name => "start";

    public override string Description => "Start the task clock (resume working).";

    public override int Group => 2;

    public override string CliSource => """
        def start(args: Namespace) -> None:
            print(call_human_agent("start"))
        """;

    public override SandboxServiceMethod Service(HumanAgentState state) => (_, _) =>
    {
        if (!state.IsRunning())
        {
            state.SetRunning(true);
            ClockCommands.ClockActionEvent("start", state);
        }

        return Task.FromResult<JsonNode?>(Text(HumanAgentText.RenderStatus(state)));
    };
}

/// <summary>Port of <c>StopCommand</c>: <c>task stop</c> pauses the task clock.</summary>
public sealed class StopCommand : HumanAgentCommand
{
    public override string Name => "stop";

    public override string Description => "Stop the task clock (pause working).";

    public override int Group => 2;

    public override string CliSource => """
        def stop(args: Namespace) -> None:
            print(call_human_agent("stop"))
        """;

    public override SandboxServiceMethod Service(HumanAgentState state) => (_, _) =>
    {
        if (state.IsRunning())
        {
            state.SetRunning(false);
            ClockCommands.ClockActionEvent("stop", state);
        }

        return Task.FromResult<JsonNode?>(Text(HumanAgentText.RenderStatus(state)));
    };
}

/// <summary>Port of <c>clock_action_event</c>: an info event from source <c>human_agent</c> recording a clock action and the total time.</summary>
public static class ClockCommands
{
    public const string EventSource = "human_agent";

    public static void ClockActionEvent(string action, HumanAgentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        SampleContext.Current?.Transcript.Info(
            EventSource,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["action"] = action,
                ["total_time"] = HumanAgentText.FormatProgressTime(state.Time(), padHours: false),
            });
    }
}
