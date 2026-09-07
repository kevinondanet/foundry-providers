namespace InspectAzureAI.Provider.Core;

/// <summary>
/// A model fallback: a request served by a different model than requested (port of <c>ModelFallback</c> in
/// <c>src/inspect_ai/model/_model_output.py</c>). Carried on <see cref="ModelOutput.Fallback"/> for one
/// generate call and aggregated per sample by <c>SampleModelAccumulators</c>.
/// </summary>
/// <param name="Model">Model that was originally requested.</param>
/// <param name="FallbackModel">Model that served the request after fallback.</param>
/// <param name="Count">Number of generate calls served via this fallback (always 1 on a single output; aggregated in the sample rollup).</param>
/// <param name="Metadata">Provider-specific fallback diagnostics (per call only; never carried into the rollup).</param>
public sealed record ModelFallback(
    string Model,
    string FallbackModel,
    int Count = 1,
    IReadOnlyDictionary<string, object?>? Metadata = null);
