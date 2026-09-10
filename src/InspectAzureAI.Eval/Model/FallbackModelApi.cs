using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model;

/// <summary>
/// Port-only client-side model fallback: an <see cref="IModelApi"/> that serves requests from
/// <see cref="Primary"/> and, once it has failed <see cref="FailuresBeforeFallback"/> consecutive times
/// (a thrown exception or a terminal <see cref="GenerateResult.Error"/>), switches permanently to the next
/// api in <see cref="Fallbacks"/> and retries the request there at once. A response served by a fallback
/// carries a <see cref="ModelFallback"/> on <see cref="ModelOutput.Fallback"/> — the same record Python's
/// server-side refusal fallback produces — so the sample rollup (<see cref="SampleModelAccumulators"/>) and
/// the log see it the same way. Python has no client-side equivalent: its <c>fallback_models</c> is a
/// first-party Anthropic API feature that Foundry does not offer.
/// </summary>
public sealed class FallbackModelApi : IModelApi
{
    private readonly object _sync = new();
    private readonly IReadOnlyList<IModelApi> _chain;
    private int _index;
    private int _consecutiveFailures;
    private string? _lastFailure;

    /// <param name="primary">The api requests start on.</param>
    /// <param name="fallbacks">The apis to switch to, in order, once the current one keeps failing.</param>
    /// <param name="failuresBeforeFallback">Consecutive failures of the current api that trigger the switch (default 1).</param>
    public FallbackModelApi(IModelApi primary, IReadOnlyList<IModelApi> fallbacks, int failuresBeforeFallback = 1)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(fallbacks);
        ArgumentOutOfRangeException.ThrowIfLessThan(failuresBeforeFallback, 1);
        if (fallbacks.Count == 0)
        {
            throw new ArgumentException("At least one fallback api is required.", nameof(fallbacks));
        }

        Primary = primary;
        Fallbacks = fallbacks;
        FailuresBeforeFallback = failuresBeforeFallback;
        _chain = [primary, .. fallbacks];
    }

    public IModelApi Primary { get; }

    public IReadOnlyList<IModelApi> Fallbacks { get; }

    public int FailuresBeforeFallback { get; }

    /// <summary>The api currently serving requests.</summary>
    public IModelApi Current
    {
        get
        {
            lock (_sync)
            {
                return _chain[_index];
            }
        }
    }

    /// <summary>Consecutive failures of <see cref="Current"/> so far.</summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (_sync)
            {
                return _consecutiveFailures;
            }
        }
    }

    /// <summary>The primary's name: what callers asked for, and what <see cref="ModelFallback.Model"/> records.</summary>
    public string ModelName => Primary.ModelName;

    public string? BaseUrl => Current.BaseUrl;
    public string ProviderName => Primary.ProviderName;
    public string QualifiedModelName => Primary.QualifiedModelName;
    public bool IsFoundry => Primary.IsFoundry;
    public IReadOnlyDictionary<string, object?> ModelArgsForLog => Primary.ModelArgsForLog;
    public int? MaxTokensForConfig(GenerateConfig config) => Current.MaxTokensForConfig(config);
    public RetryDecision ShouldRetry(Exception ex) => Current.ShouldRetry(ex);
    public bool IsAuthFailure(Exception ex) => Current.IsAuthFailure(ex);
    public bool CollapseUserMessages() => Current.CollapseUserMessages();
    public bool SupportsRemoteMcp() => Current.SupportsRemoteMcp();
    public string ConnectionKey() => Current.ConnectionKey();
    public bool ApplyRedactedReasoningTokensToInput() => Current.ApplyRedactedReasoningTokensToInput();

    public int? MaxTokens() => Current.MaxTokens();

    /// <inheritdoc />
    public async Task<GenerateResult> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        ToolChoice toolChoice,
        GenerateConfig config,
        StreamHandler? onStream,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var api = Current;
            GenerateResult result;
            try
            {
                result = await api.GenerateAsync(input, tools, toolChoice, config, onStream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!RecordFailure(api, ex.Message))
                {
                    throw;
                }

                continue;
            }

            if (result.Error is { } error)
            {
                if (!RecordFailure(api, error.Message))
                {
                    return result;
                }

                continue;
            }

            string? reason;
            lock (_sync)
            {
                _consecutiveFailures = 0;
                reason = _lastFailure;
            }

            if (result.Output is { } output && !ReferenceEquals(api, Primary))
            {
                var fallback = new ModelFallback(Primary.ModelName, output.Fallback?.FallbackModel ?? api.ModelName, Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["reason"] = reason,
                });
                return result with { Output = output with { Fallback = fallback } };
            }

            return result;
        }
    }

    /// <summary>Counts a failure of <paramref name="api"/>; true when it triggered a switch to the next api (so the request should be retried there).</summary>
    private bool RecordFailure(IModelApi api, string message)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(api, _chain[_index]))
            {
                // another caller already moved on; retry on the current api without counting against it
                return true;
            }

            _consecutiveFailures++;
            _lastFailure = message;
            if (_consecutiveFailures < FailuresBeforeFallback || _index + 1 >= _chain.Count)
            {
                return false;
            }

            _index++;
            _consecutiveFailures = 0;
            ProviderLogger.Warning($"model '{api.ModelName}' failed {FailuresBeforeFallback} time(s) ({message}); falling back to '{_chain[_index].ModelName}'.");
            return true;
        }
    }
}
