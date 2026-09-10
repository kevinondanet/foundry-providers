namespace InspectAzureAI.Provider;

/// <summary>Host-owned transport and credential hooks for direct model APIs.</summary>
public sealed record DirectClientSettings
{
    public HttpClient? HttpClient { get; init; }
    public HttpMessageHandler? Handler { get; init; }
    public Func<string, string?, string?>? ApiKeyOverride { get; init; }
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
}
