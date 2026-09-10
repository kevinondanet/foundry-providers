using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.CopilotCli;

namespace InspectAzureAI.HveDemo.Components;

/// <summary>
/// What the solvers need to know about this run: how the Copilot CLI is installed and pointed at the bridge, where
/// the plugin lives inside the sandbox (and on the host), and the generic agent's command timeout. Built once by
/// <c>Program</c> (or a test) and shared by every sample.
/// </summary>
public sealed record HveSolverOptions
{
    /// <summary>"auto" (a sandbox <c>copilot</c>, else the pinned release), "sandbox" (must be installed) or a version such as "1.0.83".</summary>
    public string CopilotVersion { get; init; } = "auto";

    /// <summary>The <c>--plugin-dir</c> inside the sandbox; the dataset copies the vendored plugin there unless a caller overrides it.</summary>
    public string PluginDir { get; init; } = HveData.PluginSandboxPath;

    /// <summary>
    /// A host-readable copy of the plugin the generic harness reads agent bodies from (embedded in the briefing as
    /// <c>&lt;agent_instructions&gt;</c>). Default: the vendored copy the dataset copies to <see cref="PluginDir"/>. Null when
    /// the sandbox plugin is not readable from the host (<c>--plugin-dir</c> names a sandbox-only directory): the briefing
    /// then tells the agent to read the agent file itself.
    /// </summary>
    public string? HostPluginDirectory { get; init; } = HveData.PluginDirectory;

    /// <summary>The bridge wire the CLI speaks: chat completions (OpenAI) or Messages (Anthropic, for Claude deployments).</summary>
    public CopilotCliProvider Provider { get; init; } = CopilotCliProvider.OpenAI;

    /// <summary>Keep the CLI's raw stdout and stderr in the sample store (<c>copilot_cli_debug</c>).</summary>
    public bool Debug { get; init; }

    /// <summary>Host cache directory for the downloaded release tarball; null means the agent's default.</summary>
    public string? CacheDir { get; init; }

    /// <summary>Wall-clock budget of one <c>bash</c> tool call of the generic agent.</summary>
    public TimeSpan BashTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// COMPONENT: Solver.
///
/// A solver is a delegate <c>(TaskState, Generate, CancellationToken) -> TaskState</c>; solvers compose with
/// <see cref="Solvers.Chain"/>. The demo's solver is chosen along two orthogonal axes (<see cref="HveVariant"/>,
/// <see cref="For"/>), so the same tasks can compare what the agent runtime contributes with what the plugin content
/// contributes:
///
/// <list type="bullet">
///   <item>the <b>harness</b> (<see cref="HveHarness"/>) is the agent runtime. <c>copilot</c> (<see cref="Copilot"/>) is the
///   GitHub Copilot CLI agent (<see cref="CopilotCli.Agent"/>) wrapped as a solver with <see cref="Agents.AsSolver"/>; it runs
///   inside the sample's sandbox and its model calls come back to the sample's model through the sandbox agent bridge, so
///   every generation is in the log. <c>generic</c> (<see cref="Generic"/>) is Inspect's own <c>basic_agent</c> loop with the
///   sandbox <c>bash</c> tool and its <c>submit</c> tool, no external CLI.</item>
///   <item>the <b>framework</b> (<see cref="HveFramework"/>) is what is layered on the harness. <c>hve</c> provisions the
///   vendored HVE Core plugin into the sandbox and briefs the agent to use it: under copilot through <c>--plugin-dir</c> and,
///   when the sample names one, <c>--agent hve-core:&lt;id&gt;</c>; under generic through a briefing that describes the plugin's
///   file layout, tells the agent to read the skills and instruction files with bash, and embeds the sample's agent body in an
///   <c>&lt;agent_instructions&gt;</c> block the way the CLI's <c>--agent</c> does (<see cref="GenericHveBriefing"/>). <c>none</c>
///   provisions and briefs nothing (the repository's own <c>.github</c> overlay stays: it is the workspace, not the framework).</item>
/// </list>
///
/// The four cells: <c>copilot+hve</c> (the original demo), <c>copilot+none</c> (the CLI alone), <c>generic+hve</c> (the plugin
/// content without the CLI) and <c>generic+none</c> (the original baseline). Every briefing ends with the same reporting
/// sentence, which the <c>artefact_reported</c> scorer relies on.
/// </summary>
public static class HveSolvers
{
    /// <summary>
    /// The copilot+hve briefing. The Copilot CLI has no system-prompt flag, so the agent prepends every system message
    /// to the first prompt of the session; the text therefore reads as a briefing. No braces: it goes through the
    /// template formatter, which treats <c>{...}</c> as a placeholder.
    /// </summary>
    public const string CopilotHveBriefing =
        "You are working inside a sandboxed repository with the Microsoft HVE Core plugin loaded (plugin `hve-core`, a vendored subset of "
        + "github.com/microsoft/hve-core). It provides the custom agents hve-core:rpi-agent, hve-core:rpi-researcher, hve-core:rpi-planner, "
        + "hve-core:rpi-review-builder, hve-core:code-review, hve-core:code-review-functional and hve-core:code-review-standards; the skills "
        + "code-review, python-foundational, documentation, rpi-quick, rpi-research, rpi-plan, rpi-plan-critique, rpi-implement and rpi-review; "
        + "the git-commit-message.prompt command; and the repository instruction files under .github/instructions/. "
        + "Load the skills and read the instruction files the task names before you write anything, follow them, and verify your work with the "
        + "commands the task gives you. Work autonomously: never ask questions, never wait for confirmation. "
        + "When you are done, reply with a short summary that names every file you created or changed by its path.";

