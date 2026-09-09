using System.Diagnostics;
using System.Runtime.ExceptionServices;
using InspectAzureAI.Eval.Concurrency;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Hooks;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Model.Compaction;
using InspectAzureAI.Eval.Model.Cost;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

using Concurrency = InspectAzureAI.Eval.Concurrency.Concurrency;

/// <summary>
/// Port of <c>model/_model.py</c> <c>Model.generate</c>: config merging and <c>max_tokens</c> defaulting,
/// consecutive-user-message collapsing when the api needs it, sample limit checks, the retry loop of
/// <c>model/_retry.py</c>, and a <see cref="ModelEvent"/> per attempt on the transcript and event sinks.
/// </summary>
public sealed partial class Model
{
    private static readonly Func<TimeSpan, CancellationToken, Task> SleepDelay = (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    public Model(IModelApi api, GenerateConfig? config = null, ModelRetryOptions? retry = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        Api = api;
        Config = api.DefaultConfig.Merge(config);
        Retry = retry ?? new ModelRetryOptions();
    }

    public string Name => Api.ModelName;

    public IModelApi Api { get; }

    public GenerateConfig Config { get; }

    public ModelRetryOptions Retry { get; }

    /// <summary>A sink bound to this instance (in addition to the ambient <see cref="ModelEventSinks"/>).</summary>
    public IModelEventSink? EventSink { get; init; }

    /// <summary>
    /// Port of <c>GenerateConfig.adaptive_connections</c>: null (the default) and <see cref="AdaptiveConnections.Default"/>
    /// gate generates with an adaptive controller; <see cref="AdaptiveConnections.Disabled"/> is the opt-out. When
    /// unset, the value carried on the resolved config's <see cref="GenerateConfig.AdaptiveConnections"/> applies
    /// (see <see cref="AdaptiveConnections.FromConfigValue"/>). An explicit <c>MaxConnections</c> in the config
    /// silently wins either way.
    /// </summary>
    public AdaptiveConnections? AdaptiveConnections { get; init; }

    /// <summary>Port of <c>model_concurrency_key</c>: the registry key of this model's connection pool.</summary>
    public string ConcurrencyKey => ModelConcurrency.Key(Api);

    /// <summary>A copy of this model that also delivers events to <paramref name="sink"/>.</summary>
    public Model WithEventSink(IModelEventSink sink) => new(Api, Config, Retry) { EventSink = sink, AdaptiveConnections = AdaptiveConnections, Role = Role };

    /// <summary>Port of <c>Model.role</c>: the named role this instance is bound to (see <see cref="ModelRoles"/>), stamped on every <see cref="ModelEvent"/> and used for per-role usage.</summary>
    public string? Role { get; init; }

    /// <summary>A copy of this model bound to <paramref name="role"/> (port of <c>copy(model)._set_role(role)</c>).</summary>
    public Model WithRole(string role)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        return new Model(Api, Config, Retry) { EventSink = EventSink, AdaptiveConnections = AdaptiveConnections, Role = role };
    }

    /// <summary>A copy of this model with <paramref name="config"/> as its config.</summary>
    public Model WithConfig(GenerateConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new Model(Api, config, Retry) { EventSink = EventSink, AdaptiveConnections = AdaptiveConnections, Role = Role };
    }

    /// <summary>
    /// Port of <c>Model.count_tokens</c>: an api that implements <see cref="ICompactionModelApi"/> supplies its own
    /// count (Python's provider override of <c>count_tokens</c>); otherwise a conservative estimate for
    /// <paramref name="messages"/> (<see cref="TokenEstimation"/>; Foundry has no token-counting endpoint).
    /// </summary>
    public Task<int> CountTokensAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        if (Api is ICompactionModelApi native)
        {
            return native.CountTokensAsync(messages, cancellationToken);
        }

