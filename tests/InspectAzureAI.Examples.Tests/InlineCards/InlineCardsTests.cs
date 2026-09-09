using System.Diagnostics;
using System.Reflection;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.AskUser;
using InspectAzureAI.Examples.InlineCards;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.InlineCards;

using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/inline_cards</c> (<see cref="InlineCardsExample"/>): the four tasks' shapes and
/// tool descriptions, the console and scripted operators, and each demo end to end without a terminal — the human
/// approver, the <c>ask_user</c> operator and the <c>^L</c> / <c>^N</c> operator are scripted, and the outcomes are
/// read back from the log (approval, input and interrupt events, tool errors, sample limits, scores).
/// </summary>
public sealed class InlineCardsTests : IDisposable
{
    private static readonly TimeSpan FastDelay = TimeSpan.FromMilliseconds(50);

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "inline-cards-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_logDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ----------------------------------------------------------------------------------------------------------
    // the tasks (ports of the four @task functions)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_four_tasks_are_shaped_like_the_python_demos()
    {
        var approval = ApprovalDemo.ApprovalDemoTask();
        var question = QuestionDemo.QuestionDemoTask();
        var cancelTool = CancelToolDemo.CancelToolDemoTask();
        var cancelSample = CancelSampleDemo.CancelSampleDemoTask();

        Assert.Equal("approval_demo", approval.Name);
        Assert.Equal("Use the dangerous_action tool to clean up /tmp/example, then submit 'ok'.", approval.Dataset[0].Input.Text);
        Assert.Equal("human", approval.Approval!.Spec);

        Assert.Equal("question_demo", question.Name);
        Assert.Equal("Use the ask_user tool to collect the API key, then any follow-up details the agent asks for, then submit 'ok'.", question.Dataset[0].Input.Text);
        Assert.Null(question.Approval);

        Assert.Equal("cancel_tool_demo", cancelTool.Name);
        Assert.Equal("Call long_running_task with seconds=60 (the operator will interrupt it), then submit 'ok'.", cancelTool.Dataset[0].Input.Text);

        Assert.Equal("cancel_sample_demo", cancelSample.Name);
        Assert.Equal("Call slow_busy_work with seconds=600 (the operator will interrupt the sample), then submit 'ok'.", cancelSample.Dataset[0].Input.Text);

        foreach (var task in new[] { approval, question, cancelTool, cancelSample })
        {
            Assert.Single(task.Dataset);
            Assert.Equal(["ok"], task.Dataset[0].Target.Values);
            Assert.Equal(["includes"], task.Scorers.Select(scorer => scorer.Name));
            Assert.Equal(10, task.MessageLimit);
            Assert.Null(task.Sandbox);
            // Task(model=model): every task carries its own mockllm model.
            Assert.Equal(MockLlm.ModelName, task.Model!.Name);
        }
    }

    [Fact]
    public void the_task_methods_are_discoverable_by_the_cli()
    {
        Assert.Equal("inline_cards_approval_demo", TaskName(typeof(ApprovalDemo), nameof(ApprovalDemo.ApprovalDemoTask)));
        Assert.Equal("question_demo", TaskName(typeof(QuestionDemo), nameof(QuestionDemo.QuestionDemoTask)));
        Assert.Equal("cancel_tool_demo", TaskName(typeof(CancelToolDemo), nameof(CancelToolDemo.CancelToolDemoTask)));
        Assert.Equal("cancel_sample_demo", TaskName(typeof(CancelSampleDemo), nameof(CancelSampleDemo.CancelSampleDemoTask)));
    }

    [Fact]
    public void the_example_is_discovered_by_the_runner_with_four_tasks_and_no_sandbox()
    {
        var example = Assert.IsType<InlineCardsExample>(ExampleRegistry.Default.Find("inline_cards"));

        Assert.Equal(["approval_demo", "question_demo", "cancel_tool_demo", "cancel_sample_demo"], example.Tasks.Select(task => task.Name));
        Assert.Equal("none", example.Defaults.Sandbox);
        Assert.Null(example.FakeSandbox(Context()));
        Assert.NotEmpty(example.Deviations);
        Assert.Equal(MockLlm.ModelName, example.CreateFakeModel(Context()).Name);
    }

