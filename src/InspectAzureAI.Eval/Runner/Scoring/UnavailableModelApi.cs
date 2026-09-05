using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Runner.Scoring;

/// <summary>
/// The active model of a scoring pass that was given none: Python rebuilds the model from the log header, which this port
/// (having no model registry) cannot do, so it carries the header name and fails any generate call explicitly.
/// </summary>
internal sealed class UnavailableModelApi(string modelName) : IModelApi
{
    public string ModelName => modelName;

    public int? MaxTokens() => null;

    public Task<GenerateResult> GenerateAsync(
        IReadOnlyList<ChatMessage> input,
        IReadOnlyList<ToolInfo> tools,
        ToolChoice toolChoice,
        GenerateConfig config,
        StreamHandler? onStream,
        CancellationToken cancellationToken = default) =>
        throw new PrerequisiteError(
            $"A scorer needs the model but none was supplied for re-scoring (the log was produced with '{modelName}'). "
            + "Pass ScoreLogOptions.Model to score this log.");
}
