using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.Simpleqa;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/simpleqa.py</c>: the <c>simpleqa</c> task over the Hugging Face <c>codelion/SimpleQA-Verified</c>
/// train split, answered with a plain <c>generate()</c> and graded by the built-in <c>model_graded_qa()</c> scorer, which
/// uses the model under evaluation as the judge. Under <c>--fake</c> the dataset is a canned five-row page of the real
/// split served through <see cref="CannedHfHub"/> and the model is <see cref="FakeSimpleqaModel"/>.
/// </summary>
public sealed class SimpleqaExample : IExample
{
    /// <summary>The task name (<c>@task def simpleqa</c>).</summary>
    public const string TaskName = "simpleqa";

    /// <summary>The Hub dataset and split of <c>hf_dataset("codelion/SimpleQA-Verified", split="train", ...)</c>.</summary>
    public const string DatasetPath = "codelion/SimpleQA-Verified";

    public const string DatasetSplit = "train";

    /// <summary>The first five rows of the split, as the datasets-server returns them, for the offline run.</summary>
    public const string SampleRowsFile = "simpleqa-verified-train-sample.jsonl";

    /// <summary>Python's <c>FieldSpec(input="problem", target="answer")</c>.</summary>
    public static readonly FieldSpec Fields = new(Input: "problem", Target: "answer");

    /// <summary>Where the fake run caches the canned rows (never the user's real <c>hf_datasets</c> cache, which a live run reads).</summary>

    public string Name => TaskName;

    public string Description => "SimpleQA-Verified factual questions answered by generate() and graded by model_graded_qa with the model itself as judge";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, Build, "codelion/SimpleQA-Verified (train) with generate, scored by model_graded_qa"),
    ];

    public ExampleDefaults Defaults { get; } = new();

    public IReadOnlyList<string> Deviations { get; } =
    [
        "hf_dataset reads the split through the Hugging Face datasets-server REST API (Datasets.Hf) rather than the datasets package; the rows are cached under the inspect cache directory as rows.jsonl.",
        "--fake serves the first five rows of the real train split (simpleqa-verified-train-sample.jsonl) through a canned datasets-server handler and plays the model with a script: the reference answer for rows 1, 2 and 4, a wrong answer for rows 3 and 5; its grader answers GRADE: C when the submission contains the criterion, else GRADE: I.",
    ];

    /// <summary>The scripted model over the canned rows (<see cref="FakeSimpleqaModel"/>).</summary>
    public Model CreateFakeModel(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return FakeSimpleqaModel.Create(ctx.DataPath(SampleRowsFile));
    }

    /// <summary>No sandbox is involved.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>
    /// Port of <c>@task def simpleqa()</c>: <c>hf_dataset("codelion/SimpleQA-Verified", split="train", sample_fields=FieldSpec(input="problem", target="answer"))</c>,
    /// <c>generate()</c>, <c>model_graded_qa()</c>. Discoverable by the <c>inspectai</c> CLI as <c>simpleqa</c>.
    /// </summary>
    [Task(TaskName)]
    public static EvalTask SimpleqaTask() => Build(Datasets.Hf(DatasetPath, DatasetSplit, sampleFields: Fields));

    /// <summary>The task over <paramref name="dataset"/> (the live split, or the canned page under <c>--fake</c>).</summary>
    public static EvalTask Build(IDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = dataset,
            Solver = Solvers.Generate(),
            Scorers = [Scorers.ModelGradedQa()],
        };
    }

    /// <summary>
    /// The canned dataset of <c>--fake</c>: the rows of <paramref name="rowsPath"/> loaded through the real <c>hf_dataset</c>
    /// loader against <see cref="CannedHfHub"/>, with a private cache directory so the user's Hub cache is untouched.
    /// </summary>
    public static IDataset SampleDataset(string rowsPath)
    {
        using var hub = new CannedHfHub(DatasetPath, DatasetSplit, rowsPath);
        using var loader = hub.CreateLoader();
        return loader.LoadAsync(new HfDatasetRequest(DatasetPath, DatasetSplit)
        {
            SampleFields = Fields,
            Cached = false,
        }).GetAwaiter().GetResult();
    }

    private static EvalTask Build(ExampleContext ctx) => ctx.Fake ? Build(SampleDataset(ctx.DataPath(SampleRowsFile))) : SimpleqaTask();
}
