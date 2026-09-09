using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Testing;

/// <summary>One canned turn of a <see cref="ScriptedModelApi"/>: an output, an exception to throw, or a factory over the request.</summary>
public sealed record ScriptedTurn
{
    public ModelOutput? Output { get; init; }

    public Exception? Exception { get; init; }

    /// <summary>A terminal provider failure returned as <c>GenerateResult.Error</c> (an HTTP 400) rather than thrown.</summary>
    public Exception? TerminalError { get; init; }

    public Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolInfo>, ModelOutput>? Factory { get; init; }

    public static ScriptedTurn From(ModelOutput output) => new() { Output = output };

    public static ScriptedTurn From(Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolInfo>, ModelOutput> factory) => new() { Factory = factory };

    /// <summary>A plain text reply (stop reason <c>stop</c>).</summary>
    public static ScriptedTurn Text(string text, ModelUsage? usage = null) =>
        From(ModelOutput.FromContent(ScriptedModelApi.DefaultModelName, text) with { Usage = usage });

    /// <summary>A reply calling <paramref name="function"/> with <paramref name="args"/> (an anonymous object, dictionary or <see cref="JsonObject"/>).</summary>
    public static ScriptedTurn ToolCall(string function, object args, string? id = null, string? text = null, ModelUsage? usage = null)
    {
        var arguments = args as JsonObject ?? JsonSerializer.SerializeToNode(args)?.AsObject() ?? new JsonObject();
        var call = new Provider.Core.ToolCall(id ?? ShortUuid.Generate(), function, arguments);
        var message = new ChatMessageAssistant(text ?? "", toolCalls: [call], model: ScriptedModelApi.DefaultModelName, source: "generate");
        return From(new ModelOutput
        {
            Model = ScriptedModelApi.DefaultModelName,
            Choices = [new ChatCompletionChoice(message, StopReason.ToolCalls)],
            Usage = usage,
        });
    }

    public static ScriptedTurn Throw(Exception exception) => new() { Exception = exception };

    public static ScriptedTurn Error(Exception exception) => new() { TerminalError = exception };
}

/// <summary>A request a <see cref="ScriptedModelApi"/> received.</summary>
public sealed record ScriptedRequest(IReadOnlyList<ChatMessage> Input, IReadOnlyList<ToolInfo> Tools, ToolChoice ToolChoice, GenerateConfig Config);

/// <summary>
/// An <see cref="IModelApi"/> replaying canned turns (the port's stand-in for Python's <c>mockllm</c>):
/// records every request; exhausted scripts answer <c>"(scripted model has no more turns)"</c> unless
/// <see cref="ThrowWhenExhausted"/>.
/// </summary>
public sealed class ScriptedModelApi : IModelApi
{
    public const string DefaultModelName = "scripted";

    public const string ExhaustedText = "(scripted model has no more turns)";

    private readonly object _sync = new();

    private readonly Queue<ScriptedTurn> _turns;

    private readonly List<ScriptedRequest> _requests = [];

    private int _active;

    private int _peak;

    public ScriptedModelApi(IEnumerable<ScriptedTurn> turns, string modelName = DefaultModelName)
    {
        ArgumentNullException.ThrowIfNull(turns);
        _turns = new Queue<ScriptedTurn>(turns);
        ModelName = modelName;
    }

    public ScriptedModelApi(params ScriptedTurn[] turns) : this(turns, DefaultModelName)
    {
    }

    public string ModelName { get; }

    public bool ThrowWhenExhausted { get; init; }

    /// <summary>Whether <c>Model</c> collapses consecutive user messages for this api (default: no).</summary>
    public bool CollapseUserMessages { get; init; }

    /// <summary>Port of <c>supports_remote_mcp()</c>: whether <c>Model</c> lets remote MCP server markers through to this api (default: no, Python's base default).</summary>
    public bool SupportsRemoteMcp { get; init; }

