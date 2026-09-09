using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Agents.Human;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.Human;

/// <summary>
/// The <c>--fake</c> sandbox provider of the human example, an addition for running the demonstration offline: registered
/// under the runner's <c>fake</c> type (replacing <see cref="ScriptedSandboxProvider"/>, whose environments answer no
/// connection requests) so <see cref="HumanFakeContainer"/>s host the <c>human_cli</c> agent, each with a
/// <see cref="HumanOperatorScript"/> playing the person. The runner's <see cref="FakeSandboxScript"/> is kept: every exec
/// call is recorded on it and its rules answer whatever the container does not emulate.
/// </summary>
public sealed class HumanFakeSandboxProvider(FakeSandboxScript script, HumanOperatorScript operatorScript) : ISandboxProvider
{
    private readonly object _sync = new();

    private readonly List<HumanFakeContainer> _containers = [];

    public FakeSandboxScript Script { get; } = script ?? throw new ArgumentNullException(nameof(script));

    public HumanOperatorScript Operator { get; } = operatorScript ?? throw new ArgumentNullException(nameof(operatorScript));

    public string Type => ScriptedSandboxProvider.TypeName;

    /// <summary>Every container created so far (one per sample), in creation order.</summary>
    public IReadOnlyList<HumanFakeContainer> Containers
    {
        get
        {
            lock (_sync)
            {
                return _containers.ToArray();
            }
        }
    }

    /// <summary>Registers a provider for <paramref name="script"/> and <paramref name="operatorScript"/> and returns the spec that selects it.</summary>
    public static SandboxSpec Register(FakeSandboxScript script, HumanOperatorScript operatorScript)
    {
        SandboxRegistry.Register(new HumanFakeSandboxProvider(script, operatorScript));
        return new SandboxSpec(ScriptedSandboxProvider.TypeName);
    }

    /// <summary>The provider currently registered under the fake type, when it is one of these.</summary>
    public static HumanFakeSandboxProvider? Current => SandboxRegistry.Get(ScriptedSandboxProvider.TypeName) as HumanFakeSandboxProvider;

