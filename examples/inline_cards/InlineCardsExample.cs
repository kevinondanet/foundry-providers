using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context.Input;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.InlineCards;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of the <c>examples/inline_cards</c> folder: four mockllm-driven react demo tasks for the interactive
/// inline cards of the Textual/ACP TUI — <see cref="ApprovalDemo"/> (a <c>dangerous_action</c> tool vetted by
/// <c>approval="human"</c>), <see cref="QuestionDemo"/> (<c>ask_user</c> twice: a single free-text field, then a
/// two-field form), <see cref="CancelToolDemo"/> (the operator cancels a 60s tool with <c>^L</c>; the model sees a
/// timeout tool error and submits) and <see cref="CancelSampleDemo"/> (the operator opens the cancel card with
/// <c>^N</c>: Score / Error / Back). Deviation: the TUI, its inline cards and the <c>--acp-server</c> mode are not
/// ported; each card has a console analogue (the human approver's console prompt, the <c>ask_user</c> console prompt,
/// Enter plus a Score/Error/Back prompt for the cancel cards), and under <c>--fake</c> every operator action is
/// scripted so the demos run unattended.
/// </summary>
public sealed class InlineCardsExample : IExample
{
    /// <summary>The Python example's name (its folder).</summary>
    public const string ExampleName = "inline_cards";

    /// <summary><c>-T decision=approve|reject|terminate</c>: what the scripted human approver answers for <c>dangerous_action</c> (default approve).</summary>
    public const string DecisionArg = "decision";

    /// <summary><c>-T decline=true</c>: the scripted operator declines the <c>ask_user</c> questions.</summary>
    public const string DeclineArg = "decline";

    /// <summary><c>-T cancel_after=&lt;seconds&gt;</c>: when the scripted operator presses <c>^L</c> / <c>^N</c> after the tool starts (default 1).</summary>
    public const string CancelAfterArg = "cancel_after";

    /// <summary><c>-T resolution=score,error,back</c>: what the scripted operator picks on the cancel card, in order (default score).</summary>
    public const string ResolutionArg = "resolution";

    public const double DefaultCancelAfterSeconds = 1;

    public string Name => ExampleName;

