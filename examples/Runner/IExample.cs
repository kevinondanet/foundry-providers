using System.Globalization;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;

namespace InspectAzureAI.Examples.Runner;

using Hooks = InspectAzureAI.Eval.Hooks.Hooks;
using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// One ported example of the inspect_ai <c>examples/</c> folder, discovered by reflection (<see cref="ExampleRegistry"/>)
/// and run by <see cref="ExampleRunner"/>: <c>dotnet run --project examples -- &lt;name&gt; [flags]</c>. Implement it as
/// a public sealed class with a parameterless constructor in <c>examples/&lt;name&gt;/&lt;Name&gt;Example.cs</c>.
/// </summary>
public interface IExample
{
    /// <summary>The Python example's name, verbatim ("hello_world", "bridge/langchain"); the runner matches it case-insensitively.</summary>
    string Name { get; }

    /// <summary>One line, printed by <c>list</c> and <c>--help</c>.</summary>
    string Description { get; }

    /// <summary>One entry per Python <c>@task</c> (name verbatim); the first is the default of <c>--task</c>.</summary>
    IReadOnlyList<ExampleTask> Tasks { get; }

    /// <summary>What the runner uses when a flag is not given.</summary>
    ExampleDefaults Defaults { get; }

    /// <summary>
    /// The scripted model behind <c>--fake</c>: a <see cref="ScriptedModelApi"/> wrapped as a <see cref="Model"/>
    /// (<see cref="ScriptedTurn.From(Func{IReadOnlyList{Provider.Core.ChatMessage}, IReadOnlyList{Provider.Core.ToolInfo}, Provider.Core.ModelOutput})"/>
    /// factories keyed on the conversation keep concurrent samples deterministic).
    /// </summary>
    Model CreateFakeModel(ExampleContext ctx);

    /// <summary>
    /// Scripted exec/read/write results for <c>--sandbox fake</c>, or null when the example needs no sandbox (or its
    /// tools work under <c>--sandbox local</c>). Called with <see cref="ExampleContext.Sandbox"/> still null when the
    /// runner is deciding which sandbox a <c>--fake</c> run gets.
    /// </summary>
    FakeSandboxScript? FakeSandbox(ExampleContext ctx);

    /// <summary>The README's "Deviations from Python" bullets; printed by <c>--help</c>.</summary>
    IReadOnlyList<string> Deviations { get; }
}

/// <summary>
/// Optional: an example that prints its own report after the runner's summary (cache hits, early stops, an eval
/// set's per-model table) gets the finished log here instead of chaining a display-only solver into its task.
/// </summary>
public interface IExampleReport
{
    /// <summary>Called once after <see cref="ExampleRunner.PrintSummary"/> with the run's log.</summary>
    void Report(EvalLog log, ExampleContext ctx);
}

/// <summary>
/// Optional: an example that needs hooks for the run (Python's <c>@hooks</c> registrations) returns them here; the
/// runner passes them to the eval with the <c>--display conversation</c> hook, so nothing touches the process-wide
/// <c>HookRegistry</c>.
/// </summary>
public interface IExampleHooks
{
    /// <summary>The hooks of one run, built after the task (so the example can share state with it).</summary>
    IReadOnlyList<Hooks> Hooks(ExampleContext ctx);
}

/// <summary>One <c>@task</c> of an example: its name (verbatim) and how to build it for a run.</summary>
public sealed record ExampleTask(string Name, Func<ExampleContext, EvalTask> Build, string? Description = null);

/// <summary>
/// An example's defaults. <paramref name="Sandbox"/> is "none" (no sandbox on the task), "local" or "docker";
/// <paramref name="ComposeFile"/> is a compose file relative to the example folder (docker only);
/// <paramref name="Approval"/> is an approval policy file relative to the example folder, or a registered approver
/// name; <paramref name="NeedsDocker"/> says a live run cannot work without Docker; <paramref name="ModelHint"/>
/// names what the deployment must support (for example "a vision-capable deployment").
/// </summary>
public sealed record ExampleDefaults(
    string Sandbox = "none",
    string? ComposeFile = null,
    string? Approval = null,
    bool NeedsDocker = false,
    string? ModelHint = null);

