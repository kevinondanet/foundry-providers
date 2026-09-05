using System.Collections;

namespace InspectAzureAI.Eval.Log.EvalFormat;

/// <summary>
/// Port of <c>LazyList</c>: a read-only list that loads its items on first access. The <c>.eval</c> recorder's
/// <see cref="EvalRecorder.LogFinishAsync"/> returns the samples and reductions this way, so a caller that only
/// looks at the header never pays for deserializing the samples.
/// </summary>
public sealed class LazyList<T> : IReadOnlyList<T>
{
    private readonly Lazy<IReadOnlyList<T>> _items;

    public LazyList(Func<IReadOnlyList<T>> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _items = new Lazy<IReadOnlyList<T>>(loader, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Whether the items have been loaded yet.</summary>
    public bool IsLoaded => _items.IsValueCreated;

    public int Count => _items.Value.Count;

    public T this[int index] => _items.Value[index];

    public IEnumerator<T> GetEnumerator() => _items.Value.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