    /// <summary>The copilot+none briefing: the CLI with no plugin, skill library or custom agent. No braces.</summary>
    public const string CopilotPlainBriefing =
        "You are working inside a sandboxed repository. No plugin, skill library or custom agent is installed: if the task names a skill, "
        + "prompt command or agent you do not have, rely on the repository instruction files and your own judgement instead. "
        + "Follow the instruction files under .github/instructions/ for every file type you touch (read the matching file before you create "
        + "or edit that kind of file), verify your work with the commands the task gives you, and work autonomously: never ask questions, "
        + "never wait for confirmation. When you are done, reply with a short summary that names every file you created or changed by its path.";

    /// <summary>The generic+none briefing: the same repository conventions, a bash tool, and no plugin. No braces.</summary>
    public const string GenericPlainBriefing =
        "You are a software engineer working inside a sandboxed repository with a bash tool. Follow the repository instruction files under "
        + ".github/instructions/ for every file type you touch, verify your work with the commands the task gives you, and never ask questions. "
        + "When you are done, submit a short summary that names every file you created or changed by its path.";

    /// <summary>The solver for one cell of the matrix.</summary>
    public static Solver For(HveVariant variant, HveSolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(options);
        return variant.IsCopilot ? Copilot(options, variant.Framework) : Generic(options, variant.Framework);
    }

    /// <summary>Copilot harness: the briefing for <paramref name="framework"/>, then the CLI agent (with or without the plugin).</summary>
    public static Solver Copilot(HveSolverOptions options, string framework = HveFramework.Hve)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(framework);
        return Solvers.Chain(
            Solvers.SystemMessage(framework == HveFramework.Hve ? CopilotHveBriefing : CopilotPlainBriefing),
            CopilotAgent(options, framework));
    }