    public string Description => "The interactive inline cards (approval, ask_user question, cancel tool call, cancel sample) as console prompts, driven by scripted mockllm agents";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(ApprovalDemo.TaskName, BuildApproval, "the agent calls dangerous_action, which the human approver vets (console prompt, or -T decision=approve|reject|terminate under --fake), then submits"),
        new ExampleTask(QuestionDemo.TaskName, BuildQuestion, "the agent asks the operator a one-field question, then a two-field form (console prompt, or scripted answers under --fake; -T decline=true declines), then submits"),
        new ExampleTask(CancelToolDemo.TaskName, BuildCancelTool, "the agent calls a 60s tool; the operator cancels the call (Enter at the console, or after -T cancel_after seconds under --fake) and the agent submits"),
        new ExampleTask(CancelSampleDemo.TaskName, BuildCancelSample, "the agent calls a 600s tool; the operator opens the cancel card (Enter then Score/Error/Back at the console, or -T resolution=score,error,back under --fake)"),
    ];

    public ExampleDefaults Defaults { get; } = new();

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The Textual/ACP TUI, its inline cards (_ApprovalCard, _ElicitationCard, _CancelCard) and the --acp-server flag are not ported. approval_demo uses the human approver's console prompt (Approve/Reject/Terminate at the terminal); question_demo uses the ask_user console prompt (one field at a time, :decline to decline); the cancel demos read Enter at the console in place of ^L / ^N, and the cancel card is the prompt \"Score (s), Error (e), or Back (b)\".",
        "Tool-call cancellation (^L) is implemented by the long_running_task tool itself: it races its sleep against the operator, records the user_cancel interrupt event and raises the engine's timeout error, so the model sees the same \"Command timed out before completing.\" tool error Python synthesises for a cancelled call and goes on to submit.",
        "Sample cancellation (^N) is implemented by the slow_busy_work tool: Score raises the engine's TerminateSampleException (the sample ends cleanly with the engine's \"operator\" limit and is scored on what it has; Python's Score action likewise completes and scores the sample), Error raises an exception that becomes the sample's error (the eval then reports an error status, exit code 1), Back leaves the tool sleeping until the operator opens the card again.",
        "Under --fake every operator action is scripted so the demos run unattended: the human approver answers -T decision (default approve, submit always approved); ask_user is answered with sk-123 then environment=staging, expiry=2027-01 (or declined with -T decline=true); the cancel demos fire after -T cancel_after seconds (default 1) and pick -T resolution (default score; a comma-separated list, e.g. back,score, for several cards). The Python demos always wait for the operator.",
        "Each task carries its own scripted mockllm model (Python's Task(model=...)), which wins over the runner's model: --model changes the banner only. The CLI discovers the approval task as inline_cards_approval_demo (examples/approval already registers approval_demo in the same assembly and the CLI resolves bare names per assembly, where Python resolves them per file); the task's own name is approval_demo.",
        "The tasks take the sandbox the runner resolves (--sandbox); the Python tasks declare none, and none is the default here too.",
    ];

    /// <summary>Every task has its own model (see <see cref="MockLlm.Placeholder"/>), so the runner's is never asked for a turn.</summary>
    public Model CreateFakeModel(ExampleContext ctx) => MockLlm.Placeholder();

    /// <summary>The demos need no sandbox.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>The console human approver, or under <c>--fake</c> the scripted one answering <c>-T decision</c>.</summary>
    public static EvalTask BuildApproval(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        IApprovalPrompter? prompter = null;
        if (ctx.Fake)
        {
            var decision = ctx.TaskArg(DecisionArg, "approve")!;
            prompter = new ScriptedApprovalPrompter(
                decision.ToLowerInvariant() switch
                {
                    "approve" => ApprovalDecision.Approve,
                    "reject" => ApprovalDecision.Reject,
                    "terminate" => ApprovalDecision.Terminate,
                    _ => throw new ArgumentException($"-T {DecisionArg} expects approve, reject or terminate, got '{decision}'"),
                },
                ctx.Out);
        }

        return ApprovalDemo.Build(ctx.Sandbox, prompter);
    }

    /// <summary>The console operator for <c>ask_user</c>, or under <c>--fake</c> the scripted answers (declined with <c>-T decline=true</c>).</summary>
    public static EvalTask BuildQuestion(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        IInputHandler? handler = null;
        if (ctx.Fake)
        {
            handler = ctx.TaskArgBool(DeclineArg, false)
                ? ScriptedInputHandler.Declining(ctx.Out)
                : new ScriptedInputHandler(QuestionDemo.FakeAnswers(), ctx.Out);
        }

        return QuestionDemo.Build(ctx.Sandbox, handler);
    }

    /// <summary>The console operator (Enter cancels the call), or under <c>--fake</c> a timer of <c>-T cancel_after</c> seconds.</summary>
    public static EvalTask BuildCancelTool(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return CancelToolDemo.Build(Operator(ctx), ctx.Sandbox);
    }

    /// <summary>The console operator (Enter opens the card), or under <c>--fake</c> a timer of <c>-T cancel_after</c> seconds picking <c>-T resolution</c>.</summary>
    public static EvalTask BuildCancelSample(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return CancelSampleDemo.Build(Operator(ctx), ctx.Sandbox);
    }

    private static IOperator Operator(ExampleContext ctx)
    {
        if (!ctx.Fake)
        {
            return new ConsoleOperator(Console.In, ctx.Out);
        }

        var delay = ctx.TaskArgDouble(CancelAfterArg, DefaultCancelAfterSeconds);
        if (delay < 0)
        {
            throw new ArgumentException($"-T {CancelAfterArg} expects a non-negative number of seconds, got '{delay}'");
        }

        var resolutions = ctx.TaskArg(ResolutionArg) is { } spec ? ScriptedOperator.ParseResolutions(spec) : [CancelResolution.Score];
        return new ScriptedOperator(TimeSpan.FromSeconds(delay), resolutions, ctx.Out);
    }
}