/// <summary>
/// What the runner hands an example when building its task and its fakes. <see cref="ExampleDirectory"/> is
/// <c>AppContext.BaseDirectory/&lt;name&gt;</c>, where the example's data files are copied by the project;
/// <see cref="Sandbox"/> is the resolved sandbox spec (null for "none"); <see cref="TaskArgs"/> holds the
/// <c>-T key=value</c> flags; <see cref="Model"/> is the <c>--model</c> value and <see cref="ResolvedModel"/> the model
/// the run uses (the fake one under <c>--fake</c>); <see cref="Out"/> is where the example may print.
/// </summary>
public sealed record ExampleContext(
    string ExampleDirectory,
    SandboxSpec? Sandbox,
    bool Fake,
    IReadOnlyDictionary<string, string> TaskArgs,
    string? Model,
    Model? ResolvedModel,
    TextWriter Out)
{
    /// <summary>A path under <see cref="ExampleDirectory"/>.</summary>
    public string DataPath(string relativePath) => Path.Combine(ExampleDirectory, relativePath);

    /// <summary>The <c>-T</c> value for <paramref name="key"/>, else <paramref name="defaultValue"/>.</summary>
    public string? TaskArg(string key, string? defaultValue = null) => TaskArgs.TryGetValue(key, out var value) ? value : defaultValue;

    /// <summary>The <c>-T</c> value for <paramref name="key"/> as an integer (a value that is not one is an <see cref="ArgumentException"/>).</summary>
    public int TaskArgInt(string key, int defaultValue) =>
        TaskArgs.TryGetValue(key, out var value)
            ? int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new ArgumentException($"-T {key} expects an integer, got '{value}'")
            : defaultValue;

    /// <summary>The <c>-T</c> value for <paramref name="key"/> as a double.</summary>
    public double TaskArgDouble(string key, double defaultValue) =>
        TaskArgs.TryGetValue(key, out var value)
            ? double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : throw new ArgumentException($"-T {key} expects a number, got '{value}'")
            : defaultValue;

    /// <summary>The <c>-T</c> value for <paramref name="key"/> as a boolean (true/false/yes/no/1/0, Python's True/False included).</summary>
    public bool TaskArgBool(string key, bool defaultValue) =>
        TaskArgs.TryGetValue(key, out var value)
            ? value.ToLowerInvariant() switch
            {
                "true" or "yes" or "1" or "on" => true,
                "false" or "no" or "0" or "off" => false,
                _ => throw new ArgumentException($"-T {key} expects true or false, got '{value}'"),
            }
            : defaultValue;
}

/// <summary>
/// The script behind <c>--sandbox fake</c>: exec commands are answered by rules matched by exact argv first, then by
/// the longest matching argv prefix, then by predicates in the order added, then <see cref="Default"/>; a rule can
/// also say the command runs for real on this host (<see cref="LocalExact"/>, <see cref="LocalPrefix"/>,
/// <see cref="RunUnmatchedLocally"/>). Files start from <see cref="Files"/> (each sample gets its own copy) and
/// reads/writes go to the sample's store. Every exec call is recorded in <see cref="Calls"/> and every environment
/// created in <see cref="Environments"/>, so tests can inspect what the tools did. A handler that must read or
/// write the answering sample's files takes the environment as its first argument (the two-argument overloads),
/// reads <see cref="ScriptedSandboxEnvironment.Current"/>, or looks it up with <see cref="EnvironmentOf"/>; a
/// handler that awaits IO uses the <c>*Async</c> overloads.
/// </summary>
public sealed class FakeSandboxScript
{
    private readonly object _sync = new();
    private readonly List<FakeExecCall> _calls = [];
    private readonly List<ScriptedSandboxEnvironment> _environments = [];

    internal List<FakeExecRule> Rules { get; } = [];

    /// <summary>The answer for a command no rule matches (null: run it locally when <see cref="RunUnmatchedLocally"/>, else <see cref="Ok"/>).</summary>
    public ExecResult? Default { get; set; }

    /// <summary>When true, a command no rule matches runs on this host (through the local sandbox) instead of answering <see cref="Default"/>.</summary>
    public bool RunUnmatchedLocally { get; set; }

    /// <summary>Address reported by <see cref="ISandboxEnvironment.HostAddress"/>.</summary>
    public string HostAddress { get; set; } = "127.0.0.1";

