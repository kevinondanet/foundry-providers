// A narrated walk through the sandbox contract: the four types in
// src/InspectAzureAI.Eval/Sandbox/ that docs/container-orchestration.md §2 says
// you can "read and predict the rest" from.
//
//   dotnet run --project src/InspectAzureAI.SandboxContractDemo            # all six lessons
//   dotnet run --project src/InspectAzureAI.SandboxContractDemo -- 2       # one lesson
//   dotnet run --project src/InspectAzureAI.SandboxContractDemo -- 1 4 6   # a selection, run in order
//   dotnet run --project src/InspectAzureAI.SandboxContractDemo -- list    # lesson titles
//
// Lessons 1-5 use the real types from InspectAzureAI.Eval plus a toy in-memory
// provider (InMemorySandbox.cs). Lesson 6 runs the very same driver code against
// the real `local` provider to show that a caller written against the contract
// never needs to know which provider it got.

using System.Globalization;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Sandbox.Local;
using InspectAzureAI.SandboxContractDemo;

(int Number, string Title, Func<Task> Run)[] lessons =
[
    (1, "SandboxSpec - what the task asked for", Lesson1Async),
    (2, "ISandboxEnvironment - what solvers and tools actually touch", Lesson2Async),
    (3, "SandboxEnvironments - what one sample got", Lesson3Async),
    (4, "ISandboxProvider - the factory, with a lifecycle", Lesson4Async),
    (5, "SandboxRegistry - how a Type string becomes a provider", Lesson5Async),
    (6, "The payoff - one driver, any provider", Lesson6Async),
];

// ---------------------------------------------------------------------------
// Argument handling: no args = every lesson; otherwise lesson numbers, run in
// ascending order (lesson 6 needs the "memory" provider registered, which it
// does itself, so any subset works standalone).
// ---------------------------------------------------------------------------
if (args.Any(a => a is "list" or "--list" or "-h" or "--help" or "help"))
{
    PrintUsage(Console.Out);
    return 0;
}

var selected = new SortedSet<int>();
foreach (var arg in args)
{
    if (int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && lessons.Any(l => l.Number == n))
    {
        selected.Add(n);
    }
    else
    {
        Console.Error.WriteLine($"Unknown lesson '{arg}'.");
        Console.Error.WriteLine();
        PrintUsage(Console.Error);
        return 2;
    }
}

if (selected.Count == 0)
{
    selected.UnionWith(lessons.Select(l => l.Number));
}

foreach (var (number, title, run) in lessons.Where(l => selected.Contains(l.Number)))
{
    Console.WriteLine();
    Console.WriteLine($"== Lesson {number}: {title}");
    Console.WriteLine();
    await run();
}

Console.WriteLine();
Say(selected.Count == lessons.Length
    ? "Done. Next: docs/container-orchestration.md §3 (the lifecycle) and §4 (the docker provider)."
    : $"Done. Run with no arguments for all {lessons.Length} lessons, or `list` for the titles.");
return 0;

// ───────────────────────────────────────────────────────────────────────────
static Task Lesson1Async()
// ───────────────────────────────────────────────────────────────────────────
{
    // Two fields. Type is a registry key; Config is an opaque string only that provider understands.
    var docker = new SandboxSpec("docker");
    var dockerCompose = new SandboxSpec("docker", "compose.yaml");
    var local = new SandboxSpec("local");
    Show("docker        ", docker);
    Show("docker+compose", dockerCompose);
    Show("local         ", local);

    // It is a record, so equality is by value. That matters: Eval.RunAsync collects one spec per sample and
    // calls Distinct() before TaskInitAsync, so three samples asking for the same thing cost ONE task init.
    SandboxSpec[] perSample = [dockerCompose, new SandboxSpec("docker", "compose.yaml"), dockerCompose, local];
    Show("specs per sample", perSample.Length);
    Show("distinct specs  ", perSample.Distinct().Count());

    // Pitfall: the registry looks Type up case-insensitively, but record equality is ordinal. "Docker" and
    // "docker" reach the same provider yet count as two specs - and two TaskInit calls.
    Show("\"Docker\" == \"docker\" spec?", new SandboxSpec("Docker") == new SandboxSpec("docker"));

    // How a sample's spec merges with the task's (ResolveSpec below is a copy of the internal
    // SandboxSetup.ResolveSpec). The task always wins the TYPE; a sample may override only the CONFIG, and
    // only when its own type matches.
    var taskSpec = new SandboxSpec("docker", "task-compose.yaml");
    Show("sample says nothing         ", ResolveSpec(taskSpec, new Sample("q")));
    Show("sample overrides config     ", ResolveSpec(taskSpec, new Sample("q") { Sandbox = new SandboxSpec("docker", "hard-mode.yaml") }));
    Show("sample wants another type   ", ResolveSpec(taskSpec, new Sample("q") { Sandbox = new SandboxSpec("local", "ignored") }));
    Show("no task spec: sample's wins ", ResolveSpec(null, new Sample("q") { Sandbox = local }));
    return Task.CompletedTask;
}

