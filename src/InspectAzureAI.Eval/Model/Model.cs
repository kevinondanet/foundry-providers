using System.Diagnostics;
using System.Runtime.ExceptionServices;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

/// <summary>
/// Port of <c>model/_model.py</c> <c>Model.generate</c>: config merging and <c>max_tokens</c> defaulting,
/// consecutive-user-message collapsing when the api needs it, sample limit checks, the retry loop of
/// <c>model/_retry.py</c>, and a <see cref="ModelEvent"/> per attempt on the transcript and event sinks.
/// </summary>
public sealed class Model
{
    private static readonly Func<TimeSpan, CancellationToken, Task> SleepDelay = (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    public Model(IModelApi api, GenerateConfig? config = null, ModelRetryOptions? retry = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        Api = api;
        Config = config ?? new GenerateConfig();
        Retry = retry ?? new ModelRetryOptions();
    }

    public string Name => Api.ModelName;

    public IModelApi Api { get; }

    public GenerateConfig Config { get; }

    public ModelRetryOptions Retry { get; }

    /// <summary>A sink bound to this instance (in addition to the ambient <see cref="ModelEventSinks"/>).</summary>
    public IModelEventSink? EventSink { get; init; }

    /// <summary>A copy of this model that also delivers events to <paramref name="sink"/>.</summary>
    public Model WithEventSink(IModelEventSink sink) => new(Api, Config, Retry) { EventSink = sink, Role = Role };

    /// <summary>Port of <c>Model.role</c>: the named role this instance is bound to (see <see cref="ModelRoles"/>), stamped on every <see cref="ModelEvent"/> and used for per-role usage.</summary>
    public string? Role { get; init; }

    /// <summary>A copy of this model bound to <paramref name="role"/> (port of <c>copy(model)._set_role(role)</c>).</summary>
    public Model WithRole(string role)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        return new Model(Api, Config, Retry) { EventSink = EventSink, Role = role };
    }

    /// <summary>A copy of this model with <paramref name="config"/> as its config.</summary>
    public Model WithConfig(GenerateConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new Model(Api, config, Retry) { EventSink = EventSink, Role = Role };
    }

    /// <summary>
    /// Port of <c>Model.count_tokens</c>: a conservative token estimate for <paramref name="messages"/>
    /// (<see cref="TokenEstimation"/>; Foundry has no token-counting endpoint, so the estimate is the only path).
    /// </summary>
    public Task<int> CountTokensAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        StreamHandler? onStream = null,
        CancellationToken cancellationToken = default) =>
        GenerateAsync([new ChatMessageUser(input)], tools, toolChoice, config, onStream, cancellationToken);

    public Task<ModelOutput> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolDef> tools,
        ToolChoice? toolChoice = null,
        GenerateConfig? config = null,
        StreamHandler? onStream = null,
        CancellationToken cancellationToken = default) =>
        GenerateAsync(input, tools.Select(t => t.ToInfo()).ToArray(), toolChoice, config, onStream, cancellationToken);

    public async Task<ModelOutput> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo>? tools = null,
        ToolChoice? toolChoice = null,
        GenerateConfig? config = null,
        StreamHandler? onStream = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var context = SampleContext.Current;
        var resolvedConfig = Config.Merge(config);
        if (resolvedConfig.MaxTokens is null)
        {
            resolvedConfig = resolvedConfig with { MaxTokens = Api.MaxTokens() };
        }

        // Python counts the caller's conversation, before its own config.system_message is inserted.
        context?.Limits.CheckMessageLimit(input.Count);

        var messages = input;
        if (resolvedConfig.SystemMessage is { } systemMessage)
        {
            messages = [new ChatMessageSystem(systemMessage), .. messages];
        }

        var resolvedTools = tools ?? [];
        var resolvedChoice = toolChoice ?? ToolChoice.Auto;
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

        var started = DateTimeOffset.UtcNow;
        var retries = 0;
        // one stream observer per generate call (spanning attempts, as in Python) whenever the call asked for
        // chunks: an on_stream handler, or a stream_idle_timeout (stall detection cannot work without them)
        var observer = onStream is not null || resolvedConfig.StreamIdleTimeout is not null ? new ModelStreamObserver(Name, onStream) : null;
        while (true)
        {
            var attemptStarted = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
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
                output = WithGenerateSource(output);
                Record(messages, resolvedTools, resolvedChoice, resolvedConfig, output, result.Call, retries, null, attemptStarted, elapsed);
                SampleModelAccumulators.RecordFallback(output);
                if (output.Usage is { } usage)
                {
                    if (Role is { } role)
                    {
                        SampleModelAccumulators.RecordRoleUsage(role, usage);
                    }

                    context?.Limits.AddUsage(usage, Name);
                }

                return output;
            }

            if (result?.Error is { } terminal)
            {
                Record(messages, resolvedTools, resolvedChoice, resolvedConfig, null, result.Call, retries, terminal.Message, attemptStarted, elapsed);
                throw new ModelGenerateException(terminal.Message, terminal, result.Call);
            }

            var failure = thrown ?? new InvalidOperationException("Model API returned neither an output nor an error.");
            Record(messages, resolvedTools, resolvedChoice, resolvedConfig, null, null, retries, failure.Message, attemptStarted, elapsed);

            // attempt / stream-idle timeouts are always retried (transient), as in Python's should_retry
            var decision = thrown switch
            {
                null => RetryDecision.No(),
                AttemptTimeoutException or StreamIdleTimeoutException => RetryDecision.Transient(),
                _ => ModelApiHooks.ShouldRetry(Api, thrown),
            };
            var budgetExhausted = Retry.Timeout is { } budget && DateTimeOffset.UtcNow - started >= budget;
            if (!decision.Retry || retries >= Retry.MaxRetries || budgetExhausted)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            var wait = decision.RetryAfter is { } retryAfter ? TimeSpan.FromSeconds(Math.Max(0, retryAfter)) : Backoff(retries);
            retries++;
            await NotifyRetryAsync(onStream, retries).ConfigureAwait(false);
            await (Retry.Delay ?? SleepDelay)(wait, cancellationToken).ConfigureAwait(false);
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

    private TimeSpan Backoff(int retries)
    {
        var cap = Math.Min(Retry.MaxBackoffSeconds, Retry.InitialBackoffSeconds * Math.Pow(2, retries));
        return TimeSpan.FromSeconds(Random.Shared.NextDouble() * Math.Max(0, cap));
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
        double elapsed)
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
        };
        SampleContext.Current?.Transcript.Add(e);
        EventSink?.OnModelEvent(e);
        ModelEventSinks.Current?.OnModelEvent(e);
    }
}
