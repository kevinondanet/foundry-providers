using System.ComponentModel;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;

namespace InspectAzureAI.Examples.InlineCards;

/// <summary>
/// Port of <c>examples/inline_cards/cancel_tool.py</c> <c>cancel_tool_demo</c>: a demo task for tool-call
/// cancellation (<c>^L</c>). A mockllm-driven react agent fires a long-running tool (60s sleep); the operator
/// cancels it mid-tool, the agent receives the failure status (the <c>timeout</c> tool error Python synthesises for a
/// cancelled call: <c>Command timed out before completing.</c>) and the mock model proceeds to submit. Deviation:
/// <c>^L</c> and <c>inspect/cancel_tool_call</c> live in the Textual/ACP TUI; here the tool itself races its sleep
/// against an <see cref="IOperator"/> (Enter at the console, or a timer under <c>--fake</c>) and records the
/// <c>user_cancel</c> interrupt event.
/// </summary>
public static class CancelToolDemo
{
    /// <summary>The task name (<c>@task def cancel_tool_demo</c>).</summary>
    public const string TaskName = "cancel_tool_demo";

    /// <summary>The tool name (<c>@tool def long_running_task</c>).</summary>
    public const string ToolName = "long_running_task";

    /// <summary>The <c>Sample(input=...)</c> of <c>cancel_tool_demo</c>, verbatim.</summary>
    public const string SampleInput = "Call long_running_task with seconds=60 (the operator will interrupt it), then submit 'ok'.";

    /// <summary>The <c>Sample(target=["ok"])</c>.</summary>
    public const string SampleTarget = "ok";

    /// <summary>The <c>AgentSubmit(description=...)</c>, verbatim.</summary>
    public const string SubmitDescription = "Submit the final answer after the tool returns or is cancelled.";

    /// <summary>The <c>tool_arguments={"seconds": 60}</c> of the scripted call.</summary>
    public const int ScriptedSeconds = 60;

    /// <summary>The error the model sees for a cancelled call (Python's <c>ToolCallError("timeout", ...)</c>).</summary>
    public const string CancelledMessage = "Command timed out before completing.";

    /// <summary>Port of <c>@tool def long_running_task()</c>: sleeps for the requested duration, unless <paramref name="operator"/> cancels the call first.</summary>
    public static ToolDef LongRunningTask(IOperator @operator)
    {
        ArgumentNullException.ThrowIfNull(@operator);
        return ToolDef.FromMethod(new Func<int, CancellationToken, Task<string>>(new LongRunningTaskTool(@operator).Execute), name: ToolName);
    }

    /// <summary>Port of the <c>get_model("mockllm/model", custom_outputs=[...])</c> of <c>cancel_tool_demo</c>.</summary>
    public static InspectAzureAI.Eval.Model.Model Model() => MockLlm.Create(
        () => ScriptedTurn.ToolCall(ToolName, new { seconds = ScriptedSeconds }),
        () => ScriptedTurn.ToolCall(Agents.DefaultSubmitName, new { answer = SampleTarget }));

    /// <summary>Port of <c>@task def cancel_tool_demo()</c>, discoverable by the <c>inspectai</c> CLI; the operator presses Enter at the console.</summary>
    [Task(TaskName)]
    public static EvalTask CancelToolDemoTask() => Build(new ConsoleOperator());

    /// <summary>
    /// The task: one sample, <c>react(tools=[long_running_task()], submit=AgentSubmit("submit", ...))</c>,
    /// <c>includes()</c>, <c>message_limit=10</c>, and its own mockllm model.
    /// </summary>
    public static EvalTask Build(IOperator @operator, SandboxSpec? sandbox = null) => new()
    {
        Name = TaskName,
        Dataset = new MemoryDataset([new Sample(SampleInput) { Target = new Target([SampleTarget]) }]),
        Solver = Agents.AsSolver(Agents.React(
            tools: [LongRunningTask(@operator)],
            submit: new AgentSubmit { Name = Agents.DefaultSubmitName, Description = SubmitDescription })),
        Scorers = [Scorers.Includes()],
        MessageLimit = 10,
        Model = Model(),
        Sandbox = sandbox,
    };

    private sealed class LongRunningTaskTool(IOperator @operator)
    {
        [Description("Sleep for the requested duration, then return.\n\nUse this when a task legitimately needs to wait — e.g.\npolling an external service. Operators can cancel a running\ninvocation with ``^L`` in the TUI.")]
        public async Task<string> Execute([Description("How long to sleep.")] int seconds, CancellationToken cancellationToken)
        {
            using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sleep = Task.Delay(TimeSpan.FromSeconds(seconds), race.Token);
            var cancel = @operator.WaitForCancelToolCallAsync(ToolName, race.Token);
            var winner = await Task.WhenAny(sleep, cancel).ConfigureAwait(false);
            await race.CancelAsync().ConfigureAwait(false);
            if (winner == sleep)
            {
                OperatorCancellation.Observe(cancel);
                await sleep.ConfigureAwait(false);
                return $"slept for {seconds} seconds";
            }

            OperatorCancellation.Observe(sleep);
            await cancel.ConfigureAwait(false);
            // Python's cancel_tool_call: the interrupt is recorded and the model gets the "timeout" tool error
            // (the ToolExecutor maps a TimeoutException to ToolCallError("timeout", "Command timed out before completing.")).
            OperatorCancellation.RecordInterrupt(ToolName);
            throw new TimeoutException(CancelledMessage);
        }
    }
}
