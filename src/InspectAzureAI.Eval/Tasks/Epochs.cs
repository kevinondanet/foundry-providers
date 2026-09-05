using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.Eval.Tasks;

/// <summary>
/// Port of <c>_eval/task/epochs.py</c> <c>Epochs</c>: how many times each sample runs and how the per-epoch
/// scores are reduced (null = the runner's default, <c>mean</c>).
/// </summary>
public sealed record Epochs
{
    public Epochs(int count, IReadOnlyList<ScoreReducer>? reducers = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        Count = count;
        Reducers = reducers;
    }

    public int Count { get; }

    public IReadOnlyList<ScoreReducer>? Reducers { get; }
}
