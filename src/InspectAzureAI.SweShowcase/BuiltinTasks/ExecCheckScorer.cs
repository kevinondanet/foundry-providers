using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;

namespace InspectAzureAI.SweShowcase.BuiltinTasks;

/// <summary>
/// The showcase's check scorer, a hand-written <c>@scorer(metrics=[accuracy(), stderr()])</c>: runs the sample's
/// <c>metadata.check</c> bash command in the default sandbox, scores <c>C</c> on exit 0 and <c>I</c> otherwise,
/// and keeps the command output as the explanation. A check that times out is incorrect, not a sample error.
/// </summary>
internal static class ExecCheckScorer
{
    public const string Name = "exec_check";

    public const string MetadataKey = "check";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    public static ScorerDef Create(TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        return Scorers.Custom(Name, async (state, _, cancellationToken) =>
        {
            var check = state.Metadata.TryGetValue(MetadataKey, out var value) && value is string command && command.Length > 0
                ? command
                : throw new InvalidOperationException($"Sample {state.SampleId} has no '{MetadataKey}' command in its metadata.");
            var metadata = new Dictionary<string, object?>(StringComparer.Ordinal) { [MetadataKey] = check };
            ExecResult result;
            try
            {
                result = await SampleContext.Require().Sandbox().ExecAsync(["bash", "-c", check], timeout: limit, cancellationToken: cancellationToken);
            }
            catch (SandboxTimeoutException ex)
            {
                return new Score(ScoreConstants.Incorrect)
                {
                    Answer = state.Output.Completion,
                    Explanation = $"Check timed out after {limit.TotalSeconds:F0} seconds.\n{ex.TruncatedOutput}".Trim(),
                    Metadata = metadata,
                };
            }

            metadata["returncode"] = result.ReturnCode;
            var output = Output(result);
            return new Score(result.Success ? ScoreConstants.Correct : ScoreConstants.Incorrect)
            {
                Answer = state.Output.Completion,
                Explanation = output.Length > 0 ? output : $"exit code {result.ReturnCode}",
                Metadata = metadata,
            };
        }, Metrics.Accuracy(), Metrics.Stderr());
    }

    private static string Output(ExecResult result)
    {
        var stdout = result.Stdout.Trim();
        var stderr = result.Stderr.Trim();
        return stdout.Length > 0 && stderr.Length > 0 ? $"{stdout}\n{stderr}" : stdout.Length > 0 ? stdout : stderr;
    }
}
