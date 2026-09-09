using System.Reflection;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Examples.ToolUse;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.ToolUse;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the port of <c>examples/tool_use.py</c> (<see cref="ToolUseTasks"/>, <see cref="ToolUseTools"/>,
/// <see cref="ToolUseExample"/>): the five tasks' shapes, the tools' schemas and behaviour, and each task run end
/// to end offline against the scripted model and the fake sandbox (plus the real local sandbox for <c>bash</c>).
/// </summary>
public sealed class ToolUseTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "tool-use-" + Guid.NewGuid().ToString("N"));

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
    // the tasks (port of the five @task functions)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_tasks_are_named_and_shaped_like_the_python_tasks()
    {
        var addition = ToolUseTasks.AdditionProblem();
        Assert.Equal("addition_problem", addition.Name);
        Assert.Equal("What is 1 + 1?", Assert.Single(addition.Dataset).Input.Text);
        Assert.Equal(new Target(["2", "2.0"]), addition.Dataset[0].Target);
        Assert.Null(addition.Sandbox);
        Assert.Equal("match", Assert.Single(addition.Scorers).Name);

        var bash = ToolUseTasks.Bash();
        Assert.Equal("bash", bash.Name);
        Assert.Equal("Please list the files in the /usr/bin directory. Is there a file named 'python3' in the directory?", Assert.Single(bash.Dataset).Input.Text);
        Assert.Equal(new Target(["Yes"]), bash.Dataset[0].Target);
        Assert.Equal(new SandboxSpec("local"), bash.Sandbox);
        Assert.Equal("includes", Assert.Single(bash.Scorers).Name);

        var read = ToolUseTasks.Read();
        Assert.Equal("read", read.Name);
        Assert.Equal("Please read the file 'foo.txt'", Assert.Single(read.Dataset).Input.Text);
        Assert.Equal(Target.Empty, read.Dataset[0].Target);
        Assert.Equal(new SandboxSpec("local"), read.Sandbox);
        Assert.Equal("match", Assert.Single(read.Scorers).Name);

        var write = ToolUseTasks.Write();
        Assert.Equal("write", write.Name);
        Assert.Equal("Please write 'bar' to a file named 'foo.txt'.", Assert.Single(write.Dataset).Input.Text);
        Assert.Equal(new SandboxSpec("local"), write.Sandbox);
        Assert.Equal("match", Assert.Single(write.Scorers).Name);

        var parallel = ToolUseTasks.ParallelAdd();
        Assert.Equal("parallel_add", parallel.Name);
        Assert.StartsWith("Please add the numbers 1+1 and 2+2, and then print the results", Assert.Single(parallel.Dataset).Input.Text, StringComparison.Ordinal);
        Assert.EndsWith("so the results are computed faster.", parallel.Dataset[0].Input.Text, StringComparison.Ordinal);
        Assert.Equal(new Target(["2 4"]), parallel.Dataset[0].Target);
        Assert.Null(parallel.Sandbox);
        Assert.Equal("includes", Assert.Single(parallel.Scorers).Name);

        Assert.Equal("\nPlease answer exactly Yes or No with no additional words.\n", ToolUseTasks.SystemMessage);
    }

    [Fact]
    public void the_task_methods_are_discoverable_by_the_cli_under_the_python_names()
    {
        var names = typeof(ToolUseTasks)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<TaskAttribute>()?.Name)
            .Where(name => name is not null)
            .ToList();

        Assert.Equal(["addition_problem", "bash", "read", "write", "parallel_add"], names);
    }

    [Fact]
    public void the_sandbox_tasks_take_the_sandbox_they_are_given()
    {
        var fake = new SandboxSpec("fake");

        Assert.Equal(fake, ToolUseTasks.Bash(fake).Sandbox);
        Assert.Equal(fake, ToolUseTasks.Read(fake).Sandbox);
        Assert.Equal(fake, ToolUseTasks.Write(fake).Sandbox);
        Assert.Throws<ArgumentNullException>(() => ToolUseTasks.Bash(null!));
    }

    [Fact]
    public void the_example_declares_the_five_tasks_and_a_local_sandbox()
    {
        var example = new ToolUseExample();

        Assert.Equal("tool_use", example.Name);
        Assert.Equal(["addition_problem", "bash", "read", "write", "parallel_add"], example.Tasks.Select(task => task.Name));
        Assert.Equal("local", example.Defaults.Sandbox);
        Assert.False(example.Defaults.NeedsDocker);
        Assert.NotEmpty(example.Deviations);
        Assert.NotNull(example.FakeSandbox(Context(null)));
        Assert.IsType<ToolUseExample>(ExampleRegistry.Default.Find("tool_use"));
    }

    [Fact]
    public void the_sandbox_tasks_refuse_to_build_without_a_sandbox()
    {
        var example = new ToolUseExample();

        Assert.Throws<PrerequisiteError>(() => example.Tasks[1].Build(Context(null)));
        Assert.Throws<PrerequisiteError>(() => example.Tasks[2].Build(Context(null)));
        Assert.Throws<PrerequisiteError>(() => example.Tasks[3].Build(Context(null)));
        Assert.Null(example.Tasks[0].Build(Context(null)).Sandbox);
        Assert.Null(example.Tasks[4].Build(Context(null)).Sandbox);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the tools (port of the four @tool functions)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_tools_carry_the_python_names_descriptions_and_parameters()
    {
        var add = ToolUseTools.Add();
        Assert.Equal("add", add.Name);
        Assert.Equal("Add two numbers.", add.Description);
        Assert.Equal(["x", "y"], add.Parameters.Required);
        Assert.Equal(["integer"], add.Parameters.Properties["x"].Type);
        Assert.Equal("First number to add.", add.Parameters.Properties["x"].Description);
        Assert.Equal("Second number to add.", add.Parameters.Properties["y"].Description);
        Assert.True(add.Parallel);

        var listFiles = ToolUseTools.ListFiles();
        Assert.Equal("list_files", listFiles.Name);
        Assert.Equal("List the files in a directory.", listFiles.Description);
        Assert.Equal(["dir"], listFiles.Parameters.Required);
        Assert.Equal("Directory", listFiles.Parameters.Properties["dir"].Description);
        Assert.Equal(["string"], listFiles.Parameters.Properties["dir"].Type);

        var readFile = ToolUseTools.ReadFile();
        Assert.Equal("read_file", readFile.Name);
        Assert.Equal("Read the contents of a file.", readFile.Description);
        Assert.Equal(["file"], readFile.Parameters.Required);
        Assert.Equal("File to read", readFile.Parameters.Properties["file"].Description);

        var writeFile = ToolUseTools.WriteFile();
        Assert.Equal("write_file", writeFile.Name);
        Assert.Equal("Write content to a file.", writeFile.Description);
        Assert.Equal(["file", "contents"], writeFile.Parameters.Required);
        Assert.Equal("File to write", writeFile.Parameters.Properties["file"].Description);
        Assert.Equal("Contents of file", writeFile.Parameters.Properties["contents"].Description);
    }

    [Fact]
    public async Task add_returns_the_sum_as_text()
    {
        var result = await ToolUseTools.Add().Execute(new JsonObject { ["x"] = 1, ["y"] = 1 }, CancellationToken.None);

        Assert.Equal("2", result.AsText());
    }

    [Fact]
    public async Task list_files_returns_stdout_and_raises_a_tool_error_with_stderr()
    {
        var sandbox = new FakeSandboxEnvironment(cmd => cmd[1] == "/usr/bin"
            ? FakeSandboxEnvironment.Ok("python3\nsh\n")
            : FakeSandboxEnvironment.Fail(2, "ls: cannot access '/nope': No such file or directory"));
        using var scope = SampleContext.Begin(SampleContextFor(sandbox));

        var listing = await ToolUseTools.ListFiles().Execute(new JsonObject { ["dir"] = "/usr/bin" }, CancellationToken.None);
        var error = await Assert.ThrowsAsync<ToolError>(() => ToolUseTools.ListFiles().Execute(new JsonObject { ["dir"] = "/nope" }, CancellationToken.None));

        Assert.Equal("python3\nsh\n", listing.AsText());
        Assert.Equal("ls: cannot access '/nope': No such file or directory", error.Message);
        Assert.Equal(["ls", "/usr/bin"], sandbox.Calls[0].Cmd);
    }

    [Fact]
    public async Task read_file_and_write_file_go_through_the_sandbox()
    {
        var sandbox = new FakeSandboxEnvironment();
        using var scope = SampleContext.Begin(SampleContextFor(sandbox));

        var written = await ToolUseTools.WriteFile().Execute(new JsonObject { ["file"] = "foo.txt", ["contents"] = "bar" }, CancellationToken.None);
        var read = await ToolUseTools.ReadFile().Execute(new JsonObject { ["file"] = "foo.txt" }, CancellationToken.None);

        Assert.Equal("", written.AsText());
        Assert.Equal("bar", read.AsText());
        await Assert.ThrowsAsync<FileNotFoundException>(() => ToolUseTools.ReadFile().Execute(new JsonObject { ["file"] = "missing.txt" }, CancellationToken.None));
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end, offline (the scripted model and the fake sandbox of the example)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task addition_problem_calls_add_and_matches_the_numeric_target()
    {
        var (log, _) = await RunAsync("addition_problem");

        AssertSuccess(log);
        var sample = Assert.Single(log.Samples!);
        var tool = Assert.Single(sample.Messages.OfType<ChatMessageTool>());
        Assert.Equal("add", tool.Function);
        Assert.Equal("2", tool.Text);
        Assert.Equal("2", sample.Output.Completion);
        Assert.Equal("C", sample.Scores!["match"].Text);
        Assert.Equal(1.0, Metric(log, "match", "accuracy"));
    }

    [Fact]
    public async Task bash_lists_usr_bin_through_the_fake_sandbox_and_answers_yes()
    {
        var (log, script) = await RunAsync("bash");

        AssertSuccess(log);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("\nPlease answer exactly Yes or No with no additional words.\n", Assert.Single(sample.Messages.OfType<ChatMessageSystem>()).Text);
        var call = Assert.Single(script.Calls);
        Assert.Equal(["ls", "/usr/bin"], call.Cmd);
        Assert.Equal(ToolUseExample.UsrBinListing, Assert.Single(sample.Messages.OfType<ChatMessageTool>()).Text);
        Assert.Equal("Yes", sample.Output.Completion);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal(1.0, Metric(log, "includes", "accuracy"));
    }

    [Fact]
    public async Task read_reads_the_seeded_file_through_the_fake_sandbox()
    {
        var (log, script) = await RunAsync("read");

        AssertSuccess(log);
        var sample = Assert.Single(log.Samples!);
        var tool = Assert.Single(sample.Messages.OfType<ChatMessageTool>());
        Assert.Equal("read_file", tool.Function);
        Assert.Null(tool.Error);
        Assert.Equal("bar", tool.Text);
        Assert.Equal("The file 'foo.txt' contains: bar", sample.Output.Completion);
        Assert.Equal("C", sample.Scores!["match"].Text);
        Assert.Empty(script.Calls);
    }

    [Fact]
    public async Task write_writes_bar_into_the_fake_sandbox()
    {
        var (log, script) = await RunAsync("write");

        AssertSuccess(log);
        var sample = Assert.Single(log.Samples!);
        var tool = Assert.Single(sample.Messages.OfType<ChatMessageTool>());
        Assert.Equal("write_file", tool.Function);
        Assert.Null(tool.Error);
        Assert.Equal("Wrote 'bar' to foo.txt.", sample.Output.Completion);
        Assert.Equal("bar", Assert.Single(script.Environments).FileText("foo.txt"));
        Assert.Equal("C", sample.Scores!["match"].Text);
    }

    [Fact]
    public async Task parallel_add_makes_two_add_calls_in_one_turn_and_prints_both_sums()
    {
        var (log, _) = await RunAsync("parallel_add");

        AssertSuccess(log);
        var sample = Assert.Single(log.Samples!);
        var calls = Assert.Single(sample.Messages.OfType<ChatMessageAssistant>(), message => message.ToolCalls is { Count: > 0 }).ToolCalls!;
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Equal("add", call.Function));
        Assert.Equal(["2", "4"], sample.Messages.OfType<ChatMessageTool>().Select(message => message.Text));
        Assert.Equal("2 4", sample.Output.Completion);
        Assert.Equal("C", sample.Scores!["includes"].Text);
        Assert.Equal(1.0, Metric(log, "includes", "accuracy"));
    }

    [Fact]
    public async Task bash_runs_the_real_ls_under_the_local_sandbox()
    {
        var example = new ToolUseExample();
        var context = Context(new SandboxSpec("local"));

        var log = await Eval.RunAsync(
            example.Tasks[1].Build(context),
            new EvalOptions { Model = example.CreateFakeModel(context), LogDir = _logDir, LogFormat = LogFormat.Eval },
            CancellationToken.None);

        AssertSuccess(log);
        var sample = Assert.Single(log.Samples!);
        var tool = Assert.Single(sample.Messages.OfType<ChatMessageTool>());
        Assert.Null(tool.Error);
        Assert.NotEmpty(tool.Text);
        Assert.Contains(sample.Output.Completion, new[] { "Yes", "No" });
        Assert.Equal(new SandboxSpec("local"), sample.Sandbox);
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_and_refuses_sandbox_none_for_the_sandbox_tasks()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["tool_use", "--task", "parallel_add", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("task      : parallel_add", text);
        Assert.Contains("status    : success (1/1 samples completed)", text);
        Assert.Contains("includes", text);

        var error = new StringWriter();
        Assert.Equal(2, await ExampleRunner.MainAsync(["tool_use", "--task", "bash", "--fake", "--sandbox", "none", "--log-dir", _logDir], ExampleRegistry.Default, TextWriter.Null, error));
        Assert.Contains("bash needs a sandbox", error.ToString());
    }

    // ----------------------------------------------------------------------------------------------------------
    // support
    // ----------------------------------------------------------------------------------------------------------

    /// <summary>Runs one task of the example the way the runner does under <c>--fake</c>: the scripted model and the registered fake sandbox.</summary>
    private async Task<(EvalLog Log, FakeSandboxScript Script)> RunAsync(string taskName)
    {
        var example = new ToolUseExample();
        var script = example.FakeSandbox(Context(null))!;
        var context = Context(ScriptedSandboxProvider.Register(script));
        var task = example.Tasks.Single(candidate => candidate.Name == taskName).Build(context);

        var log = await Eval.RunAsync(
            task,
            new EvalOptions { Model = example.CreateFakeModel(context), LogDir = _logDir, LogFormat = LogFormat.Eval },
            CancellationToken.None);
        return (log, script);
    }

    private static ExampleContext Context(SandboxSpec? sandbox) =>
        new(Path.Combine(AppContext.BaseDirectory, "tool_use"), sandbox, true, new Dictionary<string, string>(), null, null, TextWriter.Null);

    private static SampleContext SampleContextFor(ISandboxEnvironment sandbox) =>
        new() { ActiveModel = new Model(new ScriptedModelApi()), Sandboxes = SandboxEnvironments.Single(sandbox) };

    private static void AssertSuccess(EvalLog log)
    {
        if (log.Status != EvalStatus.Success)
        {
            var sampleErrors = string.Join("; ", (log.Samples ?? []).Where(sample => sample.Error is not null).Select(sample => sample.Error!.Message));
            Assert.Fail($"status {log.Status}: {log.Error?.Message} {sampleErrors}");
        }

        Assert.Equal(1, log.Results!.CompletedSamples);
        Assert.NotNull(log.Location);
        Assert.True(File.Exists(log.Location), $"the log was not written: {log.Location}");
    }

    private static double Metric(EvalLog log, string scorer, string metric) =>
        log.Results!.Scores.Single(score => score.Name == scorer).Metrics[metric].Value;
}
