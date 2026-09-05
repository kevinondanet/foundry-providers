namespace InspectAzureAI.Eval.Model.Cost;

/// <summary>
/// Port of <c>model/_model_data/model_data.py</c> <c>ModelCost</c>: model cost in dollars per million tokens.
/// </summary>
/// <param name="Input">Price per million input tokens.</param>
/// <param name="Output">Price per million output tokens.</param>
/// <param name="InputCacheWrite">
/// Price per million input tokens written to cache, at the provider's default-TTL rate (for Anthropic the
/// 5-minute rate). Longer cache TTLs are adjusted at cost computation time from the configured TTL, so a
/// longer-TTL rate must not be pre-baked into this field or it is double-applied.
/// </param>
/// <param name="InputCacheRead">Price per million input tokens read from cache.</param>
public sealed record ModelCost(double Input, double Output, double InputCacheWrite, double InputCacheRead);