    /// <summary>Overrides the retry decision for thrown turns (default: retry on 408/429/5xx of a RequestFailedException).</summary>
    public Func<Exception, RetryDecision>? ShouldRetry { get; init; }

    /// <summary>Port of <c>max_connections()</c> for the scripted api (Python default 10).</summary>
    public int ConnectionLimit { get; init; } = 10;

    /// <summary>Port of <c>connection_key()</c>: scripted apis sharing a scope share one connection pool.</summary>
    public string ConnectionScope { get; init; } = "default";

    /// <summary>Awaited at the start of every call, so a test can hold calls open and observe how many run at once.</summary>
    public Func<CancellationToken, Task>? Gate { get; init; }

    /// <summary>Calls currently in flight.</summary>
    public int ActiveCalls => Volatile.Read(ref _active);

    /// <summary>The most calls ever in flight at once.</summary>
    public int PeakConcurrentCalls => Volatile.Read(ref _peak);

    public IReadOnlyList<ScriptedRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToArray();
            }
        }
    }

    public int Remaining
    {
        get
        {
            lock (_sync)
            {
                return _turns.Count;
            }
        }
    }

    public int? MaxTokens() => 2048;

    public int MaxConnections() => ConnectionLimit;

    public string ConnectionKey() => ConnectionScope;

    public Task<GenerateResult> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        ToolChoice toolChoice,
        GenerateConfig config,
        CancellationToken cancellationToken = default) =>
        GenerateAsync(input, tools, toolChoice, config, null, cancellationToken);

    public async Task<GenerateResult> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        ToolChoice toolChoice,
        GenerateConfig config,
        StreamHandler? onStream,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var tracked = TrackCall();
        if (Gate is { } gate)
        {
            await gate(cancellationToken).ConfigureAwait(false);
        }

        ScriptedTurn? turn;
        lock (_sync)
        {
            _requests.Add(new ScriptedRequest(input, tools, toolChoice, config));
            turn = _turns.Count > 0 ? _turns.Dequeue() : null;
        }

        var call = ModelCall.Create(Snapshot(input, tools, toolChoice));
        if (turn is null)
        {
            if (ThrowWhenExhausted)
            {
                throw new InvalidOperationException("The scripted model has no more turns.");
            }

            turn = ScriptedTurn.Text(ExhaustedText);
        }

        if (turn.Exception is { } exception)
        {
            call.SetError(new JsonObject { ["error"] = new JsonObject { ["message"] = exception.Message } });
            throw exception;
        }

        if (turn.TerminalError is { } terminal)
        {
            call.SetError(new JsonObject { ["error"] = new JsonObject { ["message"] = terminal.Message } });
            return new GenerateResult(null, terminal, call);
        }

        var output = turn.Output ?? turn.Factory!(input, tools);
        call.SetResponse(new JsonObject { ["completion"] = output.Completion });
        if (onStream is not null && output.Completion.Length > 0)
        {
            await onStream(new StreamTextEvent(output.Completion)).ConfigureAwait(false);
        }

        return new GenerateResult(output, null, call);
    }

    private CallTracker TrackCall()
    {
        var active = Interlocked.Increment(ref _active);
        int peak;
        while ((peak = Volatile.Read(ref _peak)) < active && Interlocked.CompareExchange(ref _peak, active, peak) != peak)
        {
        }

        return new CallTracker(this);
    }

    private readonly struct CallTracker(ScriptedModelApi api) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref api._active);
    }

    private JsonObject Snapshot(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice) => new()
    {
        ["model"] = ModelName,
        ["messages"] = new JsonArray(input.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Text }).ToArray()),
        ["tools"] = new JsonArray(tools.Select(t => (JsonNode)JsonValue.Create(t.Name)).ToArray()),
        ["tool_choice"] = toolChoice is ToolFunction fn ? fn.Name : toolChoice.ToString(),
    };
}
