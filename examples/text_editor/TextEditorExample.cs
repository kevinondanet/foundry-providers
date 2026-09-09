using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.TextEditor;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/text_editor.py</c> as an <see cref="IExample"/>: <see cref="TextEditorTask"/> against the
/// scripted <see cref="FakeTextEditorModel"/> and the JSON-RPC-emulating <see cref="FakeTextEditorSandbox"/>
/// (<c>--fake</c>), or a Foundry deployment in a Docker sandbox. Deviation: the Python task is fixed to
/// <c>sandbox="docker"</c>; here the sandbox comes from the runner (<c>--sandbox</c>, default <c>docker</c>,
/// <c>fake</c> under <c>--fake</c>), and <c>none</c> is refused because the tool runs inside it.
/// </summary>
public sealed class TextEditorExample : IExample
{
    public string Name => "text_editor";

    public string Description => "The built-in text_editor tool driven through create, view, str_replace and insert over four generate() turns in a sandbox, checked by a custom verify_edit scorer that reads the file back";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TextEditorTask.TaskName, Build, "create /tmp/greeting.py, view it, str_replace Hello with Goodbye, insert an author line; verify_edit reads the file back"),
    ];

    public ExampleDefaults Defaults { get; } = new(Sandbox: "docker", NeedsDocker: true);

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The Python task is fixed to sandbox=\"docker\"; here the sandbox comes from the runner (--sandbox, default docker) and --sandbox none is refused because the text_editor tool runs inside the sandbox. --sandbox local is not usable on macOS or Windows: the injected inspect-sandbox-tools launcher is a Linux binary.",
        "The --fake scripted model and the fake sandbox (which reports the launcher as already injected and answers its JSON-RPC text_editor requests with an in-memory port of the launcher's editor, so nothing is downloaded and no container is needed) are additions for running offline; the Python example only runs through inspect eval.",
        "The fake editor implements view, create, str_replace and insert with the launcher's messages; undo_edit reports no history and directories are not modelled.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => FakeTextEditorModel.Create();

    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => FakeTextEditorSandbox.Create();

    private static EvalTask Build(ExampleContext ctx) =>
        TextEditorTask.Build(ctx.Sandbox ?? throw new PrerequisiteError("text_editor_task needs a sandbox (the text_editor tool runs inside it): use --sandbox docker, or --sandbox fake with --fake"));
}
