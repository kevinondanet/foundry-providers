using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Provider.Util;

/// <summary>Consumes constructor options before any wire request or log is constructed.</summary>
internal sealed class DirectProviderOptions
{
    private readonly string? _explicitKey;
    private readonly DirectClientSettings _settings;
    private readonly string _keyVar;
    public string Provider { get; }
    public string BaseUrl { get; }
    public bool? Streaming { get; }
    public double? Timeout { get; }
    public int? MaxRetries { get; }
    public IReadOnlyDictionary<string, object?> ForLog { get; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public JsonObject ExtraBody { get; }
    public JsonObject Values { get; }
    public string AccountFingerprint { get; }
    public DirectClientSettings Settings => _settings;

    public DirectProviderOptions(string provider, string? baseUrl, string? apiKey, object? streaming,
        IReadOnlyDictionary<string, object?>? modelArgs, DirectClientSettings? settings, params string[] specific)
    {
        Provider = provider;
        _settings = settings ?? new();
        _keyVar = provider.ToUpperInvariant() + "_API_KEY";
        Values = new JsonObject();
        var known = new HashSet<string>(specific.Concat(["api_key", "base_url", "streaming", "timeout", "client_timeout", "max_retries", "default_headers", "http_client", "extra_body"]), StringComparer.Ordinal);
        foreach (var (key, value) in modelArgs ?? new Dictionary<string, object?>())
        {
            if (!known.Contains(key)) throw new PrerequisiteError($"Unsupported {provider} model argument '{key}'. Use extra_body for additional request fields.");
            if (key == "http_client") throw new PrerequisiteError("Supply a typed HttpClient through DirectClientSettings, not model_args.");
            Values[key] = value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value);
        }
        _explicitKey = apiKey ?? String("api_key");
        BaseUrl = (baseUrl ?? String("base_url") ?? Environment.GetEnvironmentVariable(provider.ToUpperInvariant() + "_BASE_URL")
            ?? (provider == "openai" ? "https://api.openai.com/v1" : "https://api.anthropic.com")).TrimEnd('/');
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new PrerequisiteError($"{provider} base_url must be an absolute HTTP URL.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new PrerequisiteError("Do not put credentials in base_url; use api_key.");
        if (IsAzureUrl(BaseUrl)) throw new PrerequisiteError($"An Azure endpoint requires {provider}/azure/<deployment>, not {provider}/<model>.");
        Streaming = ProviderUtil.NormalizeStreamArg(streaming ?? (Values["streaming"] is JsonValue v ? v.ToString() : null), "streaming");
        Timeout = Number("client_timeout") ?? Number("timeout");
        MaxRetries = Number("max_retries") is { } retries ? checked((int)retries) : null;
        if (Timeout is <= 0 || MaxRetries is < 0) throw new PrerequisiteError("Timeout must be positive and max_retries must be nonnegative.");
        if (Values["default_headers"] is JsonObject headers) foreach (var pair in headers) Headers[pair.Key] = pair.Value?.ToString() ?? "";
        ExtraBody = Values["extra_body"]?.DeepClone().AsObject() ?? new();
        ModelArgumentSanitizer.RejectCredentials(ExtraBody);
        ForLog = ModelArgumentSanitizer.ForLog(modelArgs);
        var credential = ResolveKey();
        AccountFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
        Values.Remove("api_key");
    }
    public static bool IsAzureUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.EndsWith(".azure.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".azure.us", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".azure.cn", StringComparison.OrdinalIgnoreCase));
    public string ResolveKey()
    {
        var key = _explicitKey ?? Environment.GetEnvironmentVariable(_keyVar);
        key = _settings.ApiKeyOverride?.Invoke(_keyVar, key) ?? key;
        if (string.IsNullOrWhiteSpace(key)) throw new PrerequisiteError($"{_keyVar} is required for direct {Provider} calls. For Foundry, use {Provider}/azure/<deployment>.");
        return key;
    }
    public string? String(string key) => Values[key]?.ToString();
    public double? Number(string key) => Values[key] is { } value ? double.Parse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture) : null;
    public bool? Boolean(string key)
    {
        if (Values[key] is null) return null;
        if (bool.TryParse(Values[key]!.ToString(), out var value)) return value;
        throw new PrerequisiteError($"{key} must be true or false.");
    }
    public JsonObject BodyExtras(GenerateConfig config)
    {
        var extras = ExtraBody.DeepClone().AsObject();
        foreach (var pair in config.ExtraBody ?? new JsonObject()) extras[pair.Key] = pair.Value?.DeepClone();
        ModelArgumentSanitizer.RejectCredentials(extras);
        return extras;
    }
}
