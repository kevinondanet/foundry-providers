using InspectAzureAI.Eval.Log;

namespace InspectAzureAI.Eval.Runner.Scoring;

/// <summary>
/// Port of the <c>Tuple[EvalResults, list[EvalSampleReductions] | None]</c> that <c>eval_results</c> returns:
/// the results (one <see cref="EvalScore"/> per scorer and reducer view, the sample counts, the resolved headline)
/// and the per-view epoch reductions (null when there were no scorers, as in Python).
/// </summary>
public sealed record ComputedResults(EvalResults Results, IReadOnlyList<EvalSampleReductions>? Reductions);
