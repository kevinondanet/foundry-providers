using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Eval.Dataset;

/// <summary>
/// Port of <c>dataset/_dataset.py</c> <c>Sample</c>: one input (a string or a prepared message list) with
/// its target, optional choices, metadata, per-sample sandbox, files to copy into the sandbox and a setup script.
/// </summary>
public sealed record Sample(SampleInput Input)
{
    /// <summary>Python <c>int | str | None</c>; assigned 1-based by the loaders when <c>autoId</c> is requested.</summary>
    public object? Id { get; init; }

    public Target Target { get; init; } = Target.Empty;

    public IReadOnlyList<string>? Choices { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    public SandboxSpec? Sandbox { get; init; }

    /// <summary>Destination path in the sandbox → a file path (absolute once loaded from a file), a data URI, or inline text.</summary>
    public IReadOnlyDictionary<string, string>? Files { get; init; }

    /// <summary>Setup script run in the default sandbox (a file path once loaded from a file, or inline bash).</summary>
    public string? Setup { get; init; }

    /// <summary>Port of <c>sample_input_len</c>, the default <see cref="IDataset.Sort"/> key: input length in characters.</summary>
    public int InputLength => Input.IsText ? (Input.Text ?? "").Length : Input.Messages!.Sum(m => m.Text.Length);
}
