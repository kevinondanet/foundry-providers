using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.Json;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Dataset;

/// <summary>
/// Port of <c>dataset/_util.py</c> (record → sample coercion, <c>data_to_samples</c>) and
/// <c>dataset/_sources/util.py</c> <c>resolve_sample_files</c>. Malformed records raise
/// <see cref="InvalidDataException"/> with Python's <c>ValueError</c> messages.
/// </summary>
internal static class SampleRecords
{
    /// <summary>Port of <c>record_to_sample_fn</c> for a <see cref="FieldSpec"/>.</summary>
    public static RecordToSample Mapper(FieldSpec spec) => record => [ToSample(record, spec)];

    /// <summary>Port of <c>data_to_samples</c>: maps and flattens; <paramref name="autoId"/> numbers samples from 1.</summary>
    public static List<Sample> ToSamples(IEnumerable<JsonObject> records, RecordToSample mapper, bool autoId)
    {
        var nextId = 1;
        var samples = new List<Sample>();
        foreach (var record in records)
        {
            foreach (var sample in mapper(record))
            {
                samples.Add(autoId ? sample with { Id = nextId++ } : sample);
            }
        }

        return samples;
    }

    /// <summary>
    /// Port of <c>resolve_sample_files</c>: file references (files values, setup, sandbox config, user image
    /// paths) that exist relative to the dataset file become absolute paths; anything else is left as-is
    /// because it is inline content.
    /// </summary>
    public static List<Sample> ResolveFiles(IEnumerable<Sample> samples, string location)
    {
        var parent = Path.GetDirectoryName(location) ?? "";
        return samples.Select(sample => ResolveFiles(sample, parent)).ToList();
    }

    private static Sample ResolveFiles(Sample sample, string parent)
    {
        string Resolve(string file)
        {
            // an empty string is literal contents, never a path (it would resolve to the parent directory)
            if (file.Length == 0)
            {
                return file;
            }

            // tolerate 'paths' that are actually file contents (too long / invalid characters)
            try
            {
                var candidate = Path.Combine(parent, file);
                return File.Exists(candidate) ? Path.GetFullPath(candidate) : file;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                return file;
            }
        }

        if (sample.Sandbox is { Config: { } config })
        {
            sample = sample with { Sandbox = sample.Sandbox with { Config = Resolve(config) } };
        }

        if (sample.Files is { } files)
        {
            sample = sample with { Files = files.ToDictionary(pair => pair.Key, pair => Resolve(pair.Value), StringComparer.Ordinal) };
        }

        if (sample.Setup is { } setup)
        {
            sample = sample with { Setup = Resolve(setup) };
        }

        if (sample.Input.Messages is { } messages)
        {
            sample = sample with { Input = messages.Select(m => ResolveMedia(m, Resolve)).ToArray() };
        }

        return sample;
    }

    private static ChatMessage ResolveMedia(ChatMessage message, Func<string, string> resolve)
    {
        if (message is not ChatMessageUser user || user.Content.IsString)
        {
            return message;
        }

        var items = user.Content.Items!.Select(Content (item) => item switch
        {
            ContentImage image => image with { Image = resolve(image.Image) },
            ContentAudio audio => audio with { Audio = resolve(audio.Audio) },
            ContentVideo video => video with { Video = resolve(video.Video) },
            _ => item,
        });
        return user with { Content = MessageContent.FromItems(items) };
    }

    private static Sample ToSample(JsonObject record, FieldSpec spec)
    {
        IReadOnlyDictionary<string, object?>? metadata = null;
        if (spec.Metadata is { Count: > 0 } names)
        {
            metadata = names.ToDictionary(name => name, name => PlainJson.ToObject(record[name]), StringComparer.Ordinal);
        }
        else if (record.TryGetPropertyValue("metadata", out var field))
        {
            metadata = field switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => ReadMetadataText(text),
                JsonObject obj => PlainJson.ToDictionary(obj),
                _ => throw new InvalidDataException($"Unexpected type for 'metadata' field: {PythonTypeName(field)}"),
            };
        }