// ───────────────────────────────────────────────────────────────────────────
static async Task Lesson2Async()
// ───────────────────────────────────────────────────────────────────────────
{
    var env = new InMemorySandboxEnvironment("default");

    // Three verbs: write a file, read a file, run a command.
    await env.WriteFileAsync("/work/notes.txt", "line one\nline two\n");
    Show("ReadFileAsync             ", await env.ReadFileAsync("/work/notes.txt"));
    Show("relative path under cwd   ", await env.ExecAsync(["cat", "notes.txt"], cwd: "/work"));

    // cmd is ARGV, never a shell string. There is no shell, so nothing expands: "$HOME" and "*.txt" are passed
    // to the program exactly as typed. (This is also why there is no quoting or injection problem.)
    Show("echo $HOME *.txt          ", await env.ExecAsync(["echo", "$HOME", "*.txt"]));
    Show("ls *.txt (no globbing)    ", await env.ExecAsync(["ls", "*.txt"]));

    // The error contract, which the doc says to memorise:
    //   a command that RUNS AND FAILS  -> ordinary ExecResult with Success=false (no exception)
    //   a MISSING EXECUTABLE           -> ordinary ExecResult, return code 127   (no exception)
    //   the TIMEOUT expires            -> SandboxTimeoutException carrying the output so far
    //   the SANDBOX ITSELF is gone     -> SandboxUnavailableException
    Show("exit 3 (ran, failed)      ", await env.ExecAsync(["exit", "3"]));
    Show("no-such-tool (missing)    ", await env.ExecAsync(["no-such-tool", "--flag"]));
    await Expect<SandboxTimeoutException>("sleep 10 with 100ms timeout", () => env.ExecAsync(["sleep", "10"], timeout: TimeSpan.FromMilliseconds(100)));
    await Expect<FileNotFoundException>("read a missing file        ", () => env.ReadFileAsync("/nope"));
    env.Destroy();
    await Expect<SandboxUnavailableException>("exec after destroy         ", () => env.ExecAsync(["echo", "hi"]));
}

// ───────────────────────────────────────────────────────────────────────────
static async Task Lesson3Async()
// ───────────────────────────────────────────────────────────────────────────
{
    // An ORDERED name -> environment map. Position is the only thing that marks the default: the first entry
    // is what sandbox() with no name returns; the others are reached by name (e.g. an attacker box and a victim
    // box in a CTF task).
    var attacker = new InMemorySandboxEnvironment("attacker");
    var victim = new InMemorySandboxEnvironment("victim");
    var pair = SandboxEnvironments.Create(
    [
        new KeyValuePair<string, ISandboxEnvironment>("attacker", attacker),
        new KeyValuePair<string, ISandboxEnvironment>("victim", victim),
    ]);
    Show("names          ", string.Join(", ", pair.Environments.Keys));
    Show("Default is     ", ((InMemorySandboxEnvironment)pair.Default).Name);
    Show("by name        ", ((InMemorySandboxEnvironment)pair.Environments["victim"]).Name);

    // Single() is the one-environment shortcut; it registers the environment as "default".
    var one = SandboxEnvironments.Single(new InMemorySandboxEnvironment("only"));
    Show("Single() names ", string.Join(", ", one.Environments.Keys));

    // No environments is a programming error, not an empty result.
    await Expect<InvalidOperationException>("Default of empty set", () => Task.FromResult(new SandboxEnvironments(new Dictionary<string, ISandboxEnvironment>()).Default));

    // Cleanup is a closure the provider handed back, not a method on any interface. Its bool is the runner's
    // cleanup switch: true = destroy, false = keep the environments alive so a human can look inside.
    var keptAlive = 0;
    var withCleanup = SandboxEnvironments.Single(new InMemorySandboxEnvironment("x"), cleanup: destroy =>
    {
        if (!destroy)
        {
            keptAlive++;
        }

        return Task.CompletedTask;
    });
    await withCleanup.Cleanup!(false);
    Show("Cleanup(false) kept it alive?", keptAlive == 1);
    Show("Cleanup may be null          ", one.Cleanup is null);
}

