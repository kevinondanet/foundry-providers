using System.Globalization;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Context;

/// <summary>
/// Port of the sample-level <c>message_limit</c>, <c>token_limit</c> and <c>time_limit</c> of
/// <c>util/_limit.py</c>: usage is accumulated per sample and checked cooperatively by
/// <c>Model.GenerateAsync</c> (message limit before the call, token limit after). Python scopes the limits
/// around the solvers only and scores outside them; <see cref="Suspend"/> is the runner leaving those scopes.
/// </summary>
public sealed class Limits
{
    private readonly object _sync = new();

    private readonly Dictionary<string, ModelUsage> _usageByModel = new(StringComparer.Ordinal);

    private volatile bool _suspended;

    /// <summary>False once <see cref="Suspend"/> has been called: usage keeps accumulating but no check raises.</summary>
    public bool Enforced => !_suspended;

    /// <summary>Stops enforcing the limits (the scorers of a sample that hit its limit must still be able to generate).</summary>
    public void Suspend() => _suspended = true;

    public int? MessageLimit { get; init; }

    public int? TokenLimit { get; init; }

    public TimeSpan? TimeLimit { get; init; }

    private double? _costLimit;

    /// <summary>Port of <c>cost_limit</c>: the maximum cost in dollars per sample, or null for unlimited; a negative value is rejected like Python's <c>ValueError</c>.</summary>
    public double? CostLimit
    {
        get => _costLimit;
        init
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, $"Cost limit value must be a non-negative float or None: {value}");
            }

            _costLimit = value;
        }
    }

    /// <summary>Cost recorded against the cost limit (Python's <c>_CostLimit.usage</c>): the <c>total_cost</c> of every priced call, recorded after the token check like <c>record_model_cost</c>.</summary>
    public double CostUsage { get; private set; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Usage accumulated across every model call of the sample.</summary>
    public ModelUsage TotalUsage { get; private set; } = new();

    /// <summary>Usage per model name (the log's <c>model_usage</c>).</summary>
    public IReadOnlyDictionary<string, ModelUsage> UsageByModel
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, ModelUsage>(_usageByModel, StringComparer.Ordinal);
            }
        }
    }

    public TimeSpan Elapsed => DateTimeOffset.UtcNow - StartedAt;

    /// <summary>Records usage and then checks the token limit, like Python's <c>record_model_usage</c> + <c>check_token_limit</c>.</summary>
    public void AddUsage(ModelUsage usage, string? model = null)
    {
        lock (_sync)
        {
            TotalUsage += usage;
            if (model is not null)
            {
                _usageByModel[model] = _usageByModel.TryGetValue(model, out var existing) ? existing + usage : usage;
            }
        }

        CheckTokenLimit();

        if (usage.TotalCost is { } cost)
        {
            RecordModelCost(cost);
            CheckCostLimit();
        }
    }

    /// <summary>Port of <c>record_model_cost</c>: records cost against the cost limit without checking it.</summary>
    public void RecordModelCost(double cost)
    {
        lock (_sync)
        {
            CostUsage += cost;
        }
    }

    /// <summary>Port of <c>check_cost_limit</c>: raises once the recorded cost exceeds <see cref="CostLimit"/> (strictly, like Python).</summary>
    public void CheckCostLimit()
    {
        if (_suspended || CostLimit is not { } limit)
        {
            return;
        }

        double cost;
        lock (_sync)
        {
            cost = CostUsage;
        }

        if (cost > limit)
        {
            var message = $"Cost limit exceeded. value: ${cost.ToString("N4", CultureInfo.InvariantCulture)}; limit: ${limit.ToString("N4", CultureInfo.InvariantCulture)}";
            throw new LimitExceededException("cost", LimitExceededException.FormatLimit(limit), cost, message);
        }
    }

    /// <summary>
    /// Port of <c>check_message_limit</c>: raises when <paramref name="count"/> exceeds the limit, or
    /// equals it when <paramref name="raiseForEqual"/> (a generate at the limit would be wasted).
    /// </summary>
    public void CheckMessageLimit(int count, bool raiseForEqual = true)
    {
        if (_suspended || MessageLimit is not { } limit)
        {
            return;
        }

        if (count > limit || (raiseForEqual && count == limit))
        {
            var reachedOrExceeded = count == limit ? "reached" : "exceeded";
            var limitStr = LimitExceededException.FormatLimit(limit);
            var message = $"Message limit {reachedOrExceeded}. count: {LimitExceededException.FormatLimit(count)}; limit: {limitStr}";
            throw new LimitExceededException("message", limitStr, count, message);
        }
    }

    /// <summary>Port of <c>check_token_limit</c>: raises once total tokens exceed the limit.</summary>
    public void CheckTokenLimit()
    {
        if (_suspended || TokenLimit is not { } limit)
        {
            return;
        }

        int total;
        lock (_sync)
        {
            total = TotalUsage.TotalTokens;
        }

        if (total > limit)
        {
            var limitStr = LimitExceededException.FormatLimit(limit);
            var message = $"Token limit exceeded. value: {LimitExceededException.FormatLimit(total)}; limit: {limitStr}";
            throw new LimitExceededException("token", limitStr, total, message);
        }
    }

    /// <summary>Raises once the sample's wall-clock time exceeds <see cref="TimeLimit"/>.</summary>
    public void CheckTimeLimit()
    {
        if (_suspended || TimeLimit is not { } limit)
        {
            return;
        }

        var elapsed = Elapsed;
        if (elapsed > limit)
        {
            var limitStr = LimitExceededException.FormatLimit(limit.TotalSeconds);
            throw new LimitExceededException("time", limitStr, elapsed.TotalSeconds, $"Time limit exceeded. limit: {limitStr} seconds");
        }
    }
}
