using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.HveDemo;
using InspectAzureAI.HveDemo.Components;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.HveDemo.Tests;

/// <summary>
/// The solver component across the matrix: the generic+hve briefing (layout, plugin directory line, agent body embedding
/// and its fallback), the literal system-message solver it rides on, the per-cell chain descriptions, and what
/// <see cref="HveTasks.Build"/> provisions and scores under <c>none</c> and <c>hve</c>.
/// </summary>
public sealed class HveSolversTests
{
    private static readonly string[] OverlayInstructions =
    [
        "bash.instructions.md",
        "commit-message.instructions.md",
        "copilot-tracking.instructions.md",
        "markdown.instructions.md",
        "python-script.instructions.md",
        "python-tests.instructions.md",
    ];

    [Fact]
    public void generic_hve_briefing_names_the_layout_and_the_plugin_directory()
    {
        var text = HveSolvers.GenericHveBriefing("/opt/hve-core", null, HveData.PluginDirectory);

        Assert.Contains(HveBriefing.FrameworkMarker, text);
        Assert.Contains(HveBriefing.PluginDirectoryPrefix + "/opt/hve-core", text);
        Assert.Contains(".github/skills/**/<name>/SKILL.md", text);
        Assert.Contains(".github/agents/**/<id>.agent.md", text);
        Assert.Contains(".github/prompts/**/<name>.prompt.md", text);
        Assert.All(OverlayInstructions, file => Assert.Contains(file, text));
        Assert.Contains("cat \"/opt/hve-core\"/.github/skills/*/python-foundational/SKILL.md", text);
        Assert.Equal("/opt/hve-core", HveBriefing.PluginDirectoryIn(text));

        // No braces (the layout text could go through the template formatter unharmed) and no agent block without an agent.
        Assert.DoesNotContain("{", text);
        Assert.DoesNotContain(HveBriefing.AgentInstructionsOpen, text);
        Assert.DoesNotContain(HveBriefing.AgentFallbackPrefix, text);
    }

    [Fact]
    public void plugin_directory_line_accepts_paths_with_spaces()
    {
        var text = HveSolvers.GenericHveBriefing("/Users/a b/hve/plugin", null, null);
        Assert.Equal("/Users/a b/hve/plugin", HveBriefing.PluginDirectoryIn(text));
    }

    [Fact]
    public void generic_hve_briefing_embeds_the_agent_body_with_the_front_matter_stripped()
    {
        var text = HveSolvers.GenericHveBriefing("/opt/hve-core", "hve-core:code-review-functional", HveData.PluginDirectory);

        Assert.Contains(HveBriefing.AgentInstructionsOpen, text);
        Assert.Contains(HveBriefing.AgentInstructionsClose, text);
        Assert.Contains("configuration (hve-core:code-review-functional)", text);
        var marker = HveScorers.AgentBodyMarker(HveData.PluginDirectory, "code-review-functional")!;
        Assert.Equal("# Code Review Functional", marker);
        Assert.True(HveScorers.ContainsLine(text, marker));
        Assert.DoesNotContain("name: Code Review Functional", text);
        Assert.DoesNotContain(HveBriefing.AgentFallbackPrefix, text);

        // The block is the CLI's shape: the preamble, a blank line, then the body starting with its H1 on its own line.
        var lines = text.Split('\n');
        var open = Array.IndexOf(lines, HveBriefing.AgentInstructionsOpen);
        Assert.True(open >= 0);
        Assert.StartsWith("The following instructions come from the selected agent's configuration (hve-core:code-review-functional).", lines[open + 1]);
        Assert.Equal("", lines[open + 2]);
        Assert.Equal("# Code Review Functional", lines[open + 3]);
        Assert.Equal(HveBriefing.AgentInstructionsClose, lines[^1]);
    }

    [Fact]
    public void generic_hve_briefing_falls_back_when_the_host_cannot_read_the_agent()
    {
        var unreadable = HveSolvers.GenericHveBriefing("/opt/hve-core", "hve-core:rpi-agent", null);
        Assert.Contains(HveBriefing.AgentFallbackPrefix + "hve-core:rpi-agent", unreadable);
        Assert.Contains("\"/opt/hve-core\"/.github/agents/*/rpi-agent.agent.md", unreadable);
        Assert.Contains("\"/opt/hve-core\"/.github/agents/*/*/rpi-agent.agent.md", unreadable);
        Assert.DoesNotContain(HveBriefing.AgentInstructionsOpen, unreadable);

        var unknown = HveSolvers.GenericHveBriefing("/opt/hve-core", "hve-core:nobody", HveData.PluginDirectory);
        Assert.Contains(HveBriefing.AgentFallbackPrefix + "hve-core:nobody", unknown);
        Assert.DoesNotContain(HveBriefing.AgentInstructionsOpen, unknown);
    }

    [Fact]
    public void agent_body_returns_null_for_missing_directory_or_agent()
    {
        Assert.Null(HveSolvers.AgentBody(null, "hve-core:rpi-agent"));
        Assert.Null(HveSolvers.AgentBody(Path.GetTempPath(), "hve-core:rpi-agent"));
        Assert.StartsWith("# RPI Agent", HveSolvers.AgentBody(HveData.PluginDirectory, "hve-core:rpi-agent")!);
        Assert.StartsWith("# RPI Agent", HveSolvers.AgentBody(HveData.PluginDirectory, "rpi-agent")!);
    }