        return TokenEstimation.CountTokensAsync(
            messages,
            text => Task.FromResult(TokenEstimation.CountTextTokens(text)),
            media => Task.FromResult(TokenEstimation.CountMediaTokens(media)));
    }

    public Task<ModelOutput> GenerateAsync(
        string input,
        IReadOnlyList<ToolInfo>? tools = null,
        ToolChoice? toolChoice = null,
        GenerateConfig? config = null,
        CachePolicy? cache = null,
        StreamHandler? onStream = null,
        CancellationToken cancellationToken = default) =>
        GenerateAsync([new ChatMessageUser(input)], tools, toolChoice, config, cache, onStream, cancellationToken);

    public Task<ModelOutput> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolDef> tools,
        ToolChoice? toolChoice = null,
        GenerateConfig? config = null,
        CachePolicy? cache = null,
        StreamHandler? onStream = null,
        CancellationToken cancellationToken = default) =>
        // Python (Model.generate): apply any tool model_input handlers before the provider sees the conversation.
        GenerateAsync(ToolModelInput.Resolve(tools, input, ToolModelInput.HintsFor(Api)), tools.Select(t => t.ToInfo()).ToArray(), toolChoice, config, cache, onStream, cancellationToken);

    /// <summary>
    /// Port of <c>Model.generate</c>. <paramref name="cache"/> enables the prompt cache (<c>true</c> selects
    /// <see cref="CachePolicy.Default"/>): a hit is returned without a provider call and recorded as a
    /// <see cref="CacheMode.Read"/> event; otherwise every attempt is recorded as <see cref="CacheMode.Write"/> and the
    /// output is stored after the call (and after the usage limit check, as in Python).
    /// </summary>
    public async Task<ModelOutput> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo>? tools = null,
        ToolChoice? toolChoice = null,
        GenerateConfig? config = null,
        CachePolicy? cache = null,
        StreamHandler? onStream = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var context = SampleContext.Current;
        var resolvedConfig = Config.Merge(config);
        var maxRetries = resolvedConfig.MaxRetries ?? Retry.MaxRetries;
        var retryTimeout = resolvedConfig.Timeout is { } timeout ? TimeSpan.FromSeconds(timeout) : Retry.Timeout;
        // Python: a call that passes no cache argument falls back to config.cache (bool | CachePolicy).
        cache ??= resolvedConfig.Cache switch { CachePolicy policy => policy, true => CachePolicy.Default, _ => null };
        if (resolvedConfig.MaxTokens is null)
        {
            resolvedConfig = resolvedConfig with { MaxTokens = Api.MaxTokensForConfig(resolvedConfig) };
        }

        // Python counts the caller's conversation, before its own config.system_message is inserted.
        context?.Limits.CheckMessageLimit(input.Count);
        MessageLimit.CheckMessageLimit(input.Count, raiseForEqual: true);

        var messages = input;
        if (resolvedConfig.SystemMessage is { } systemMessage)
        {
            messages = [new ChatMessageSystem(systemMessage), .. messages];
        }

        var resolvedTools = tools ?? [];
        var resolvedChoice = toolChoice ?? ToolChoice.Auto;

        // Python: raise error if we don't support remote_mcp and we have an mcp server
        if (!ModelApiHooks.SupportsRemoteMcp(Api))
        {
            foreach (var tool in resolvedTools)
            {
                if (McpServerRemote.IsMcpServerTool(tool))
                {
                    throw new InvalidOperationException($"Remote MCP execution is not supported for {Name}. Please use \"local\" execution instead.");
                }
            }
        }

        if (resolvedChoice is ToolFunction function)
        {
            resolvedTools = resolvedTools.Where(t => t.Name == function.Name).ToArray();
        }

        if (resolvedTools.Count == 0 || ReferenceEquals(resolvedChoice, ToolChoice.None))
        {
            resolvedTools = [];
            resolvedChoice = ToolChoice.None;
        }

        if (ModelApiHooks.CollapseUserMessages(Api))
        {
            messages = CollapseUserMessages(messages);
        }

        await using var connection = await ConnectionSlot.HoldAsync(ConnectionSemaphore(resolvedConfig), cancellationToken).ConfigureAwait(false);
        using var request = Concurrency.BeginRequest(connection.Controller);
        using var retryWait = new RetryWaitScope(Name, context);
        var cacheMode = cache is null ? (CacheMode?)null : CacheMode.Write;
        var cacheEntry = cache is null
            ? null
            : new CacheEntry(ModelApiHooks.BaseUrl(Api), resolvedConfig, messages, ModelIdentity.ForCache(Api), cache, resolvedChoice, resolvedTools, context?.SampleState?.Epoch, Api.IsFoundry ? null : Api.ModelArgsForLog);

        var started = DateTimeOffset.UtcNow;
        var retries = 0;
        // one stream observer per generate call (spanning attempts, as in Python) whenever the call asked for
        // chunks: an on_stream handler, or a stream_idle_timeout (stall detection cannot work without them)
        var observer = onStream is not null || resolvedConfig.StreamIdleTimeout is not null ? new ModelStreamObserver(Name, onStream) : null;
        while (true)
        {
            await HookEmitter.EmitBeforeModelGenerateAsync(Name, messages, resolvedTools, resolvedChoice, resolvedConfig, cacheMode, cancellationToken).ConfigureAwait(false);
            var attemptStarted = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            if (cacheEntry is not null && await PromptCache.FetchAsync(cacheEntry, cancellationToken).ConfigureAwait(false) is { } cached)
            {
                Record(messages, resolvedTools, resolvedChoice, resolvedConfig, cached, null, retries, null, attemptStarted, stopwatch.Elapsed.TotalSeconds, CacheMode.Read);
                if (cached.Usage is { } cachedUsage)
                {
                    await HookEmitter.EmitModelCacheUsageAsync(Name, cachedUsage, cancellationToken).ConfigureAwait(false);
                }

                // a cache hit also advances the conversation by one assistant message (Python records the turn in the outer frame)
                return CompleteGenerate(cached);
            }

            GenerateResult? result = null;
            Exception? thrown = null;
            using (var attempt = new GenerateAttempt(observer, resolvedConfig, cancellationToken))
            {
                try
                {
                    result = await Api.GenerateAsync(messages, resolvedTools, resolvedChoice, resolvedConfig, onStream, attempt.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }

                // Python checks the scopes' cancel_called after the call: a fired timeout wins over whatever the
                // attempt produced, the stall scope's sharper diagnosis over the attempt timeout
                if (attempt.TimeoutError is { } timeoutError)
                {
                    result = null;
                    thrown = timeoutError;
                }
            }

            var elapsed = stopwatch.Elapsed.TotalSeconds;

            if (result?.Output is { } output)
            {
                output = ModelCosts.PriceOutput(output.Fallback?.FallbackModel ?? Name, WithGenerateSource(output));
                Record(messages, resolvedTools, resolvedChoice, resolvedConfig, output, result.Call, retries, null, attemptStarted, elapsed, cacheMode);
                if (output.Usage is { } usage)
                {
                    Throughput.RecordGenerate(Name, usage);
                    if (Role is { } role)
                    {
                        SampleModelAccumulators.RecordRoleUsage(role, usage);
                    }

                    // record_and_check_model_usage: record on every limit, check the token limits root first, then
                    // record and check cost -- a call that trips both raises the token limit with its tokens counted
                    context?.Limits.RecordUsage(usage, Name);
                    TokenLimit.RecordModelUsage(usage);
                    context?.Limits.CheckTokenLimit();
                    TokenLimit.CheckTokenLimit();
                    if (usage.TotalCost is { } cost)
                    {
                        context?.Limits.RecordModelCost(cost);
                        context?.Limits.CheckCostLimit();
                    }

                    await HookEmitter.EmitModelUsageAsync(Name, usage, elapsed, retries, cancellationToken).ConfigureAwait(false);
                }

                NotifyCleanSuccess(request.Request);
                if (cacheEntry is not null)
                {
                    await PromptCache.StoreAsync(cacheEntry, output, cancellationToken).ConfigureAwait(false);
                }

                return CompleteGenerate(output);
            }

            if (result?.Error is { } terminal)
            {
                Record(messages, resolvedTools, resolvedChoice, resolvedConfig, null, result.Call, retries, terminal.Message, attemptStarted, elapsed, cacheMode);
                throw new ModelGenerateException(terminal.Message, terminal, result.Call);
            }

            var failure = thrown ?? new InvalidOperationException("Model API returned neither an output nor an error.");
            Record(messages, resolvedTools, resolvedChoice, resolvedConfig, null, null, retries, failure.Message, attemptStarted, elapsed, cacheMode);

            // attempt / stream-idle timeouts are always retried (transient), as in Python's should_retry
            var decision = thrown switch
            {
                null => RetryDecision.No(),
                AttemptTimeoutException or StreamIdleTimeoutException => RetryDecision.Transient(),
                _ => ModelApiHooks.ShouldRetry(Api, thrown),
            };
            if (!decision.Retry && thrown is not null && HookEmitter.HasApiKeyOverride && ModelApiHooks.IsAuthFailure(Api, thrown))
            {
                decision = RetryDecision.Transient();
            }

            if (decision.Retry)
            {
                Concurrency.ReportHttpRetry(decision.Kind, decision.RetryAfter, Name);
            }

            var budgetExhausted = retryTimeout is { } budget && DateTimeOffset.UtcNow - started >= budget;
            if (!decision.Retry || retries >= maxRetries || budgetExhausted)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            var wait = decision.RetryAfter is { } retryAfter ? TimeSpan.FromSeconds(Math.Max(0, retryAfter)) : Backoff(retries);
            retries++;
            await NotifyRetryAsync(onStream, retries).ConfigureAwait(false);
            await HookEmitter.EmitModelRetryAsync(Name, retries, wait.TotalSeconds, RetryErrorInfo.Of(thrown), cancellationToken).ConfigureAwait(false);
            Throughput.RecordRetryWait(Name, wait.TotalSeconds, waiter: context);
            // Credit the scheduled wait before sleeping so the working-limit monitor cannot count it as work.
            // Reconcile to elapsed time even on cancellation (or an injected delay that returns early).
            WorkingLimit.ReportSampleWaitingTime(wait);
            var waitStarted = Stopwatch.GetTimestamp();
            try
            {
                await (Retry.Delay ?? SleepDelay)(wait, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                WorkingLimit.ReportSampleWaitingTime(Stopwatch.GetElapsedTime(waitStarted) - wait);
            }
        }
    }

    /// <summary>Port of <c>collapse_consecutive_user_messages</c> / <c>combine_messages</c> for user messages.</summary>
    public static IReadOnlyList<ChatMessage> CollapseUserMessages(IReadOnlyList<ChatMessage> messages)
    {
        var collapsed = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (message is ChatMessageUser user && collapsed.Count > 0 && collapsed[^1] is ChatMessageUser previous)
            {
                collapsed[^1] = Combine(previous, user);
            }
            else
            {
                collapsed.Add(message);
            }
        }

        return collapsed;
    }

    private static ChatMessageUser Combine(ChatMessageUser a, ChatMessageUser b)
    {
        MessageContent content = a.Content.IsString && b.Content.IsString
            ? a.Content.Text + "\n" + b.Content.Text
            : MessageContent.FromItems([.. a.ContentList, .. b.ContentList]);

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var source in new[] { a.Metadata, b.Metadata })
        {
            if (source is null)
            {
                continue;
            }

            foreach (var (key, value) in source)
            {
                metadata[key] = value;
            }
        }

        metadata["combined_from"] = new[] { a.Id, b.Id };
        var toolCallIds = (a.ToolCallId ?? []).Concat(b.ToolCallId ?? []).ToArray();
        return new ChatMessageUser(content)
        {
            ToolCallId = toolCallIds.Length > 0 ? toolCallIds : null,
            Source = b.Source ?? a.Source,
            Metadata = metadata,
        };
    }

    private static ModelOutput WithGenerateSource(ModelOutput output)
    {
        if (output.Choices.All(c => c.Message.Source is not null))
        {
            return output;
        }

        return output with
        {
            Choices = output.Choices
                .Select(c => c.Message.Source is null ? c with { Message = c.Message with { Source = "generate" } } : c)
                .ToArray(),
        };
    }

    /// <summary>
    /// The outer frame of Python's <c>Model.generate</c>, run once per returned output after retries and fallbacks
    /// have resolved -- cache hits included: a cached response originally served by a fallback is still a
    /// fallback-served response, and a hit advances the conversation by one assistant message, so it is a turn.
    /// <see cref="TurnLimit.RecordTurn"/> records the tripping turn before it raises.
    /// </summary>
    private static ModelOutput CompleteGenerate(ModelOutput output)
    {
        SampleModelAccumulators.RecordFallback(output);
        TurnLimit.RecordTurn();
        return output;
    }

    private TimeSpan Backoff(int retries)
    {
        var cap = Math.Min(Retry.MaxBackoffSeconds, Retry.InitialBackoffSeconds * Math.Pow(2, retries));
        return TimeSpan.FromSeconds(Random.Shared.NextDouble() * Math.Max(0, cap));
    }

    /// <summary>
    /// Port of <c>Model._connection_concurrency</c>: this model's connection pool, keyed by <see cref="ConcurrencyKey"/>.
    /// Adaptive (the default) creates a controller starting at the config's <c>Start</c>; an explicit
    /// <c>MaxConnections</c> wins silently with a static limit; otherwise the api's <c>MaxConnections()</c> applies.
    /// The slot is held across retries and their backoff, exactly as Python holds it.
    /// </summary>
    private IConcurrencySemaphore ConnectionSemaphore(GenerateConfig config)
    {
        var key = ConcurrencyKey;
        var setting = AdaptiveConnections ?? AdaptiveConnections.FromConfigValue(config.AdaptiveConnections);
        if (Concurrency.AdaptiveActive(setting, config.MaxConnections))
        {
            var adaptive = (setting ?? AdaptiveConnections.Default).Resolve();
            return Concurrency.GetOrCreateSemaphore(Name, adaptive.Start, key, visible: true, adaptive: adaptive);
        }

        return Concurrency.GetOrCreateSemaphore(Name, config.MaxConnections ?? Api.MaxConnections(), key, visible: true);
    }

    /// <summary>A clean success (no retries, not a cache hit) counts toward the controller's round; anything else is neutral.</summary>
    private static void NotifyCleanSuccess(ConnectionRequest request)
    {
        if (request.Controller is { } controller && !request.HadRetry && !request.WasCacheHit)
        {
            controller.NotifySuccess();
        }
    }

    /// <summary>Port of <c>cleared_retry_wait()</c>: clears the sample's retry-wait mark once the whole retried call resolves.</summary>
    private readonly struct RetryWaitScope(string model, object? waiter) : IDisposable
    {
        public void Dispose()
        {
            if (waiter is not null)
            {
                Throughput.ClearRetryWait(model, waiter);
            }
        }
    }

    private static async Task NotifyRetryAsync(StreamHandler? onStream, int attempt)
    {
        if (onStream is null)
        {
            return;
        }

        try
        {
            await onStream(new StreamRetryEvent(attempt)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A misbehaving stream handler must not turn a retry into a failure (the providers detach such handlers too).
        }
    }

    private void Record(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        ToolChoice toolChoice,
        GenerateConfig config,
        ModelOutput? output,
        ModelCall? call,
        int retries,
        string? error,
        DateTimeOffset started,
        double elapsed,
        CacheMode? cache = null)
    {
        var e = new ModelEvent
        {
            Timestamp = started,
            Model = Name,
            Role = Role,
            Input = input,
            Tools = tools,
            ToolChoice = toolChoice,
            Config = config,
            Output = output ?? ModelOutput.FromContent(Name, ""),
            Call = call,
            Retries = retries,
            Error = error,
            Completed = DateTimeOffset.UtcNow,
            WorkingTime = elapsed,
            Cache = cache,
        };
        SampleContext.Current?.Transcript.Add(e);
        EventSink?.OnModelEvent(e);
        ModelEventSinks.Current?.OnModelEvent(e);
    }
}
