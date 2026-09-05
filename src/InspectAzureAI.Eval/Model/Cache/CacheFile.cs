using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model.Cache;

/// <summary>
/// The on-disk shape of one prompt cache entry. Python pickles an <c>(expiry, ModelOutput)</c> tuple; the port
/// writes <c>{"expiry": ISO-8601 or null, "output": ModelOutput JSON}</c> using the eval log serializer, so an entry
/// is readable by anything that reads the log's <c>output</c> field.
/// </summary>
internal sealed record CacheFile(DateTimeOffset? Expiry, ModelOutput Output)
{
    public static string Serialize(DateTimeOffset? expiry, ModelOutput output)
    {
        var node = new JsonObject
        {
            ["expiry"] = expiry is { } e ? JsonValue.Create(e.ToString("O", CultureInfo.InvariantCulture)) : null,
            ["output"] = JsonSerializer.SerializeToNode(output, EvalLogWriter.Options),
        };
        return node.ToJsonString(EvalLogWriter.Options);
    }

    /// <summary>Parses an entry; malformed JSON or a wrong shape is a <see cref="JsonException"/>.</summary>
    public static CacheFile Parse(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("A prompt cache entry must be a JSON object.");
        DateTimeOffset? expiry = null;
        if (node.TryGetPropertyValue("expiry", out var expiryNode) && expiryNode is not null)
        {
            if (expiryNode is not JsonValue expiryValue || !expiryValue.TryGetValue<string>(out var text))
            {
                throw new JsonException("The prompt cache entry 'expiry' must be an ISO-8601 string or null.");
            }

            try
            {
                expiry = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }
            catch (FormatException ex)
            {
                throw new JsonException($"The prompt cache entry 'expiry' is not a timestamp: '{text}'.", ex);
            }
        }

        var outputNode = node["output"] as JsonObject ?? throw new JsonException("The prompt cache entry has no 'output' object.");
        var output = outputNode.Deserialize<ModelOutput>(EvalLogWriter.Options) ?? throw new JsonException("The prompt cache entry 'output' is null.");
        return new CacheFile(expiry, output);
    }
}
