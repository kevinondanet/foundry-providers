using InspectAzureAI.Eval.Context.Input;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Runner;

/// <summary>
/// Tests for the helpers the examples share through <c>examples/Runner</c>: the scripted-model factories, the
/// placeholder secrets, the fake sandbox's environment-aware and asynchronous handlers, the canned Hugging Face hub's
/// private cache, and the declining <c>ask_user</c> handler the runner installs for <c>--fake</c> runs.
/// </summary>
public sealed class RunnerHelpersTests
{
    private static FakeExecCall Call(params string[] cmd) => new(cmd, null, null, null, null, null);

    [Fact]
    public async Task fake_models_answer_with_the_examples_model_name_and_a_rough_usage()
    {
        var model = FakeModels.Answering("my-scripted", messages => $"echo: {messages[^1].Text}");

        var output = await model.GenerateAsync("Hello world, this is a test");

        Assert.Equal("my-scripted", output.Model);
        Assert.Equal("echo: Hello world, this is a test", output.Completion);
        Assert.NotNull(output.Usage);
        Assert.Equal("Hello world, this is a test".Length / 4 + 1, output.Usage!.InputTokens);
        Assert.Equal(output.Usage.InputTokens + output.Usage.OutputTokens, output.Usage.TotalTokens);
    }

    [Fact]
    public async Task fake_models_scripted_sees_the_tools_offered()
    {
        var model = FakeModels.Scripted("tools-scripted", (messages, tools) => FakeModels.Output("tools-scripted", string.Join(",", tools.Select(tool => tool.Name)), messages), turns: 2);

        var output = await model.GenerateAsync([new ChatMessageUser("hi")], [new ToolInfo("add", "adds"), new ToolInfo("sub", "subtracts")]);

        Assert.Equal("add,sub", output.Completion);
    }

    [Fact]
    public void fake_secrets_set_a_placeholder_only_when_the_variable_is_absent()
    {
        var variable = "INSPECT_EXAMPLES_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(FakeSecrets.EnsurePlaceholder(variable, "placeholder"));
            Assert.Equal("placeholder", Environment.GetEnvironmentVariable(variable));
            Assert.False(FakeSecrets.EnsurePlaceholder(variable, "other"));
            Assert.Equal("placeholder", Environment.GetEnvironmentVariable(variable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task environment_aware_handlers_see_the_answering_samples_files()
    {
        var script = new FakeSandboxScript()
            .WithFile("notes.txt", "seed")
            .OnPrefix((environment, call) => FakeSandboxScript.Ok(environment.FileText("notes.txt") ?? "<missing>"), "cat")
            .OnExact((environment, _) =>
            {
                environment.Files["notes.txt"] = "written"u8.ToArray();
                return FakeSandboxScript.Ok();
            }, "touch");
        using var first = new ScriptedSandboxEnvironment(script);
        using var second = new ScriptedSandboxEnvironment(script);
        script.Record(first);
        script.Record(second);

        await first.ExecAsync(["touch"]);
        var fromFirst = await first.ExecAsync(["cat"]);
        var fromSecond = await second.ExecAsync(["cat"]);

        Assert.Equal("written", fromFirst.Stdout);
        Assert.Equal("seed", fromSecond.Stdout);
        Assert.Same(first, script.EnvironmentOf(script.Calls[0]));
        Assert.Same(second, script.EnvironmentOf(script.Calls[2]));
        Assert.Null(script.EnvironmentOf(Call("never", "recorded")));
        Assert.Null(ScriptedSandboxEnvironment.Current);
    }

    [Fact]
    public async Task asynchronous_handlers_are_awaited_and_fall_through_on_null()
    {
        var script = new FakeSandboxScript()
            .OnPrefixAsync(async (call, cancellationToken) =>
            {
                await Task.Yield();
                return call.Cmd.Count > 1 ? FakeSandboxScript.Ok($"async {call.Cmd[1]}") : null;
            }, "probe")
            .OnPrefix(FakeSandboxScript.Ok("sync"), "probe");
        using var environment = new ScriptedSandboxEnvironment(script);

        Assert.Equal("async x", (await environment.ExecAsync(["probe", "x"])).Stdout);
        Assert.Equal("sync", (await environment.ExecAsync(["probe"])).Stdout);
    }

    [Fact]
    public void the_canned_hub_loader_uses_a_private_cache_directory()
    {
        var rows = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "rows-" + Guid.NewGuid().ToString("N") + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rows)!);
        File.WriteAllText(rows, "{\"row\":{\"q\":\"1\"}}\n\n{\"row\":{\"q\":\"2\"}}\n");
        try
        {
            using var hub = new CannedHfHub("org/dataset", "test", rows);
            using var loader = hub.CreateLoader();

            Assert.Equal(2, hub.Rows.Count);
            Assert.Contains("inspect-examples", CannedHfHub.PrivateCacheDir);
            Assert.NotNull(loader);
        }
        finally
        {
            File.Delete(rows);
        }
    }

    [Fact]
    public async Task the_declining_input_handler_declines_and_records_every_question()
    {
        var output = new StringWriter();
        var handler = ScriptedInputHandler.Declining(output);
        var schema = new ElicitationSchema { Properties = new(StringComparer.Ordinal) { ["name"] = new ElicitationStringProperty() }, Required = ["name"] };

        var result = await handler.RequestAsync(new InputRequest("Your name?", schema), CancellationToken.None);

        Assert.Equal(InputOutcome.Declined, result.Outcome);
        Assert.Single(handler.Requests);
        Assert.Contains("[ask_user] Your name?", output.ToString());
    }

    [Fact]
    public async Task a_fake_run_installs_the_declining_input_handler_and_restores_the_previous_one()
    {
        var previous = InputHandlers.Default;
        var logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "helpers-" + Guid.NewGuid().ToString("N"));
        var output = new StringWriter();
        try
        {
            var exit = await ExampleRunner.MainAsync(["hello_world", "--fake", "--log-dir", logDir], ExampleRegistry.Default, output, output);

            Assert.True(exit == 0, output.ToString());
            Assert.Same(previous, InputHandlers.Default);
        }
        finally
        {
            InputHandlers.Default = previous;
            try
            {
                Directory.Delete(logDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