        return new Sample(ReadInput(record[spec.Input]))
        {
            Target = ReadTarget(record[spec.Target]),
            Choices = ReadChoices(record[spec.Choices]),
            Id = PlainJson.ToObject(record[spec.Id]),
            Metadata = metadata,
            Sandbox = ReadSandbox(record[spec.Sandbox]),
            Files = ReadFiles(record[spec.Files]),
            Setup = record[spec.Setup] is { } setup ? PythonStr(setup) : null,
        };
    }

    private static IReadOnlyDictionary<string, object?> ReadMetadataText(string text)
    {
        var node = JsonNode.Parse(text);
        return node is JsonObject obj
            ? PlainJson.ToDictionary(obj)
            : throw new InvalidDataException($"Unexpected type for 'metadata' field: {PythonTypeName(node)}");
    }

    /// <summary>Port of <c>read_input</c>: a string or a message list; empty/missing input is an error.</summary>
    public static SampleInput ReadInput(JsonNode? input) => input switch
    {
        null => throw new InvalidDataException("No input in dataset"),
        JsonValue value when value.TryGetValue<string>(out var text) => text.Length > 0 ? (SampleInput)text : throw new InvalidDataException("No input in dataset"),
        JsonArray array => array.Count > 0 ? ReadMessages(array) : throw new InvalidDataException("No input in dataset"),
        _ => throw new InvalidDataException($"Unexpected type for 'input' field: {PythonTypeName(input)}"),
    };

    /// <summary>Port of <c>read_messages</c>: role dispatch, every message tagged <c>source="input"</c>.</summary>
    public static SampleInput ReadMessages(JsonArray messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var node in messages)
        {
            if (node is not JsonObject message)
            {
                throw new InvalidDataException("role not specified for chat input in dataset");
            }

            var content = message["content"] is { } contentNode
                ? Deserialize<MessageContent>(contentNode)
                : throw new InvalidDataException("content not specified for chat input in dataset");
            var role = message["role"] is JsonValue roleValue && roleValue.TryGetValue<string>(out var r) ? r : null;
            result.Add(role switch
            {
                "system" => new ChatMessageSystem(content) { Source = "input" },
                "user" => new ChatMessageUser(content) { Source = "input" },
                "assistant" => new ChatMessageAssistant(content, toolCalls: DeserializeOrNull<IReadOnlyList<ToolCall>>(message["tool_calls"]), source: "input"),
                "tool" => new ChatMessageTool(
                    content,
                    toolCallId: DeserializeOrNull<string>(message["tool_call_id"]),
                    function: DeserializeOrNull<string>(message["function"]),
                    error: DeserializeOrNull<ToolCallError>(message["error"]))
                {
                    Source = "input",
                },
                _ => throw new InvalidDataException("role not specified for chat input in dataset"),
            });
        }

        return result.ToArray();
    }

    /// <summary>Port of <c>read_target</c>: missing → "", list → stringified items, scalar → <c>str()</c>.</summary>
    public static Target ReadTarget(JsonNode? target) => target switch
    {
        null => Target.Empty,
        JsonArray array => new Target(array.Select(item => item is null ? "None" : PythonStr(item)).ToArray()),
        _ => new Target(PythonStr(target)),
    };

    /// <summary>Port of <c>read_choices</c>: a list, a comma- (or whitespace-) separated string, or a single scalar.</summary>
    public static IReadOnlyList<string>? ReadChoices(JsonNode? choices)
    {
        switch (choices)
        {
            case null:
                return null;
            case JsonArray array:
                return array.Select(item => item is null ? "None" : PythonStr(item)).Where(text => text.Trim().Length > 0).ToArray();
            case JsonValue value when value.TryGetValue<string>(out var text):
                var parts = text.Split(',');
                if (parts.Length == 1)
                {
                    parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                }

                return parts.Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
            default:
                return [PythonStr(choices)];
        }
    }

    /// <summary>Port of <c>read_sandbox</c>: a type name, a JSON-encoded or literal <c>[type, config]</c> pair.</summary>
    public static SandboxSpec? ReadSandbox(JsonNode? sandbox)
    {
        if (sandbox is null)
        {
            return null;
        }

        if (sandbox is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (!text.TrimStart().StartsWith('['))
            {
                return new SandboxSpec(text);
            }

            sandbox = JsonNode.Parse(text);
        }

        if (sandbox is JsonArray pair)
        {
            return pair.Count == 2
                ? new SandboxSpec(pair[0] is null ? "None" : PythonStr(pair[0]!), pair[1] is null ? "None" : PythonStr(pair[1]!))
                : throw new InvalidDataException($"Invalid 'sandbox' value: '{pair.ToJsonString()}'. Sandbox must be string or 2-item list");
        }

        throw new InvalidDataException($"Unexpected type for 'sandbox' field: {PythonTypeName(sandbox)}");
    }

    /// <summary>Port of <c>read_files</c>: an object (or JSON string encoding one) of string values.</summary>
    public static IReadOnlyDictionary<string, string>? ReadFiles(JsonNode? files)
    {
        if (files is null)
        {
            return null;
        }

        if (files is JsonValue value && value.TryGetValue<string>(out var text))
        {
            files = JsonNode.Parse(text);
        }

        if (files is JsonObject obj && obj.All(pair => pair.Value is JsonValue v && v.TryGetValue<string>(out _)))
        {
            return obj.ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>(), StringComparer.Ordinal);
        }

        throw new InvalidDataException($"Unexpected type for 'files' field: {PythonTypeName(files)}");
    }

    /// <summary>Python <c>str()</c> of a JSON scalar: bools as True/False, numbers as written, containers as JSON.</summary>
    private static string PythonStr(JsonNode node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            if (value.TryGetValue<bool>(out var flag))
            {
                return flag ? "True" : "False";
            }
        }

        return node.ToJsonString();
    }

    private static string PythonTypeName(JsonNode? node) => node switch
    {
        null => "<class 'NoneType'>",
        JsonArray => "<class 'list'>",
        JsonObject => "<class 'dict'>",
        JsonValue value when value.TryGetValue<string>(out _) => "<class 'str'>",
        JsonValue value when value.TryGetValue<bool>(out _) => "<class 'bool'>",
        JsonValue value when value.TryGetValue<long>(out _) => "<class 'int'>",
        _ => "<class 'float'>",
    };

    private static T Deserialize<T>(JsonNode node) =>
        node.Deserialize<T>(EvalLogWriter.Options) ?? throw new InvalidDataException($"Could not read {typeof(T).Name} from dataset record.");

    private static T? DeserializeOrNull<T>(JsonNode? node) where T : class =>
        node is null ? null : Deserialize<T>(node);
}
