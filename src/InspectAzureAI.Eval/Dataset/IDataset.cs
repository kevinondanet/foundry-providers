namespace InspectAzureAI.Eval.Dataset;

/// <summary>
/// Port of <c>dataset/_dataset.py</c> <c>Dataset</c>: a sequence of samples with a name and location.
/// <see cref="Shuffle"/> and <see cref="Sort"/> mutate in place (returning nothing, like Python);
/// <see cref="Filter"/> and <see cref="Slice"/> return new datasets.
/// </summary>
public interface IDataset : IReadOnlyList<Sample>
{
    string? Name { get; }

    string? Location { get; }

    /// <summary>Whether the dataset was shuffled after reading.</summary>
    bool Shuffled { get; }

    /// <summary>Port of <c>Dataset.filter</c>: a new dataset (named <paramref name="name"/> or this name) with the matching samples.</summary>
    IDataset Filter(Func<Sample, bool> predicate, string? name = null);

    /// <summary>Port of <c>Dataset.shuffle</c>: in-place Fisher–Yates; a seed gives a reproducible order.</summary>
    void Shuffle(int? seed = null);

    /// <summary>Port of <c>Dataset.sort</c>: stable in-place sort by <paramref name="key"/> (default <see cref="Sample.InputLength"/>).</summary>
    void Sort(bool reverse = false, Func<Sample, IComparable>? key = null);

    /// <summary>Port of <c>dataset[start:end]</c>: a new dataset over the range, carrying name, location and shuffled.</summary>
    IDataset Slice(Range range);
}