// ───────────────────────────────────────────────────────────────────────────
static async Task Lesson4Async()
// ───────────────────────────────────────────────────────────────────────────
{
    // Three methods, two scopes. Per task: TaskInit / TaskCleanup (build the image once). Per sample:
    // SampleInit, whose result carries its own cleanup. Drive one task with three concurrent samples by hand,
    // exactly in the order Eval.RunAsync would.
    var memory = new InMemorySandboxProvider();
    const string task = "demo-task";
    const string config = "attacker,victim"; // this provider reads Config as a list of environment names

    await memory.TaskInitAsync(task, config);

    var samples = await Task.WhenAll(Enumerable.Range(1, 3).Select(async id =>
    {
        var metadata = new Dictionary<string, string> { ["id"] = id.ToString(CultureInfo.InvariantCulture) };
        var got = await memory.SampleInitAsync(task, config, metadata);
        await got.Default.WriteFileAsync("/flag.txt", $"flag{{sample-{id}}}\n");
        var read = await got.Default.ExecAsync(["cat", "/flag.txt"]);
        return (id, got, read.Stdout.Trim());
    }));
    foreach (var (id, got, flag) in samples)
    {
        Say($"  sample {id}: environments=[{string.Join(", ", got.Environments.Keys)}] flag={flag}");
    }

    Show("live environments before cleanup", memory.LiveEnvironments);
    await samples[0].got.Cleanup!(false); // keep sample 1 for inspection...
    await samples[1].got.Cleanup!(true);  // ...destroy the others
    await samples[2].got.Cleanup!(true);
    Show("live environments after cleanup ", memory.LiveEnvironments);
    await memory.TaskCleanupAsync(task, config, cleanup: true);

    Say("  provider log (the sequence diagram in §3, as it actually happened):");
    foreach (var line in memory.Log)
    {
        Say($"    {line}");
    }
}

// ───────────────────────────────────────────────────────────────────────────
static async Task Lesson5Async()
// ───────────────────────────────────────────────────────────────────────────
{
    // A static, case-insensitive dictionary pre-seeded with "local" and "docker". One instance per type serves
    // the whole process, which is why InMemorySandboxProvider had to be thread-safe.
    Show("built-in types  ", string.Join(", ", SandboxRegistry.Types));
    SandboxRegistry.Register(new InMemorySandboxProvider());
    Show("after Register  ", string.Join(", ", SandboxRegistry.Types));
    Show("Get(\"MEMORY\")   ", SandboxRegistry.Get("MEMORY").GetType().Name);
    Show("same instance?  ", ReferenceEquals(SandboxRegistry.Get("memory"), SandboxRegistry.Get("Memory")));
    await Expect<ArgumentException>("Get(\"podman\")", () => Task.FromResult(SandboxRegistry.Get("podman")));
}

// ───────────────────────────────────────────────────────────────────────────
static async Task Lesson6Async()
// ───────────────────────────────────────────────────────────────────────────
{
    // RunOneSampleAsync below knows only the four types. Run it against the toy provider, then against the
    // real `local` provider (host processes in a per-sample temp directory). Same code, same contract.
    if (!SandboxRegistry.Types.Contains("memory", StringComparer.OrdinalIgnoreCase))
    {
        SandboxRegistry.Register(new InMemorySandboxProvider()); // lesson 5 does this when it runs first
    }

    await RunOneSampleAsync(new SandboxSpec("memory"));
    await RunOneSampleAsync(new SandboxSpec("local"));
}