    [Fact]
    public async Task agent_body_survives_braces_literally()
    {
        var temp = Path.Combine(Path.GetTempPath(), "inspect-hve-tests", Guid.NewGuid().ToString("N"));
        var agents = Path.Combine(temp, ".github", "agents", "x");
        Directory.CreateDirectory(agents);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agents, "braces.agent.md"), "---\nname: Braces\n---\n# Braces\n{{ not a template }} and {kind}\n");
            var state = TestSupport.State(TestSupport.Metadata(agent: "hve-core:braces"));

            // The solver ignores generate; the briefing is inserted verbatim, so the body's braces are not template placeholders.
            await HveSolvers.LiteralSystemMessage(s => HveSolvers.GenericHveBriefing(temp, HveDataset.Agent(s.Metadata), temp))(state, null!, CancellationToken.None);

            var system = Assert.IsType<ChatMessageSystem>(state.Messages[0]);
            Assert.Contains("{{ not a template }}", system.Text);
            Assert.Contains("{kind}", system.Text);
            Assert.True(HveScorers.ContainsLine(system.Text, "# Braces"));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task literal_system_message_inserts_after_the_last_system_message_and_skips_null()
    {
        var state = new TaskState("test-model", "s1", 1, "u", [new ChatMessageSystem("a"), new ChatMessageUser("u")]);

        await HveSolvers.LiteralSystemMessage(_ => "b")(state, null!, CancellationToken.None);
        Assert.Equal(["a", "b", "u"], state.Messages.Select(message => message.Text));
        Assert.IsType<ChatMessageSystem>(state.Messages[1]);

        await HveSolvers.LiteralSystemMessage(_ => null)(state, null!, CancellationToken.None);
        Assert.Equal(3, state.Messages.Count);

        var bare = new TaskState("test-model", "s2", 1, "u", [new ChatMessageUser("u")]);
        await HveSolvers.LiteralSystemMessage(_ => "first")(bare, null!, CancellationToken.None);
        Assert.Equal("first", Assert.IsType<ChatMessageSystem>(bare.Messages[0]).Text);
    }

    [Fact]
    public void describe_names_the_chain_for_each_cell()
    {
        Assert.Equal("Solvers.Chain(SystemMessage(HVE briefing), Agents.AsSolver(CopilotCli.Agent(--plugin-dir, --agent from metadata)))", HveSolvers.Describe(new HveVariant(HveHarness.Copilot, HveFramework.Hve)));
        Assert.Equal("Solvers.Chain(SystemMessage(plain briefing), Agents.AsSolver(CopilotCli.Agent(no plugin, no agent)))", HveSolvers.Describe(new HveVariant(HveHarness.Copilot, HveFramework.None)));
        Assert.Equal("Solvers.Chain(HVE briefing + <agent_instructions> per sample, BasicAgent(bash, submit))", HveSolvers.Describe(new HveVariant(HveHarness.Generic, HveFramework.Hve)));
        Assert.Equal("Solvers.Chain(SystemMessage(plain briefing), BasicAgent(bash, submit))", HveSolvers.Describe(new HveVariant(HveHarness.Generic, HveFramework.None)));
    }

    [Fact]
    public void build_under_none_has_three_scorers_and_no_plugin_files()
    {
        var task = HveTasks.Build("skill", new HveVariant(HveHarness.Copilot, HveFramework.None), new SandboxSpec("fake"), new HveSolverOptions());

        Assert.Equal([HveScorers.ExecCheckName, HveScorers.ArtefactReportedName, HveScorers.ArtefactQualityName], task.Scorers.Select(scorer => scorer.Name));
        Assert.DoesNotContain(HveData.PluginSandboxPath, task.Dataset.First().Files!.Keys);
        Assert.Equal("none", task.Metadata!["plugin"]);
        Assert.Equal("none", task.Metadata["framework"]);
        Assert.Equal("copilot", task.Metadata["harness"]);
        Assert.Equal("copilot+none", task.Metadata["solver"]);

        // The grouping is applied before the cut, so the suite's remaining scorers keep their per-kind metric.
        var suite = HveTasks.Build(null, new HveVariant(HveHarness.Generic, HveFramework.None), new SandboxSpec("fake"), new HveSolverOptions());
        Assert.Equal(3, suite.Scorers.Count);
        Assert.Contains("grouped", suite.Scorers[0].Metrics.Select(metric => metric.Name));
    }

    [Fact]
    public void build_under_hve_keeps_four_scorers_and_the_plugin_files()
    {
        var task = HveTasks.Build("implement", new HveVariant(HveHarness.Generic, HveFramework.Hve), new SandboxSpec("fake"), new HveSolverOptions());

        Assert.Equal(4, task.Scorers.Count);
        Assert.Equal(HveScorers.ArtefactUsedName, task.Scorers[3].Name);
        Assert.Contains(HveData.PluginSandboxPath, task.Dataset.First().Files!.Keys);
        Assert.StartsWith("hve-core", (string)task.Metadata!["plugin"]!);
        Assert.Equal("generic+hve", task.Metadata["solver"]);
    }
}
