using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.Images;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/images/images.py</c> as an <see cref="IExample"/>: the <c>images</c> task (vision QA over
/// <c>images.jsonl</c>, whose two inputs are a user message with a text part and a local PNG part — <c>ballons.png</c>
/// and <c>bike.png</c> — under a system message demanding a single bracketed value, answered by <c>generate()</c>
/// and scored by <c>match()</c> against <c>"3"</c> and <c>["bike", "bicycle"]</c>). The data files are copied
/// verbatim from the Python folder. Deviation: Python opens <c>images.jsonl</c> relative to the working directory
/// (<c>inspect eval images.py</c> runs from the example folder); here the copy beside the built assembly
/// (<c>images/images.jsonl</c>) is used when it exists, so the image paths resolve wherever the run starts.
/// Deviation: the task inlines the dataset's image files as data URIs itself (<see cref="SampleImages"/>), the
/// pre-run step of <c>_eval/task/images.py</c> that the engine does not port yet and that the Foundry routes require.
/// </summary>
public sealed class ImagesExample : IExample
{
    /// <summary>The task name (<c>@task def images</c>).</summary>
    public const string TaskName = "images";

    /// <summary>The dataset file of the Python example.</summary>
    public const string DatasetFile = "images.jsonl";

    /// <summary><c>SYSTEM_MESSAGE</c>, verbatim (including the leading and trailing newline).</summary>
    public const string SystemMessage =
        "\nFor the following exercise, it is important that you answer with only a single word or numeric value in brackets. For example, [22] or [house]. Do not include any discussion, narrative, or rationale, just a single value in brackets.\n";

    public string Name => TaskName;

    public string Description => "Vision QA: two image questions (ballons.png, bike.png) from a jsonl dataset with image content parts, a bracketed-answer system message, generate and match";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, ctx => Build(ctx.DataPath(DatasetFile)), "system_message + generate over images.jsonl, scored by match"),
    ];

    public ExampleDefaults Defaults { get; } = new(ModelHint: "a vision-capable deployment (gpt-4o / gpt-4.1 / gpt-5 family, or claude-* on the anthropic route)");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "Python opens images.jsonl relative to the working directory (inspect eval images.py runs from the example folder); here the task reads the copy beside the built assembly (images/images.jsonl), falling back to the working directory, so the relative image paths of the dataset resolve wherever the run starts.",
        "The eval engine has no port of the pre-run media step of _eval/task/images.py (Python's default trusted_pre_run policy, which turns each sample's image files into data URIs before the run), and the Foundry routes refuse a bare file path; so the task inlines its own dataset's PNGs as data:image/png;base64 URIs when it is built, and the log's sample inputs carry those data URIs rather than file paths.",
        "The --fake scripted model is an addition for running the example offline: it answers [3] for ballons.png and [bike] for bike.png, by the image file name when the message still carries one and by the question text once the image is inlined, without looking at the pixels.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => FakeImagesModel.Create();

    /// <summary>No sandbox: the task has no tools.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>
    /// Port of <c>@task def images()</c>, discoverable by the <c>inspectai</c> CLI (<c>eval images --assembly ...</c>):
    /// <c>json_dataset("images.jsonl")</c>, <c>[system_message(SYSTEM_MESSAGE), generate()]</c>, <c>match()</c>.
    /// </summary>
    [Task(TaskName)]
    public static EvalTask Images() => Build(DefaultDatasetPath());

    /// <summary>
    /// The task over the dataset at <paramref name="datasetPath"/>: its image paths resolve relative to that file
    /// (<c>json_dataset</c>) and the files are then inlined as data URIs (<see cref="SampleImages"/>).
    /// </summary>
    public static EvalTask Build(string datasetPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(datasetPath);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = SampleImages.Materialize(Datasets.Json(datasetPath)),
            Solver = Solvers.Chain(Solvers.SystemMessage(SystemMessage), Solvers.Generate()),
            Scorers = [Scorers.Match()],
        };
    }

    /// <summary>
    /// Where <c>images.jsonl</c> is: the copy beside this assembly (<c>images/images.jsonl</c>, as the examples
    /// project and the CLI's <c>--assembly</c> load see it), else the file name itself, relative to the working
    /// directory as in Python.
    /// </summary>
    public static string DefaultDatasetPath()
    {
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(ImagesExample).Assembly.Location) })
        {
            if (directory is null)
            {
                continue;
            }

            var candidate = Path.Combine(directory, TaskName, DatasetFile);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return DatasetFile;
    }
}
