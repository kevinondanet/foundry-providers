using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.OpenAI;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model;

/// <summary>Port of <c>get_model()</c> for this repo's Foundry providers: picks the route and wraps the api in a <see cref="Model"/>.</summary>
public static class FoundryModels
{
    public const string ModelVar = "INSPECT_AZUREAI_MODEL";

    public const string DefaultModel = "gpt-5.4-mini";

    /// <summary>The three routes: <c>models</c> (chat completions), <c>anthropic</c> (Messages) and <c>responses</c> (OpenAI Responses API).</summary>
    public static readonly IReadOnlyList<string> Routes = ["models", "anthropic", "responses"];

    /// <summary>
    /// Route selection: an explicit route wins; otherwise names starting with "claude" (case-insensitive) go to
    /// <see cref="AnthropicFoundryModelApi"/>, names Foundry serves on the Responses API (gpt-5.6*, *-pro, codex,
    /// the o-series; <see cref="OpenAIUtil.PrefersResponsesRoute"/>) go to <see cref="OpenAIResponsesModelApi"/>,
    /// everything else to <see cref="AzureAIModelApi"/>. Model name default: $INSPECT_AZUREAI_MODEL, else "gpt-5.4-mini".
    /// </summary>
    public static IModelApi CreateApi(
        string? model,
        string? route = null,
        object? streaming = null,
        IReadOnlyDictionary<string, object?>? modelArgs = null,
        AzureAIClientSettings? settings = null,
        GenerateConfig? config = null)
    {
        model ??= Environment.GetEnvironmentVariable(ModelVar);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = DefaultModel;
        }

        if (route is not null && !Routes.Contains(route, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"route expects models, anthropic or responses, got '{route}'", nameof(route));
        }

        return RouteFor(model, route) switch
        {
            "anthropic" => new AnthropicFoundryModelApi(model, config: config, streaming: streaming, modelArgs: modelArgs, settings: settings),
            "responses" => new OpenAIResponsesModelApi(model, config: config, streaming: streaming, modelArgs: modelArgs, settings: settings),
            _ => new AzureAIModelApi(model, config: config, streaming: streaming, modelArgs: modelArgs, settings: settings),
        };
    }

    /// <summary>The route a model name takes: the explicit <paramref name="route"/> (normalised), else the name heuristics described on <see cref="CreateApi"/>.</summary>
    public static string RouteFor(string model, string? route = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (route is not null)
        {
            return route.ToLowerInvariant();
        }

        if (model.StartsWith("claude", StringComparison.OrdinalIgnoreCase))
        {
            return "anthropic";
        }

        return OpenAIUtil.PrefersResponsesRoute(model) ? "responses" : "models";
    }

    public static Model Create(
        string? model,
        GenerateConfig? config = null,
        string? route = null,
        object? streaming = null,
        IReadOnlyDictionary<string, object?>? modelArgs = null,
        AzureAIClientSettings? settings = null,
        ModelRetryOptions? retry = null) =>
        new(CreateApi(model, route, streaming, modelArgs, settings, config), config, retry);
}
