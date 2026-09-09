// ============================================================================
//  LAYER 5: THE MODEL LAYER
//  Python: inspect_ai/model  (public: get_model, Model, ChatMessage*, GenerateConfig ...)
//
//  This is the public, provider-neutral API for talking to a model. A solver
//  calls `model.generate(messages, tools)` and gets a ModelOutput back; it
//  never sees HTTP, API keys or vendor JSON. The model layer's jobs are:
//
//    - resolve "provider/model-name" to a provider via the registry;
//    - merge GenerateConfig (temperature, max_tokens, max_retries ...);
//    - retry transient failures (rate limits) with backoff;
//    - enforce token / cost limits and the connection semaphore (omitted here);
//    - cache responses (omitted here);
//    - record a ModelEvent in the transcript with the exact request/response.
//
//  It calls DOWN into a provider through the ModelAPI abstract class defined
//  in this file. It never names a concrete provider: `get_model` asks the
//  registry. That is how the model layer "talks to providers" without
//  importing them.
// ============================================================================
using inspect_ai._util.display;
using inspect_ai._util.registry;
using inspect_ai.log;

namespace inspect_ai.model;

// ---------------------------------------------------------------------------
//  Data types shared by everything above layer 5 (Python: _chat_message.py,
//  _model_output.py, _generate_config.py, tool/_tool_info.py)
// ---------------------------------------------------------------------------

/// <summary>A tool call the model asked for.</summary>
public sealed record ToolCall(string Id, string Function, IReadOnlyDictionary<string, string> Arguments);

/// <summary>What the model needs to know about a tool: name, description, parameter schema.</summary>
public sealed record ToolInfo(string Name, string Description, IReadOnlyDictionary<string, string> Parameters);

/// <summary>One conversation message. Role is system / user / assistant / tool.</summary>
public sealed record ChatMessage(
    string Role,
    string Content,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? Function = null)
{
    public static ChatMessage System(string text) => new("system", text);
    public static ChatMessage User(string text) => new("user", text);
    public static ChatMessage Tool(string toolCallId, string function, string result) => new("tool", result, ToolCallId: toolCallId, Function: function);
}

public sealed record ModelUsage(int InputTokens, int OutputTokens);

/// <summary>Raw request/response bytes, captured for the transcript (Python: ModelCall).</summary>
public sealed record ModelCall(string Request, string Response);

/// <summary>What generate() returns. StopReason is "stop", "tool_calls", "max_tokens", ...</summary>
public sealed record ModelOutput(ChatMessage Message, string StopReason, ModelUsage Usage, ModelCall Call);

public sealed record GenerateConfig(int MaxRetries = 5, double Temperature = 0.0, int MaxTokens = 4096);

// ---------------------------------------------------------------------------
//  The provider contract. Providers (layer 6) subclass this. The model layer
//  calls it and never anything more specific.
// ---------------------------------------------------------------------------
public abstract class ModelAPI(string modelName)
{
    public string ModelName { get; } = modelName;

    /// <summary>One request to the vendor API, no retries, no bookkeeping.</summary>
    public abstract Task<ModelOutput> Generate(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, GenerateConfig config);

    /// <summary>Does this exception deserve a retry? Providers know their own error types; the model layer only asks.</summary>
    public virtual bool ShouldRetry(Exception ex) => false;

    /// <summary>How long the vendor asked us to wait (a Retry-After header), if it said. Null: the model layer's own backoff.</summary>
    public virtual TimeSpan? RetryAfter(Exception ex) => null;
}

/// <summary>Python's `@modelapi(name=...)` decorator, in attribute form.</summary>
public sealed class ModelApiAttribute(string name) : RegistryAttribute(RegistryType.ModelApi, name);

// ---------------------------------------------------------------------------
//  Model: the object a solver holds. Wraps a ModelAPI with config, retries and
//  transcript recording.
// ---------------------------------------------------------------------------
public sealed class Model(ModelAPI api, GenerateConfig config)
{
    public string Name => $"{api.GetType().Name}/{api.ModelName}";
    public GenerateConfig Config { get; } = config;

