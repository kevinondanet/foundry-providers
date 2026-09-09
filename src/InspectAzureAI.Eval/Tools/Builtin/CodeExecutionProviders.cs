using System.Globalization;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Eval.Tools.Builtin;

/// <summary>
/// Port of the <c>CodeExecutionProviders</c> TypedDict of <c>tool/_tools/_code_execution.py</c>: the per-provider
/// configuration of <see cref="BuiltinTools.CodeExecution"/>. Every provider is enabled by default (a null
/// property); <c>false</c> disables it (a provider with native code execution then falls back to the sandbox,
/// and <see cref="Python"/> = false disables the sandbox fallback itself); <see cref="OpenAI"/> and
/// <see cref="Python"/> also accept a dictionary of options, as Python's <c>dict[str, Any] | bool</c> does
/// (<c>container</c> and friends for OpenAI; <c>timeout</c> in seconds and <c>sandbox</c> for the
/// <c>python()</c> fallback). Deviation: Python's TypedDict is enforced only by type checkers, so the port is a
/// typed class rather than a string-keyed dictionary; the provider names are checked at compile time and there
/// is no unknown-provider case.
/// </summary>
public sealed class CodeExecutionProviders
{
    /// <summary>Use OpenAI's native code interpreter (default on); false uses the sandbox, a dictionary passes custom options.</summary>
    public CodeExecutionProviderOption? OpenAI { get; init; }

    /// <summary>Use Anthropic's native code execution (default on); false uses the sandbox instead.</summary>
    public bool? Anthropic { get; init; }

    /// <summary>Use Google's native code execution (default on); false uses the sandbox instead.</summary>
    public bool? Google { get; init; }

    /// <summary>Use Grok's native code execution (default on); false uses the sandbox instead.</summary>
    public bool? Grok { get; init; }

    /// <summary>Use Mistral's native code execution (default on); false uses the sandbox instead.</summary>
    public bool? Mistral { get; init; }

    /// <summary>
    /// Use the <c>python()</c> tool as the fallback for providers without native code execution (default on);
    /// false disables the fallback, a dictionary passes the <c>python()</c> options <c>timeout</c> (seconds) and
    /// <c>sandbox</c> (see <see cref="CodeExecutionProviderOption.PythonOptions"/>).
    /// </summary>
    public CodeExecutionProviderOption? Python { get; init; }
}

/// <summary>
/// A provider value of <see cref="CodeExecutionProviders"/>, Python's <c>dict[str, Any] | bool</c>: either a
/// switch (<see cref="Enabled"/>) or a dictionary of provider options (<see cref="Options"/>). Both convert implicitly.
/// </summary>
public sealed class CodeExecutionProviderOption
{
    private CodeExecutionProviderOption(bool? enabled, JsonObject? options)
    {
        Enabled = enabled;
        Options = options;
    }

    /// <summary>The switch, when the value is the bool form.</summary>
    public bool? Enabled { get; }

    /// <summary>The options, when the value is the dictionary form.</summary>
    public JsonObject? Options { get; }

    /// <summary>A bool value: false disables the provider, true leaves it at its default (as Python's <c>_normalize_config</c> does).</summary>
    public static implicit operator CodeExecutionProviderOption(bool enabled) => new(enabled, null);

    /// <summary>A dictionary of provider options.</summary>
    public static implicit operator CodeExecutionProviderOption(JsonObject options) => new(null, options ?? throw new ArgumentNullException(nameof(options)));

    /// <summary>
    /// The options dictionary the <c>python()</c> fallback reads: <c>timeout</c> (whole seconds when the span is
    /// whole, fractional seconds otherwise) and <c>sandbox</c>, each left out when null.
    /// </summary>
    public static CodeExecutionProviderOption PythonOptions(TimeSpan? timeout = null, string? sandbox = null)
    {
        var options = new JsonObject();
        if (timeout is { } span)
        {
            var seconds = span.TotalSeconds;
            options["timeout"] = seconds == Math.Floor(seconds) && seconds <= int.MaxValue ? (int)seconds : seconds;
        }

        if (sandbox is not null)
        {
            options["sandbox"] = sandbox;
        }

        return options;
    }

