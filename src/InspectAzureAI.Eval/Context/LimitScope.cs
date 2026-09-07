namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of <c>util/_limit.py</c> <c>LimitScope</c>, the object <c>apply_limits()</c> yields: the limits it entered
/// (left in reverse order on dispose) and, after <see cref="Limit.ApplyAsync"/>, the <see cref="LimitError"/> one of
/// them raised. A limit that fails to enter leaves the ones already entered before the exception propagates.
/// </summary>
public sealed class LimitScope : IDisposable
{
    private readonly List<Limit> _entered = [];

    private bool _disposed;

    private bool _ran;

    internal LimitScope(IReadOnlyList<Limit> limits)
    {
        Limits = limits;
        try
        {
            foreach (var limit in limits)
            {
                limit.Enter();
                _entered.Add(limit);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The limits this scope applies, in entry order.</summary>
    public IReadOnlyList<Limit> Limits { get; }

    /// <summary>The error one of <see cref="Limits"/> raised inside <see cref="Limit.ApplyAsync"/>, or null.</summary>
    public LimitExceededException? LimitError { get; internal set; }

    /// <summary>
    /// Port of the body of <c>apply_limits(limits, catch_errors)</c>: runs <paramref name="body"/> inside this scope
    /// (entered when the scope was created) and leaves it afterwards. The body's token fires when any applied
    /// <see cref="TimeLimit"/> elapses (linked to <paramref name="cancellationToken"/>) and that deadline becomes the
    /// time limit's <see cref="LimitExceededException"/>. A <see cref="LimitExceededException"/> raised by one of
    /// <see cref="Limits"/> is recorded as <see cref="LimitError"/> and, unless <paramref name="catchErrors"/>,
    /// rethrown; an error from any other scope always propagates, as does the caller's own cancellation.
    /// A scope runs one body.
    /// </summary>
    public async Task RunAsync(Func<CancellationToken, Task> body, bool catchErrors = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (_ran)
        {
            throw new InvalidOperationException("A LimitScope runs one body; create a new scope with Limit.Apply.");
        }

        _ran = true;
        try
        {
            var tokens = Limits.OfType<TimeLimit>().Select(limit => limit.Token).Where(token => token.CanBeCanceled).Append(cancellationToken).ToArray();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(tokens);
            try
            {
                await body(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && Limits.OfType<TimeLimit>().FirstOrDefault(limit => limit.Exceeded) is { } elapsed)
            {
                elapsed.ThrowIfExceeded();
            }
        }
        catch (LimitExceededException ex) when (Owns(ex))
        {
            LimitError = ex;
            if (!catchErrors)
            {
                throw;
            }
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>Whether <paramref name="error"/> was raised by one of the applied limits (an error without a source belongs to no scope).</summary>
    internal bool Owns(LimitExceededException error) => error.SourceLimit is { } source && Limits.Contains(source);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = _entered.Count - 1; i >= 0; i--)
        {
            _entered[i].Dispose();
        }
    }
}
