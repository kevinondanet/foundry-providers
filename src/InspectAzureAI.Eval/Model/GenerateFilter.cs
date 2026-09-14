using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Model;

/// <summary>Port of <c>model/_model.py</c> <c>GenerateInput</c>: the inputs of one generation, as a filter may rewrite them.</summary>
/// <param name="Input">Messages sent to the model.</param>
/// <param name="Tools">Tools offered to the model.</param>
/// <param name="ToolChoice">Tool choice for the generation.</param>
/// <param name="Config">Generation config.</param>
public sealed record GenerateInput(IReadOnlyList<ChatMessage> Input, IReadOnlyList<ToolInfo> Tools, ToolChoice ToolChoice, GenerateConfig Config);

/// <summary>
/// The result of a <see cref="GenerateFilter"/> (Python's <c>ModelOutput | GenerateInput | None</c>): an
/// <see cref="Output"/> answers the generation without calling the model; an <see cref="Input"/> replaces what the
/// model is called with. Both convert implicitly, so a filter can return either directly.
/// </summary>
public sealed record GenerateFilterResult
{
    /// <summary>An output returned instead of generating.</summary>
    public ModelOutput? Output { get; init; }

    /// <summary>Replacement inputs for the generation.</summary>
    public GenerateInput? Input { get; init; }

    public static implicit operator GenerateFilterResult(ModelOutput output) => new() { Output = output };

    public static implicit operator GenerateFilterResult(GenerateInput input) => new() { Input = input };
}

/// <summary>
/// Port of <c>model/_model.py</c> <c>ModelGenerateFilter</c> (the Model-first form; the deprecated string-model form
/// is not ported): inspects a bridged generation before it runs. Return null to generate as requested.
/// </summary>
public delegate Task<GenerateFilterResult?> GenerateFilter(
    Model model,
    IReadOnlyList<ChatMessage> input,
    IReadOnlyList<ToolInfo> tools,
    ToolChoice toolChoice,
    GenerateConfig config,
    CancellationToken cancellationToken);
