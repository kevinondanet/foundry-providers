using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InspectAzureAI.Provider;

/// <summary>
/// Reads server-sent events from a chat-completions stream and yields each <c>data:</c> payload as a raw
/// JSON object, stopping at <c>data: [DONE]</c>. This replaces the SDK's typed
/// <c>StreamingChatCompletionsUpdate</c> parsing so that undeclared fields (tool-call <c>index</c>,
/// per-choice <c>content_filter_results</c>) reach the accumulator, matching the Python SDK's dict-backed
/// updates.
/// </summary>
public static class SseParser
{
    /// <summary>Enumerates the JSON updates carried by <paramref name="stream"/>.</summary>
    public static async IAsyncEnumerable<JsonObject> ReadUpdatesAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream);
        var data = new List<string>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (data.Count > 0)
                {
                    var payload = string.Join("\n", data);
                    data.Clear();
                    if (payload == "[DONE]")
                    {
                        yield break;
                    }

                    yield return JsonNode.Parse(payload)?.AsObject() ?? throw new JsonException("Stream update was not a JSON object.");
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..];
                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }

                data.Add(value);
            }
        }

        if (data.Count > 0)
        {
            var payload = string.Join("\n", data);
            if (payload != "[DONE]")
            {
                yield return JsonNode.Parse(payload)?.AsObject() ?? throw new JsonException("Stream update was not a JSON object.");
            }
        }
    }
}
