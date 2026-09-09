using System.Text.Json;
using System.Text.Json.Nodes;
namespace InspectAzureAI.Provider.Util;

/// <summary>Authentication redaction for recorded configuration; never edits the caller's settings.</summary>
public static class ModelArgumentSanitizer
{
    public static bool IsSecret(string key)
    {
        var normalized = key.Replace('-', '_').ToLowerInvariant();
        return normalized is "api_key" or "auth_token" or "access_token" or "authorization" or "proxy_authorization" or "x_api_key" or "cookie" or "set_cookie"
            || normalized.StartsWith("aws_", StringComparison.Ordinal);
    }
    public static IReadOnlyDictionary<string, object?> ForLog(IReadOnlyDictionary<string, object?>? args)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in args ?? new Dictionary<string, object?>())
        {
            if (IsSecret(key) || value is HttpClient or HttpMessageHandler or Delegate) continue;
            var node = value is JsonNode json ? json.DeepClone() : JsonSerializer.SerializeToNode(value);
            result[key] = Clean(node);
        }
        return result;
    }
    public static JsonNode? Clean(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                if (IsSecret(key)) obj.Remove(key);
                else Clean(obj[key]);
            }
        }
        else if (node is JsonArray array) foreach (var child in array) Clean(child);
        return node;
    }
    public static void RejectCredentials(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (IsSecret(pair.Key)) throw new Core.PrerequisiteError("Authentication belongs in provider credentials, not extra_body.");
                RejectCredentials(pair.Value);
            }
        }
        else if (node is JsonArray array) foreach (var child in array) RejectCredentials(child);
    }
}
