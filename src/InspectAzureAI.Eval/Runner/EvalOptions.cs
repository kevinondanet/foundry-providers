namespace InspectAzureAI.Eval.Runner;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of the eval-level arguments of <c>_eval/eval.py</c> <c>eval()</c> this runner honours. The limits and
/// <see cref="FailOnError"/> override the task's own values only when set; <see cref="MaxSamples"/> bounds the
/// samples in flight; <see cref="Cleanup"/> reaches the sandbox provider so a failed sample can be inspected.
/// </summary>
public sealed record EvalOptions
{
    public required Model Model { get; init; }

    /// <summary>Port of <c>limit</c>: run only the first N samples (ignored when <see cref="SampleIds"/> is given, as in Python).</summary>
    public int? Limit { get; init; }

    /// <summary>Port of <c>sample_id</c>: the ids (ints or strings, compared textually) to run.</summary>
    public IReadOnlyList<object>? SampleIds { get; init; }

    public int? Epochs { get; init; }

    public int MaxSamples { get; init; } = 4;

    public bool? FailOnError { get; init; }

    public string LogDir { get; init; } = "logs";

    public bool Cleanup { get; init; } = true;

    public int? MessageLimit { get; init; }

    public int? TokenLimit { get; init; }

    public TimeSpan? TimeLimit { get; init; }

    public IEvalReporter? Reporter { get; init; }
}
