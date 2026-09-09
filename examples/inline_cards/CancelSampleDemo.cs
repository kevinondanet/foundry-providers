using System.ComponentModel;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;

namespace InspectAzureAI.Examples.InlineCards;

/// <summary>
/// Port of <c>examples/inline_cards/cancel_sample.py</c> <c>cancel_sample_demo</c>: a demo task for sample
/// cancellation (<c>^N</c>). A mockllm-driven react agent fires a very long-running tool (10 min sleep); the operator
/// opens the cancel card mid-tool: <c>Score</c> ends the sample cleanly (scoring runs on whatever the agent has
/// produced so far), <c>Error</c> ends it as an error, <c>Back</c> dismisses the card and lets the agent continue.
/// Deviation: <c>^N</c>, the inline <c>_CancelCard</c> and <c>inspect/cancel_sample</c> live in the Textual/ACP TUI;
/// here the tool races its sleep against an <see cref="IOperator"/> (Enter then a Score/Error/Back prompt at the
/// console, or a scripted list under <c>--fake</c>): Score raises the engine's <see cref="TerminateSampleException"/>
/// (the sample ends with an <c>operator</c> limit and is scored), Error raises <see cref="OperatorCancelledException"/>
/// (the sample's error), Back keeps sleeping.
/// </summary>
public static class CancelSampleDemo
{
    /// <summary>The task name (<c>@task def cancel_sample_demo</c>).</summary>
    public const string TaskName = "cancel_sample_demo";

    /// <summary>The tool name (<c>@tool def slow_busy_work</c>).</summary>
    public const string ToolName = "slow_busy_work";

    /// <summary>The <c>Sample(input=...)</c> of <c>cancel_sample_demo</c>, verbatim.</summary>
    public const string SampleInput = "Call slow_busy_work with seconds=600 (the operator will interrupt the sample), then submit 'ok'.";

    /// <summary>The <c>Sample(target=["ok"])</c>.</summary>
    public const string SampleTarget = "ok";

    /// <summary>The <c>AgentSubmit(description=...)</c>, verbatim.</summary>
    public const string SubmitDescription = "Submit the final answer (only reached if the operator picks Back).";

    /// <summary>The <c>tool_arguments={"seconds": 600}</c> of the scripted call.</summary>
    public const int ScriptedSeconds = 600;

    /// <summary>The reason recorded on the sample's <c>operator</c> limit when the operator picks Score.</summary>
    public const string ScoreReason = "Operator cancelled the sample (Score): scored on what the agent produced so far.";

    /// <summary>The sample's error message when the operator picks Error.</summary>
    public const string ErrorReason = "Operator cancelled the sample (Error).";

    /// <summary>Port of <c>@tool def slow_busy_work()</c>: pretends to be busy for a long time while <paramref name="operator"/> may open the cancel card.</summary>
    public static ToolDef SlowBusyWork(IOperator @operator)
    {
        ArgumentNullException.ThrowIfNull(@operator);
        return ToolDef.FromMethod(new Func<int, CancellationToken, Task<string>>(new SlowBusyWorkTool(@operator).Execute), name: ToolName);
    }

    /// <summary>Port of the <c>get_model("mockllm/model", custom_outputs=[...])</c> of <c>cancel_sample_demo</c>: the submit turn is reached only if the operator picks Back.</summary>
    public static InspectAzureAI.Eval.Model.Model Model() => MockLlm.Create(
        () => ScriptedTurn.ToolCall(ToolName, new { seconds = ScriptedSeconds }),
        () => ScriptedTurn.ToolCall(Agents.DefaultSubmitName, new { answer = SampleTarget }));

    /// <summary>Port of <c>@task def cancel_sample_demo()</c>, discoverable by the <c>inspectai</c> CLI; the operator uses the console.</summary>
    [Task(TaskName)]
    public static EvalTask CancelSampleDemoTask() => Build(new ConsoleOperator());

    /// <summary>
    /// The task: one sample, <c>react(tools=[slow_busy_work()], submit=AgentSubmit("submit", ...))</c>,
    /// <c>includes()</c>, <c>message_limit=10</c>, and its own mockllm model.
    /// </summary>
    public static EvalTask Build(IOperator @operator, SandboxSpec? sandbox = null) => new()
    {
        Name = TaskName,
        Dataset = new MemoryDataset([new Sample(SampleInput) { Target = new Target([SampleTarget]) }]),
        Solver = Agents.AsSolver(Agents.React(
            tools: [SlowBusyWork(@operator)],
            submit: new AgentSubmit { Name = Agents.DefaultSubmitName, Description = SubmitDescription })),
        Scorers = [Scorers.Includes()],
        MessageLimit = 10,
        Model = Model(),
        Sandbox = sandbox,
    };

    private sealed class SlowBusyWorkTool(IOperator @operator)
    {
        [Description("Pretend to be busy for a long time, then return.\n\nThe agent keeps the operator on the hook for ``seconds``\nseconds before this tool finishes; in the meantime ``^N``\nin the TUI fires the inline cancel card.")]
        public async Task<string> Execute([Description("How long to sleep.")] int seconds, CancellationToken cancellationToken)
        {
            using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sleep = Task.Delay(TimeSpan.FromSeconds(seconds), race.Token);
            while (true)
            {
                var card = @operator.WaitForCancelSampleAsync(ToolName, race.Token);
                var winner = await Task.WhenAny(sleep, card).ConfigureAwait(false);
                if (winner == sleep)
                {
                    await race.CancelAsync().ConfigureAwait(false);
                    OperatorCancellation.Observe(card);
                    await sleep.ConfigureAwait(false);
                    return $"slept for {seconds} seconds";
                }

                var resolution = await card.ConfigureAwait(false);
                if (resolution == CancelResolution.Back)
                {
                    // The card is dismissed; the agent keeps running (the operator may open it again).
                    continue;
                }

                await race.CancelAsync().ConfigureAwait(false);
                OperatorCancellation.Observe(sleep);
                OperatorCancellation.RecordInterrupt(ToolName);
                throw resolution == CancelResolution.Score
                    ? new TerminateSampleException(ScoreReason)
                    : new OperatorCancelledException(ErrorReason);
            }
        }
    }
}
