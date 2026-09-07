namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>util/_store.py</c> <c>Store</c>: the per-sample key/value bag solvers, tools and agents share.
/// Guarded by a lock because tool calls in one stage run as concurrent tasks.
/// </summary>
public sealed class Store
{
    private readonly object _sync = new();

    private readonly Dictionary<string, object?> _data = new(StringComparer.Ordinal);

    public Store()
    {
    }

    public Store(IEnumerable<KeyValuePair<string, object?>> data)
    {
        foreach (var (key, value) in data)
        {
            _data[key] = value;
        }
    }

    public object? Get(string key)
    {
        lock (_sync)
        {
            return _data.GetValueOrDefault(key);
        }
    }

    /// <summary>Port of <c>Store.get(key, default)</c>: a missing key is initialised with the default, as in Python.</summary>
    public T Get<T>(string key, T @default)
    {
        lock (_sync)
        {
            if (_data.TryGetValue(key, out var value) && value is T typed)
            {
                return typed;
            }

            if (@default is not null && !_data.ContainsKey(key))
            {
                _data[key] = @default;
            }

            return @default;
        }
    }

    public void Set(string key, object? value)
    {
        lock (_sync)
        {
            _data[key] = value;
        }
    }

    public bool Contains(string key)
    {
        lock (_sync)
        {
            return _data.ContainsKey(key);
        }
    }

    public void Delete(string key)
    {
        lock (_sync)
        {
            _data.Remove(key);
        }
    }

    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_sync)
            {
                return _data.Keys.ToArray();
            }
        }
    }

    public IReadOnlyDictionary<string, object?> ToDictionary()
    {
        lock (_sync)
        {
            return new Dictionary<string, object?>(_data, StringComparer.Ordinal);
        }
    }

    /// <summary>The value under <paramref name="key"/> when present (Python's <c>key in store</c> plus the lookup), without initialising a default.</summary>
    public bool TryGetValue(string key, out object? value)
    {
        lock (_sync)
        {
            return _data.TryGetValue(key, out value);
        }
    }

    /// <summary>Port of <c>model_cls(store=store, instance=instance)</c>: a <see cref="StoreModel"/> bound to this store (see <see cref="StoreModel.Create{TModel}"/>).</summary>
    public TModel As<TModel>(string? instance = null) where TModel : StoreModel, new() => StoreModel.Create<TModel>(this, instance);

    /// <summary>
    /// Port of <c>store_as(model_cls, instance)</c>: a <see cref="StoreModel"/> bound to the ambient sample store. Outside a
    /// <see cref="SampleContext"/> this is an <see cref="InvalidOperationException"/> (Python falls back to a process-wide default store).
    /// </summary>
    public static TModel StoreAs<TModel>(string? instance = null) where TModel : StoreModel, new() => SampleContext.Require().Store.As<TModel>(instance);
}