    /// <summary>Python: `await model.generate(input, tools, config)`.</summary>
    public async Task<ModelOutput> Generate(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo>? tools = null, GenerateConfig? config = null)
    {
        tools ??= Array.Empty<ToolInfo>();
        config ??= Config;
        Display.Step("L5 model", $"generate: {input.Count} messages, {tools.Count} tools, max_retries={config.MaxRetries}");

        var retries = 0;
        while (true)
        {
            try
            {
                // ---- down into layer 6 ----------------------------------
                var output = await api.Generate(input, tools, config);

                // ---- up into the transcript -----------------------------
                Transcript.transcript().Emit(new ModelEvent(
                    "L5 model", Name, input.Count, output.StopReason, output.Call.Request, output.Call.Response, retries,
                    output.Usage.InputTokens, output.Usage.OutputTokens));
                return output;
            }
            catch (Exception ex) when (api.ShouldRetry(ex) && retries < config.MaxRetries)
            {
                retries++;
                // Retry-After is a floor, not the whole story: a service that says "1s" while
                // still saturated would burn every retry in five seconds without our own backoff.
                var delay = api.RetryAfter(ex) is { } hinted && hinted > Backoff(retries) ? hinted : Backoff(retries);
                Display.Step("L5 model", $"retryable {ex.GetType().Name}: '{Truncate(ex.Message)}' -> retry {retries}/{config.MaxRetries} after {delay.TotalMilliseconds:0}ms");
                Transcript.transcript().Emit(new InfoEvent("L5 model", $"retry {retries} after {ex.GetType().Name}: {ex.Message}"));
                await Task.Delay(delay);
            }
        }
    }

    /// <summary>Exponential backoff with jitter, 1s doubling to a 30s cap (Python: tenacity's wait_exponential_jitter).
    /// The mock provider's simulated 429 pays the first second too; that is what a real one costs.</summary>
    private static TimeSpan Backoff(int attempt)
        => TimeSpan.FromMilliseconds(Math.Min(30_000, 1_000 * Math.Pow(2, attempt - 1)) + Random.Shared.Next(0, 250));

    private static string Truncate(string s) => s.Length <= 120 ? s : s[..117] + "...";
}

/// <summary>Python: `get_model("openai/gpt-4o")`, `get_model()` and `get_model(role="grader")`.</summary>
public static class Models
{
    // The model the current eval is running with, and the models bound to
    // named roles (Python: the active-model contextvar and `--model-role`).
    // Set by the engine; read by anything that needs "the model" or "the
    // grader" without being told which provider that is.
    private static readonly AsyncLocal<Model?> Active = new();
    private static readonly AsyncLocal<IReadOnlyDictionary<string, Model>?> Roles = new();

    /// <summary>
    /// With a name: resolve "provider/model" through the registry. Without one:
    /// the model bound to <paramref name="role"/> if the run bound it, else the
    /// eval's active model. Nothing above layer 5 ever names a provider.
    /// </summary>
    public static Model get_model(string? name = null, GenerateConfig? config = null, string? role = null)
    {
        if (name is null)
        {
            if (role is not null && Roles.Value is { } roles && roles.TryGetValue(role, out var bound)) return bound;
            return Active.Value ?? throw new InvalidOperationException("get_model() without a name called outside a running eval.");
        }

        // "provider/model": the part before the slash is a registry name,
        // the part after it is passed to the provider untouched.
        var slash = name.IndexOf('/');
        if (slash <= 0) throw new ArgumentException("model names look like 'provider/model-name'", nameof(name));
        var provider = name[..slash];
        var modelName = name[(slash + 1)..];

        Display.Step("L5 model", $"get_model('{name}') -> registry lookup for provider '{provider}'");
        var api = Registry.Create<ModelAPI>(RegistryType.ModelApi, provider, modelName);
        return new Model(api, config ?? new GenerateConfig());
    }

    /// <summary>Engine-only: make the eval's model (and its role bindings) ambient for everything it runs.</summary>
    internal static IDisposable BeginActive(Model model, IReadOnlyDictionary<string, Model>? roles = null)
    {
        var (previousModel, previousRoles) = (Active.Value, Roles.Value);
        Active.Value = model;
        Roles.Value = roles;
        return new Restore(() => { Active.Value = previousModel; Roles.Value = previousRoles; });
    }

    private sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }
}
