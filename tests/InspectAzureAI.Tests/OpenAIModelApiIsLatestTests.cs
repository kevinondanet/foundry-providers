using System.Reflection;
using InspectAzureAI.Provider.OpenAI;

namespace InspectAzureAI.Tests;

/// <summary><see cref="OpenAIModelApi.IsLatest"/> and <see cref="OpenAIModelApi.IsLatestModelName"/> expose the provider's private latest-family predicate.</summary>
public class OpenAIModelApiIsLatestTests
{
    [Theory]
    [InlineData("gpt-5.6-sol", false)]
    [InlineData("gpt-4o", false)]
    [InlineData("GPT-5", false)]
    [InlineData("o3", false)]
    [InlineData("o4-mini", false)]
    [InlineData("my-o1-deployment", false)]
    [InlineData("codex-mini-latest", false)]
    [InlineData("text-embedding-3-small", false)]
    [InlineData("whisper-1", false)]
    [InlineData("dall-e-3", false)]
    [InlineData("tts-1-hd", false)]
    [InlineData("omni-moderation-latest", false)]
    [InlineData("gpt-image-1", false)]
    [InlineData("sora-2", false)]
    [InlineData("o3-deep-research", false)]
    [InlineData("frontier", true)]
    [InlineData("Next-Model", true)]
    public void is_latest_and_is_latest_model_name_agree_with_the_private_predicate(string modelName, bool expected)
    {
        using var api = new OpenAIModelApi(modelName, baseUrl: "https://api.openai.com/v1", apiKey: "test");
        var latest = typeof(OpenAIModelApi).GetProperty("Latest", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Equal(expected, (bool)latest.GetValue(api)!);
        Assert.Equal(expected, api.IsLatest);
        Assert.Equal(expected, OpenAIModelApi.IsLatestModelName(modelName));
    }
}
