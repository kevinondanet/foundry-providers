using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Human;
using InspectAzureAI.Eval.Agents.Human.Commands;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// A container stand-in for the sandbox service protocol: a <see cref="FakeSandboxEnvironment"/> whose exec table
/// emulates the file commands the service and the human agent installer run (find, cat, tee, rm, wc, mkdir, ls,
/// whoami, which), serialised behind one lock so the host's polling loop and the "person" in the container never
/// race, plus a <see cref="CallAsync"/> that behaves like the generated Python client (write a request, poll for the response).
/// </summary>
internal sealed class ContainerSimulator : ISandboxEnvironment, ISandboxConnectionProvider
{
    private readonly Lock _sync = new();

    public ContainerSimulator()
    {
        Fake.OnExecCall = Handle;
    }

    public FakeSandboxEnvironment Fake { get; } = new();

    public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

    public string User { get; set; } = "agent";

    public bool PythonInstalled { get; set; } = true;

    /// <summary>Consulted before the built-in emulation; a null result falls through to it.</summary>
    public Func<FakeExecCall, ExecResult?>? Override { get; set; }

    public SandboxConnection Connection { get; set; } = new("docker", "docker exec -it sim bash -l", Container: "sim");

    public string HostAddress => Fake.HostAddress;

    public IReadOnlyList<FakeExecCall> Calls
    {
        get
        {
            lock (_sync)
            {
                return Fake.Calls.ToList();
            }
        }
    }

    public bool HasFile(string path)
    {
        lock (_sync)
        {
            return Fake.Files.ContainsKey(path);
        }
    }

    public string? Text(string path)
    {
        lock (_sync)
        {
            return Fake.Files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
        }
    }

    public void PutFile(string path, string text)
    {
        lock (_sync)
        {
            Fake.Files[path] = Encoding.UTF8.GetBytes(text);
        }
    }

