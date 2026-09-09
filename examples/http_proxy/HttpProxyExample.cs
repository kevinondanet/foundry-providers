using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.HttpProxy;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/http_proxy/task.py</c> <c>http_proxy_demo</c> as an <see cref="IExample"/>: a Claude Code agent
/// (<see cref="Claude.ClaudeCode"/>, driven through the sandbox agent bridge) is asked to integrate the non-existent
/// FutureModel API in a container whose compose file disables network egress (<c>network_mode: none</c>) and points
/// <c>HTTP(S)_PROXY</c> at an in-container mitmproxy whose addon (<c>remap.py</c>) rewrites <c>api.futuremodel.ai</c>
/// to the bridge and 403s everything else. Deviation: the C# bridge listens on the host, which <c>network_mode:
/// none</c> makes unreachable from the container (see <see cref="Deviations"/>); under <c>--fake</c> the sandbox is
/// scripted and a stand-in <see cref="FakeClaudeCli"/> plays the CLI and the script against the real bridge.
/// </summary>
public sealed class HttpProxyExample : IExample
{
    public const string TaskName = "http_proxy_demo";

    /// <summary>The sample input, as written in <c>task.py</c>.</summary>
    public const string Input = "Write a script that integrates the FutureModel API (https://api.futuremodel.ai/v1/chat/completions) and run it to generate a haiku about coding. The model name is 'futuremodel-1' and the API key is in the FUTUREMODEL_API_KEY environment variable.";

    public const string FakeModelName = FakeHttpProxyModel.ModelName;

    /// <summary>The compose file next to the task (Python's <c>sandbox="docker"</c> auto-discovers it).</summary>
    public const string ComposeFile = "compose.yaml";

    /// <summary>Where the project copies this example's data files.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "http_proxy");

    public string Name => "http_proxy";

    public string Description => "HTTP proxy interception: Claude Code, in a no-egress container whose mitmproxy remaps the fake FutureModel API to the sandbox agent bridge, integrates the API and generates a haiku";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, Build, "Claude Code writes and runs a script against https://api.futuremodel.ai (remapped to the bridge by mitmproxy) to generate a haiku about coding"),
    ];

    public ExampleDefaults Defaults { get; } = new(
        Sandbox: "docker",
        ComposeFile: ComposeFile,
        NeedsDocker: true,
        ModelHint: "any deployment (Claude Code speaks the Anthropic Messages dialect to the bridge; the agent's script speaks the OpenAI chat completions dialect)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The agent is a thin wrapper over the Swe project's ClaudeCode.Agent (Version = \"sandbox\": the image's own npm-installed claude) instead of a hand-rolled sandbox().exec: the CLI also receives --session-id, --output-format stream-json --verbose and a seeded ~/.claude/settings.json, and its JSONL output is recorded on the transcript as claude_code info events.",
        "Authentication to the bridge is its per-instance token in ANTHROPIC_AUTH_TOKEN (the C# SandboxAgentBridge answers 401 without it), not Python's placeholder ANTHROPIC_API_KEY.",
        "Python's sandbox_agent_bridge runs a model proxy inside the container on localhost:13131 and relays to the host over file RPC; the C# SandboxAgentBridge listens on the host and is reached through host.docker.internal. compose.yaml (copied verbatim) sets network_mode: none, which blocks that path, so a live docker run of this port cannot reach the bridge until the engine grows an in-sandbox relay (the bridge port/URL remap.py forwards to also differs: it targets localhost:13131). The proxy chain itself (mitmproxy, remap.py, the 403 policy, the CA trust) is unchanged.",
        "The script's FutureModel request carries FUTUREMODEL_API_KEY, which Python's in-container proxy ignores; the C# bridge requires its own token, so a live run would also need remap.py to substitute it (not done: remap.py is verbatim). The offline stand-in presents the bridge token.",
        "The Python task says sandbox=\"docker\" and inspect discovers compose.yaml next to task.py; here the compose file is named explicitly (SandboxSpec(\"docker\", \"<folder>/compose.yaml\")), and the sandbox comes from the runner (--sandbox, default docker). --sandbox local cannot work (no claude, no mitmproxy on this host); --fake uses the scripted sandbox, in which a stand-in claude POSTs its turns to the bridge's Anthropic route and the script's FutureModel request to its OpenAI route, and writes futuremodel_haiku.py into the fake sandbox.",
    ];

    /// <summary>Port of <c>@task def http_proxy_demo</c> (Docker compose sandbox, as in Python).</summary>
    [Task(TaskName)]
    public static EvalTask HttpProxyDemo() => Build(new SandboxSpec("docker", Path.Combine(DefaultDirectory, ComposeFile)));

    /// <summary>The task on <paramref name="sandbox"/>.</summary>
    public static EvalTask Build(SandboxSpec sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset([new Sample(Input)]),
            Solver = Agents.AsSolver(Claude.ClaudeCode()),
            Sandbox = sandbox,
        };
    }

    public Model CreateFakeModel(ExampleContext ctx) => FakeHttpProxyModel.Create();

    /// <summary>
    /// The scripted container: <c>which claude</c> finds the image's CLI, <c>pwd</c> is the image's WORKDIR, the
    /// settings seed succeeds, and the launch is played by <see cref="FakeClaudeCli"/> against the bridge.
    /// </summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx)
    {
        var script = new FakeSandboxScript();
        var cli = new FakeClaudeCli(() => ScriptedSandboxEnvironment.Current ?? script.Environments.LastOrDefault());
        return script
            .OnExact(FakeSandboxScript.Ok(FakeClaudeCli.BinaryPath + "\n"), "bash", "-c", "which claude")
            .OnExact(FakeSandboxScript.Ok(FakeClaudeCli.WorkingDirectory + "\n"), "bash", "-c", "pwd")
            .OnMatch(call => call.Cmd is ["bash", "-c", var cmd] && cmd.StartsWith("mkdir -p \"$HOME/.claude\"", StringComparison.Ordinal), _ => FakeSandboxScript.Ok())
            .OnMatch(FakeClaudeCli.IsLaunch, cli.Run)
            .WithDefault(FakeSandboxScript.Fail(127, "fake sandbox: command not scripted\n"));
    }

    private static EvalTask Build(ExampleContext ctx)
    {
        var sandbox = ctx.Sandbox ?? throw new PrerequisiteError($"{TaskName} needs a sandbox (the agent runs in it): use --sandbox docker (or --fake)");
        if (sandbox.Type == "docker")
        {
            ctx.Out.WriteLine("note: compose.yaml (verbatim) sets network_mode: none, which blocks the container's route to the host-side sandbox agent bridge; see this example's README, \"Deviations from Python\".");
        }

        return Build(sandbox);
    }
}
