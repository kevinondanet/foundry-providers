using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Browser;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/browser/browser.py</c> <c>browser</c> as an <see cref="IExample"/>: one sample that asks the
/// model to navigate to https://www.aisi.gov.uk/ with the <c>web_browser()</c> tools (headless Chromium inside the
/// <c>aisiuk/inspect-tool-support</c> image of <c>compose.yaml</c>), find the page describing the UK AISI's work and
/// summarise it in two paragraphs, scored with <c>includes()</c>. Under <c>--fake</c> the scripted
/// <see cref="FakeBrowserModel"/> goes to the site, clicks its "About" link and summarises the page the scripted
/// <see cref="FakeBrowserSandbox"/> serves as the <c>inspect-tool-support</c> JSON-RPC service.
/// Deviation: Python's <c>sandbox="docker"</c> finds <c>compose.yaml</c> next to the task file; here the compose
/// file is passed explicitly (<c>SandboxSpec("docker", "&lt;example dir&gt;/compose.yaml")</c>).
/// </summary>
public sealed class BrowserExample : IExample
{
    /// <summary>The Python task's name.</summary>
    public const string TaskName = "browser";

    /// <summary>The <c>Sample(input=...)</c>, verbatim.</summary>
    public const string Input = "Use the web browser tool to navigate to https://www.aisi.gov.uk/. Then, see if you can find a page on the site that describes the work of the UK AISI. Then, summarize this work in two paragraphs.";

    /// <summary>A safety net for <c>--fake</c>: the script needs three turns (go, click, summary).</summary>
    public const int FakeMessageLimit = 20;

    public string Name => "browser";

    public string Description => "Web browser tools: the model navigates to aisi.gov.uk with web_browser_go/click and summarises the UK AISI's work, inside the aisiuk/inspect-tool-support container";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, Build, "asks the model to browse to https://www.aisi.gov.uk/, find the page describing the UK AISI's work and summarise it in two paragraphs"),
    ];

    public ExampleDefaults Defaults { get; } = new(Sandbox: "docker", ComposeFile: "compose.yaml", NeedsDocker: true, ModelHint: "a tool-calling deployment (the browser returns text accessibility trees, so vision is not needed)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "Python's sandbox=\"docker\" resolves the compose.yaml next to browser.py; here the compose file is passed explicitly as SandboxSpec(\"docker\", \"<example dir>/compose.yaml\") by the runner and the [Task] method.",
        "The --fake scripted model (web_browser_go, then web_browser_click on the site's About link, then a two-paragraph summary) and the fake sandbox (a scripted inspect-tool-support JSON-RPC service serving a hand-written accessibility tree for the home and About pages) are additions for running offline; the Python example only runs through inspect eval against the real container and the live site.",
        "A message limit of 20 guards the fake run; the Python task has none.",
        "--sandbox none is refused (the web_browser tools need a sandbox), and --sandbox local fails at the first tool call with the tool's PrerequisiteError because inspect-tool-support is not on this host's PATH.",
    ];

    /// <summary>Port of <c>@task def browser()</c> with its <c>sandbox="docker"</c> (the example's compose.yaml), for the <c>inspectai</c> CLI.</summary>
    [Task(TaskName)]
    public static EvalTask BrowserTask() => Build(new SandboxSpec("docker", Path.Combine(AppContext.BaseDirectory, "browser", "compose.yaml")));

    /// <summary>Port of <c>@task def browser()</c>: the sample, <c>use_tools(web_browser())</c>, <c>generate()</c> and <c>includes()</c>, in <paramref name="sandbox"/>.</summary>
    public static EvalTask Build(SandboxSpec sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset([new Sample(Input)]),
            Solver = Solvers.Chain(
                Solvers.UseTools(WebBrowser.Create()),
                Solvers.Generate()),
            Scorers = [Scorers.Includes()],
            Sandbox = sandbox,
        };
    }

    public Model CreateFakeModel(ExampleContext ctx) => FakeBrowserModel.Create();

    /// <summary>The scripted <c>inspect-tool-support</c> service (see <see cref="FakeBrowserSandbox"/>).</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => FakeBrowserSandbox.Create();

    private static EvalTask Build(ExampleContext ctx)
    {
        var task = Build(ctx.Sandbox ?? throw new PrerequisiteError("browser needs a sandbox (the web_browser tools run in it): use --sandbox docker or --sandbox fake"));
        return ctx.Fake ? task with { MessageLimit = FakeMessageLimit } : task;
    }
}
