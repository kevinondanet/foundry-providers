using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Solvers;

namespace InspectAzureAI.Examples.Cache;

/// <summary>
/// A display-only addition of this port (not in <c>cache.py</c>): a solver the runner chains after
/// <see cref="CacheTasks.SolverWithCache"/> that prints, per sample and epoch, whether the model call of
/// <c>generate</c> was a cache <c>write</c> (the provider was called and its output stored) or a cache <c>read</c>
/// (served from the prompt cache, no provider call), as the log's <c>ModelEvent.cache</c> records it, plus the
/// cache directory once. Deviation: Python shows cache hits only in the log viewer.
/// </summary>
public static class CacheReport
{
    /// <summary>The line printed once per run, before the first sample line.</summary>
    public const string DirectoryPrefix = "cache dir : ";

    /// <summary>The prefix of every per-sample line.</summary>
    public const string SamplePrefix = "cache     : ";

    /// <summary>A solver that prints the cache mode of the sample's last model call to <paramref name="output"/>.</summary>
    public static Solver Solver(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var directoryPrinted = false;
        var sync = new object();
        return (state, _, _) =>
        {
            var last = SampleContext.Current?.Transcript.Events.OfType<ModelEvent>().LastOrDefault();
            lock (sync)
            {
                if (!directoryPrinted)
                {
                    directoryPrinted = true;
                    output.WriteLine($"{DirectoryPrefix}{CacheOps.CachePath(state.Model)}");
                }

                output.WriteLine($"{SamplePrefix}sample {state.SampleId} epoch {state.Epoch}: {Describe(last)}");
            }

            return Task.FromResult(state);
        };
    }

    /// <summary>The wording of one cache mode, as the per-sample line shows it.</summary>
    public static string Describe(ModelEvent? modelEvent) => modelEvent switch
    {
        null => "no model call",
        { Cache: CacheMode.Read } => "read (served from the prompt cache, no provider call)",
        { Cache: CacheMode.Write } => "write (provider called, output stored in the cache)",
        _ => "none (no cache policy)",
    };
}
