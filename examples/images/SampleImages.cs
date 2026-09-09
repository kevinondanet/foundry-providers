using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Examples.Images;

/// <summary>
/// A local port of the trusted pre-run media step of <c>_eval/task/images.py</c> (<c>sample_with_base64_content</c>
/// over <c>materialize_media</c>) for this example: the dataset's user-message <see cref="ContentImage"/> parts that
/// reference an existing local file become <c>data:image/...;base64,...</c> URIs, which is what the Foundry routes
/// require of an image before it is sent (<c>InlineMedia.InlineMediaDataUri</c> rejects a bare path with "Media
/// references must be materialized before model submission"). Deviation: the eval engine of this repository has no
/// port of that step yet, so the example materialises its own dataset when the task is built rather than the
/// engine doing it for every task before the run; images that are already data URIs, remote URLs or missing files
/// are left as they are.
/// </summary>
internal static class SampleImages
{
    /// <summary>A copy of <paramref name="dataset"/> (same name and location) whose local image files are inlined as data URIs.</summary>
    public static IDataset Materialize(IDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return new MemoryDataset(dataset.Select(Materialize), dataset.Name, dataset.Location, dataset.Shuffled);
    }

    /// <summary><paramref name="sample"/> with the image parts of its user messages inlined.</summary>
    public static Sample Materialize(Sample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Input.Messages is not { } messages)
        {
            return sample;
        }

        return sample with { Input = messages.Select(Materialize).ToArray() };
    }

    private static ChatMessage Materialize(ChatMessage message)
    {
        if (message is not ChatMessageUser user || user.Content.IsString)
        {
            return message;
        }

        var items = user.Content.Items!.Select(Content (item) => item is ContentImage image ? image with { Image = Materialize(image.Image) } : item);
        return user with { Content = MessageContent.FromItems(items) };
    }

    /// <summary>The data URI of a local image file; anything else (a data URI, a URL, a missing file) unchanged.</summary>
    public static string Materialize(string image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length == 0 || InlineMedia.IsDataUri(image))
        {
            return image;
        }

        try
        {
            if (!File.Exists(image))
            {
                return image;
            }

            return InlineMedia.AsDataUri(MimeType(image), Convert.ToBase64String(File.ReadAllBytes(image)));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return image;
        }
    }

    /// <summary>The image MIME type by file extension (Python's <c>mimetypes.guess_type</c> for the common raster formats), <c>image/png</c> otherwise.</summary>
    public static string MimeType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/png",
    };
}
