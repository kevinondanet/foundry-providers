namespace InspectAzureAI.Eval.Concurrency;

/// <summary>
/// Port of the <c>ConcurrencySemaphore</c> protocol: a named, keyed limiter in the <see cref="Concurrency"/>
/// registry. <see cref="Name"/> is the display string; <see cref="Key"/> the registry identity (defaults to the
/// name); <see cref="Concurrency"/> the current limit; <see cref="Value"/> the free capacity; <see cref="InUse"/>
/// the exact holder count, which can exceed the limit while a shrink drains (so a display must read it rather
/// than derive <c>Concurrency - Value</c>).
/// </summary>
public interface IConcurrencySemaphore
{
    string Name { get; }

    string Key { get; }

    bool Visible { get; }

    int Concurrency { get; }

    int Value { get; }

    int InUse { get; }

    /// <summary>Takes one slot (the port of entering <c>semaphore.semaphore</c>).</summary>
    ValueTask<ConcurrencyLease> AcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of <c>ResizableSemaphore</c>: every static registry entry is one of these, so any named
/// <see cref="Concurrency"/> limit can be retuned mid-eval by assigning <see cref="Concurrency"/>.
/// </summary>
public sealed class ResizableSemaphore : IConcurrencySemaphore
{
    public ResizableSemaphore(string name, int concurrency, bool visible = true, string? key = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        Key = string.IsNullOrEmpty(key) ? name : key;
        Visible = visible;
        Limiter = new ResizableLimiter(concurrency);
    }

    public string Name { get; }

    public string Key { get; }

    public bool Visible { get; }

    /// <summary>The backing limiter (the retunable object the control channel would address).</summary>
    public ResizableLimiter Limiter { get; }

    /// <summary>The live limit; assigning it lowers or raises the limit in place (lowering below in-use blocks new acquires).</summary>
    public int Concurrency
    {
        get => Limiter.Limit;
        set => Limiter.Limit = value;
    }

    public int Value => Limiter.Available;

    public int InUse => Limiter.InUse;

    public ValueTask<ConcurrencyLease> AcquireAsync(CancellationToken cancellationToken = default) => Limiter.AcquireAsync(cancellationToken);
}
