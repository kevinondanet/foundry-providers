using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;

namespace InspectAzureAI.Examples.CategoricalDemo;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/categorical_demo.py</c> <c>Verdict</c> (a <c>StrEnum</c> with the values yes / no / unsure).
/// Deviation: a C# enum carries no string value, so the score label of a member is its name lower-cased
/// (<see cref="Metrics.CategoryName{TEnum}(TEnum)"/>: <c>Verdict.Yes</c> scores as <c>"yes"</c>), which reproduces the Python values.
/// </summary>
public enum Verdict
{
    Yes,
    No,
    Unsure,
}

/// <summary>Port of <c>examples/categorical_demo.py</c> <c>SabotageType</c> (a <c>StrEnum</c> with the values none / subtle / overt); labelled like <see cref="Verdict"/>.</summary>
public enum SabotageType
{
    None,
    Subtle,
    Overt,
}

/// <summary>
/// Port of <c>examples/categorical_demo.py</c>: three scorers that ignore the model output and draw seeded categories, so the
/// viewer can compare how a string-valued categorical score (<c>verdict</c>), a dict-valued score with two independent
/// categorical dimensions (<c>behaviour</c>) and the legacy one-hot dictionary (<c>verdict_one_hot</c>) render, over the
/// <c>frequency()</c> / <c>categorical()</c> metrics and <c>metrics={"*": [accuracy()]}</c>. Python runs it with
/// <c>--model mockllm/model</c>; here <c>--fake</c> plays that model (<see cref="CreateFakeModel"/>).
/// </summary>
public sealed class CategoricalDemoExample : IExample
{
    /// <summary>The task name (<c>@task def categorical_demo</c>).</summary>
    public const string TaskName = "categorical_demo";

    /// <summary>Python's <c>mockllm/model</c>: its default output, returned for every generate call.</summary>
    public const string MockModelName = "mockllm/model";

    /// <summary><c>MockLLM.default_output</c>, verbatim.</summary>
    public const string MockDefaultOutput = "Default output from mockllm/model";

    /// <summary>Python's <c>Task(epochs=3)</c>.</summary>
    public const int EpochCount = 3;

    /// <summary>Python's <c>range(40)</c> samples.</summary>
    public const int SampleCount = 40;

    /// <summary>Generate calls the scripted model answers before it reports itself exhausted (40 samples x 3 epochs is 120; <c>--epochs</c> may ask for more).</summary>
    private const int TurnBudget = 10_000;

    /// <summary>Port of <c>_VERDICT_WEIGHTS</c>: "weight the random draws so the headline numbers aren't uniform".</summary>
    private static readonly (Verdict Member, double Weight)[] VerdictWeights =
    [
        (Verdict.Yes, 0.55),
        (Verdict.No, 0.30),
        (Verdict.Unsure, 0.15),
    ];

    /// <summary>Port of <c>_SABOTAGE_WEIGHTS</c>.</summary>
    private static readonly (SabotageType Member, double Weight)[] SabotageWeights =
    [
        (SabotageType.None, 0.60),
        (SabotageType.Subtle, 0.30),
        (SabotageType.Overt, 0.10),
    ];

    public string Name => TaskName;