    [Fact]
    public void the_tools_carry_the_python_docstrings()
    {
        var dangerous = ApprovalDemo.DangerousAction();
        Assert.Equal("dangerous_action", dangerous.Name);
        Assert.Equal("Perform an action that needs human approval before running.", dangerous.Description);
        Assert.Equal("Free-form description of what to do.", dangerous.Parameters.Properties["action"].Description);
        Assert.Equal(["action"], dangerous.Parameters.Required);

        var longRunning = CancelToolDemo.LongRunningTask(new ScriptedOperator(FastDelay));
        Assert.Equal("long_running_task", longRunning.Name);
        Assert.Equal(
            "Sleep for the requested duration, then return.\n\nUse this when a task legitimately needs to wait — e.g.\npolling an external service. Operators can cancel a running\ninvocation with ``^L`` in the TUI.",
            longRunning.Description);
        Assert.Equal("How long to sleep.", longRunning.Parameters.Properties["seconds"].Description);
        Assert.Equal(["integer"], longRunning.Parameters.Properties["seconds"].Type);

        var slow = CancelSampleDemo.SlowBusyWork(new ScriptedOperator(FastDelay));
        Assert.Equal("slow_busy_work", slow.Name);
        Assert.StartsWith("Pretend to be busy for a long time, then return.\n\nThe agent keeps the operator on the hook for ``seconds``", slow.Description, StringComparison.Ordinal);
        Assert.Equal("How long to sleep.", slow.Parameters.Properties["seconds"].Description);
    }