    public IReadOnlyList<string> FilesUnder(string directory)
    {
        lock (_sync)
        {
            return Fake.Files.Keys.Where(key => Parent(key) == directory).Order(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>The generated client's <c>call_NAME(method, **params)</c>: drop a request file, poll for the response, raise its error.</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null, string service = HumanAgentService.ServiceName, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString();
        var request = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        var responsePath = $"{SandboxService.ServicesDir}/{service}/{SandboxService.ResponsesDir}/{id}.json";
        PutFile($"{SandboxService.ServicesDir}/{service}/{SandboxService.RequestsDir}/{id}.json", request.ToJsonString());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            await Task.Delay(5, timeout.Token);
            string? text;
            lock (_sync)
            {
                text = Fake.Files.TryGetValue(responsePath, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
                if (text is not null)
                {
                    Fake.Files.Remove(responsePath);
                }
            }

            if (text is null)
            {
                continue;
            }

            var response = JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException("response is not an object");
            if (response["error"] is { } error)
            {
                throw new InvalidOperationException(error.GetValue<string>());
            }

            return response["result"];
        }
    }

    public Task<ExecResult> ExecAsync(IReadOnlyList<string> cmd, string? input = null, string? cwd = null, IReadOnlyDictionary<string, string>? env = null, string? user = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Fake.ExecAsync(cmd, input, cwd, env, user, timeout, cancellationToken);
        }
    }

    public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        PutFile(path, contents);
        return Task.CompletedTask;
    }

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            Fake.Files[path] = contents.ToArray();
        }

        return Task.CompletedTask;
    }

    public Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Text(path) ?? throw new FileNotFoundException($"File '{path}' was not found.", path));

    public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Fake.Files.TryGetValue(path, out var bytes) ? Task.FromResult(bytes) : throw new FileNotFoundException($"File '{path}' was not found.", path);
        }
    }

    public Task<SandboxConnection> ConnectionAsync(string? user = null, CancellationToken cancellationToken = default) => Task.FromResult(Connection);

    private static string Parent(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    private ExecResult? Handle(FakeExecCall call)
    {
        if (Override?.Invoke(call) is { } overridden)
        {
            return overridden;
        }

        var cmd = call.Cmd;
        var last = cmd[^1];
        switch (cmd[0])
        {
            case "find":
            {
                var files = Fake.Files.Keys.Where(key => Parent(key) == cmd[1] && key.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
                return FakeSandboxEnvironment.Ok(files.Count == 0 ? "" : string.Join("\0", files) + "\0");
            }

            case "cat":
                return Fake.Files.TryGetValue(last, out var content)
                    ? FakeSandboxEnvironment.Ok(Encoding.UTF8.GetString(content))
                    : FakeSandboxEnvironment.Fail(1, $"cat: {last}: No such file or directory");
            case "tee":
                Fake.Files[last] = Encoding.UTF8.GetBytes(call.Input ?? "");
                return FakeSandboxEnvironment.Ok(call.Input ?? "");
            case "rm":
                if (cmd.Contains("-rf"))
                {
                    foreach (var key in Fake.Files.Keys.Where(key => key == last || key.StartsWith(last + "/", StringComparison.Ordinal)).ToList())
                    {
                        Fake.Files.Remove(key);
                    }

                    Directories.RemoveWhere(directory => directory == last || directory.StartsWith(last + "/", StringComparison.Ordinal));
                }
                else
                {
                    Fake.Files.Remove(last);
                }

                return FakeSandboxEnvironment.Ok();
            case "wc":
                return Fake.Files.TryGetValue(last, out var counted)
                    ? FakeSandboxEnvironment.Ok($"{counted.Length} {last}\n")
                    : FakeSandboxEnvironment.Fail(1, $"wc: {last}: No such file or directory");
            case "mkdir":
                if (!cmd.Contains("-p") && Directories.Contains(last))
                {
                    return FakeSandboxEnvironment.Fail(1, $"mkdir: cannot create directory '{last}': File exists");
                }

                Directories.Add(last);
                return FakeSandboxEnvironment.Ok();
            case "whoami":
                return FakeSandboxEnvironment.Ok(User + "\n");
            case "which":
                return PythonInstalled ? FakeSandboxEnvironment.Ok("/usr/bin/python3\n") : FakeSandboxEnvironment.Fail(1);
            case "ls":
            {
                var names = Fake.Files.Keys.Where(key => Parent(key) == last).Select(Name).Order(StringComparer.Ordinal).ToList();
                return FakeSandboxEnvironment.Ok(names.Count == 0 ? "" : string.Join("\n", names) + "\n");
            }

            default:
                return FakeSandboxEnvironment.Ok();
        }
    }
}

/// <summary>
/// The human CLI agent (port of <c>agent/_human/</c>) driven end to end against a <see cref="ContainerSimulator"/>:
/// the installer's file set, the start/note/submit flow, validation, intermediate scoring, quit with session
/// logs, time accounting through a fake clock, and the console view.
/// </summary>
public class HumanAgentTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    private const string TaskText = "Find the flag hidden in /home/agent and submit it.";

    /// <summary>A sample context around a simulator whose scorer marks a "42" completion correct.</summary>
    private sealed class HumanScope : IDisposable
    {
        private readonly IDisposable _scope;

        public HumanScope()
        {
            var model = new Model(new ScriptedModelApi());
            var store = new Store();
            var sampleState = new TaskState("scripted", 1, 1, TaskText, [new ChatMessageUser(TaskText)], store: store);
            Context = new SampleContext
            {
                ActiveModel = model,
                Store = store,
                Sandboxes = SandboxEnvironments.Single(Sim),
                SampleState = sampleState,
                Scorer = state =>
                {
                    var completion = state.Output.Completion;
                    ScoredCompletions.Add(completion);
                    return Task.FromResult<IReadOnlyList<Score>>([new Score(completion == "42" ? "C" : "I") { Answer = completion }]);
                },
            };
            _scope = SampleContext.Begin(Context);
        }

        public ContainerSimulator Sim { get; } = new();

        public SampleContext Context { get; }

        public List<string> ScoredCompletions { get; } = [];

        public StringWriter Console { get; } = new();

        public HumanAgentState State => Context.Store.As<HumanAgentState>();

        public IReadOnlyList<InfoEvent> InfoEvents => Context.Transcript.Events.OfType<InfoEvent>().ToList();

        public Task<AgentState> Run(AgentDef agent, CancellationToken cancellationToken) =>
            agent.Execute(new AgentState([new ChatMessageUser(TaskText)]), cancellationToken);

        public void Dispose() => _scope.Dispose();
    }

    private sealed class FakeClock
    {
        public double Now { get; set; }

        public double Read() => Now;
    }

    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(30));

    private static string Result(JsonNode? node) => node?.GetValue<string>() ?? "";

    private static JsonObject Args(string name, string? value) => new() { [name] = value };

    [Fact]
    public async Task start_note_submit_flow_installs_serves_and_sets_the_answer()
    {
        using var scope = new HumanScope();
        using var deadline = Deadline();
        var agent = HumanCli.Agent(recordSession: false, view: new ConsoleHumanAgentView(scope.Console), pollingInterval: Poll);

        var run = scope.Run(agent, deadline.Token);
        var sim = scope.Sim;

        var stopped = Result(await sim.CallAsync("validate", Args("answer", "42"), cancellationToken: deadline.Token));
        var started = Result(await sim.CallAsync("start", cancellationToken: deadline.Token));
        var noted = await sim.CallAsync("note", Args("content", "## Human Agent Note\nlooked in /home/agent"), cancellationToken: deadline.Token);
        var valid = await sim.CallAsync("validate", Args("answer", "42"), cancellationToken: deadline.Token);
        var submitted = await sim.CallAsync("submit", Args("answer", "42"), cancellationToken: deadline.Token);
        var final = await run;

        Assert.Equal("FAILED: Task is stopped (use 'task start' to start)", stopped);
        Assert.StartsWith("Status: Running  Time: 0:00:0", started, StringComparison.Ordinal);
        Assert.Null(noted);
        Assert.Null(valid);
        Assert.Null(submitted);
        Assert.Equal("42", final.Output.Completion);
        Assert.Equal(HumanCli.ModelName, final.Output.Model);
        Assert.Equal("42", scope.State.Answer);
        Assert.False(scope.State.IsRunning());
        Assert.Equal(AgentName(), agent.Name);

        // the store carries the state under Python's field names
        Assert.Contains(scope.Context.Store.Keys, key => key.EndsWith(":answer", StringComparison.Ordinal));

        // installed: the service module and the task CLI staging files were written through tee, then removed
        Assert.True(sim.HasFile($"{SandboxService.ServicesDir}/human_agent/human_agent.py"));
        Assert.Contains(sim.Calls, call => call.Cmd[0] == "mkdir" && call.Cmd[^1] == HumanAgentInstall.HumanAgentDir && call.User == "root");
        Assert.Contains(sim.Calls, call => call.Cmd[0] == "bash" && call.Cmd[1] == "./install.sh" && call.Cwd == HumanAgentInstall.InstallDir);
        Assert.Contains(sim.Calls, call => call.Cmd[0] == "which" && call.Cmd[1] == "python3");

        // transcript: the initial stop, the start, and the note
        var events = scope.InfoEvents;
        Assert.All(events, e => Assert.Equal("human_agent", e.Source));
        Assert.Equal("stop", events[0].Data!["action"]!.GetValue<string>());
        Assert.Equal("0:00:00", events[0].Data!["total_time"]!.GetValue<string>());
        Assert.Equal("start", events[1].Data!["action"]!.GetValue<string>());
        Assert.Equal("## Human Agent Note\nlooked in /home/agent", events[2].Data!.GetValue<string>());

        // the console view printed the login command, the status changes, the note and the answer
        var console = scope.Console.ToString();
        Assert.Contains("docker exec -it sim bash -l", console, StringComparison.Ordinal);
        Assert.Contains("Login to the system with the following command", console, StringComparison.Ordinal);
        Assert.Contains("[human_agent] Status: Stopped  Time: 0:00:00", console, StringComparison.Ordinal);
        Assert.Contains("[human_agent] Status: Running", console, StringComparison.Ordinal);
        Assert.Contains("Note recorded:\n## Human Agent Note", console, StringComparison.Ordinal);
        Assert.Contains("[human_agent] Answer submitted: '42'", console, StringComparison.Ordinal);
        Assert.Contains("[human_agent] Final answer: 42", console, StringComparison.Ordinal);

        static string AgentName() => HumanCli.AgentName;
    }

    [Fact]
    public async Task validation_requires_an_answer_and_enforces_the_pattern_from_the_start()
    {
        using var scope = new HumanScope();
        using var deadline = Deadline();
        var agent = HumanCli.Agent(answerPattern: "[0-9]+", recordSession: false, view: new ConsoleHumanAgentView(scope.Console), pollingInterval: Poll);

        var run = scope.Run(agent, deadline.Token);
        var sim = scope.Sim;
        await sim.CallAsync("start", cancellationToken: deadline.Token);

        var missing = Result(await sim.CallAsync("validate", Args("answer", null), cancellationToken: deadline.Token));
        var blank = Result(await sim.CallAsync("validate", Args("answer", "   "), cancellationToken: deadline.Token));
        var wrong = Result(await sim.CallAsync("validate", Args("answer", "abc"), cancellationToken: deadline.Token));
        var prefixed = await sim.CallAsync("validate", Args("answer", "42abc"), cancellationToken: deadline.Token);
        await sim.CallAsync("submit", Args("answer", "7"), cancellationToken: deadline.Token);
        var final = await run;

        Assert.Equal("FAILED: An explicit answer is required for scoring this task.", missing);
        Assert.Equal("FAILED: An explicit answer is required for scoring this task.", blank);
        Assert.Equal("FAILED: Your answer was not in the required format (please review the task instructions)", wrong);
        Assert.Null(prefixed);
        Assert.Equal("7", final.Output.Completion);
    }

    [Fact]
    public async Task intermediate_scoring_scores_the_answer_and_lists_it_in_the_status()
    {
        using var scope = new HumanScope();
        using var deadline = Deadline();
        var clock = new FakeClock { Now = 1_000 };
        HumanAgentState.ClockOverride.Value = clock.Read;
        try
        {
            var agent = HumanCli.Agent(answer: false, intermediateScoring: true, recordSession: false, view: new ConsoleHumanAgentView(scope.Console), pollingInterval: Poll);

            var run = scope.Run(agent, deadline.Token);
            var sim = scope.Sim;
            await sim.CallAsync("start", cancellationToken: deadline.Token);
            clock.Now += 65;

            var scoredAnswer = Result(await sim.CallAsync("score", Args("answer", "42"), cancellationToken: deadline.Token));
            var scoredFiles = Result(await sim.CallAsync("score", Args("answer", null), cancellationToken: deadline.Token));
            var status = Result(await sim.CallAsync("status", cancellationToken: deadline.Token));
            await sim.CallAsync("submit", Args("answer", null), cancellationToken: deadline.Token);
            var final = await run;

            Assert.Equal("Answer: 42, Score: C", scoredAnswer);
            Assert.Equal("Answer: , Score: I", scoredFiles);
            Assert.Equal(["42", ""], scope.ScoredCompletions);
            Assert.Equal("", final.Output.Completion);

            var scorings = scope.State.Scorings;
            Assert.Equal(2, scorings.Count);
            Assert.Equal(65, scorings[0].Time);
            Assert.Equal("C", scorings[0].Scores[0].Text);

            var lines = status.Split('\n');
            Assert.Equal("Status: Running  Time: 0:01:05", lines[0]);
            Assert.Equal("", lines[1]);
            Assert.Equal("Intermediate Scores", lines[2]);
            Assert.StartsWith("Answer", lines[3], StringComparison.Ordinal);
            Assert.EndsWith("Time", lines[3], StringComparison.Ordinal);
            Assert.StartsWith("42", lines[4], StringComparison.Ordinal);
            Assert.Contains(" C ", lines[4], StringComparison.Ordinal);
            Assert.EndsWith(" 0:01:05", lines[4], StringComparison.Ordinal);
            Assert.Contains("[human_agent] Intermediate score. Answer: 42, Score: C", scope.Console.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            HumanAgentState.ClockOverride.Value = null;
        }
    }

    [Fact]
    public async Task quit_ends_the_task_without_an_answer_and_collects_the_session_logs()
    {
        using var scope = new HumanScope();
        using var deadline = Deadline();
        var sim = scope.Sim;
        sim.PutFile($"{HumanAgentInstall.RecordSessionDir}/agent_20260908_101500.output", "$ ls\nflag.txt\n");
        sim.PutFile($"{HumanAgentInstall.RecordSessionDir}/agent_20260908_101500.timing", "0.1 3\n");
        var agent = HumanCli.Agent(recordSession: true, view: new ConsoleHumanAgentView(scope.Console), pollingInterval: Poll);

        var run = scope.Run(agent, deadline.Token);
        await sim.CallAsync("start", cancellationToken: deadline.Token);
        var stopped = Result(await sim.CallAsync("stop", cancellationToken: deadline.Token));
        var quit = await sim.CallAsync("quit", cancellationToken: deadline.Token);
        var final = await run;

        Assert.StartsWith("Status: Stopped", stopped, StringComparison.Ordinal);
        Assert.Null(quit);
        Assert.Equal("", scope.State.Answer);
        Assert.Equal("", final.Output.Completion);
        Assert.Equal(HumanCli.ModelName, final.Output.Model);
        var logs = scope.State.Logs;
        Assert.Equal(["agent_20260908_101500.output", "agent_20260908_101500.timing"], logs.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("$ ls\nflag.txt\n", logs["agent_20260908_101500.output"]);
        Assert.Contains(sim.Calls, call => call.Cmd[0] == "ls" && call.Cmd[^1] == HumanAgentInstall.RecordSessionDir);

        // start, stop events carry the running time; the .bashrc records the session
        var actions = scope.InfoEvents.Select(e => e.Data!["action"]!.GetValue<string>()).ToList();
        Assert.Equal(["stop", "start", "stop"], actions);
        var console = scope.Console.ToString();
        Assert.Contains("[human_agent] Task quit without an answer.", console, StringComparison.Ordinal);
        Assert.Contains("[human_agent] Task ended without an answer.", console, StringComparison.Ordinal);
    }

    [Fact]
    public async Task time_accounting_only_advances_while_the_clock_runs()
    {
        using var scope = new HumanScope();
        using var deadline = Deadline();
        var clock = new FakeClock { Now = 10_000 };
        HumanAgentState.ClockOverride.Value = clock.Read;
        try
        {
            var agent = HumanCli.Agent(recordSession: false, view: new ConsoleHumanAgentView(scope.Console), pollingInterval: Poll);
            var run = scope.Run(agent, deadline.Token);
            var sim = scope.Sim;

            clock.Now += 500;
            var idle = Result(await sim.CallAsync("status", cancellationToken: deadline.Token));
            await sim.CallAsync("start", cancellationToken: deadline.Token);
            clock.Now += 30;
            var running = Result(await sim.CallAsync("status", cancellationToken: deadline.Token));
            var stopped = Result(await sim.CallAsync("stop", cancellationToken: deadline.Token));
            clock.Now += 100;
            var paused = Result(await sim.CallAsync("status", cancellationToken: deadline.Token));
            await sim.CallAsync("start", cancellationToken: deadline.Token);
            var restarted = Result(await sim.CallAsync("start", cancellationToken: deadline.Token));
            clock.Now += 3_600 + 15;
            await sim.CallAsync("submit", Args("answer", "42"), cancellationToken: deadline.Token);
            await run;

            Assert.Equal("Status: Stopped  Time: 0:00:00", idle);
            Assert.Equal("Status: Running  Time: 0:00:30", running);
            Assert.Equal("Status: Stopped  Time: 0:00:30", stopped);
            Assert.Equal("Status: Stopped  Time: 0:00:30", paused);
            Assert.Equal("Status: Running  Time: 0:00:30", restarted);
            Assert.Equal(30 + 3_615, scope.State.Time());
            Assert.Equal(30 + 3_615, scope.State.AccumulatedTime);

            var totals = scope.InfoEvents.Select(e => (e.Data!["action"]!.GetValue<string>(), e.Data!["total_time"]!.GetValue<string>())).ToList();
            Assert.Equal([("stop", "0:00:00"), ("start", "0:00:00"), ("stop", "0:00:30"), ("start", "0:00:30")], totals);
        }
        finally
        {
            HumanAgentState.ClockOverride.Value = null;
        }
    }

    [Fact]
    public void the_state_folds_runs_into_accumulated_time_and_formats_progress_time()
    {
        var clock = new FakeClock { Now = 100 };
        HumanAgentState.ClockOverride.Value = clock.Read;
        try
        {
            var state = new Store().As<HumanAgentState>();
            Assert.False(state.IsRunning());
            Assert.Equal(0, state.Time());

            state.SetRunning(true);
            state.SetRunning(true);
            clock.Now = 160;
            Assert.Equal(60, state.Time());
            Assert.Equal(100, state.StartedRunning);

            state.SetRunning(false);
            clock.Now = 1_000;
            Assert.Equal(60, state.Time());
            Assert.Equal(60, state.AccumulatedTime);

            state.SetRunning(true);
            clock.Now = 1_010.9;
            Assert.Equal(70.9, state.Time(), precision: 6);
        }
        finally
        {
            HumanAgentState.ClockOverride.Value = null;
        }

        Assert.Equal(" 0:00:00", HumanAgentText.FormatProgressTime(0));
        Assert.Equal("0:00:59", HumanAgentText.FormatProgressTime(59.9, padHours: false));
        Assert.Equal(" 1:01:05", HumanAgentText.FormatProgressTime(3_665));
        Assert.Equal("12:00:00", HumanAgentText.FormatProgressTime(12 * 3_600));
    }

    [Fact]
    public async Task the_installer_writes_the_task_cli_bashrc_and_install_script_then_removes_the_staging_directory()
    {
        var sim = new ContainerSimulator();
        var commands = HumanAgentCommands.Create(new AgentState([]), sim, answer: true, answerPattern: null, intermediateScoring: true, recordSession: true, instructions: null);

        var installed = await HumanAgentInstall.InstallAsync(sim, user: null, commands, bashrcContent: "export EDITOR=vim", recordSession: true);

        Assert.True(installed);
        var calls = sim.Calls;
        var argv = calls.Select(call => string.Join(" ", call.Cmd)).ToList();
        Assert.Equal("mkdir /opt/human_agent", argv[0]);
        Assert.Equal("root", calls[0].User);
        Assert.Equal("whoami", argv[1]);
        Assert.Equal("chown agent /opt/human_agent", argv[2]);
        Assert.Equal("root", calls[2].User);
        Assert.Equal("mkdir -p human_agent_install", argv[3]);
        Assert.Equal("chown agent human_agent_install", argv[4]);
        Assert.Equal("tee -- human_agent_install/task.py", argv[5]);
        Assert.Equal("chmod +x human_agent_install/task.py", argv[6]);
        Assert.Equal("tee -- human_agent_install/.bashrc", argv[7]);
        Assert.Equal("chmod +x human_agent_install/.bashrc", argv[8]);
        Assert.Equal("tee -- human_agent_install/install.sh", argv[9]);
        Assert.Equal("chmod +x human_agent_install/install.sh", argv[10]);
        Assert.Equal("bash ./install.sh", argv[11]);
        Assert.Equal("human_agent_install", calls[11].Cwd);
        Assert.Equal("rm -rf human_agent_install", argv[12]);
        Assert.Equal(13, calls.Count);

        // the staging files are gone after rm -rf; their content was captured on the way through tee
        Assert.Empty(sim.FilesUnder("human_agent_install"));
        var taskPy = calls[5].Input!;
        Assert.Contains("from human_agent import call_human_agent", taskPy, StringComparison.Ordinal);
        Assert.Contains("def submit(args: Namespace) -> None:", taskPy, StringComparison.Ordinal);
        Assert.Contains("def score(args: Namespace) -> None:", taskPy, StringComparison.Ordinal);
        Assert.Contains("def instructions(args: Namespace) -> None:", taskPy, StringComparison.Ordinal);
        Assert.DoesNotContain("def validate", taskPy, StringComparison.Ordinal);
        Assert.Contains("submit_parser = subparsers.add_parser(\"submit\", help=\"Submit your final answer for the task.\")", taskPy, StringComparison.Ordinal);
        Assert.Contains("submit_parser.add_argument(\"answer\", nargs=\"?\", help=\"Answer to submit for scoring (optional, not required for all tasks)\")", taskPy, StringComparison.Ordinal);
        Assert.Contains("if command == \"submit\": submit(args)", taskPy, StringComparison.Ordinal);
        Assert.Contains("elif command == \"instructions\": instructions(args)", taskPy, StringComparison.Ordinal);
        Assert.EndsWith("else: parser.print_help()\n", taskPy, StringComparison.Ordinal);

        var bashrc = calls[7].Input!;
        Assert.Contains("alias task='python3 /opt/human_agent/task.py'", bashrc, StringComparison.Ordinal);
        Assert.Contains("local commands=\"submit quit score note status start stop instructions\"", bashrc, StringComparison.Ordinal);
        Assert.Contains("export EDITOR=vim", bashrc, StringComparison.Ordinal);
        Assert.Contains("exec script -q -f -m advanced -I \"$INPUTFILE\" -O \"$OUTPUTFILE\" -T \"$TIMINGFILE\" -c \"bash --login -i\"", bashrc, StringComparison.Ordinal);
        Assert.Contains("LOGDIR=/var/tmp/user-sessions", bashrc, StringComparison.Ordinal);
        Assert.Contains("task instructions > ~/instructions.txt", bashrc, StringComparison.Ordinal);
        Assert.EndsWith("task start\n", bashrc, StringComparison.Ordinal);

        var installSh = calls[9].Input!;
        Assert.Contains("HUMAN_AGENT=\"/opt/human_agent\"", installSh, StringComparison.Ordinal);
        Assert.Contains("cp task.py $HUMAN_AGENT", installSh, StringComparison.Ordinal);
        Assert.Contains("USER=\"agent\"", installSh, StringComparison.Ordinal);
        Assert.Contains("cat .bashrc >> $USER_HOME/.bashrc", installSh, StringComparison.Ordinal);

        // a second install is a no-op (the mkdir fails because the directory exists)
        var again = await HumanAgentInstall.InstallAsync(sim, user: null, commands, bashrcContent: null, recordSession: true);
        Assert.False(again);
        Assert.Equal(14, sim.Calls.Count);
    }

    [Fact]
    public void the_bashrc_omits_recording_when_the_session_is_not_recorded_and_the_task_cli_omits_score_without_intermediate_scoring()
    {
        var sim = new ContainerSimulator();
        var commands = HumanAgentCommands.Create(new AgentState([]), sim, answer: true, answerPattern: null, intermediateScoring: false, recordSession: false, instructions: null);

        var bashrc = HumanAgentInstall.BashrcScript(commands, null, recordSession: false);
        var taskPy = HumanAgentInstall.TaskScript(commands);
        var installSh = HumanAgentInstall.InstallScript("root");

        Assert.DoesNotContain("exec script", bashrc, StringComparison.Ordinal);
        Assert.Contains("local commands=\"submit quit note status start stop instructions\"", bashrc, StringComparison.Ordinal);
        Assert.StartsWith("\n\n### Inspect Human Agent Setup", bashrc, StringComparison.Ordinal);
        Assert.DoesNotContain("def score", taskPy, StringComparison.Ordinal);
        Assert.DoesNotContain("score_parser", taskPy, StringComparison.Ordinal);
        Assert.Contains("USER=\"root\"", installSh, StringComparison.Ordinal);
        Assert.Equal(["submit", "validate", "quit", "note", "status", "start", "stop", "instructions"], commands.Select(command => command.Name));
    }

    [Fact]
    public async Task instructions_list_the_commands_by_group_with_the_extra_and_task_instructions()
    {
        using var scope = new HumanScope();
        using var deadline = Deadline();
        var agent = HumanCli.Agent(intermediateScoring: true, recordSession: false, instructions: "The flag looks like flag{...}.", view: new ConsoleHumanAgentView(scope.Console), pollingInterval: Poll);

        var run = scope.Run(agent, deadline.Token);
        var sim = scope.Sim;
        var text = Result(await sim.CallAsync("instructions", cancellationToken: deadline.Token));
        await sim.CallAsync("start", cancellationToken: deadline.Token);
        await sim.CallAsync("submit", Args("answer", "flag{x}"), cancellationToken: deadline.Token);
        await run;

        var lines = text.Split('\n');
        Assert.Contains("═ Human Agent Task ═", lines[0], StringComparison.Ordinal);
        // the rule is the full width less the leading space the final strip removes (as Python's render_text does)
        Assert.Equal(HumanAgentText.InstructionsWidth - 1, lines[0].Length);
        Assert.Contains(" You will be completing a task based on the instructions presented below.", text, StringComparison.Ordinal);
        var commandLines = lines.Where(line => line.StartsWith(" task ", StringComparison.Ordinal)).Select(line => line.Trim()).ToList();
        Assert.Equal(
            [
                "task submit  Submit your final answer for the task.",
                "task quit  Quit the task without submitting an answer.",
                "task score  Score the task to check progress.",
                "task note  Record a note in the task transcript.",
                "task status  Print task status (clock, scoring, etc.)",
                "task start  Start the task clock (resume working).",
                "task stop  Stop the task clock (pause working).",
                "task instructions  Display task commands and instructions.",
            ],
            commandLines.Select(line => System.Text.RegularExpressions.Regex.Replace(line, " {2,}", "  ")));
        Assert.DoesNotContain("task validate", text, StringComparison.Ordinal);
        Assert.Contains(" The flag looks like flag{...}.", text, StringComparison.Ordinal);
        Assert.Contains("╭─ Task Instructions ", text, StringComparison.Ordinal);
        Assert.Contains($"│ {TaskText}", text, StringComparison.Ordinal);
        Assert.EndsWith("╯", text, StringComparison.Ordinal);
        Assert.All(lines.Where(line => line.StartsWith('│')), line => Assert.Equal(HumanAgentText.InstructionsWidth, line.Length));
    }

    [Fact]
    public async Task the_agent_requires_a_sandbox_that_supports_connections()
    {
        var model = new Model(new ScriptedModelApi());
        var agent = HumanCli.Agent(pollingInterval: Poll);

        using (SampleContext.Begin(new SampleContext { ActiveModel = model }))
        {
            var noSandbox = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Execute(new AgentState([]), CancellationToken.None));
            Assert.Equal("Human agent must run in a task with a sandbox.", noSandbox.Message);
        }

        using (SampleContext.Begin(new SampleContext { ActiveModel = model, Sandboxes = SandboxEnvironments.Single(new FakeSandboxEnvironment()) }))
        {
            var noConnection = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Execute(new AgentState([]), CancellationToken.None));
            Assert.Equal("Human agent must run with a sandbox that supports connections.", noConnection.Message);
            Assert.IsType<NotSupportedException>(noConnection.InnerException);
        }

        var noContext = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Execute(new AgentState([]), CancellationToken.None));
        Assert.Equal("Human agent must run in a task with a sandbox.", noContext.Message);
    }

    [Fact]
    public async Task the_service_is_cancelled_with_the_sample()
    {
        using var scope = new HumanScope();
        using var cancel = new CancellationTokenSource();
        var agent = HumanCli.Agent(recordSession: false, view: new ConsoleHumanAgentView(scope.Console), pollingInterval: Poll);

        var run = scope.Run(agent, cancel.Token);
        await scope.Sim.CallAsync("start", cancellationToken: CancellationToken.None);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Null(scope.State.Answer);
    }
}