    /// <summary>The initial files of every sample's sandbox, by path.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Every exec call answered so far, across samples, in order.</summary>
    public IReadOnlyList<FakeExecCall> Calls
    {
        get
        {
            lock (_sync)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>Every environment created so far (one per sample), in creation order; each holds the sample's files.</summary>
    public IReadOnlyList<ScriptedSandboxEnvironment> Environments
    {
        get
        {
            lock (_sync)
            {
                return _environments.ToArray();
            }
        }
    }

    public static ExecResult Ok(string stdout = "", string stderr = "") => new(true, 0, stdout, stderr);

    public static ExecResult Fail(int returnCode, string stderr = "", string stdout = "") => new(false, returnCode, stdout, stderr);

    /// <summary>Answers <paramref name="argv"/> (matched exactly) with <paramref name="result"/>.</summary>
    public FakeSandboxScript OnExact(ExecResult result, params string[] argv) => Add(new FakeExecRule(FakeExecRuleKind.Exact, argv, _ => result));

    /// <summary>Answers <paramref name="argv"/> (matched exactly) with what <paramref name="handler"/> returns (null falls through to the next rule).</summary>
    public FakeSandboxScript OnExact(Func<FakeExecCall, ExecResult?> handler, params string[] argv) => Add(new FakeExecRule(FakeExecRuleKind.Exact, argv, handler));

    /// <summary>Answers any command starting with <paramref name="prefix"/> with <paramref name="result"/>.</summary>
    public FakeSandboxScript OnPrefix(ExecResult result, params string[] prefix) => Add(new FakeExecRule(FakeExecRuleKind.Prefix, prefix, _ => result));

    /// <summary>Answers any command starting with <paramref name="prefix"/> with what <paramref name="handler"/> returns (null falls through).</summary>
    public FakeSandboxScript OnPrefix(Func<FakeExecCall, ExecResult?> handler, params string[] prefix) => Add(new FakeExecRule(FakeExecRuleKind.Prefix, prefix, handler));

    /// <summary>Answers any command <paramref name="predicate"/> accepts with what <paramref name="handler"/> returns (null falls through).</summary>
    public FakeSandboxScript OnMatch(Func<FakeExecCall, bool> predicate, Func<FakeExecCall, ExecResult?> handler) => Add(new FakeExecRule(FakeExecRuleKind.Predicate, [], handler, predicate));

    /// <summary>Answers <paramref name="argv"/> (matched exactly) with what <paramref name="handler"/> returns, given the answering sample's environment (null falls through).</summary>
    public FakeSandboxScript OnExact(Func<ScriptedSandboxEnvironment, FakeExecCall, ExecResult?> handler, params string[] argv) => Add(new FakeExecRule(FakeExecRuleKind.Exact, argv, WithEnvironment(handler)));

    /// <summary>Answers any command starting with <paramref name="prefix"/> with what <paramref name="handler"/> returns, given the answering sample's environment (null falls through).</summary>
    public FakeSandboxScript OnPrefix(Func<ScriptedSandboxEnvironment, FakeExecCall, ExecResult?> handler, params string[] prefix) => Add(new FakeExecRule(FakeExecRuleKind.Prefix, prefix, WithEnvironment(handler)));

    /// <summary>Answers any command <paramref name="predicate"/> accepts with what <paramref name="handler"/> returns, given the answering sample's environment (null falls through).</summary>
    public FakeSandboxScript OnMatch(Func<FakeExecCall, bool> predicate, Func<ScriptedSandboxEnvironment, FakeExecCall, ExecResult?> handler) => Add(new FakeExecRule(FakeExecRuleKind.Predicate, [], WithEnvironment(handler), predicate));

    /// <summary>Answers <paramref name="argv"/> (matched exactly) with what the asynchronous <paramref name="handler"/> returns (null falls through).</summary>
    public FakeSandboxScript OnExactAsync(Func<FakeExecCall, CancellationToken, Task<ExecResult?>> handler, params string[] argv) => Add(new FakeExecRule(FakeExecRuleKind.Exact, argv, null, AsyncHandler: handler));

    /// <summary>Answers any command starting with <paramref name="prefix"/> with what the asynchronous <paramref name="handler"/> returns (null falls through).</summary>
    public FakeSandboxScript OnPrefixAsync(Func<FakeExecCall, CancellationToken, Task<ExecResult?>> handler, params string[] prefix) => Add(new FakeExecRule(FakeExecRuleKind.Prefix, prefix, null, AsyncHandler: handler));

    /// <summary>Answers any command <paramref name="predicate"/> accepts with what the asynchronous <paramref name="handler"/> returns (null falls through).</summary>
    public FakeSandboxScript OnMatchAsync(Func<FakeExecCall, bool> predicate, Func<FakeExecCall, CancellationToken, Task<ExecResult?>> handler) => Add(new FakeExecRule(FakeExecRuleKind.Predicate, [], null, predicate, AsyncHandler: handler));

    /// <summary>The environment (sample) that recorded <paramref name="call"/>, or null when none did (a call handed to a handler directly).</summary>
    public ScriptedSandboxEnvironment? EnvironmentOf(FakeExecCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return Environments.FirstOrDefault(environment => environment.Recorded(call));
    }

    /// <summary>Runs <paramref name="argv"/> (matched exactly) for real on this host.</summary>
    public FakeSandboxScript LocalExact(params string[] argv) => Add(new FakeExecRule(FakeExecRuleKind.Exact, argv, null, Local: true));

    /// <summary>Runs any command starting with <paramref name="prefix"/> for real on this host.</summary>
    public FakeSandboxScript LocalPrefix(params string[] prefix) => Add(new FakeExecRule(FakeExecRuleKind.Prefix, prefix, null, Local: true));

    /// <summary>Adds an initial text file.</summary>
    public FakeSandboxScript WithFile(string path, string contents)
    {
        Files[path] = System.Text.Encoding.UTF8.GetBytes(contents);
        return this;
    }

    /// <summary>Adds an initial binary file.</summary>
    public FakeSandboxScript WithFile(string path, byte[] contents)
    {
        Files[path] = contents;
        return this;
    }

    /// <summary>Sets <see cref="Default"/>.</summary>
    public FakeSandboxScript WithDefault(ExecResult result)
    {
        Default = result;
        return this;
    }

    /// <summary>Sets <see cref="RunUnmatchedLocally"/>.</summary>
    public FakeSandboxScript WithUnmatchedLocal(bool value = true)
    {
        RunUnmatchedLocally = value;
        return this;
    }

    /// <summary>Resolves <paramref name="call"/> against the rules: the answer, or a "run it locally" instruction (a null result with <c>Local</c> true).</summary>
    internal async Task<(ExecResult? Result, bool Local)> ResolveAsync(FakeExecCall call, CancellationToken cancellationToken)
    {
        var exact = Rules.Where(rule => rule.Kind == FakeExecRuleKind.Exact && rule.Matches(call));
        var prefixes = Rules.Where(rule => rule.Kind == FakeExecRuleKind.Prefix && rule.Matches(call)).OrderByDescending(rule => rule.Argv.Count);
        var predicates = Rules.Where(rule => rule.Kind == FakeExecRuleKind.Predicate && rule.Matches(call));
        foreach (var rule in exact.Concat(prefixes).Concat(predicates))
        {
            if (rule.Local)
            {
                return (null, true);
            }

            var answer = rule.AsyncHandler is { } asyncHandler
                ? await asyncHandler(call, cancellationToken).ConfigureAwait(false)
                : rule.Handler?.Invoke(call);
            if (answer is { } result)
            {
                return (result, false);
            }
        }

        return Default is { } fallback ? (fallback, false) : RunUnmatchedLocally ? (null, true) : (Ok(), false);
    }

    /// <summary>Adapts an environment-taking handler: the environment is the one answering the call (<see cref="ScriptedSandboxEnvironment.Current"/>, else the one that recorded it).</summary>
    private Func<FakeExecCall, ExecResult?> WithEnvironment(Func<ScriptedSandboxEnvironment, FakeExecCall, ExecResult?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return call => handler(
            ScriptedSandboxEnvironment.Current ?? EnvironmentOf(call) ?? throw new InvalidOperationException("The exec call was not made through a scripted sandbox environment."),
            call);
    }

    internal void Record(FakeExecCall call)
    {
        lock (_sync)
        {
            _calls.Add(call);
        }
    }

    internal void Record(ScriptedSandboxEnvironment environment)
    {
        lock (_sync)
        {
            _environments.Add(environment);
        }
    }

    private FakeSandboxScript Add(FakeExecRule rule)
    {
        Rules.Add(rule);
        return this;
    }
}

internal enum FakeExecRuleKind
{
    Exact,
    Prefix,
    Predicate,
}

/// <summary>One rule of a <see cref="FakeSandboxScript"/>.</summary>
internal sealed record FakeExecRule(
    FakeExecRuleKind Kind,
    IReadOnlyList<string> Argv,
    Func<FakeExecCall, ExecResult?>? Handler,
    Func<FakeExecCall, bool>? Predicate = null,
    bool Local = false,
    Func<FakeExecCall, CancellationToken, Task<ExecResult?>>? AsyncHandler = null)
{
    public bool Matches(FakeExecCall call) => Kind switch
    {
        FakeExecRuleKind.Exact => call.Cmd.SequenceEqual(Argv, StringComparer.Ordinal),
        FakeExecRuleKind.Prefix => call.Cmd.Count >= Argv.Count && call.Cmd.Take(Argv.Count).SequenceEqual(Argv, StringComparer.Ordinal),
        _ => Predicate?.Invoke(call) == true,
    };
}
