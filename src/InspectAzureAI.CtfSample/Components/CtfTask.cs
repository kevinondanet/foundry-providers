using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;

namespace InspectAzureAI.CtfSample.Components;

/// <summary>
/// COMPONENT: Task.
///
/// A task binds the other three components together with the environment they run in and the budget each sample
/// gets. <see cref="EvalTask"/> is the port of Inspect's <c>Task(dataset=..., solver=..., scorer=..., sandbox=...)</c>.
/// The runner then takes one sample at a time, provisions its sandbox (a Docker container built from
/// <c>ctf/Dockerfile</c>), runs the sample's <c>setup</c> script to plant the flag, drives the solver, and scores the result.
/// </summary>
internal static class CtfTask
{
    public const string Name = "ctf";

    /// <summary>Wall-clock budget of one <c>bash</c> tool call inside the container.</summary>
    public static readonly TimeSpan BashTimeout = TimeSpan.FromMinutes(2);

    public static EvalTask Build(SandboxSpec sandbox, string? category = null, int maxAttempts = 1) => new()
    {
        Name = Name,
        Version = "1",
        Dataset = CtfDataset.Load(category),
        Solver = CtfSolvers.Agent(BashTimeout, maxAttempts),
        Scorers = CtfScorers.All(),
        Sandbox = sandbox,

        // Per-sample budgets: a runaway agent is stopped and whatever it has is still scored.
        MessageLimit = 60,
        TimeLimit = TimeSpan.FromMinutes(10),

        // A demo should show every sample; a failed one is recorded in the log instead of aborting the run.
        FailOnError = false,

        Metadata = new Dictionary<string, object?>
        {
            ["suite"] = "picoCTF-style",
            ["flag_format"] = "picoCTF{...}",
        },
    };
}