// ---------------------------------------------------------------------------
// A provider-agnostic sample runner: the shape of what Eval.RunAsync + SandboxSetup do for every sample.
// ---------------------------------------------------------------------------
static async Task RunOneSampleAsync(SandboxSpec spec)
{
    Say($"  --- spec {spec} ---");
    var provider = SandboxRegistry.Get(spec.Type);           // 1. Type -> provider
    await provider.TaskInitAsync("payoff", spec.Config);      // 2. once per task
    var envs = await provider.SampleInitAsync("payoff", spec.Config, new Dictionary<string, string> { ["id"] = "1" }); // 3. once per sample
    try
    {
        var sandbox = envs.Default;                          // 4. first entry = default
        if (sandbox is LocalSandboxEnvironment host)
        {
            Say($"  (local provider: files live under {host.WorkingDirectory})");
        }

        await sandbox.WriteFileAsync("notes.txt", "hello from the contract\n");
        string[] cat = OperatingSystem.IsWindows() ? ["cmd", "/c", "type", "notes.txt"] : ["cat", "notes.txt"];
        Show("  exec cat notes.txt   ", await sandbox.ExecAsync(cat));
        Show("  ReadFileAsync        ", (await sandbox.ReadFileAsync("notes.txt")).TrimEnd());
        Show("  missing executable   ", await sandbox.ExecAsync(["definitely-not-a-command"]));
    }
    finally
    {
        if (envs.Cleanup is { } cleanup)
        {
            await cleanup(true);                             // 5. runner's cleanup flag
        }

        await provider.TaskCleanupAsync("payoff", spec.Config, cleanup: true); // 6. once per task
    }

    if (envs.Default is LocalSandboxEnvironment gone)
    {
        Show("  temp dir still exists after Cleanup(true)?", Directory.Exists(gone.WorkingDirectory));
    }
}

// Verbatim from src/InspectAzureAI.Eval/Runner/SandboxSetup.cs (internal there): the merge rule for a
// task's spec and a sample's spec.
static SandboxSpec? ResolveSpec(SandboxSpec? taskSandbox, Sample sample)
{
    if (taskSandbox is null)
    {
        return sample.Sandbox;
    }

    var config = sample.Sandbox is { Config: not null } own && string.Equals(own.Type, taskSandbox.Type, StringComparison.OrdinalIgnoreCase)
        ? own.Config
        : taskSandbox.Config;
    return new SandboxSpec(taskSandbox.Type, config);
}

// ---------------------------------------------------------------------------
// Console helpers.
// ---------------------------------------------------------------------------
void PrintUsage(TextWriter writer)
{
    writer.WriteLine("usage: dotnet run --project src/InspectAzureAI.SandboxContractDemo [-- <lesson>...|list]");
    writer.WriteLine();
    writer.WriteLine("  no arguments  run every lesson in order");
    writer.WriteLine("  <lesson>...   run only these lessons (numbers, any order; run ascending)");
    writer.WriteLine("  list          print the lesson titles");
    writer.WriteLine();
    foreach (var (number, title, _) in lessons)
    {
        writer.WriteLine($"  {number}  {title}");
    }
}

static void Say(string text) => Console.WriteLine(text);

static void Show(string label, object? value)
{
    var text = value switch
    {
        ExecResult r => $"ExecResult(Success={r.Success}, ReturnCode={r.ReturnCode}, Stdout={Quote(r.Stdout)}, Stderr={Quote(r.Stderr)})",
        string s => Quote(s),
        null => "null",
        _ => value.ToString(),
    };
    Console.WriteLine($"  {label} = {text}");
}

static string Quote(string s) => "\"" + s.Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

static async Task Expect<TException>(string label, Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
        Console.WriteLine($"  {label} -> (no exception - unexpected)");
    }
    catch (TException ex)
    {
        Console.WriteLine($"  {label} -> throws {typeof(TException).Name}: {ex.Message}");
    }
}
