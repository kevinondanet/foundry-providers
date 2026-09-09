using System.Reflection;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Approval;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Approval;

// Declared inside the namespace so that `Eval` names the runner rather than the InspectAzureAI.Eval namespace
// (which the enclosing InspectAzureAI namespace would otherwise supply).
using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// Tests for the port of <c>examples/approval/approval.py</c> <c>approval_demo</c> (<see cref="ApprovalDemo"/>): the
/// task's shape, and the README's <c>inspect eval approval.py --approval approval.yaml</c> run end to end without a
/// network, Docker or a terminal — <see cref="FakeApprovalModel"/> plays the model, a fake sandbox provider stands in
/// for Docker and a scripted prompter for the human at the console — with every decision read back from the log's
/// <see cref="ApprovalEvent"/>s. Explanations are asserted verbatim: they are what the Python original logs.
/// </summary>
public sealed class ApprovalDemoTests : IDisposable
{
    /// <summary>The first <c>Sample(input=...)</c> of <c>approval_demo</c>, as written in <c>approval.py</c>.</summary>
    private const string BashInput =
        "Please use the bash tool to demonstrate the use of the bash ls command, then demonstrate the use of the bash rm command.";

    /// <summary>The second <c>Sample(input=...)</c>, as written in <c>approval.py</c> (its wording is the Python's).</summary>
    private const string PythonInput =
        "Please use the python tool to the use of the Python print function, then demonstrate the math.factorial function, then demonstrate the use of the shutil.rmtree function.";

    /// <summary>A guard against a runaway react loop (a policy that rejected submit would otherwise re-prompt the scripted model forever); the two scripts need about 8 and 10 messages.</summary>
    private const int MessageLimit = 40;