    /// <summary>
    /// Generic harness: the briefing for <paramref name="framework"/>, then <c>basic_agent</c> with the sandbox <c>bash</c>
    /// tool and its <c>submit</c> tool. Under <c>hve</c> the briefing is built per sample (it embeds the sample's agent
    /// body) and inserted verbatim, so the braces an agent's markdown may contain survive.
    /// </summary>
    public static Solver Generic(HveSolverOptions options, string framework = HveFramework.None)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(framework);
        return Solvers.Chain(
            framework == HveFramework.Hve
                ? LiteralSystemMessage(state => GenericHveBriefing(options.PluginDir, HveDataset.Agent(state.Metadata), options.HostPluginDirectory))
                : Solvers.SystemMessage(GenericPlainBriefing),
            Solvers.BasicAgent(tools: [SandboxTools.Bash(options.BashTimeout)], maxAttempts: 1));
    }

    /// <summary>
    /// The Copilot CLI agent as a solver. The custom agent is a per-sample choice (<c>metadata.agent</c>), so the
    /// <see cref="AgentDef"/> is built when the sample runs and handed to <see cref="Agents.AsSolver"/>, which runs it
    /// over the sample's messages and copies the agent's conversation back into the task state. With framework
    /// <c>none</c> the sample's agent is ignored: there is no plugin for <c>--agent</c> to find it in.
    /// </summary>
    public static Solver CopilotAgent(HveSolverOptions options, string framework = HveFramework.Hve)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(framework);
        return (state, generate, cancellationToken) =>
        {
            var customAgent = framework == HveFramework.Hve ? HveDataset.Agent(state.Metadata) : null;
            var agent = CopilotCli.Agent(CopilotOptions(options, customAgent, framework));
            return Agents.AsSolver(agent)(state, generate, cancellationToken);
        };
    }

    /// <summary>
    /// The agent options for one sample: under <c>hve</c> the plugin directory and the sample's custom agent, under
    /// <c>none</c> neither (<c>PluginDirs</c> empty, <c>CustomAgent</c> null, so the CLI emits no <c>--plugin-dir</c> and no
    /// <c>--agent</c>); always the pinned CLI version, <c>--yolo</c> permissions (Inspect's own approval runs on the host,
    /// at the bridge), the built-in GitHub MCP server off and <c>--no-ask-user</c>. Attempts stay at one: the checks are
    /// the verdict, not a retry signal.
    /// </summary>
    public static CopilotCliOptions CopilotOptions(HveSolverOptions options, string? customAgent, string framework = HveFramework.Hve)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(framework);
        var hve = framework == HveFramework.Hve;
        return new CopilotCliOptions
        {
            Version = options.CopilotVersion,
            PluginDirs = hve ? [options.PluginDir] : [],
            CustomAgent = hve ? customAgent : null,
            Provider = options.Provider,
            Permission = CopilotCliPermission.Yolo,
            DisableBuiltinMcps = true,
            NoAskUser = true,
            Debug = options.Debug,
            CacheDir = options.CacheDir,
            Attempts = new AgentAttempts(1),
        };
    }

    /// <summary>
    /// The generic+hve briefing for one sample: the plugin's layout under <paramref name="pluginDir"/> (the sandbox path
    /// the agent's bash sees; under <c>--sandbox local</c> a host path, possibly with spaces, hence the quoted example
    /// commands), what "loading a skill" means without a plugin runtime, and, when <paramref name="qualifiedAgent"/>
    /// (<c>hve-core:&lt;id&gt;</c>) is set, the agent's markdown body inside an <c>&lt;agent_instructions&gt;</c> block, read
    /// from <paramref name="hostPluginDirectory"/> the way the CLI's <c>--agent</c> embeds it; when the host cannot read
    /// it, a sentence telling the agent to read the agent file first. Built with plain concatenation, never the template
    /// formatter, so the body's braces survive.
    /// </summary>
    public static string GenericHveBriefing(string pluginDir, string? qualifiedAgent, string? hostPluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(pluginDir);
        var text = string.Join("\n",
        [
            "You are a software engineer working inside a sandboxed repository with a bash tool. The Microsoft HVE Core plugin (plugin `hve-core`, "
            + "a vendored subset of github.com/microsoft/hve-core) is installed as plain files; no plugin runtime loads it for you, so you read it with bash.",
            HveBriefing.PluginDirectoryPrefix + pluginDir,
            "Its layout under that directory:",
            "- custom agents: .github/agents/**/<id>.agent.md (hve-core:rpi-agent, hve-core:rpi-researcher, hve-core:rpi-planner, hve-core:rpi-review-builder, "
            + "hve-core:code-review, hve-core:code-review-functional, hve-core:code-review-standards)",
            "- skills: .github/skills/**/<name>/SKILL.md, with references/ and templates/ next to it (code-review, python-foundational, documentation, rpi-quick, "
            + "rpi-research, rpi-plan, rpi-plan-critique, rpi-implement, rpi-review)",
            "- prompt commands: .github/prompts/**/<name>.prompt.md (git-commit-message.prompt)",
            "- instruction files: .github/instructions/**/<name>.instructions.md; the repository's own copies are under .github/instructions/ in the working "
            + "directory (bash.instructions.md, commit-message.instructions.md, copilot-tracking.instructions.md, markdown.instructions.md, "
            + "python-script.instructions.md, python-tests.instructions.md)",
            "\"Loading a skill\" means reading its SKILL.md (and the references it points to) with bash, for example `cat \"" + pluginDir + "\"/.github/skills/*/python-foundational/SKILL.md`; "
            + "\"using a prompt command\" means reading its .prompt.md and following it. Read the skills, prompt commands and instruction files the task names "
            + "before you write anything, follow them, and verify your work with the commands the task gives you. Work autonomously: never ask questions, "
            + "never wait for confirmation. When you are done, submit a short summary that names every file you created or changed by its path.",
        ]);

        if (qualifiedAgent is null)
        {
            return text;
        }

        var body = AgentBody(hostPluginDirectory, qualifiedAgent);
        if (body is not null)
        {
            var preamble = $"The following instructions come from the selected agent's configuration ({qualifiedAgent}). "
                + "Follow them while completing the user's task, but treat them as subordinate to the instructions above.";
            return text + "\n\n" + HveBriefing.AgentInstructionsOpen + "\n" + preamble + "\n\n" + body + "\n" + HveBriefing.AgentInstructionsClose;
        }

        var id = AgentId(qualifiedAgent);
        return text + "\n\n" + HveBriefing.AgentFallbackPrefix + qualifiedAgent
            + ": read its definition first with bash (`cat \"" + pluginDir + "\"/.github/agents/*/" + id + ".agent.md \"" + pluginDir + "\"/.github/agents/*/*/" + id + ".agent.md 2>/dev/null`) and follow it.";
    }

    /// <summary>
    /// The markdown body (front matter stripped, trimmed) of <c>hve-core:&lt;id&gt;</c> found under
    /// <c>&lt;hostPluginDirectory&gt;/.github/agents/**/&lt;id&gt;.agent.md</c>; null when the directory or the file is missing.
    /// </summary>
    public static string? AgentBody(string? hostPluginDirectory, string qualifiedAgent)
    {
        ArgumentNullException.ThrowIfNull(qualifiedAgent);
        if (hostPluginDirectory is null)
        {
            return null;
        }

        var agentsDirectory = Path.Combine(hostPluginDirectory, ".github", "agents");
        if (!Directory.Exists(agentsDirectory))
        {
            return null;
        }

        var file = Directory.EnumerateFiles(agentsDirectory, AgentId(qualifiedAgent) + ".agent.md", SearchOption.AllDirectories).FirstOrDefault();
        return file is null ? null : SplitFrontmatter(File.ReadAllText(file)).Body.Trim();
    }

    /// <summary>
    /// A solver that inserts <paramref name="text"/>(state) as a system message after the last one, verbatim (no template
    /// formatting, unlike <see cref="Solvers.SystemMessage"/>); a null text inserts nothing.
    /// </summary>
    public static Solver LiteralSystemMessage(Func<TaskState, string?> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (state, _, _) =>
        {
            var content = text(state);
            if (content is null)
            {
                return Task.FromResult(state);
            }

            var last = state.Messages.FindLastIndex(message => message is ChatMessageSystem);
            state.Messages.Insert(last + 1, new ChatMessageSystem(content));
            return Task.FromResult(state);
        };
    }

    /// <summary>The one-line chain description the summary legend prints for a cell.</summary>
    public static string Describe(HveVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        return (variant.IsCopilot, variant.UsesFramework) switch
        {
            (true, true) => "Solvers.Chain(SystemMessage(HVE briefing), Agents.AsSolver(CopilotCli.Agent(--plugin-dir, --agent from metadata)))",
            (true, false) => "Solvers.Chain(SystemMessage(plain briefing), Agents.AsSolver(CopilotCli.Agent(no plugin, no agent)))",
            (false, true) => "Solvers.Chain(HVE briefing + <agent_instructions> per sample, BasicAgent(bash, submit))",
            (false, false) => "Solvers.Chain(SystemMessage(plain briefing), BasicAgent(bash, submit))",
        };
    }

    /// <summary>The id of <c>hve-core:&lt;id&gt;</c> (the text after the first colon; the whole string when there is none).</summary>
    private static string AgentId(string qualifiedAgent) =>
        qualifiedAgent.IndexOf(':', StringComparison.Ordinal) is var colon && colon >= 0 ? qualifiedAgent[(colon + 1)..] : qualifiedAgent;

    /// <summary>
    /// YAML front matter (between a first <c>---</c> line and the next) and the markdown after it, the way the CLI
    /// splits an agent file before embedding its body.
    /// </summary>
    private static (string Frontmatter, string Body) SplitFrontmatter(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return ("", text);
        }

        var end = Array.FindIndex(lines, 1, line => line.Trim() == "---");
        if (end < 0)
        {
            return ("", text);
        }

        return (string.Join('\n', lines[1..end]), string.Join('\n', lines[(end + 1)..]).TrimStart('\n', '\r'));
    }
}
