using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

/// <summary>
/// The per-attempt scopes <c>Model._generate</c> opens around one provider call: the ambient
/// <see cref="ModelStreamObserver"/> (installed for the attempt), the <c>attempt_timeout</c> cancel scope and
/// the <c>stream_idle_timeout</c> <see cref="StallScope"/> (armed on the observer, nested inside the attempt
/// timeout). <see cref="Token"/> links the caller's token with both scopes; after the call
/// <see cref="TimeoutError"/> says which scope fired, checking the inner (stall) scope first so its sharper
/// diagnosis wins when both did.
/// </summary>
internal sealed class GenerateAttempt : IDisposable
{
    private readonly IDisposable? _observerScope;
    private readonly StallScope? _stall;
    private readonly CancellationTokenSource? _attemptTimeoutSource;
    private readonly CancellationTokenSource? _linked;
    private readonly int? _attemptTimeout;
    private readonly int? _streamIdleTimeout;

    public GenerateAttempt(ModelStreamObserver? observer, GenerateConfig config, CancellationToken cancellationToken)
    {
        _attemptTimeout = config.AttemptTimeout;
        _streamIdleTimeout = config.StreamIdleTimeout;
        if (_streamIdleTimeout is { } idleTimeout)
        {
            if (observer is null)
            {
                throw new InvalidOperationException("A stream observer is required to arm stream_idle_timeout.");
            }

            _stall = new StallScope(idleTimeout);
            observer.ArmStallScope(_stall);
        }

        if (observer is not null)
        {
            _observerScope = ModelStreamObserver.Install(observer);
        }

        if (_attemptTimeout is { } attemptTimeout)
        {
            _attemptTimeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(0, attemptTimeout)));
        }

        var tokens = new List<CancellationToken> { cancellationToken };
        if (_stall is not null)
        {
            tokens.Add(_stall.Token);
        }

        if (_attemptTimeoutSource is not null)
        {
            tokens.Add(_attemptTimeoutSource.Token);
        }

        if (tokens.Count > 1)
        {
            _linked = CancellationTokenSource.CreateLinkedTokenSource([.. tokens]);
            Token = _linked.Token;
        }
        else
        {
            Token = cancellationToken;
        }
    }

    /// <summary>The token to hand the provider: the caller's, the attempt timeout and the stall scope combined.</summary>
    public CancellationToken Token { get; }

    /// <summary>The timeout that fired during the attempt (stall scope first), or null.</summary>
    public Exception? TimeoutError =>
        _stall is { Fired: true } ? new StreamIdleTimeoutException(_streamIdleTimeout)
        : _attemptTimeoutSource is { IsCancellationRequested: true } ? new AttemptTimeoutException(_attemptTimeout)
        : null;

    public void Dispose()
    {
        _linked?.Dispose();
        _attemptTimeoutSource?.Dispose();
        _stall?.Dispose();
        _observerScope?.Dispose();
    }
}
