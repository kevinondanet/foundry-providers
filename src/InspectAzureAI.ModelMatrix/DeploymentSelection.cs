using InspectAzureAI.Provider.Foundry;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.ModelMatrix;

/// <summary>A discovered deployment with the route it takes and, when it is not run, why.</summary>
internal sealed record SelectedDeployment(FoundryDeployment Deployment, string Route, string? SkipReason)
{
    public bool Selected => SkipReason is null;
}

/// <summary>Which deployments the matrix runs, and over which route.</summary>
internal static class DeploymentSelection
{
    public const string ModelsRoute = "models";

    public const string AnthropicRoute = "anthropic";

    public const string ResponsesRoute = "responses";

    /// <summary>
    /// ARM's <c>Format</c> decides the route: Anthropic deployments speak the Messages API; OpenAI deployments without
    /// chat completions (gpt-5.4-pro, codex) or whose name prefers it (gpt-5.6*, o-series; see
    /// <see cref="OpenAIUtil.PrefersResponsesRoute"/>) speak the Responses API; everything else takes the model-inference
    /// route (the showcase's <c>claude-*</c> name heuristic is not needed when the catalog is at hand).
    /// </summary>
    public static string RouteFor(FoundryDeployment deployment) =>
        string.Equals(deployment.Format, "Anthropic", StringComparison.OrdinalIgnoreCase) ? AnthropicRoute
        : string.Equals(deployment.Format, "OpenAI", StringComparison.OrdinalIgnoreCase) && (!deployment.SupportsChat || OpenAIUtil.PrefersResponsesRoute(deployment.Name)) ? ResponsesRoute
        : ModelsRoute;

    /// <summary>The matrix's rows: every deployment (or just the <c>--only</c> ones), each either selected or skipped with a reason.</summary>
    public static IReadOnlyList<SelectedDeployment> Select(IEnumerable<FoundryDeployment> deployments, MatrixOptions options)
    {
        var only = new HashSet<string>(options.Only, StringComparer.OrdinalIgnoreCase);
        var skip = new HashSet<string>(options.Skip, StringComparer.OrdinalIgnoreCase);
        var formats = new HashSet<string>(options.Formats, StringComparer.OrdinalIgnoreCase);
        return deployments
            .Where(d => only.Count == 0 || only.Contains(d.Name))   // --only defines the matrix; the rest are not rows
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new SelectedDeployment(d, RouteFor(d), SkipReason(d, skip, formats, options.IncludeNonChat)))
            .ToList();
    }

    /// <summary>Names given to <c>--only</c> that match no deployment, so a typo is reported rather than silently running nothing.</summary>
    public static IReadOnlyList<string> UnknownOnly(IEnumerable<FoundryDeployment> deployments, MatrixOptions options)
    {
        var names = new HashSet<string>(deployments.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        return options.Only.Where(name => !names.Contains(name)).ToList();
    }

    private static string? SkipReason(FoundryDeployment deployment, HashSet<string> skip, HashSet<string> formats, bool includeNonChat)
    {
        if (skip.Contains(deployment.Name))
        {
            return "--skip";
        }

        if (formats.Count > 0 && !formats.Contains(deployment.Format))
        {
            return $"format={deployment.Format}";
        }

        if (!deployment.IsSucceeded)
        {
            return $"provisioningState={deployment.State}";
        }

        // A Responses-only OpenAI deployment (chatCompletion=false) is still runnable, over the Responses route.
        if (!deployment.SupportsChat && !includeNonChat && RouteFor(deployment) != ResponsesRoute)
        {
            return "chatCompletion=false";
        }

        return null;
    }
}