    public Task TaskInitAsync(string taskName, string? config, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<SandboxEnvironments> SampleInitAsync(string taskName, string? config, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        var container = new HumanFakeContainer(Script, Operator.NewSession());
        lock (_sync)
        {
            _containers.Add(container);
        }

        return Task.FromResult(SandboxEnvironments.Single(container));
    }

    public Task TaskCleanupAsync(string taskName, string? config, bool cleanup, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// One sample's scripted container: emulates the file commands the human agent installer and the sandbox service run
/// (<c>mkdir</c>, <c>tee</c>, <c>cat</c>, <c>rm</c>, <c>find</c>, <c>ls</c>, <c>wc</c>, <c>whoami</c>, <c>which</c>,
/// <c>test</c>, <c>chown</c>, <c>chmod</c>, <c>bash ./install.sh</c>) against its own file store, records every exec
/// call on the script, and lets the <see cref="HumanOperatorSession"/> act on every poll of the service's request
/// directory. It answers connection requests (the human agent refuses sandboxes that do not) with a placeholder command.
/// </summary>
public sealed class HumanFakeContainer(FakeSandboxScript script, HumanOperatorSession operatorSession) : ISandboxEnvironment, ISandboxConnectionProvider
{
    /// <summary>The container's default user, as in the <c>python:3.12-bookworm</c> image.</summary>
    public const string DefaultUser = "root";

    /// <summary>The name the connection reports.</summary>
    public const string ContainerName = "inspect-human-fake";

    /// <summary>What the console view prints as the login command.</summary>
    public const string LoginCommand = "# no login needed: a scripted operator runs the task commands (--sandbox docker prints the real docker exec command)";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly object _sync = new();

    public FakeSandboxScript Script { get; } = script ?? throw new ArgumentNullException(nameof(script));

    public HumanOperatorSession Operator { get; } = operatorSession ?? throw new ArgumentNullException(nameof(operatorSession));

    public string HostAddress => Script.HostAddress;

    /// <summary>The container's files, by absolute or install-relative path.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(script.Files, StringComparer.Ordinal);

    /// <summary>Directories created with <c>mkdir</c> (a second plain <c>mkdir</c> of one fails, which is how the installer detects a previous install).</summary>
    public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

    /// <summary>The exec calls this container answered, in order.</summary>
    public List<FakeExecCall> Calls { get; } = [];

    public Task<SandboxConnection> ConnectionAsync(string? user = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SandboxConnection("fake", LoginCommand, Container: ContainerName));

    public async Task<ExecResult> ExecAsync(
        IReadOnlyList<string> cmd,
        string? input = null,
        string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? user = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        cancellationToken.ThrowIfCancellationRequested();
        var call = new FakeExecCall(cmd, input, cwd, env, user, timeout);
        Script.Record(call);
        ExecResult? emulated;
        lock (_sync)
        {
            Calls.Add(call);
            emulated = Emulate(call);
        }

        return emulated ?? (await Script.ResolveAsync(call, cancellationToken).ConfigureAwait(false)).Result ?? FakeSandboxScript.Ok();
    }

    public Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        WriteFileAsync(path, Encoding.UTF8.GetBytes(contents), cancellationToken);

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            Files[path] = contents.ToArray();
        }

        return Task.CompletedTask;
    }

    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        StrictUtf8.GetString(await ReadFileBytesAsync(path, cancellationToken).ConfigureAwait(false));

    public Task<byte[]> ReadFileBytesAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Files.TryGetValue(path, out var bytes)
                ? Task.FromResult(bytes)
                : throw new FileNotFoundException($"File '{path}' was not found.", path);
        }
    }

    /// <summary>The text of a file, for assertions; null when absent.</summary>
    public string? FileText(string path)
    {
        lock (_sync)
        {
            return Files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
        }
    }

    /// <summary>Whether <paramref name="path"/> exists (a file or a directory).</summary>
    public bool Exists(string path)
    {
        lock (_sync)
        {
            return Files.ContainsKey(path) || Directories.Contains(path);
        }
    }

    private ExecResult? Emulate(FakeExecCall call)
    {
        var cmd = call.Cmd;
        if (cmd.Count == 0)
        {
            return null;
        }

        var last = Resolve(cmd[^1], call.Cwd);
        switch (cmd[0])
        {
            case "mkdir":
                if (!cmd.Contains("-p") && Directories.Contains(last))
                {
                    return FakeSandboxScript.Fail(1, $"mkdir: cannot create directory '{last}': File exists");
                }

                Directories.Add(last);
                return FakeSandboxScript.Ok();
            case "whoami":
                return FakeSandboxScript.Ok(DefaultUser + "\n");
            case "which":
                return cmd.Count > 1 && cmd[1] == "python3" ? FakeSandboxScript.Ok("/usr/local/bin/python3\n") : FakeSandboxScript.Fail(1);
            case "chown" or "chmod" or "sh" or "test":
                return FakeSandboxScript.Ok();
            case "tee":
                Files[last] = Encoding.UTF8.GetBytes(call.Input ?? "");
                return FakeSandboxScript.Ok(call.Input ?? "");
            case "cat":
                return Files.TryGetValue(last, out var content)
                    ? FakeSandboxScript.Ok(Encoding.UTF8.GetString(content))
                    : FakeSandboxScript.Fail(1, $"cat: {last}: No such file or directory");
            case "wc":
                return Files.TryGetValue(last, out var counted)
                    ? FakeSandboxScript.Ok($"{counted.Length} {last}\n")
                    : FakeSandboxScript.Fail(1, $"wc: {last}: No such file or directory");
            case "rm":
                if (cmd.Contains("-rf") || cmd.Contains("-r"))
                {
                    foreach (var key in Files.Keys.Where(key => key == last || key.StartsWith(last + "/", StringComparison.Ordinal)).ToList())
                    {
                        Files.Remove(key);
                    }

                    Directories.RemoveWhere(directory => directory == last || directory.StartsWith(last + "/", StringComparison.Ordinal));
                }
                else
                {
                    Files.Remove(last);
                }

                return FakeSandboxScript.Ok();
            case "find":
            {
                // the service's poll: find <requests> -maxdepth 1 -name *.json -type f -print0
                var directory = cmd.Count > 1 ? cmd[1] : last;
                Operator.OnPoll(this, directory);
                var files = Files.Keys.Where(key => Parent(key) == directory && key.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
                return FakeSandboxScript.Ok(files.Count == 0 ? "" : string.Join("\0", files) + "\0");
            }

            case "ls":
            {
                var names = Files.Keys.Where(key => Parent(key) == last).Select(Name).Order(StringComparer.Ordinal).ToList();
                return FakeSandboxScript.Ok(names.Count == 0 ? "" : string.Join("\n", names) + "\n");
            }

            case "bash" when cmd.Count > 1 && cmd[1] == $"./{HumanAgentInstall.InstallSh}":
                // install.sh copies task.py into /opt/human_agent and appends the .bashrc fragment
                if (Files.TryGetValue(Resolve(HumanAgentInstall.TaskPy, call.Cwd), out var taskPy))
                {
                    Files[$"{HumanAgentInstall.HumanAgentDir}/{HumanAgentInstall.TaskPy}"] = taskPy;
                }

                if (Files.TryGetValue(Resolve(HumanAgentInstall.Bashrc, call.Cwd), out var bashrc))
                {
                    Files[$"/{DefaultUser}/{HumanAgentInstall.Bashrc}"] = bashrc;
                }

                return FakeSandboxScript.Ok();
            default:
                return null;
        }
    }

    private static string Resolve(string path, string? cwd) =>
        path.StartsWith('/') || cwd is null ? path : $"{cwd.TrimEnd('/')}/{path}";

    private static string Parent(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
}

/// <summary>
/// The person in the scripted container: what they type at the <c>task</c> CLI, in order. Each session
/// (<see cref="NewSession"/>, one per sample) runs <c>task instructions</c>, <c>task start</c>, <c>task note</c>,
/// <c>task status</c> and <c>task submit &lt;answer&gt;</c> (which validates first, as the generated CLI does), printing
/// the commands and their replies to <paramref name="output"/> the way the terminal would show them.
/// </summary>
public sealed class HumanOperatorScript(TextWriter output, string answer = HumanExample.DefaultFakeAnswer)
{
    /// <summary>The note the operator records.</summary>
    public const string Note = "## Human Agent Note\nRead the instructions and started the clock; working in the sandbox now.";

    /// <summary>What the generated CLI prints before sending <c>submit</c>.</summary>
    public const string ThankYou = "\nThank you for working on this task!\n\nYour task will now be scored and you will be disconnected from this container.\n";

    public TextWriter Output { get; } = output ?? throw new ArgumentNullException(nameof(output));

    public string Answer { get; } = answer ?? throw new ArgumentNullException(nameof(answer));

    /// <summary>The steps of one session, in order: the shell line shown, the service method and its parameters.</summary>
    public IReadOnlyList<HumanOperatorStep> Steps =>
    [
        new("task instructions", "instructions", new JsonObject()),
        new("task start", "start", new JsonObject()),
        new($"task note\n{Note}\n^D", "note", new JsonObject { ["content"] = Note }),
        new("task status", "status", new JsonObject()),
        new($"task submit {Answer}", "validate", new JsonObject { ["answer"] = Answer }, StopOnStringResult: true),
        new(null, "submit", new JsonObject { ["answer"] = Answer }, Preface: ThankYou),
    ];

    public HumanOperatorSession NewSession() => new(this);
}

/// <summary>One <c>task</c> command of the scripted operator: <paramref name="Shell"/> is what the terminal shows (null: a continuation of the previous command), <paramref name="Method"/>/<paramref name="Parameters"/> the service request; <paramref name="StopOnStringResult"/> ends the session when the reply is text (the CLI's validate error path); <paramref name="Preface"/> is printed before the request goes out.</summary>
public sealed record HumanOperatorStep(string? Shell, string Method, JsonObject Parameters, bool StopOnStringResult = false, string? Preface = null);

/// <summary>
/// One sample's run of the <see cref="HumanOperatorScript"/>, driven by the sandbox service's polls: when the reply to
/// the request in flight has been written it is consumed and printed, then the next step's request is dropped into
/// the requests directory (a request file the service reads with <c>cat</c>, answers with <c>tee</c> into
/// <c>responses/</c> and removes with <c>rm</c>, exactly as the generated Python client expects).
/// </summary>
public sealed class HumanOperatorSession(HumanOperatorScript script)
{
    private readonly Queue<HumanOperatorStep> _steps = new(script.Steps);

    private string? _inFlightId;

    private HumanOperatorStep? _inFlight;

    public HumanOperatorScript Script { get; } = script;

    /// <summary>The replies received so far, by method.</summary>
    public List<(string Method, JsonNode? Result)> Replies { get; } = [];

    /// <summary>Whether every step was answered (or the session stopped on a validation error).</summary>
    public bool Finished { get; private set; }

    /// <summary>Called by the container (under its lock) whenever the service lists <paramref name="requestsDir"/>.</summary>
    public void OnPoll(HumanFakeContainer container, string requestsDir)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(requestsDir);
        if (!requestsDir.EndsWith("/" + SandboxService.RequestsDir, StringComparison.Ordinal))
        {
            return;
        }

        var serviceDir = requestsDir[..^(SandboxService.RequestsDir.Length + 1)];
        if (_inFlight is { } step && _inFlightId is { } id)
        {
            var responsePath = $"{serviceDir}/{SandboxService.ResponsesDir}/{id}.json";
            if (!container.Files.TryGetValue(responsePath, out var bytes))
            {
                return;
            }

            container.Files.Remove(responsePath);
            _inFlight = null;
            _inFlightId = null;
            var response = JsonNode.Parse(Encoding.UTF8.GetString(bytes)) as JsonObject;
            var result = response?["result"];
            Replies.Add((step.Method, result));
            if (response?["error"] is { } error)
            {
                Script.Output.WriteLine($"Error: {error}");
                Finished = true;
                _steps.Clear();
                return;
            }

            if (result is JsonValue value && value.TryGetValue<string>(out var text))
            {
                Script.Output.WriteLine(text);
                if (step.StopOnStringResult)
                {
                    Finished = true;
                    _steps.Clear();
                    return;
                }
            }
        }

        if (_steps.Count == 0)
        {
            Finished = true;
            return;
        }

        var next = _steps.Dequeue();
        if (next.Shell is { } shell)
        {
            Script.Output.WriteLine($"$ {shell}");
        }

        if (next.Preface is { } preface)
        {
            Script.Output.WriteLine(preface);
        }

        var requestId = Guid.NewGuid().ToString("N");
        var request = new JsonObject { ["id"] = requestId, ["method"] = next.Method, ["params"] = next.Parameters.DeepClone() };
        container.Files[$"{requestsDir}/{requestId}.json"] = Encoding.UTF8.GetBytes(request.ToJsonString());
        _inFlight = next;
        _inFlightId = requestId;
    }
}
