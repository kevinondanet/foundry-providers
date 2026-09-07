using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Model.Compaction;

namespace InspectAzureAI.Swe.MiniSwe;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Options of the mini-swe-agent port: the inspect_swe <c>mini_swe_agent()</c> parameters that apply to a
/// native loop plus the <c>AgentConfig</c> / <c>LocalEnvironmentConfig</c> knobs of upstream
/// <c>agents/default.py</c> and <c>environments/local.py</c>.
/// </summary>
public sealed record MiniSweAgentOptions
{
    public string Name { get; init; } = "mini-swe-agent";

    public string Description { get; init; } = "Minimal AI agent that solves software engineering tasks using bash commands.";

    /// <summary>
    /// Additional system prompt. As in inspect_swe it joins the task's system messages, which are folded into
    /// the task prompt as a <c>System instructions:</c> section; the mini-swe system template stays as is.
    /// </summary>
    public string? SystemPrompt { get; init; }

    public AgentAttempts Attempts { get; init; } = new();

    /// <summary>Model to drive the loop; null uses the sample's active model.</summary>
    public Model? Model { get; init; }

    public string? Cwd { get; init; }

    /// <summary>Extra environment for every command, layered over <see cref="MiniSweTemplates.DefaultEnvironment"/>.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public string? User { get; init; }

    /// <summary>Name of the sandbox environment to run in (null = the sample default).</summary>
    public string? Sandbox { get; init; }

    /// <summary>Port of <c>AgentConfig.step_limit</c> (0 = no limit).</summary>
    public int StepLimit { get; init; }

    /// <summary>Port of <c>AgentConfig.wall_time_limit_seconds</c> (0 = no limit).</summary>
    public int WallTimeLimitSeconds { get; init; }

    /// <summary>Port of <c>AgentConfig.max_consecutive_format_errors</c> (0 = no limit).</summary>
    public int MaxConsecutiveFormatErrors { get; init; } = 3;

    /// <summary>Port of <c>LocalEnvironmentConfig.timeout</c>.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public string SystemTemplate { get; init; } = MiniSweTemplates.System;

    public string InstanceTemplate { get; init; } = MiniSweTemplates.Instance;

    /// <summary>
    /// Prompt cache policy for every model call of the loop (Inspect's <c>generate(cache=...)</c>); null disables
    /// the cache. Port-only: upstream mini-swe-agent has no cache and inspect_swe runs it through the bridge.
    /// </summary>
    public CachePolicy? Cache { get; init; }

    /// <summary>
    /// Conversation compaction for the loop (see <see cref="Compaction.Hook"/>): the input of every model call is
    /// compacted once the strategy's threshold is reached and a context-window overflow is recovered by a forced
    /// compaction, as the Inspect react agent does. Null (the default) sends the whole trajectory, as upstream does.
    /// </summary>
    public CompactionHook? Compaction { get; init; }
}
