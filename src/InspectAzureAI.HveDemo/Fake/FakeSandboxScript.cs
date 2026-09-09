using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;

namespace InspectAzureAI.HveDemo.Fake;

/// <summary>
/// A copy of <c>examples/Runner/IExample.cs</c> <c>FakeSandboxScript</c> (the demo does not reference the examples
/// project): scripted exec results for <c>--sandbox fake</c>, matched exactly, by prefix or by predicate, with the
/// initial files of every sample's sandbox, and a record of every call and environment. Rules answering null fall
/// through; a <c>Local*</c> rule runs the command for real on this host in the sample's mirror directory
/// (<see cref="ScriptedSandboxEnvironment.MirrorDirectory"/>), which is also what an unmatched command does when
/// <see cref="RunUnmatchedLocally"/> is set.
/// </summary>
public sealed class FakeSandboxScript
{
    private readonly object _sync = new();
    private readonly List<FakeExecCall> _calls = [];
    private readonly List<ScriptedSandboxEnvironment> _environments = [];

    internal List<FakeExecRule> Rules { get; } = [];

    /// <summary>The answer for a command no rule matches (null: run it locally when <see cref="RunUnmatchedLocally"/>, else <see cref="Ok"/>).</summary>
    public ExecResult? Default { get; set; }

    /// <summary>When true, a command no rule matches runs on this host (in the sample's mirror directory) instead of answering <see cref="Default"/>.</summary>
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

    /// <summary>Answers any command <paramref name="predicate"/> accepts with what <paramref name="handler"/> returns, given the answering sample's environment (null falls through).</summary>
    public FakeSandboxScript OnMatch(Func<FakeExecCall, bool> predicate, Func<ScriptedSandboxEnvironment, FakeExecCall, ExecResult?> handler) => Add(new FakeExecRule(FakeExecRuleKind.Predicate, [], WithEnvironment(handler), predicate));

    /// <summary>Answers any command <paramref name="predicate"/> accepts with what the asynchronous <paramref name="handler"/> returns, given the answering sample's environment (null falls through).</summary>
    public FakeSandboxScript OnMatchAsync(Func<FakeExecCall, bool> predicate, Func<ScriptedSandboxEnvironment, FakeExecCall, CancellationToken, Task<ExecResult?>> handler) =>
        Add(new FakeExecRule(FakeExecRuleKind.Predicate, [], null, predicate, AsyncHandler: (call, cancellationToken) => handler(Environment(call), call, cancellationToken)));

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

    private Func<FakeExecCall, ExecResult?> WithEnvironment(Func<ScriptedSandboxEnvironment, FakeExecCall, ExecResult?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return call => handler(Environment(call), call);
    }

    /// <summary>The environment answering <paramref name="call"/>: the current one on this async flow, else the one that recorded it.</summary>
    private ScriptedSandboxEnvironment Environment(FakeExecCall call) =>
        ScriptedSandboxEnvironment.Current ?? EnvironmentOf(call) ?? throw new InvalidOperationException("The exec call was not made through a scripted sandbox environment.");

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
