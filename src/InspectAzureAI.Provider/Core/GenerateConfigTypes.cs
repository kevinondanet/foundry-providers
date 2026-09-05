using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace InspectAzureAI.Provider.Core;

/// <summary>
/// Schema for the model response when using structured output (port of <c>ResponseSchema</c> in
/// <c>src/inspect_ai/model/_generate_config.py</c>). The azureai route sends it as a chat-completions
/// <c>response_format</c> of type <c>json_schema</c>; the Anthropic route sends it as <c>output_format</c>
/// under the structured-outputs beta. The output should still be validated by the caller.
/// </summary>
public sealed record ResponseSchema
{
    public ResponseSchema(string name, JsonSchema jsonSchema)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(jsonSchema);
        Name = name;
        JsonSchema = jsonSchema;
    }

    /// <summary>The name of the response schema. Must be a-z, A-Z, 0-9, or contain underscores and dashes, with a maximum length of 64.</summary>
    public string Name { get; init; }

    /// <summary>The schema for the response format, described as a JSON Schema object.</summary>
    public JsonSchema JsonSchema { get; init; }

    /// <summary>A description of what the response format is for, used by the model to determine how to respond in the format.</summary>
    public string? Description { get; init; }

    /// <summary>Whether to enable strict schema adherence when generating the output (OpenAI and Mistral only).</summary>
    public bool? Strict { get; init; }
}

/// <summary>Batch processing configuration (port of <c>BatchConfig</c>). Carried on <see cref="GenerateConfig.Batch"/>; the Foundry providers do not batch.</summary>
public sealed record BatchConfig
{
    /// <summary>Target minimum number of requests to include in each batch (default 100).</summary>
    public int? Size { get; init; }

    /// <summary>Maximum number of requests to include in each batch (provider-specific default).</summary>
    public int? MaxSize { get; init; }

    /// <summary>Maximum time (in seconds) to wait before sending a partially filled batch (default 15).</summary>
    public double? SendDelay { get; init; }

    /// <summary>Time interval (in seconds) between checking for new batch requests and batch completion status (default 15).</summary>
    public double? Tick { get; init; }

    /// <summary>Maximum number of batches to have in flight at once for a provider (default 100).</summary>
    public int? MaxBatches { get; init; }

    /// <summary>Maximum number of consecutive check failures before failing a batch (default 1000).</summary>
    public int? MaxConsecutiveCheckFailures { get; init; }
}

/// <summary>
/// Image output configuration (port of <c>ImageOutput</c>): <see cref="Options"/> carries provider-specific
/// options keyed by provider name (e.g. <c>openai</c>).
/// </summary>
public sealed record ImageOutput
{
    public IReadOnlyDictionary<string, JsonObject>? Options { get; init; }
}

/// <summary>
/// An output modality (port of <c>OutputModality = Literal["image"] | ImageOutput</c>): the literal
/// <see cref="Image"/>, or an <see cref="ImageOutput"/> configuration via <see cref="Configured"/>. Serialises as
/// Python does: the string <c>"image"</c>, or the <see cref="ImageOutput"/> object.
/// </summary>
[JsonConverter(typeof(OutputModalityConverter))]
public sealed record OutputModality
{
    private OutputModality(string kind, ImageOutput? imageOutput)
    {
        Kind = kind;
        ImageOutput = imageOutput;
    }

    /// <summary>The <c>"image"</c> literal.</summary>
    public static readonly OutputModality Image = new("image", null);

    /// <summary>An <see cref="Core.ImageOutput"/> configuration.</summary>
    public static OutputModality Configured(ImageOutput imageOutput)
    {
        ArgumentNullException.ThrowIfNull(imageOutput);
        return new OutputModality("image", imageOutput);
    }

    /// <summary>The modality kind (always <c>image</c> today).</summary>
    public string Kind { get; }

    /// <summary>The configuration when this modality was given as an <see cref="Core.ImageOutput"/>; null for the bare literal.</summary>
    public ImageOutput? ImageOutput { get; }
}

/// <summary>Module-level helpers of <c>_generate_config.py</c> over <see cref="GenerateConfig"/> fields.</summary>
public static class GenerateConfigUtil
{
    /// <summary>Port of <c>DEFAULT_BATCH_SIZE</c> (<c>src/inspect_ai/_util/constants.py</c>).</summary>
    public const int DefaultBatchSize = 100;

    /// <summary>Port of <c>has_image_output</c>.</summary>
    public static bool HasImageOutput(IReadOnlyList<OutputModality>? modalities) => ImageOutputConfig(modalities) is not null;

    /// <summary>
    /// Port of <c>image_output_config</c>: the last <see cref="ImageOutput"/> in <paramref name="modalities"/>, a
    /// default <see cref="ImageOutput"/> when only bare <c>image</c> entries exist, or null when no image output is present.
    /// </summary>
    public static ImageOutput? ImageOutputConfig(IReadOnlyList<OutputModality>? modalities)
    {
        if (modalities is null)
        {
            return null;
        }

        ImageOutput? last = null;
        var found = false;
        foreach (var modality in modalities)
        {
            if (modality.Kind == "image")
            {
                found = true;
                if (modality.ImageOutput is { } configured)
                {
                    last = configured;
                }
            }
        }

        return found ? last ?? new ImageOutput() : null;
    }

    /// <summary>
    /// Port of <c>normalized_batch_config</c>: a <see cref="BatchConfig"/> is returned as is, <c>false</c>/<c>0</c>/null
    /// disable batching (null), <c>true</c> means the default size and an integer is the batch size. Any other
    /// value is an <see cref="ArgumentException"/> (Python's annotation admits only these).
    /// </summary>
    public static BatchConfig? NormalizedBatchConfig(object? batch) => batch switch
    {
        null => null,
        BatchConfig config => config,
        bool enabled => enabled ? new BatchConfig { Size = DefaultBatchSize } : null,
        int size => size == 0 ? null : new BatchConfig { Size = size },
        long size => size == 0 ? null : new BatchConfig { Size = checked((int)size) },
        _ => throw new ArgumentException($"batch expects a bool, an int or a BatchConfig, got {batch.GetType().Name}.", nameof(batch)),
    };
}

/// <summary>System.Text.Json converter for <see cref="OutputModality"/>: the <c>"image"</c> literal or an <see cref="ImageOutput"/> object.</summary>
public sealed class OutputModalityConverter : JsonConverter<OutputModality>
{
    public override OutputModality Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var literal = reader.GetString();
            return literal == "image" ? OutputModality.Image : throw new JsonException($"Unknown output modality '{literal}'.");
        }

        var configured = JsonSerializer.Deserialize<ImageOutput>(ref reader, options) ?? throw new JsonException("An output modality must be a string or an object.");
        return OutputModality.Configured(configured);
    }

    public override void Write(Utf8JsonWriter writer, OutputModality value, JsonSerializerOptions options)
    {
        if (value.ImageOutput is { } configured)
        {
            JsonSerializer.Serialize(writer, configured, options);
        }
        else
        {
            writer.WriteStringValue(value.Kind);
        }
    }
}
