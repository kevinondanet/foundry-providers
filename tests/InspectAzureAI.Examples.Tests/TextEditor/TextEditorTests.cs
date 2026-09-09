using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Support;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Examples.TextEditor;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.TextEditor;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the port of <c>examples/text_editor.py</c> (<see cref="TextEditorTask"/>, <see cref="TextEditorExample"/>):
/// the task's shape, the <c>verify_edit</c> scorer, the fake launcher's editor messages (the launcher's own texts),
/// and the whole four-turn edit run end to end offline through the scripted model and the JSON-RPC-emulating
/// fake sandbox, checked down to the requests the tool put on the wire.
/// </summary>
public sealed class TextEditorTests : IDisposable
{
    private const string FinalSource = "def greet(name):\n# Author: Inspector\n    return f'Goodbye, {name}!'\n";

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "text-editor-" + Guid.NewGuid().ToString("N"));

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
    // the task (port of @task def text_editor_task)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_task_is_named_and_shaped_like_the_python_task()
    {
        var task = TextEditorTask.Create();

        Assert.Equal("text_editor_task", task.Name);
        Assert.Equal(new SandboxSpec("docker"), task.Sandbox);
        var sample = Assert.Single(task.Dataset);
        Assert.Equal(
            "Use the text_editor tool to create a file at /tmp/greeting.py with a Python function called `greet` that takes a `name` parameter and returns the string 'Hello, {name}!'.",
            sample.Input.Text);
        Assert.Equal(new Target("Goodbye"), sample.Target);
        Assert.Equal("verify_edit", Assert.Single(task.Scorers).Name);
        Assert.Equal(["accuracy"], task.Scorers[0].Metrics.Select(metric => metric.Name));
        Assert.Equal("Now use the text_editor to view the file /tmp/greeting.py and confirm its contents.", TextEditorTask.ViewMessage);
        Assert.Equal("Now use the text_editor str_replace command to change 'Hello' to 'Goodbye' in /tmp/greeting.py.", TextEditorTask.ReplaceMessage);
        Assert.Equal("Now use the text_editor insert command to insert the line '# Author: Inspector' after line 1 of /tmp/greeting.py.", TextEditorTask.InsertMessage);
    }

    [Fact]
    public void the_task_method_is_discoverable_by_the_cli()
    {
        var attribute = typeof(TextEditorTask).GetMethod(nameof(TextEditorTask.Create))!.GetCustomAttribute<TaskAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("text_editor_task", attribute!.Name);
        Assert.Equal(new SandboxSpec("fake"), TextEditorTask.Build(new SandboxSpec("fake")).Sandbox);
        Assert.Throws<ArgumentNullException>(() => TextEditorTask.Build(null!));
    }

    [Fact]
    public void the_example_needs_docker_and_refuses_to_build_without_a_sandbox()
    {
        var example = new TextEditorExample();

        Assert.Equal("text_editor", example.Name);
        Assert.Equal(["text_editor_task"], example.Tasks.Select(task => task.Name));
        Assert.Equal("docker", example.Defaults.Sandbox);
        Assert.True(example.Defaults.NeedsDocker);
        Assert.NotEmpty(example.Deviations);
        Assert.NotNull(example.FakeSandbox(Context(null)));
        Assert.Throws<PrerequisiteError>(() => example.Tasks[0].Build(Context(null)));
        Assert.IsType<TextEditorExample>(ExampleRegistry.Default.Find("text_editor"));
    }

    // ----------------------------------------------------------------------------------------------------------
    // the scorer (port of verify_edit)
    // ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FinalSource, 1.0, "File contains expected edit.")]
    [InlineData("def greet(name):\n# Author: Inspector\n    return f'Hello, {name}!'\n", 0.0, "Edit not applied correctly.")]
    [InlineData("def greet(name):\n    return f'Goodbye, {name}!'\n", 0.0, "Edit not applied correctly.")]
    public async Task verify_edit_reads_the_file_from_the_sandbox(string content, double expected, string explanation)
    {
        var sandbox = new FakeSandboxEnvironment();
        sandbox.Files["/tmp/greeting.py"] = Encoding.UTF8.GetBytes(content);
        using var scope = SampleContext.Begin(SampleContextFor(sandbox));

        // The scorer never touches the state (Python's verify_edit ignores it too).
        var score = await TextEditorTask.VerifyEditScore(null!, new Target("Goodbye"), CancellationToken.None);

        Assert.Equal(expected, score.AsFloat());
        Assert.Equal(content, score.Answer);
        Assert.Equal(explanation, score.Explanation);
    }

    [Fact]
    public async Task verify_edit_scores_a_missing_file_zero()
    {
        using var scope = SampleContext.Begin(SampleContextFor(new FakeSandboxEnvironment()));

        var score = await TextEditorTask.VerifyEditScore(null!, new Target("Goodbye"), CancellationToken.None);

        Assert.Equal(0.0, score.AsFloat());
        Assert.Equal("File not found", score.Answer);
        Assert.Equal("The file /tmp/greeting.py was not created.", score.Explanation);
    }

    // ----------------------------------------------------------------------------------------------------------
    // the fake launcher's editor (the messages of inspect_sandbox_tools' text_editor.py)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void the_emulator_answers_create_view_str_replace_and_insert_like_the_launcher()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        const string path = "/tmp/greeting.py";

        Assert.Equal(
            "File created successfully at: /tmp/greeting.py",
            Execute(files, new { command = "create", path, file_text = "def greet(name):\n    return f'Hello, {name}!'\n" }));
        Assert.Equal(
            "Here's the result of running `cat -n` on /tmp/greeting.py:\n     1\tdef greet(name):\n     2\t    return f'Hello, {name}!'\n     3\t\n",
            Execute(files, new { command = "view", path }));
        Assert.Equal(
            "Here's the result of running `cat -n` on /tmp/greeting.py:\n     2\t    return f'Hello, {name}!'\n",
            Execute(files, new { command = "view", path, view_range = new[] { 2, 2 } }));
        Assert.Equal(
            "The file /tmp/greeting.py has been edited. Here's the result of running `cat -n` on a snippet of /tmp/greeting.py:\n"
            + "     1\tdef greet(name):\n     2\t    return f'Goodbye, {name}!'\n     3\t\n"
            + "Review the changes and make sure they are as expected. Edit the file again if necessary.",
            Execute(files, new { command = "str_replace", path, old_str = "Hello", new_str = "Goodbye" }));
        Assert.Equal(
            "The file /tmp/greeting.py has been edited. Here's the result of running `cat -n` on a snippet of the edited file:\n"
            + "     1\tdef greet(name):\n     2\t# Author: Inspector\n     3\t    return f'Goodbye, {name}!'\n     4\t\n"
            + "Review the changes and make sure they are as expected (correct indentation, no duplicate lines, etc). Edit the file again if necessary.",
            Execute(files, new { command = "insert", path, insert_line = 1, new_str = "# Author: Inspector" }));
        Assert.Equal(FinalSource, Encoding.UTF8.GetString(files[path]));
    }

    [Fact]
    public void the_emulator_reports_the_launchers_tool_exceptions()
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["/tmp/a.txt"] = Encoding.UTF8.GetBytes("x\nx\n") };

        Assert.Equal("The path /tmp/missing does not exist. Please provide a valid path.", ToolException(files, new { command = "view", path = "/tmp/missing" }));
        Assert.Equal("File already exists at: /tmp/a.txt. Cannot overwrite files using command `create`.", ToolException(files, new { command = "create", path = "/tmp/a.txt", file_text = "" }));
        Assert.Equal("No replacement was performed, old_str `zzz` did not appear verbatim in /tmp/a.txt.", ToolException(files, new { command = "str_replace", path = "/tmp/a.txt", old_str = "zzz" }));
        Assert.Equal("No replacement was performed. Multiple occurrences of old_str `x` in lines [1, 2]. Please ensure it is unique", ToolException(files, new { command = "str_replace", path = "/tmp/a.txt", old_str = "x" }));
        Assert.Equal("Invalid `insert_line` parameter: 9. It should be within the range of lines of the file: [0, 3]", ToolException(files, new { command = "insert", path = "/tmp/a.txt", insert_line = 9, new_str = "y" }));
        Assert.Equal("No edit history found for /tmp/a.txt. The text editor only retains the last 10 edits per file.", ToolException(files, new { command = "undo_edit", path = "/tmp/a.txt" }));
        Assert.Equal("a       b", TextEditorEmulator.ExpandTabs("a\tb"));
    }

    // ----------------------------------------------------------------------------------------------------------
    // end to end, offline (port of `inspect eval text_editor.py` against the scripted model and fake sandbox)
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_four_edits_run_offline_and_verify_edit_scores_1()
    {
        var example = new TextEditorExample();
        var script = example.FakeSandbox(Context(null))!;
        var context = Context(ScriptedSandboxProvider.Register(script));

        var log = await Eval.RunAsync(
            example.Tasks[0].Build(context),
            new EvalOptions { Model = example.CreateFakeModel(context), LogDir = _logDir, LogFormat = LogFormat.Eval },
            CancellationToken.None);

        if (log.Status != EvalStatus.Success)
        {
            Assert.Fail($"status {log.Status}: {log.Error?.Message} {string.Join("; ", (log.Samples ?? []).Where(sample => sample.Error is not null).Select(sample => sample.Error!.Message))}");
        }

        var sample = Assert.Single(log.Samples!);

        // The conversation: four user prompts, four text_editor calls in the order the prompts ask for, no tool errors.
        Assert.Equal(
            [TextEditorTask.Input, TextEditorTask.ViewMessage, TextEditorTask.ReplaceMessage, TextEditorTask.InsertMessage],
            sample.Messages.OfType<ChatMessageUser>().Select(message => message.Text));
        var calls = sample.Messages.OfType<ChatMessageAssistant>().Where(message => message.ToolCalls is { Count: > 0 }).Select(message => Assert.Single(message.ToolCalls!)).ToList();
        Assert.Equal(["create", "view", "str_replace", "insert"], calls.Select(call => call.Arguments["command"]!.GetValue<string>()));
        Assert.All(calls, call => Assert.Equal("text_editor", call.Function));
        var results = sample.Messages.OfType<ChatMessageTool>().ToList();
        Assert.Equal(4, results.Count);
        Assert.All(results, result => Assert.Null(result.Error));
        Assert.Equal("File created successfully at: /tmp/greeting.py", results[0].Text);
        Assert.StartsWith("Here's the result of running `cat -n` on /tmp/greeting.py:", results[1].Text, StringComparison.Ordinal);
        Assert.Equal("I inserted '# Author: Inspector' after line 1 of /tmp/greeting.py.", sample.Output.Completion);

        // The score: the scorer read the edited file back from the sandbox.
        Assert.Equal(1.0, sample.Scores!["verify_edit"].AsFloat());
        Assert.Equal(FinalSource, sample.Scores["verify_edit"].Answer);
        Assert.Equal("File contains expected edit.", sample.Scores["verify_edit"].Explanation);
        Assert.Equal(1.0, log.Results!.Scores.Single(score => score.Name == "verify_edit").Metrics["accuracy"].Value);
        Assert.Equal(FinalSource, Assert.Single(script.Environments).FileText("/tmp/greeting.py"));

        // The wire: the tool probed for the launcher, then sent four JSON-RPC requests through `<cli> exec` on stdin.
        Assert.Equal(["test", "-r", SandboxToolSupport.SandboxCli], script.Calls[0].Cmd);
        var requests = script.Calls.Where(call => call.Cmd.Count == 2 && call.Cmd[1] == "exec").Select(call => JsonNode.Parse(call.Input!)!.AsObject()).ToList();
        Assert.Equal(4, requests.Count);
        Assert.All(requests, request => Assert.Equal("text_editor", request["method"]!.GetValue<string>()));
        Assert.All(script.Calls.Where(call => call.Cmd[1] == "exec"), call => Assert.Equal(SandboxToolSupport.SandboxCli, call.Cmd[0]));
        var insert = requests[3]["params"]!.AsObject();
        Assert.Equal(1, insert["insert_line"]!.GetValue<int>());
        Assert.Equal("# Author: Inspector", insert["insert_text"]!.GetValue<string>());
        // insert_text is re-wired to new_str by the tool, as in Python.
        Assert.Equal("# Author: Inspector", insert["new_str"]!.GetValue<string>());
        Assert.False(requests[0]["params"]!.AsObject().ContainsKey("old_str"));
    }

    [Fact]
    public async Task the_runner_runs_the_example_offline_under_the_fake_sandbox_and_exits_0()
    {
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["text_editor", "--fake", "--log-dir", _logDir], ExampleRegistry.Default, output, output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("sandbox   : fake, scripted by the example", text);
        Assert.Contains("status    : success (1/1 samples completed)", text);
        Assert.Contains("verify_edit", text);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));

        var error = new StringWriter();
        Assert.Equal(2, await ExampleRunner.MainAsync(["text_editor", "--fake", "--sandbox", "none", "--log-dir", _logDir], ExampleRegistry.Default, TextWriter.Null, error));
        Assert.Contains("needs a sandbox", error.ToString());
    }

    // ----------------------------------------------------------------------------------------------------------
    // support
    // ----------------------------------------------------------------------------------------------------------

    private static string Execute(Dictionary<string, byte[]> files, object parameters) =>
        TextEditorEmulator.Execute(files, System.Text.Json.JsonSerializer.SerializeToNode(parameters)!.AsObject());

    private static string ToolException(Dictionary<string, byte[]> files, object parameters)
    {
        var failure = Assert.ThrowsAny<Exception>(() => Execute(files, parameters));
        Assert.Equal(SandboxToolsErrorMapper.ToolExceptionCode, (int)failure.GetType().GetProperty("Code")!.GetValue(failure)!);
        return failure.Message;
    }

    private static ExampleContext Context(SandboxSpec? sandbox) =>
        new(Path.Combine(AppContext.BaseDirectory, "text_editor"), sandbox, true, new Dictionary<string, string>(), null, null, TextWriter.Null);

    private static SampleContext SampleContextFor(ISandboxEnvironment sandbox) =>
        new() { ActiveModel = new Model(new ScriptedModelApi()), Sandboxes = SandboxEnvironments.Single(sandbox) };
}
