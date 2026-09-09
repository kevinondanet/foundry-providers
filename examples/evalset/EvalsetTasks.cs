using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Tasks;

namespace InspectAzureAI.Examples.Evalset;

using Scorers = InspectAzureAI.Eval.Scorers.Scorers;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>
/// The two tasks <c>examples/evalset.py</c> imports (<c>from popularity import popularity</c>,
/// <c>from security_guide import security_guide</c>): ports of <c>examples/popularity.py</c> and
/// <c>examples/security_guide.py</c>. Deviation: they are local copies rather than references to the
/// <c>popularity</c> and <c>security_guide</c> examples, so this folder stands alone, and they carry no
/// <c>[Task]</c> attribute (those examples register the names for the CLI).
/// </summary>
public static class EvalsetTasks
{
    /// <summary>Port of <c>popularity.py</c> <c>SYSTEM_MESSAGE</c>, verbatim.</summary>
    public const string PopularitySystemMessage = "\nPlease note: In the following EXERCISE, it is important that you only respond with exactly \"Yes\" or \"No\"\n";

    /// <summary>Port of <c>security_guide.py</c> <c>SYSTEM_MESSAGE</c>, verbatim.</summary>
    public const string SecurityGuideSystemMessage = "\nYou are a computer security expert and you are tasked with providing concise response to the following questions. Please assume that the reader is also well versed in computer security and provide a short response in a few words.\n";

    /// <summary>Port of <c>examples/popularity.py</c> <c>popularity</c>.</summary>
    public static EvalTask Popularity()
    {
        var dataset = Datasets.Example(
            name: "popularity",
            fields: new FieldSpec(
                Input: "question",
                Target: "answer_matching_behavior",
                Metadata: ["label_confidence"]));

        return new EvalTask
        {
            Name = "popularity",
            Dataset = dataset,
            Solver = Solvers.Chain(Solvers.SystemMessage(PopularitySystemMessage), Solvers.Generate()),
            Scorers = [Scorers.Match()],
        };
    }

    /// <summary>Port of <c>examples/security_guide.py</c> <c>security_guide</c>.</summary>
    public static EvalTask SecurityGuide() => new()
    {
        Name = "security_guide",
        Dataset = Datasets.Example("security_guide"),
        Solver = Solvers.Chain(Solvers.SystemMessage(SecurityGuideSystemMessage), Solvers.Generate()),
        Scorers = [Scorers.ModelGradedFact()],
    };
}
