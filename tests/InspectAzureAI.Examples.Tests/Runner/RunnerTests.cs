using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Examples.Approval;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Tests.Runner;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Tests for the examples runner (<c>examples/Runner</c>): argument parsing, the reflection registry, the
/// <c>fake</c> sandbox's scripted exec/read/write, and the exit codes of <see cref="ExampleRunner.MainAsync"/>,
/// including an end-to-end offline run of a scripted example.
/// </summary>
public sealed class RunnerTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-examples-tests", "runner-" + Guid.NewGuid().ToString("N"));

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
    // argument parsing
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void parse_reads_every_flag_of_the_contract()
    {
        var options = RunOptions.Parse(
        [
            "hello_world", "--task", "hello", "--fake", "--model", "gpt-x", "--route", "Anthropic", "--sandbox", "Docker",
            "--approval", "policy.json", "--log-dir", "/tmp/logs", "--limit", "3", "--epochs", "2", "--display", "conversation",
            "-T", "n=5", "-T", "name=a b", "-Tflag=true",
        ]);

        Assert.Equal("hello_world", options.Command);
        Assert.Equal("hello", options.Task);
        Assert.True(options.Fake);
        Assert.Equal("gpt-x", options.Model);
        Assert.Equal("anthropic", options.Route);
        Assert.Equal("docker", options.Sandbox);
        Assert.Equal("policy.json", options.Approval);
        Assert.Equal("/tmp/logs", options.LogDir);
        Assert.Equal(3, options.Limit);
        Assert.Equal(2, options.Epochs);
        Assert.Equal("conversation", options.Display);
        Assert.Equal(new Dictionary<string, string> { ["n"] = "5", ["name"] = "a b", ["flag"] = "true" }, options.TaskArgs);
        Assert.False(options.Help);
    }

    [Fact]
    public void parse_defaults_and_help()
    {
        var options = RunOptions.Parse(["approval", "--help"]);
        Assert.Equal("approval", options.Command);
        Assert.True(options.Help);
        Assert.Null(options.Task);
        Assert.Null(options.Sandbox);
        Assert.Equal("logs", options.LogDir);
        Assert.Empty(options.TaskArgs);

        Assert.Null(RunOptions.Parse(["-h"]).Command);
        Assert.Equal("list", RunOptions.Parse(["list"]).Command);
    }

    [Theory]
    [InlineData("models", "models")]
    [InlineData("Anthropic", "anthropic")]
    [InlineData("RESPONSES", "responses")]
    public void parse_normalises_every_route(string given, string expected) =>
        Assert.Equal(expected, RunOptions.Parse(["approval", "--route", given]).Route);

    [Theory]
    [InlineData("--sandbox", "podman")]
    [InlineData("--route", "openai")]
    [InlineData("--display", "full")]
    [InlineData("--limit", "0")]
    [InlineData("--epochs", "x")]
    [InlineData("-T", "novalue")]
    [InlineData("--bogus", "1")]
    public void parse_rejects_bad_flags(string flag, string value) =>
        Assert.Throws<ArgumentException>(() => RunOptions.Parse(["approval", flag, value]));

    [Fact]
    public void parse_rejects_a_missing_value_and_a_second_command()
    {
        Assert.Throws<ArgumentException>(() => RunOptions.Parse(["approval", "--model"]));
        Assert.Throws<ArgumentException>(() => RunOptions.Parse(["approval", "extra"]));
    }

    [Fact]
    public void task_args_are_typed_through_the_context()
    {
        var ctx = new ExampleContext("/x", null, true, new Dictionary<string, string> { ["n"] = "5", ["ratio"] = "0.5", ["flag"] = "True", ["name"] = "z" }, null, null, TextWriter.Null);

        Assert.Equal(5, ctx.TaskArgInt("n", 1));
        Assert.Equal(1, ctx.TaskArgInt("missing", 1));
        Assert.Equal(0.5, ctx.TaskArgDouble("ratio", 1));
        Assert.True(ctx.TaskArgBool("flag", false));
        Assert.Equal("z", ctx.TaskArg("name"));
        Assert.Null(ctx.TaskArg("missing"));
        Assert.Equal(Path.Combine("/x", "data.jsonl"), ctx.DataPath("data.jsonl"));
        Assert.Throws<ArgumentException>(() => ctx.TaskArgInt("name", 0));
        Assert.Throws<ArgumentException>(() => ctx.TaskArgBool("name", false));
    }

    // ----------------------------------------------------------------------------------------------------------
    // registry
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void registry_finds_approval_by_reflection()
    {
        var registry = ExampleRegistry.Default;

        var approval = Assert.IsType<ApprovalExample>(registry.Find("approval"));
        Assert.Same(approval, registry.Find("APPROVAL"));
        Assert.Same(approval, registry.Get("approval/"));
        Assert.Equal([ApprovalDemo.TaskName], approval.Tasks.Select(task => task.Name));
        Assert.Equal("docker", approval.Defaults.Sandbox);
        Assert.Equal("approval.json", approval.Defaults.Approval);
        Assert.NotEmpty(approval.Deviations);
        Assert.Null(registry.Find("no_such_example"));
        Assert.Throws<ArgumentException>(() => registry.Get("no_such_example"));
        Assert.Contains(registry.Examples, example => example.Name == "approval");
    }

    [Fact]
    public void registry_discovers_examples_of_another_assembly_and_rejects_duplicates()
    {
        var registry = ExampleRegistry.Discover(typeof(RunnerTests).Assembly);

        Assert.IsType<ScriptedExample>(registry.Get("scripted_example"));
        Assert.Throws<InvalidOperationException>(() => ExampleRegistry.Of(new ScriptedExample(), new ScriptedExample()));
    }

    [Fact]
    public void example_directory_is_under_the_base_directory()
    {
        var directory = ExampleRunner.ExampleDirectory(new ApprovalExample());

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "approval"), directory);
        Assert.True(File.Exists(Path.Combine(directory, "approval.json")), "approval/approval.json was not linked into the test output");
    }

    // ----------------------------------------------------------------------------------------------------------
    // the fake sandbox
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task fake_sandbox_matches_exact_then_prefix_then_predicate_then_default()
    {
        var script = new FakeSandboxScript()
            .OnExact(FakeSandboxScript.Ok("exact"), "ls", "-la")
            .OnPrefix(FakeSandboxScript.Ok("short"), "ls")
            .OnPrefix(FakeSandboxScript.Ok("long"), "ls", "-l")
            .OnMatch(call => call.Cmd.Any(arg => arg.Contains("cat")), call => FakeSandboxScript.Fail(1, $"cat: {call.Cmd[^1]}: No such file"))
            .WithDefault(FakeSandboxScript.Ok("default"))
            .WithFile("/etc/motd", "hello");
        var provider = new ScriptedSandboxProvider(script);
        var environments = await provider.SampleInitAsync("t", null, new Dictionary<string, string>());
        var sandbox = environments.Default;

        Assert.Equal("exact", (await sandbox.ExecAsync(["ls", "-la"])).Stdout);
        Assert.Equal("long", (await sandbox.ExecAsync(["ls", "-l", "/"])).Stdout);
        Assert.Equal("short", (await sandbox.ExecAsync(["ls", "/"])).Stdout);
        var missing = await sandbox.ExecAsync(["bash", "-c", "cat x"]);
        Assert.False(missing.Success);
        Assert.Equal(1, missing.ReturnCode);
        Assert.Equal("cat: cat x: No such file", missing.Stderr);
        Assert.Equal("default", (await sandbox.ExecAsync(["python3", "-c", "print(1)"])).Stdout);

        Assert.Equal("hello", await sandbox.ReadFileAsync("/etc/motd"));
        await sandbox.WriteFileAsync("/out.txt", "written");
        Assert.Equal("written", await sandbox.ReadFileAsync("/out.txt"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => sandbox.ReadFileAsync("/nope"));

        Assert.Equal(5, script.Calls.Count);
        Assert.Equal(["ls", "-la"], script.Calls[0].Cmd);
        var environment = Assert.Single(script.Environments);
        Assert.Equal("written", environment.FileText("/out.txt"));
        Assert.Null(script.Files.GetValueOrDefault("/out.txt"));
        await environments.Cleanup!(true);
    }

    [Fact]
    public async Task fake_sandbox_runs_a_command_locally_when_the_script_says_so()
    {
        var script = new FakeSandboxScript().LocalPrefix("echo");
        var sandbox = new ScriptedSandboxEnvironment(script);

        var result = await sandbox.ExecAsync(["echo", "from the host"]);

        Assert.True(result.Success);
        Assert.Equal("from the host", result.Stdout.TrimEnd());
        Assert.Equal("", (await sandbox.ExecAsync(["unmatched"])).Stdout);
        sandbox.Dispose();
    }

    [Fact]
    public void fake_sandbox_registers_under_the_fake_type()
    {
        var spec = ScriptedSandboxProvider.Register(new FakeSandboxScript());

        Assert.Equal(new SandboxSpec("fake"), spec);
        Assert.IsType<ScriptedSandboxProvider>(SandboxRegistry.Get("fake"));
    }

    // ----------------------------------------------------------------------------------------------------------
    // exit codes and the end-to-end run
    // ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task usage_errors_exit_2_and_help_exits_0()
    {
        var registry = ExampleRegistry.Discover(typeof(RunnerTests).Assembly);

        Assert.Equal(2, await Run(registry, ["scripted_example", "--bogus"]));
        Assert.Equal(2, await Run(registry, ["no_such_example", "--fake"]));
        Assert.Equal(2, await Run(registry, []));
        Assert.Equal(2, await Run(registry, ["scripted_example", "--fake", "--task", "nope"]));
        Assert.Equal(2, await Run(registry, ["scripted_example", "--fake", "--approval", "no-such-approver-or-file"]));

        var output = new StringWriter();
        Assert.Equal(0, await ExampleRunner.MainAsync(["--help"], registry, output, output));
        Assert.Contains("--sandbox docker|local|fake|none", output.ToString());

        output = new StringWriter();
        Assert.Equal(0, await ExampleRunner.MainAsync(["scripted_example", "--help"], registry, output, output));
        Assert.Contains("scripted_example", output.ToString());
        Assert.Contains("scripted_task", output.ToString());
        Assert.Contains("a deviation", output.ToString());

        output = new StringWriter();
        Assert.Equal(0, await ExampleRunner.MainAsync(["list"], registry, output, output));
        Assert.Contains("scripted_example", output.ToString());
    }

    [Fact]
    public async Task an_example_without_a_fake_sandbox_script_cannot_run_under_sandbox_fake()
    {
        var error = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["approval", "--fake", "--sandbox", "fake", "--log-dir", _logDir], ExampleRegistry.Default, TextWriter.Null, error);

        Assert.Equal(2, exit);
        Assert.Contains("no fake sandbox script", error.ToString());
    }

    [Fact]
    public async Task a_scripted_example_runs_offline_under_the_fake_sandbox_and_exits_0()
    {
        var example = new ScriptedExample();
        var registry = ExampleRegistry.Of(example);
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(
            ["scripted_example", "--fake", "--log-dir", _logDir, "-T", "greeting=hi", "--display", "conversation"],
            registry,
            output,
            output);

        var text = output.ToString();
        Assert.True(exit == 0, text);
        Assert.Contains("sandbox   : fake, scripted by the example", text);
        Assert.Contains("task args : greeting=hi", text);
        Assert.Contains("status    : success (1/1 samples completed)", text);
        Assert.Contains("scripted_scorer", text);
        Assert.Contains("accuracy", text);
        Assert.Contains("── Assistant ──", text);
        Assert.Contains("log       : ", text);
        Assert.Equal("hi", example.LastContext!.TaskArg("greeting"));
        Assert.Equal(new SandboxSpec("fake"), example.LastContext.Sandbox);
        Assert.True(example.LastContext.Fake);
        Assert.NotNull(example.LastContext.ResolvedModel);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "scripted_example"), example.LastContext.ExampleDirectory);
        var call = Assert.Single(example.Script!.Calls);
        Assert.Equal(["bash", "--login", "-c", "cat /etc/motd"], call.Cmd);
        Assert.Single(Directory.GetFiles(_logDir, "*.eval"));
    }

    [Fact]
    public async Task a_failed_eval_exits_1()
    {
        var example = new ScriptedExample { Fail = true };
        var output = new StringWriter();

        var exit = await ExampleRunner.MainAsync(["scripted_example", "--fake", "--log-dir", _logDir], ExampleRegistry.Of(example), output, output);

        Assert.Equal(1, exit);
        Assert.Contains("status    : error", output.ToString());
    }

    [Fact]
    public async Task sandbox_none_builds_the_task_without_a_sandbox()
    {
        var example = new ScriptedExample();

        var exit = await ExampleRunner.MainAsync(["scripted_example", "--fake", "--sandbox", "none", "--log-dir", _logDir], ExampleRegistry.Of(example), TextWriter.Null, TextWriter.Null);

        Assert.Equal(0, exit);
        Assert.Null(example.LastContext!.Sandbox);
    }

    private static Task<int> Run(ExampleRegistry registry, string[] args) => ExampleRunner.MainAsync(args, registry, TextWriter.Null, TextWriter.Null);

    /// <summary>A test example: one sample, a scripted model that reads a file through bash then answers, an exact-match scorer, a fake sandbox script.</summary>
    public sealed class ScriptedExample : IExample
    {
        public bool Fail { get; init; }

        public ExampleContext? LastContext { get; private set; }

        public FakeSandboxScript? Script { get; private set; }

        public string Name => "scripted_example";

        public string Description => "a scripted example for the runner tests";

        public IReadOnlyList<ExampleTask> Tasks =>
        [
            new ExampleTask("scripted_task", Build, "the one task"),
        ];

        public ExampleDefaults Defaults => new(Sandbox: "docker", ComposeFile: "compose.yaml");

        public IReadOnlyList<string> Deviations => ["a deviation"];

        public Model CreateFakeModel(ExampleContext ctx)
        {
            LastContext = ctx;
            var turns = Fail
                ? new[] { ScriptedTurn.Error(new InvalidOperationException("scripted failure")) }
                : new[]
                {
                    ScriptedTurn.ToolCall("bash", new { cmd = "cat /etc/motd" }),
                    ScriptedTurn.Text("hello"),
                };
            return new Model(new ScriptedModelApi(turns, "scripted-example"));
        }

        public FakeSandboxScript? FakeSandbox(ExampleContext ctx) =>
            Script ??= new FakeSandboxScript().OnPrefix(FakeSandboxScript.Ok("hello\n"), "bash");

        private EvalTask Build(ExampleContext ctx)
        {
            LastContext = ctx;
            return new EvalTask
            {
                Name = "scripted_task",
                Dataset = new MemoryDataset([new Sample("say hello") { Target = "hello" }]),
                Solver = Solvers.Chain(
                    ctx.Sandbox is null ? Solvers.Generate() : Solvers.UseTools(SandboxTools.Bash()),
                    Solvers.Generate()),
                Scorers = [Scorers.Custom("scripted_scorer", (state, target, _) => Task.FromResult(new Score(state.Output.Completion.Trim() == target.Text ? "C" : "I")), Metrics.Accuracy())],
                Sandbox = ctx.Sandbox,
            };
        }
    }
}
