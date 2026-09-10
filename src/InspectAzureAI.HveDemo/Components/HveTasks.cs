using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;

namespace InspectAzureAI.HveDemo.Components;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// COMPONENT: Task.
///
/// A task binds the other three components together with the environment they run in and the budget each sample
/// gets: <see cref="EvalTask"/> is the port of Inspect's <c>Task(dataset=..., solver=..., scorer=..., sandbox=...)</c>.
/// Four tasks are registered with <see cref="TaskAttribute"/>, one per sample kind plus the whole suite; all four
/// run in a Docker container built from <c>hve/sandbox/Dockerfile</c>, into which the runner copies the sample's
/// workspace and (under <c>--framework hve</c>) the vendored HVE Core plugin, runs the setup script, and then the solver
/// runs the cell's harness (the Copilot CLI or Inspect's generic agent loop). The registered tasks run the default cell,
/// <c>copilot+hve</c>; <c>Program</c> builds the other three through <see cref="Build"/>. The suite asks
/// <see cref="HveScorers.All"/> for a <see cref="Metrics.Grouped"/> of each scorer's own headline metric per <c>kind</c>
/// (a task-level <c>Metrics</c> override would replace every scorer's metrics and relabel the evidence fraction of
/// <c>hve_artefact_used</c> as accuracy).
/// </summary>
public static class HveTasks
{
    public const string ImplementName = "hve_implement";

    public const string ReviewName = "hve_review";

    public const string SkillName = "hve_skill";

    public const string SuiteName = "hve_suite";

    /// <summary>The task name for a kind (<c>implement</c>, <c>review</c>, <c>skill</c>) or the suite (null).</summary>
    public static string NameFor(string? kind) => kind?.ToLowerInvariant() switch
    {
        null or "" or "suite" => SuiteName,
        "implement" => ImplementName,
        "review" => ReviewName,
        "skill" => SkillName,
        var other => throw new ArgumentException($"Unknown task kind '{other}' (expected implement, review, skill or suite).", nameof(kind)),
    };

    /// <summary>Messages the bridged conversation may reach before the sample is stopped and scored as it stands.</summary>
    public const int MessageLimit = 200;

    /// <summary>Wall-clock budget per sample: the RPI lifecycle makes many bridged calls.</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(25);

    /// <summary>Implementation samples: write code or tests following the repository's HVE instruction files and skills.</summary>
    [Task(ImplementName)]
    public static EvalTask Implement() => Build("implement", HveVariant.Default, DockerSandbox(), new HveSolverOptions());

    /// <summary>Review samples: the HVE code-review sub-agents produce a findings file for a prepared diff.</summary>
    [Task(ReviewName)]
    public static EvalTask Review() => Build("review", HveVariant.Default, DockerSandbox(), new HveSolverOptions());

    /// <summary>Skill samples: a prompt command and a documentation skill drive a single artefact.</summary>
    [Task(SkillName)]
    public static EvalTask Skill() => Build("skill", HveVariant.Default, DockerSandbox(), new HveSolverOptions());

    /// <summary>Every sample, with the per-kind accuracy breakdown.</summary>
    [Task(SuiteName)]
    public static EvalTask Suite() => Build(null, HveVariant.Default, DockerSandbox(), new HveSolverOptions());

    /// <summary>The default sandbox: build <c>hve/sandbox/Dockerfile</c>, one container per sample.</summary>
    public static SandboxSpec DockerSandbox() => new("docker", HveData.SandboxDirectory);

    /// <summary>
    /// The builder behind the four tasks and <c>Program</c>: <paramref name="kind"/> filters the dataset (null is the
    /// suite), <paramref name="variant"/> is the harness x framework cell, <paramref name="sandbox"/> is where the
    /// samples run, and <paramref name="options"/> configures the solvers. <paramref name="grader"/> is the model
    /// behind <c>artefact_quality</c> (null: the active model); <paramref name="pluginSandboxPath"/> is where the
    /// dataset copies the plugin under <c>hve</c> (null: it does not, the plugin dir of <paramref name="options"/>
    /// already exists). Under <c>none</c> no plugin is provisioned and <c>hve_artefact_used</c> is left out of the
    /// scorers: there is nothing to use.
    /// </summary>
    public static EvalTask Build(string? kind, HveVariant variant, SandboxSpec sandbox, HveSolverOptions options, Model? grader = null, string? pluginSandboxPath = HveData.PluginSandboxPath)
    {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(options);
        var filter = kind is null or "" || string.Equals(kind, "suite", StringComparison.OrdinalIgnoreCase) ? null : kind.ToLowerInvariant();
        var name = NameFor(filter);

        // The suite reports every scorer's headline metric overall and per kind (Python's grouped(accuracy(), "kind")); the
        // grouping is applied before the cut, so the scorers that stay under none keep their grouped metric.
        var scorers = HveScorers.All(grader, groupBy: filter is null ? "kind" : null);
        var task = new EvalTask
        {
            Name = name,
            Version = "1",
            Dataset = HveDataset.Load(filter, variant.UsesFramework ? pluginSandboxPath : null),
            Solver = HveSolvers.For(variant, options),
            Scorers = variant.UsesFramework ? scorers : scorers.Where(scorer => scorer.Name != HveScorers.ArtefactUsedName).ToList(),
            Sandbox = sandbox,

            // Per-sample budgets: a runaway agent is stopped and whatever it produced is still checked and scored.
            MessageLimit = MessageLimit,
            TimeLimit = TimeLimit,

            // A demo should show every sample; a failed one is recorded in the log instead of aborting the run.
            FailOnError = false,

            Metadata = new Dictionary<string, object?>
            {
                ["harness"] = variant.Harness,
                ["framework"] = variant.Framework,
                ["solver"] = variant.Label,
                ["plugin"] = variant.UsesFramework ? "hve-core (vendored subset of microsoft/hve-core 3.2.2, MIT)" : "none",
                ["kind"] = filter ?? "suite",
                ["copilot_version"] = options.CopilotVersion,
            },
        };

        return task;
    }
}