    /// <summary>The example policy (the JSON form of <c>approval.yaml</c>), copied next to the test assembly by the test project.</summary>
    private static readonly string PolicyPath = Path.Combine(AppContext.BaseDirectory, "approval", "approval.json");

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "approval-demo-" + Guid.NewGuid().ToString("N"));

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
    // the task (port of @task def approval_demo)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_approval_demo()
    {
        var task = ApprovalDemo.ApprovalDemoTask();

        Assert.Equal("approval_demo", task.Name);
        Assert.Equal(new SandboxSpec("docker"), task.Sandbox);
        Assert.Equal(2, task.Dataset.Count);
        Assert.Equal(BashInput, task.Dataset[0].Input.Text);
        Assert.Equal(PythonInput, task.Dataset[1].Input.Text);
        // As in Python the task carries no policy of its own (it comes from --approval) and no scorer.
        Assert.Null(task.Approval);
        Assert.Empty(task.Scorers);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli_as_approval_demo()
    {
        var method = typeof(ApprovalDemo).GetMethod(nameof(ApprovalDemo.ApprovalDemoTask));

        Assert.NotNull(method);
        Assert.True(method!.IsStatic);
        var attribute = method.GetCustomAttribute<TaskAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal("approval_demo", attribute!.Name);
    }

    [Fact]
    public void building_the_task_registers_the_example_approvers_so_the_policy_file_resolves()
    {
        ApprovalDemo.ApprovalDemoTask();

        Assert.True(ApproverRegistry.IsRegistered("bash_allowlist"));
        Assert.True(ApproverRegistry.IsRegistered("python_allowlist"));
        Assert.True(File.Exists(PolicyPath), $"approval.json was not copied next to the test assembly: {PolicyPath}");
        var policies = ApprovalOption.FromSpec(PolicyPath).Resolve();
        Assert.Equal(["bash_allowlist", "python_allowlist", "human"], policies.Select(policy => policy.Approver.Name));
    }

    [Fact]
    public void build_uses_the_sandbox_it_is_given_and_rejects_none()
    {
        var local = ApprovalDemo.Build(new SandboxSpec("local"));

        Assert.Equal("approval_demo", local.Name);
        Assert.Equal(new SandboxSpec("local"), local.Sandbox);
        Assert.Equal(2, local.Dataset.Count);
        Assert.Throws<ArgumentNullException>(() => ApprovalDemo.Build(null!));
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end (port of `inspect eval approval.py --approval approval.yaml`, offline)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_demo_runs_offline_under_the_example_policy_and_logs_every_decision()
    {
        Assert.True(File.Exists(PolicyPath), $"approval.json was not copied next to the test assembly: {PolicyPath}");
        ExampleApprovers.Register();
        var provider = new FakeSandboxProvider();
        SandboxRegistry.Register(provider);
        var prompter = new ScriptedHumanPrompter();
        var previous = Approvers.DefaultPrompter;

        EvalLog log;
        Approvers.DefaultPrompter = prompter;
        try
        {
            var task = ApprovalDemo.Build(new SandboxSpec(FakeSandboxProvider.TypeName));
            log = await Eval.RunAsync(
                task,
                new EvalOptions
                {
                    Model = FakeApprovalModel.Create(),
                    Approval = ApprovalOption.FromSpec(PolicyPath),
                    LogDir = _logDir,
                    LogFormat = LogFormat.Eval,
                    // One sample at a time keeps the order of sandbox commands and human prompts deterministic.
                    MaxSamples = 1,
                    MessageLimit = MessageLimit,
                },
                CancellationToken.None);
        }
        finally
        {
            Approvers.DefaultPrompter = previous;
        }

        Assert.Same(previous, Approvers.DefaultPrompter);

        // The run: both samples complete, the log is written and records the three policies of approval.yaml.
        if (log.Status != EvalStatus.Success)
        {
            Assert.Fail(Describe(log));
        }

        Assert.Null(log.Error);
        Assert.Equal(2, log.Results!.TotalSamples);
        Assert.Equal(2, log.Results.CompletedSamples);
        Assert.NotNull(log.Location);
        Assert.EndsWith(".eval", log.Location, StringComparison.Ordinal);
        Assert.True(File.Exists(log.Location), $"the log was not written: {log.Location}");
        Assert.Equal(
            ["bash_allowlist", "python_allowlist", "human"],
            log.Eval.Config.Approval!["approvers"]!.AsArray().Select(approver => approver!["name"]!.GetValue<string>()));

        var samples = log.Samples!;
        Assert.Equal(2, samples.Count);
        Assert.All(samples, sample => Assert.Null(sample.Error));
        var bash = SampleFor(samples, BashInput);
        var python = SampleFor(samples, PythonInput);

        // Bash sample: `ls -la` is on the allowlist; `rm` is not, so bash_allowlist escalates and the human rejects.
        (string Approver, string Decision, string? Explanation)[] expectedBash =
        [
            ("bash_allowlist", "approve", "Command 'ls -la' is approved."),
            ("bash_allowlist", "escalate", "Command 'rm' is not in the allowed list. Allowed commands: ls, echo, cat"),
            ("human", "reject", HumanApprovals.Rejected),
        ];
        Assert.Equal(expectedBash, ToolDecisions(bash));
        Assert.Equal(["ls -la", "rm -rf /tmp/demo", "rm -rf /tmp/demo"], ToolArguments(bash));
        Assert.Equal(["ok", "approval"], ToolErrors(bash));
        Assert.Equal(HumanApprovals.Rejected, ToolMessages(bash)[^1].Error!.Message);

        // Python sample: print and math are allowed; shutil is not an allowed module, so python_allowlist escalates
        // and the human rejects.
        (string Approver, string Decision, string? Explanation)[] expectedPython =
        [
            ("python_allowlist", "approve", "Python code is approved."),
            ("python_allowlist", "approve", "Python code is approved."),
            ("python_allowlist", "escalate", "Module 'shutil' is not in the allowed list. Allowed modules: math"),
            ("human", "reject", HumanApprovals.Rejected),
        ];
        Assert.Equal(expectedPython, ToolDecisions(python));
        Assert.Equal(
            ["print('hello')", "import math\nprint(math.factorial(5))", "import shutil\nshutil.rmtree('/tmp/demo')", "import shutil\nshutil.rmtree('/tmp/demo')"],
            ToolArguments(python));
        Assert.Equal(["ok", "ok", "approval"], ToolErrors(python));
        Assert.Equal(HumanApprovals.Rejected, ToolMessages(python)[^1].Error!.Message);

        // The policy's `human: "*"` entry routes the react agent's submit call to the human as well (bash_allowlist
        // and python_allowlist only match their tools); the scripted human approves it, which ends each sample.
        foreach (var sample in new[] { bash, python })
        {
            var submit = Approvals(sample)[^1];
            Assert.Equal(Agents.DefaultSubmitName, submit.Call.Function);
            Assert.Equal("human", submit.Approver);
            Assert.Equal("approve", submit.Decision);
            Assert.Equal(HumanApprovals.Approved, submit.Explanation);
            Assert.Single(Approvals(sample), approval => approval.Call.Function == Agents.DefaultSubmitName);
        }

        // The human was consulted exactly for the escalations and the two submits, with Python's default choices.
        Assert.Equal(["bash", Agents.DefaultSubmitName, "python", Agents.DefaultSubmitName], prompter.Requests.Select(request => request.Call.Function));
        Assert.All(prompter.Requests, request => Assert.Equal(HumanApprovals.DefaultChoices, request.Choices));

        // The sandbox: one environment per sample, and only the approved calls reached it.
        Assert.Equal(1, provider.TaskInits);
        Assert.Equal(1, provider.TaskCleanups);
        Assert.Equal(["1", "2"], provider.SampleIds);
        var executed = provider.Environments.SelectMany(environment => environment.Calls).ToArray();
        Assert.All(executed, call => Assert.Equal(["bash", "--login", "-c"], call.Cmd.Take(3)));
        Assert.Equal(["ls -la", "print('hello')", "import math\nprint(math.factorial(5))"], executed.Select(call => call.Input ?? call.Cmd[^1]));
        Assert.DoesNotContain(executed, call => (call.Input ?? call.Cmd[^1]).Contains("rm", StringComparison.Ordinal));
    }

    // ----------------------------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------------------------

    private static EvalSample SampleFor(IReadOnlyList<EvalSample> samples, string input) =>
        Assert.Single(samples, sample => sample.Input.Text == input);

    private static ApprovalEvent[] Approvals(EvalSample sample) => sample.Events.OfType<ApprovalEvent>().ToArray();

    /// <summary>The decisions on the sample's bash / python calls (the submit call is asserted separately).</summary>
    private static (string Approver, string Decision, string? Explanation)[] ToolDecisions(EvalSample sample) =>
        Approvals(sample)
            .Where(approval => approval.Call.Function != Agents.DefaultSubmitName)
            .Select(approval => (approval.Approver, approval.Decision, approval.Explanation))
            .ToArray();

    /// <summary>The first argument of each decided bash / python call, whatever its name (what the approvers read); "" when absent.</summary>
    private static string[] ToolArguments(EvalSample sample) =>
        Approvals(sample)
            .Where(approval => approval.Call.Function != Agents.DefaultSubmitName)
            .Select(approval => approval.Call.Arguments.Select(pair => pair.Value?.GetValue<string>()).FirstOrDefault() ?? "")
            .ToArray();

    private static ChatMessageTool[] ToolMessages(EvalSample sample) =>
        sample.Messages.OfType<ChatMessageTool>().Where(message => message.Function != Agents.DefaultSubmitName).ToArray();

    /// <summary>The error type of each bash / python tool result, or "ok" when the call ran.</summary>
    private static string[] ToolErrors(EvalSample sample) => ToolMessages(sample).Select(message => message.Error?.Type ?? "ok").ToArray();

    private static string Describe(EvalLog log)
    {
        var lines = new List<string> { $"status {log.Status}: {log.Error?.Message ?? "(no error)"}" };
        foreach (var sample in log.Samples ?? [])
        {
            lines.Add($"sample {sample.Id} (epoch {sample.Epoch}): {sample.Error?.Message ?? "ok"}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The human at the terminal, scripted the way the console app's <c>--fake</c> prompter is: rejects every call
    /// escalated to it and approves only the react agent's submit call (which the policy's <c>human: "*"</c> entry
    /// routes to the human too), keeping every request it was shown.
    /// </summary>
    private sealed class ScriptedHumanPrompter : IApprovalPrompter
    {
        public List<ApprovalRequest> Requests { get; } = [];

        public Task<ApprovalDecision> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Requests)
            {
                Requests.Add(request);
            }

            var decision = request.Call.Function == Agents.DefaultSubmitName ? ApprovalDecision.Approve : ApprovalDecision.Reject;
            return Task.FromResult(decision);
        }
    }

    /// <summary>
    /// Stands in for the Docker provider of <c>sandbox="docker"</c>: hands each sample a
    /// <see cref="FakeSandboxEnvironment"/> (every command succeeds with empty output) and counts the task-level
    /// lifecycle calls.
    /// </summary>
    private sealed class FakeSandboxProvider : ISandboxProvider
    {
        public const string TypeName = "fake-approval-demo";

        private readonly object _sync = new();

        public string Type => TypeName;

        public int TaskInits { get; private set; }

        public int TaskCleanups { get; private set; }

        public List<FakeSandboxEnvironment> Environments { get; } = [];

        /// <summary>The <c>__sample_id__</c> metadata each environment was created for, in creation order.</summary>
        public List<string> SampleIds { get; } = [];

        public Task TaskInitAsync(string taskName, string? config, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                TaskInits++;
            }

            return Task.CompletedTask;
        }

        public Task<SandboxEnvironments> SampleInitAsync(string taskName, string? config, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        {
            var environment = new FakeSandboxEnvironment();
            lock (_sync)
            {
                Environments.Add(environment);
                SampleIds.Add(metadata.TryGetValue("__sample_id__", out var id) ? id : "");
            }

            return Task.FromResult(SandboxEnvironments.Single(environment));
        }

        public Task TaskCleanupAsync(string taskName, string? config, bool cleanup, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                TaskCleanups++;
            }

            return Task.CompletedTask;
        }
    }
}
