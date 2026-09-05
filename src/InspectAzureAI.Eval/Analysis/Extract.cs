using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Analysis;

/// <summary>Port of <c>analysis/_dataframe/extract.py</c>: the value transforms and text renderings shared by the column sets.</summary>
public static class Extract
{
    /// <summary>Port of <c>list_as_str</c>: a list joined with commas (each element as Python's <c>str()</c>), a scalar as its <c>str()</c>.</summary>
    public static JsonNode? ListAsStr(JsonNode? x)
    {
        var items = x is JsonArray array ? array.ToList() : [x];
        return JsonValue.Create(string.Join(',', items.Select(PythonFormat.Str)));
    }

    /// <summary>Port of <c>remove_namespace</c>: the text after the first <c>/</c> of a string (e.g. <c>pkg/task</c> → <c>task</c>); other values unchanged.</summary>
    public static JsonNode? RemoveNamespace(JsonNode? x)
    {
        if (x is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var slash = text.IndexOf('/', StringComparison.Ordinal);
            return JsonValue.Create(slash < 0 ? text : text[(slash + 1)..]);
        }

        return x;
    }

    /// <summary>Port of <c>score_values</c>: the <c>value</c> of every score in a scores dictionary, keyed by scorer.</summary>
    public static JsonNode? ScoreValues(JsonNode? x)
    {
        var scores = x as JsonObject ?? throw new ArgumentException("scores must be a dictionary", nameof(x));
        var values = new JsonObject();
        foreach (var (key, score) in scores)
        {
            var obj = score as JsonObject ?? throw new ArgumentException($"score '{key}' is not a dictionary", nameof(x));
            values[key] = obj.TryGetPropertyValue("value", out var value) ? value?.DeepClone() : null;
        }

        return values;
    }

    /// <summary>Port of <c>score_value</c>: the first score of a scores dictionary (null when empty).</summary>
    public static JsonNode? ScoreValue(JsonNode? x)
    {
        var scores = x as JsonObject ?? throw new ArgumentException("scores must be a dictionary", nameof(x));
        return scores.FirstOrDefault().Value?.DeepClone();
    }

    /// <summary>
    /// Port of <c>score_details</c>: every score's <c>value</c> plus, when present and truthy, its <c>answer</c>,
    /// <c>explanation</c>, <c>reason</c> and <c>metadata</c> as <c>{scorer}_{field}</c> entries. A non-dictionary
    /// input gives an empty dictionary, as in Python.
    /// </summary>
    public static JsonNode? ScoreDetails(JsonNode? x)
    {
        var details = new JsonObject();
        if (x is not JsonObject scores)
        {
            return details;
        }

        foreach (var (key, score) in scores)
        {
            if (score is not JsonObject obj)
            {
                continue;
            }

            details[key] = obj.TryGetPropertyValue("value", out var value) ? value?.DeepClone() : null;
            foreach (var extra in new[] { "answer", "explanation", "reason", "metadata" })
            {
                if (obj.TryGetPropertyValue(extra, out var extraValue) && Truthy(extraValue))
                {
                    details[$"{key}_{extra}"] = extraValue!.DeepClone();
                }
            }
        }

        return details;
    }

    /// <summary>
    /// Port of <c>auto_id</c>: the shortuuid encoding of the MD5 of <c>{base}_{index}</c> — the stable id given to
    /// samples, messages and events that carry none of their own.
    /// </summary>
    public static string AutoId(string @base, string index)
    {
        ArgumentNullException.ThrowIfNull(@base);
        ArgumentNullException.ThrowIfNull(index);
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"{@base}_{index}"));
        return ShortUuidEncode(hash);
    }

    /// <summary>Port of <c>messages_as_str</c> over a sample input: a string is one user message.</summary>
    public static string MessagesAsStr(SampleInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.IsText ? MessagesAsStr([new ChatMessageUser(input.Text ?? "")]) : MessagesAsStr(input.Messages!);
    }

    /// <summary>Port of <c>messages_as_str</c>: the messages rendered with <see cref="MessageAsStr"/> and joined by blank lines.</summary>
    public static string MessagesAsStr(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return string.Join("\n\n", messages.Select(MessageAsStr));
    }

    /// <summary>
    /// Port of <c>message_as_str</c>: <c>role:\ncontent\n</c>, with the tool calls of an assistant message
    /// (<c>Tool Call: name</c> plus its arguments one per line) and the error of a failed tool message.
    /// </summary>
    public static string MessageAsStr(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var role = message.Role;
        var content = message.Text.Length > 0 ? PythonStrip(message.Text) : "";
        if (message is ChatMessageAssistant { ToolCalls: { } toolCalls })
        {
            var entry = new StringBuilder($"{role}:\n{content}\n");
            foreach (var tool in toolCalls)
            {
                var argsText = string.Join('\n', tool.Arguments.Select(pair => $"{pair.Key}: {PythonFormat.Str(pair.Value)}"));
                entry.Append($"\nTool Call: {tool.Function}\nArguments:\n{argsText}");
            }

            return entry.ToString();
        }

        if (message is ChatMessageTool { Error: { } error } toolMessage)
        {
            var funcName = string.IsNullOrEmpty(toolMessage.Function) ? "unknown" : toolMessage.Function;
            return $"{role}:\n{content}\n\nError in tool call '{funcName}':\n{error.Message}\n";
        }

        return $"{role}:\n{content}\n";
    }

    /// <summary>The shortuuid encoding of 16 big-endian bytes: base 57, most significant digit first, padded to 22 characters.</summary>
    internal static string ShortUuidEncode(ReadOnlySpan<byte> bytes)
    {
        var number = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        var alphabet = ShortUuid.Alphabet;
        var digits = new StringBuilder();
        while (number > 0)
        {
            number = BigInteger.DivRem(number, alphabet.Length, out var digit);
            digits.Append(alphabet[(int)digit]);
        }

        while (digits.Length < ShortUuid.Length)
        {
            digits.Append(alphabet[0]);
        }

        var chars = digits.ToString().ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    /// <summary>Python <c>str.strip()</c>: leading and trailing whitespace removed.</summary>
    internal static string PythonStrip(string text) => text.Trim();

    /// <summary>Python truthiness of a JSON value.</summary>
    internal static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        JsonValue value => PythonFormat.Scalar(value) switch
        {
            bool b => b,
            long l => l != 0,
            double d => d != 0,
            string s => s.Length > 0,
            _ => true,
        },
        _ => true,
    };
}