    [Fact]
    public void the_question_schemas_match_the_python_dicts()
    {
        var single = QuestionDemo.SingleQuestionSchema();
        Assert.Equal("""{"type":"object","properties":{"answer":{"type":"string","title":"Your answer","description":"Free-form text.","min_length":1}},"required":["answer"]}""", single.ToJsonString());

        var two = QuestionDemo.TwoQuestionSchema();
        Assert.Equal(["environment", "expiry"], two["properties"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal("Which environment? (staging / prod / …)", two["properties"]!["environment"]!["description"]!.GetValue<string>());
        Assert.Equal(["environment", "expiry"], two["required"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    // ----------------------------------------------------------------------------------------------------------
    // the operators (console analogues of ^L / ^N)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_console_operator_reads_enter_then_a_score_error_back_choice()
    {
        var output = new StringWriter();
        var op = new ConsoleOperator(new StringReader("\nx\ns\n"), output);

        Assert.Equal(CancelResolution.Score, await op.WaitForCancelSampleAsync("slow_busy_work", CancellationToken.None));
        Assert.Contains(ConsoleOperator.InvalidChoice, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Score (s), Error (e), or Back (b) [s/e/b] (b): ", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(CancelResolution.Back, await new ConsoleOperator(new StringReader("\n\n"), TextWriter.Null).WaitForCancelSampleAsync("t", CancellationToken.None));
        Assert.Equal(CancelResolution.Error, await new ConsoleOperator(new StringReader("\nerror\n"), TextWriter.Null).WaitForCancelSampleAsync("t", CancellationToken.None));
        await new ConsoleOperator(new StringReader("\n"), TextWriter.Null).WaitForCancelToolCallAsync("t", CancellationToken.None);

        // End of input: the operator never interrupts (the wait only ends with the token).
        using var cts = new CancellationTokenSource(FastDelay);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ConsoleOperator(new StringReader(""), TextWriter.Null).WaitForCancelToolCallAsync("t", cts.Token));
    }

    [Fact]
    public async Task the_scripted_operator_fires_after_its_delay_and_serves_resolutions_in_order()
    {
        var op = new ScriptedOperator(FastDelay, [CancelResolution.Back, CancelResolution.Error], TextWriter.Null);

        await op.WaitForCancelToolCallAsync("t", CancellationToken.None);
        Assert.Equal(1, op.ToolCallCancels);
        Assert.Equal(CancelResolution.Back, await op.WaitForCancelSampleAsync("t", CancellationToken.None));
        Assert.Equal(CancelResolution.Error, await op.WaitForCancelSampleAsync("t", CancellationToken.None));
        Assert.Equal([CancelResolution.Back, CancelResolution.Error], op.Resolutions);

        // Once the script is exhausted the card is never opened again.
        using var cts = new CancellationTokenSource(FastDelay);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => op.WaitForCancelSampleAsync("t", cts.Token));

        Assert.Equal([CancelResolution.Score, CancelResolution.Back], ScriptedOperator.ParseResolutions("Score, back"));
        Assert.Throws<ArgumentException>(() => ScriptedOperator.ParseResolutions("abort"));
        Assert.Throws<ArgumentException>(() => ScriptedOperator.ParseResolutions(""));
    }

    // ----------------------------------------------------------------------------------------------------------
    // approval_demo end to end
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task approval_demo_runs_offline_with_the_call_approved()
    {
        var prompter = new ScriptedApprovalPrompter(ApprovalDecision.Approve, TextWriter.Null);

        var log = await RunAsync(ApprovalDemo.Build(prompter: prompter));

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal(["dangerous_action", Agents.DefaultSubmitName], prompter.Requests.Select(request => request.Call.Function));
        Assert.All(prompter.Requests, request => Assert.Equal(HumanApprovals.DefaultChoices, request.Choices));

        var approvals = sample.Events.OfType<ApprovalEvent>().ToArray();
        Assert.Equal([("dangerous_action", "human", "approve", HumanApprovals.Approved), (Agents.DefaultSubmitName, "human", "approve", HumanApprovals.Approved)],
            approvals.Select(approval => (approval.Call.Function, approval.Approver, approval.Decision, approval.Explanation)));
        Assert.Equal("rm -rf /tmp/example", approvals[0].Call.Arguments["action"]!.GetValue<string>());

        var tool = Assert.Single(sample.Messages.OfType<ChatMessageTool>(), message => message.Function == "dangerous_action");
        Assert.Null(tool.Error);
        Assert.Equal("performed: rm -rf /tmp/example", tool.Text);
    }

    [Fact]
    public async Task approval_demo_with_a_rejection_gives_the_model_an_approval_error_and_still_submits()
    {
        var log = await RunAsync(ApprovalDemo.Build(prompter: new ScriptedApprovalPrompter(ApprovalDecision.Reject, TextWriter.Null)));

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        var tool = Assert.Single(sample.Messages.OfType<ChatMessageTool>(), message => message.Function == "dangerous_action");
        Assert.Equal("approval", tool.Error!.Type);
        Assert.Equal(HumanApprovals.Rejected, tool.Error.Message);
    }

    [Fact]
    public async Task approval_demo_with_terminate_ends_the_sample_with_an_operator_limit()
    {
        var log = await RunAsync(ApprovalDemo.Build(prompter: new ScriptedApprovalPrompter(ApprovalDecision.Terminate, TextWriter.Null)));

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("operator", sample.Limit!.Type);
        Assert.Equal("I", sample.Scores!["includes"].Text);
        Assert.DoesNotContain(sample.Events.OfType<ToolEvent>(), e => e.Function == Agents.DefaultSubmitName);
    }

    // ----------------------------------------------------------------------------------------------------------
    // question_demo end to end
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task question_demo_runs_offline_with_both_questions_answered()
    {
        var handler = new ScriptedInputHandler(QuestionDemo.FakeAnswers(), TextWriter.Null);

        var log = await RunAsync(QuestionDemo.Build(handler: handler));

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal([QuestionDemo.FirstMessage, QuestionDemo.SecondMessage], handler.Requests.Select(request => request.Message));
        Assert.Equal(["answer"], handler.Requests[0].Schema.Properties.Keys);
        Assert.Equal(["environment", "expiry"], handler.Requests[1].Schema.Properties.Keys);

        var inputs = sample.Events.OfType<InputEvent>().ToArray();
        Assert.Equal(2, inputs.Length);
        Assert.All(inputs, input => Assert.Equal("accepted", input.Outcome));
        Assert.Equal("sk-123", inputs[0].Content!["answer"]?.ToString());
        Assert.Equal("staging", inputs[1].Content!["environment"]?.ToString());

        var tools = sample.Messages.OfType<ChatMessageTool>().ToArray();
        Assert.Equal(["ask_user", "ask_user"], tools.Select(message => message.Function));
        Assert.Equal(["ask_user", "ask_user", Agents.DefaultSubmitName], sample.Events.OfType<ToolEvent>().Select(e => e.Function));
        Assert.Equal("""{"answer":"sk-123"}""", tools[0].Text);
        Assert.Equal("""{"environment":"staging","expiry":"2027-01"}""", tools[1].Text);
    }

    [Fact]
    public async Task question_demo_declined_twice_still_submits()
    {
        var log = await RunAsync(QuestionDemo.Build(handler: ScriptedInputHandler.Declining(TextWriter.Null)));

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal(2, sample.Messages.OfType<ChatMessageTool>().Count(message => message.Function == "ask_user" && message.Error is not null));
        Assert.Equal(["declined", "declined"], sample.Events.OfType<InputEvent>().Select(input => input.Outcome));
    }

    // ----------------------------------------------------------------------------------------------------------
    // cancel_tool_demo end to end (^L)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task cancel_tool_demo_gives_the_model_a_timeout_error_and_the_agent_submits()
    {
        var op = new ScriptedOperator(FastDelay, output: TextWriter.Null);
        var stopwatch = Stopwatch.StartNew();

        var log = await RunAsync(CancelToolDemo.Build(op));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"the 60s sleep was not cancelled ({stopwatch.Elapsed})");
        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Null(sample.Limit);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal(1, op.ToolCallCancels);

        var tools = sample.Messages.OfType<ChatMessageTool>().ToArray();
        Assert.Equal(["long_running_task"], tools.Select(message => message.Function));
        Assert.Equal(["long_running_task", Agents.DefaultSubmitName], sample.Events.OfType<ToolEvent>().Select(e => e.Function));
        Assert.Equal("timeout", tools[0].Error!.Type);
        Assert.Equal("Command timed out before completing.", tools[0].Error!.Message);

        var toolEvent = Assert.Single(sample.Events.OfType<ToolEvent>(), e => e.Function == "long_running_task");
        Assert.Equal("60", toolEvent.Arguments["seconds"]!.ToJsonString());
        Assert.Equal("timeout", toolEvent.Error!.Type);
        var interrupt = Assert.Single(sample.Events.OfType<InterruptEvent>());
        Assert.Equal("user_cancel", interrupt.Source);
        Assert.Equal("tool_call", interrupt.Interrupted);
        Assert.Equal(toolEvent.Id, interrupt.InterruptedToolCallId);
    }

    // ----------------------------------------------------------------------------------------------------------
    // cancel_sample_demo end to end (^N)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task cancel_sample_demo_score_ends_the_sample_cleanly_and_scores_it()
    {
        var op = new ScriptedOperator(FastDelay, [CancelResolution.Score], TextWriter.Null);
        var stopwatch = Stopwatch.StartNew();

        var log = await RunAsync(CancelSampleDemo.Build(op));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"the 600s sleep was not cancelled ({stopwatch.Elapsed})");
        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Null(sample.Error);
        Assert.Equal("operator", sample.Limit!.Type);
        Assert.Equal(CancelSampleDemo.ScoreReason, sample.Limit!.Reason);
        Assert.Equal("I", sample.Scores!["includes"].Text);
        Assert.Equal([CancelResolution.Score], op.Resolutions);
        // The submit turn is never reached: the sample-cancel path short-circuits the agent loop.
        Assert.DoesNotContain(sample.Events.OfType<ToolEvent>(), e => e.Function == Agents.DefaultSubmitName);
        var interrupt = Assert.Single(sample.Events.OfType<InterruptEvent>());
        Assert.Equal(("user_cancel", "tool_call"), (interrupt.Source, interrupt.Interrupted));
        Assert.Equal(Assert.Single(sample.Events.OfType<ToolEvent>()).Id, interrupt.InterruptedToolCallId);
        Assert.Contains(sample.Events.OfType<SampleLimitEvent>(), e => e.Type == "operator");
    }

    [Fact]
    public async Task cancel_sample_demo_back_lets_the_tool_continue_until_the_next_card()
    {
        var op = new ScriptedOperator(FastDelay, [CancelResolution.Back, CancelResolution.Score], TextWriter.Null);

        var log = await RunAsync(CancelSampleDemo.Build(op));

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal([CancelResolution.Back, CancelResolution.Score], op.Resolutions);
        Assert.Equal("operator", Assert.Single(log.Samples!).Limit!.Type);
    }

    [Fact]
    public async Task cancel_sample_demo_error_ends_the_sample_as_an_error()
    {
        var log = await RunAsync(CancelSampleDemo.Build(new ScriptedOperator(FastDelay, [CancelResolution.Error], TextWriter.Null)));

        Assert.Equal(EvalStatus.Error, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.NotNull(sample.Error);
        Assert.Contains(CancelSampleDemo.ErrorReason, sample.Error!.Message, StringComparison.Ordinal);
        Assert.Single(sample.Events.OfType<InterruptEvent>());
    }

    // ----------------------------------------------------------------------------------------------------------
    // the runner
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_runner_runs_every_task_offline()
    {
        var output = new StringWriter();
        Assert.Equal(0, await Run(output, "inline_cards", "--fake", "--log-dir", _logDir));
        Assert.Contains("[human approver] dangerous_action(rm -rf /tmp/example)", output.ToString(), StringComparison.Ordinal);

        output = new StringWriter();
        Assert.Equal(0, await Run(output, "inline_cards", "--fake", "--task", "question_demo", "--log-dir", _logDir));
        Assert.Contains("[ask_user] What's the API key for the staging service?", output.ToString(), StringComparison.Ordinal);

        output = new StringWriter();
        Assert.Equal(0, await Run(output, "inline_cards", "--fake", "--task", "cancel_tool_demo", "-T", "cancel_after=0.05", "--log-dir", _logDir));
        Assert.Contains("(scripted ^L)", output.ToString(), StringComparison.Ordinal);

        output = new StringWriter();
        Assert.Equal(0, await Run(output, "inline_cards", "--fake", "--task", "cancel_sample_demo", "-T", "cancel_after=0.05", "-T", "resolution=back,score", "--log-dir", _logDir));
        Assert.Contains("-> Back (scripted)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("-> Score (scripted)", output.ToString(), StringComparison.Ordinal);

        // Error is the sample's error: the log is not a success (exit 1); a bad -T value is a usage error (exit 2).
        Assert.Equal(1, await Run(TextWriter.Null, "inline_cards", "--fake", "--task", "cancel_sample_demo", "-T", "cancel_after=0.05", "-T", "resolution=error", "--log-dir", _logDir));
        Assert.Equal(2, await Run(TextWriter.Null, "inline_cards", "--fake", "--task", "cancel_sample_demo", "-T", "resolution=abort", "--log-dir", _logDir));
        Assert.Equal(2, await Run(TextWriter.Null, "inline_cards", "--fake", "-T", "decision=maybe", "--log-dir", _logDir));
    }

    private static Task<int> Run(TextWriter output, params string[] args) => ExampleRunner.MainAsync(args, ExampleRegistry.Default, output, output);

    private async Task<EvalLog> RunAsync(EvalTask task) =>
        await Eval.RunAsync(task, new EvalOptions { LogDir = _logDir, LogFormat = LogFormat.Eval }, CancellationToken.None);

    private static string? TaskName(Type type, string method) => type.GetMethod(method)!.GetCustomAttribute<TaskAttribute>()!.Name;

    private static ExampleContext Context() =>
        new(Path.Combine(AppContext.BaseDirectory, "inline_cards"), null, true, new Dictionary<string, string>(), null, null, TextWriter.Null);
}
