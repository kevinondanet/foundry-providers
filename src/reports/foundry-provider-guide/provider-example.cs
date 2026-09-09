using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;

public static class ProviderExample
{
    public static async Task<string> AskAsync(
        string resourceRoot,
        string deployment,
        bool useMessages,
        CancellationToken cancellationToken = default)
    {
        string root = resourceRoot.TrimEnd('/');
        IModelApi provider = useMessages
            ? new AnthropicFoundryModelApi(deployment, baseUrl: root + "/anthropic")
            : new AzureAIModelApi(deployment, baseUrl: root + "/models");

        GenerateResult result = await provider.GenerateAsync(
            [new ChatMessageUser("Reply with a short greeting.")],
            [],
            ToolChoice.Auto,
            new GenerateConfig { MaxTokens = 1024 },
            cancellationToken);

        if (result.Error is { } error) throw error;
        return result.Output?.Completion ?? "";
    }
}