    public string Description => "Categorical scorers: a string-valued verdict, a dict-valued behaviour with two categorical dimensions, and the legacy one-hot verdict, reported through the frequency()/categorical() metrics";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(TaskName, _ => CategoricalDemoTask(), "40 samples x 3 epochs; the scorers draw seeded verdicts, so any model output will do"),
    ];

    public ExampleDefaults Defaults { get; } = new();

    public IReadOnlyList<string> Deviations { get; } =
    [
        "The seed of each draw is a deterministic mix of the sample id and epoch (Seed) rather than Python's hash((sample_id, epoch)), and the draw uses a seeded System.Random rather than random.Random (Mersenne Twister), so individual verdicts differ from the Python run while the weighted proportions match in expectation.",
        "Verdict and SabotageType are C# enums rather than StrEnums; their score labels are the member names lower-cased (yes/no/unsure, none/subtle/overt), which are the Python values.",
        "The scorer factories are VerdictScorer, BehaviourScorer and VerdictOneHotScorer (a C# member cannot share the name of the Verdict enum); the registered scorer names are verdict, behaviour and verdict_one_hot as in Python.",
        "--fake plays Python's mockllm/model: every generate call answers \"Default output from mockllm/model\".",
    ];

    /// <summary>Port of <c>--model mockllm/model</c>: a scripted model that answers every call with <see cref="MockDefaultOutput"/>.</summary>
    public Model CreateFakeModel(ExampleContext ctx) => CreateMockModel();

    /// <summary>The demo needs no sandbox.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>The <c>mockllm/model</c> stand-in used by <c>--fake</c> (and by tests).</summary>
    public static Model CreateMockModel() =>
        new(new ScriptedModelApi(Enumerable.Repeat(ScriptedTurn.Text(MockDefaultOutput), TurnBudget), MockModelName));

    /// <summary>
    /// Port of <c>@task def categorical_demo()</c>: 40 samples ("Sample question {i}", target "yes"), <c>generate()</c>, the
    /// three scorers, three epochs. Discoverable by the <c>inspectai</c> CLI as <c>categorical_demo</c>.
    /// </summary>
    [Task(TaskName)]
    public static EvalTask CategoricalDemoTask() => new()
    {
        Name = TaskName,
        Dataset = new MemoryDataset(Enumerable.Range(0, SampleCount).Select(i => new Sample($"Sample question {i}") { Target = "yes" })),
        Solver = Solvers.Generate(),
        Scorers = [VerdictScorer(), BehaviourScorer(), VerdictOneHotScorer()],
        Epochs = new Epochs(EpochCount),
    };

    /// <summary>Port of <c>@scorer(metrics=categorical(Verdict)) def verdict()</c>: "Single categorical score: value is one of yes/no/unsure."</summary>
    public static ScorerDef VerdictScorer() => Scorers.Custom("verdict", ScoreVerdict, [.. Metrics.Categorical<Verdict>()]);

    /// <summary>
    /// Port of <c>@scorer(metrics={"sabotage_type": categorical(SabotageType), "eval_aware": categorical(Verdict)}) def behaviour()</c>:
    /// "Two independent categorical dimensions in a single dict-valued score."
    /// </summary>
    public static ScorerDef BehaviourScorer() => Scorers.Custom(
        "behaviour",
        ScoreBehaviour,
        new MetricDict
        {
            ["sabotage_type"] = Metrics.Categorical<SabotageType>(),
            ["eval_aware"] = Metrics.Categorical<Verdict>(),
        });

    /// <summary>Port of <c>@scorer(metrics={"*": [accuracy()]}) def verdict_one_hot()</c>: "Legacy one-hot encoding of the same verdict, for viewer comparison."</summary>
    public static ScorerDef VerdictOneHotScorer() => Scorers.Custom("verdict_one_hot", ScoreVerdictOneHot, MetricDict.ForAllKeys(Metrics.Accuracy()));

    /// <summary>Port of <c>_draw_verdict(seed)</c>: <c>random.Random(seed).choices(list(Verdict), weights=...)[0]</c>.</summary>
    public static Verdict DrawVerdict(int seed) => Choose(VerdictWeights, seed);

    /// <summary>Port of <c>_draw_sabotage(seed)</c>.</summary>
    public static SabotageType DrawSabotage(int seed) => Choose(SabotageWeights, seed);

    /// <summary>
    /// Port of <c>hash((state.sample_id, state.epoch))</c>. Deviation: a deterministic mix of the id (an integer as is, a
    /// numeric string parsed, any other id through a stable FNV-1a hash) and the epoch, not Python's tuple hash.
    /// </summary>
    public static int Seed(object sampleId, int epoch)
    {
        ArgumentNullException.ThrowIfNull(sampleId);
        var id = sampleId switch
        {
            int i => i,
            long l => unchecked((int)l),
            string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => StableHash(sampleId.ToString() ?? ""),
        };
        return unchecked(id * 1_000_003 + epoch);
    }

    private static Task<Score> ScoreVerdict(TaskState state, Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var v = Metrics.CategoryName(DrawVerdict(Seed(state.SampleId, state.Epoch)));
        return Task.FromResult(new Score(v)
        {
            Answer = v,
            Explanation = $"Grader judged the response as '{v}'.",
        });
    }

    private static Task<Score> ScoreBehaviour(TaskState state, Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var seed = Seed(state.SampleId, state.Epoch);
        var sabotage = Metrics.CategoryName(DrawSabotage(seed));
        var aware = Metrics.CategoryName(DrawVerdict(unchecked(seed * 31)));
        var value = new OrderedDictionary<string, ScoreValue?>(StringComparer.Ordinal)
        {
            ["sabotage_type"] = sabotage,
            ["eval_aware"] = aware,
        };
        return Task.FromResult(new Score(new ScoreValue.Dict(value))
        {
            Explanation = $"Classified sabotage_type='{sabotage}', eval_aware='{aware}'.",
        });
    }

    private static Task<Score> ScoreVerdictOneHot(TaskState state, Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var v = DrawVerdict(Seed(state.SampleId, state.Epoch));
        var value = new OrderedDictionary<string, ScoreValue?>(StringComparer.Ordinal);
        foreach (var member in Enum.GetValues<Verdict>())
        {
            value[Metrics.CategoryName(member)] = member == v ? 1 : 0;
        }

        return Task.FromResult(new Score(new ScoreValue.Dict(value)) { Answer = Metrics.CategoryName(v) });
    }

    /// <summary>Port of <c>random.choices(population, weights)[0]</c>: cumulative weights, one uniform draw, bisect right.</summary>
    private static T Choose<T>((T Member, double Weight)[] weighted, int seed)
    {
        var random = new Random(seed);
        var total = weighted.Sum(entry => entry.Weight);
        var draw = random.NextDouble() * total;
        var cumulative = 0.0;
        foreach (var (member, weight) in weighted)
        {
            cumulative += weight;
            if (draw < cumulative)
            {
                return member;
            }
        }

        return weighted[^1].Member;
    }

    /// <summary>FNV-1a over the UTF-16 code units: stable across runs, unlike <see cref="string.GetHashCode()"/>.</summary>
    private static int StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var ch in text)
        {
            hash = (hash ^ ch) * 16777619u;
        }

        return unchecked((int)hash);
    }
}