    /// <summary>Python's <c>True</c>/<c>False</c>, or the options as JSON.</summary>
    public override string ToString() => Options?.ToJsonString() ?? (Enabled == true ? "True" : "False");
}

/// <summary>
/// Port of the provider-configuration helpers of <c>_code_execution.py</c>: <see cref="ValidProviders"/>
/// (<c>valid_providers</c>) and <see cref="Normalize"/> (<c>_normalize_config</c>), plus the reading of the
/// <c>python</c> options that <c>code_execution()</c> does inline.
/// </summary>
public static class CodeExecutionProviderConfig
{
    /// <summary>Port of <c>valid_providers</c>, in the order <c>_normalize_config</c> enables them by default.</summary>
    public static readonly IReadOnlyList<string> ValidProviders = ["openai", "anthropic", "google", "grok", "mistral", "python"];

    /// <summary>
    /// Port of <c>_normalize_config</c>: every provider enabled with empty options, then each configured value
    /// applied in place: a dictionary replaces the provider's options (keeping its position), <c>false</c>
    /// removes the provider, <c>true</c> leaves it as it is. Insertion order is kept, as in the Python dict.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonObject> Normalize(CodeExecutionProviders? providers)
    {
        var normalized = new OrderedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var name in ValidProviders)
        {
            normalized[name] = new JsonObject();
        }

        if (providers is null)
        {
            return normalized;
        }

        Apply(normalized, "openai", providers.OpenAI);
        Apply(normalized, "anthropic", Switch(providers.Anthropic));
        Apply(normalized, "google", Switch(providers.Google));
        Apply(normalized, "grok", Switch(providers.Grok));
        Apply(normalized, "mistral", Switch(providers.Mistral));
        Apply(normalized, "python", providers.Python);
        return normalized;
    }

    /// <summary>
    /// The <c>python()</c> arguments of the fallback tool from the normalized providers: null when "python" is
    /// disabled, otherwise the <c>timeout</c> (seconds, as an integer or fractional number) and <c>sandbox</c>
    /// (a string) of its options, each null when absent. Deviation: Python hands whatever is in the dict to
    /// <c>python()</c> and fails later inside the sandbox; a timeout that is not a number or a sandbox that is not
    /// a string is an <see cref="ArgumentException"/> at construction here.
    /// </summary>
    public static (TimeSpan? Timeout, string? Sandbox)? PythonToolOptions(IReadOnlyDictionary<string, JsonObject> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        if (!providers.TryGetValue("python", out var options))
        {
            return null;
        }

        TimeSpan? timeout = null;
        if (options.TryGetPropertyValue("timeout", out var timeoutNode) && timeoutNode is not null)
        {
            if (timeoutNode is not JsonValue timeoutValue || !ToolInputValidator.TryGetDouble(timeoutValue, out var seconds) || timeoutValue.TryGetValue<bool>(out _))
            {
                throw new ArgumentException(
                    $"The python provider's 'timeout' must be a number of seconds, got {ToolInputValidator.Repr(timeoutNode)}.", nameof(providers));
            }

            timeout = TimeSpan.FromSeconds(seconds);
        }

        string? sandbox = null;
        if (options.TryGetPropertyValue("sandbox", out var sandboxNode) && sandboxNode is not null)
        {
            if (sandboxNode is not JsonValue sandboxValue || !sandboxValue.TryGetValue<string>(out sandbox))
            {
                throw new ArgumentException(
                    $"The python provider's 'sandbox' must be a sandbox environment name, got {ToolInputValidator.Repr(sandboxNode)}.", nameof(providers));
            }
        }

        return (timeout, sandbox);
    }

    private static CodeExecutionProviderOption? Switch(bool? enabled) => enabled is { } value ? value : null;

    private static void Apply(OrderedDictionary<string, JsonObject> normalized, string name, CodeExecutionProviderOption? option)
    {
        if (option is null)
        {
            return;
        }

        if (option.Options is { } options)
        {
            normalized[name] = options.DeepClone().AsObject();
        }
        else if (option.Enabled is false)
        {
            normalized.Remove(name);
        }

        // true leaves the provider alone, as Python's "else: pass" does
    }
}
