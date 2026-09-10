using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.HveDemo;
using InspectAzureAI.HveDemo.Components;
using InspectAzureAI.HveDemo.Fake;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.CopilotCli;

namespace InspectAzureAI.HveDemo.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// The whole demo offline, one cell of the harness x framework matrix at a time: the tasks through the fake sandbox, with
/// <see cref="FakeCopilotCli"/> playing the CLI against the real sandbox agent bridge under the copilot harness and
/// <see cref="FakeHveModel"/> (a <c>ScriptedModelApi</c>) producing the tool calls that make every check pass. Needs
/// <c>python3</c>, <c>bash</c> and <c>git</c> on this host, as the checks and the commit-message setup script run for real in
/// the sample's mirror directory. Assertions marked "generic evidence rules" rely on <see cref="HveScorers"/> reading
/// Inspect's own tool events, which is how the generic harness earns artefact evidence with no Copilot CLI JSONL to read.
/// </summary>
public sealed class HveOfflineEndToEndTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-hve-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_logDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task the_suite_passes_every_check_through_the_fake_cli_and_the_real_bridge()
    {
        var (sandbox, script) = HveFake.Register();
        var options = new HveSolverOptions { CopilotVersion = HveFake.CopilotVersion };
        var task = HveTasks.Build(null, HveVariant.Default, sandbox, options);
        Assert.Equal("copilot+hve", task.Metadata!["solver"]);

        var log = await Eval.RunAsync(task, new EvalOptions { Model = FakeHveModel.Create(), LogDir = _logDir, LogFormat = LogFormat.Eval, MaxSamples = 1 });

        Assert.Equal(EvalStatus.Success, log.Status);
        var samples = log.Samples!;
        Assert.Equal(8, samples.Count);
        Assert.All(samples, sample => Assert.Null(sample.Error));

        foreach (var sample in samples)
        {
            var scores = sample.Scores!;
            Assert.Equal("C", scores[HveScorers.ExecCheckName].Text);
            Assert.Equal("C", scores[HveScorers.ArtefactReportedName].Text);
            Assert.Equal("C", scores[HveScorers.ArtefactQualityName].Text);
            Assert.True(scores[HveScorers.ArtefactUsedName].AsFloat() >= 0.5, $"{sample.Id}: {scores[HveScorers.ArtefactUsedName].Explanation}");

            // The CLI's JSONL is on the transcript as copilot_cli info events, in the namespaced shape the wire probe recorded.
            var cli = sample.Events.OfType<InfoEvent>().Where(e => e.Source == CopilotCliEvents.Source).Select(e => (JsonObject)e.Data!).ToList();
            var types = cli.Select(line => line["type"]!.GetValue<string>()).ToList();
            Assert.Contains("session.skills_loaded", types);
            Assert.Contains("user.message", types);
            Assert.Contains("assistant.message", types);
            Assert.Contains("tool.execution_start", types);
            Assert.Contains("tool.execution_complete", types);
            Assert.Equal("result", types[^1]);
            Assert.Equal(0, cli[^1]["exitCode"]!.GetValue<int>());
            Assert.Contains(cli, line => line["type"]!.GetValue<string>() == "tool.execution_start" && line["data"]!["toolName"]!.GetValue<string>() == "create");

            // Every generation went through the bridge, so the model events carry the CLI's system prompt.
            var modelEvents = sample.Events.OfType<ModelEvent>().ToList();
            Assert.NotEmpty(modelEvents);
            Assert.Contains("GitHub Copilot CLI", modelEvents[0].Input[0].Text);
            Assert.Contains("<available_skills>", modelEvents[0].Input[0].Text);
        }

        // The review samples ran with their custom agents: the agent body reached the system prompt (strong evidence).
        var functional = samples.First(s => Equals(s.Id, "review-functional-cart"));
        Assert.Equal(1.0, functional.Scores![HveScorers.ArtefactUsedName].Metadata!["agent/code-review-functional"]);
        Assert.Contains(functional.Events.OfType<ModelEvent>(), e => e.Input[0].Text.Contains("<agent_instructions>\nThe following instructions come from the selected agent's configuration.", StringComparison.Ordinal));
        // ... and, like the live CLI, the review sub-agent was offered only the tools its front matter maps to (no bash).
        Assert.All(functional.Events.OfType<ModelEvent>().Where(e => e.Tools.Count > 0), e => Assert.Equal(["create", "skill", "view"], e.Tools.Select(t => t.Name).Order()));
        // The check assets were restored from hve/checks before the check ran; the answer key never sat in the workspace.
        Assert.Contains("tests/planted.json", ((System.Collections.IEnumerable)functional.Scores[HveScorers.ExecCheckName].Metadata!["check_files"]!).Cast<object?>().Select(item => item?.ToString()));

        // The suite's metrics include the per-kind breakdown (one metric per kind, named after the kind, in the "grouped" group),
        // and each scorer keeps its own headline metric: accuracy for the check, mean for the evidence fraction.
        var check = log.Results!.Scores.First(score => score.Name == HveScorers.ExecCheckName);
        Assert.Equal(1.0, check.Metrics["accuracy"].Value);
        Assert.Contains(check.Metrics.Values, metric => metric.Name == "review" && metric.Group == "grouped" && metric.Value == 1.0);
        Assert.Contains(check.Metrics.Values, metric => metric.Name == "all" && metric.Group == "grouped");
        var used = log.Results!.Scores.First(score => score.Name == HveScorers.ArtefactUsedName);
        Assert.True(used.Metrics.ContainsKey("mean"));
        Assert.False(used.Metrics.ContainsKey("accuracy"));
        Assert.Contains(used.Metrics.Values, metric => metric.Name == "skill" && metric.Group == "grouped");

        // The fake CLI logged its bridge requests into each sample's fake sandbox: chat completions, all answered 200.
        Assert.Equal(8, script.Environments.Count);
        foreach (var environment in script.Environments)
        {
            var requests = environment.FileText(FakeCopilotCli.RequestLog)!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToList();
            Assert.True(requests.Count >= 3, "at least skill/view, create, bash and the summary turns");
            Assert.All(requests, request => Assert.Equal("/v1/chat/completions", request["path"]!.GetValue<string>()));
            Assert.All(requests, request => Assert.Equal(200, request["status"]!.GetValue<int>()));
            Assert.False(Directory.Exists(environment.MirrorDirectory), "the mirror directory is deleted with the sandbox");
        }
    }

    [Fact]
    public async Task generic_none_passes_the_checks_with_three_scorers_and_no_plugin()
    {
        var (sandbox, _) = HveFake.Register();
        var task = HveTasks.Build("skill", new HveVariant(HveHarness.Generic, HveFramework.None), sandbox, new HveSolverOptions());
        Assert.Equal(3, task.Scorers.Count);
        Assert.DoesNotContain(HveData.PluginSandboxPath, task.Dataset.First().Files!.Keys);

        var log = await Eval.RunAsync(task, new EvalOptions { Model = FakeHveModel.Create(), LogDir = _logDir, LogFormat = LogFormat.Eval, Limit = 1, MaxSamples = 1 });

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("skill-commit-message", sample.Id);
        Assert.Equal("C", sample.Scores![HveScorers.ExecCheckName].Text);
        Assert.Equal("C", sample.Scores[HveScorers.ArtefactReportedName].Text);
        Assert.False(sample.Scores.ContainsKey(HveScorers.ArtefactUsedName));
        Assert.DoesNotContain(sample.Events.OfType<InfoEvent>(), e => e.Source == CopilotCliEvents.Source);

        // No plugin, so nothing to read first: the generic agent's first tool call is the heredoc write.
        var first = sample.Events.OfType<ToolEvent>().First();
        Assert.Equal("bash", first.Function);
        Assert.StartsWith("mkdir -p", first.Arguments["cmd"]!.GetValue<string>());
    }

    [Fact]
    public async Task generic_hve_reads_the_skills_before_writing_and_scores_full_evidence()
    {
        var (sandbox, _) = HveFake.Register();
        var task = HveTasks.Build("implement", new HveVariant(HveHarness.Generic, HveFramework.Hve), sandbox, new HveSolverOptions());

        var log = await Eval.RunAsync(task, new EvalOptions { Model = FakeHveModel.Create(), Limit = 2, MaxSamples = 1, LogDir = _logDir, LogFormat = LogFormat.Eval });

        Assert.Equal(EvalStatus.Success, log.Status);
        var samples = log.Samples!;
        Assert.Equal(2, samples.Count);
        Assert.All(samples, sample => Assert.Null(sample.Error));
        Assert.Contains(samples, s => Equals(s.Id, "implement-slugify"));
        var rpi = samples.First(s => Equals(s.Id, "implement-config-loader-rpi"));

        foreach (var sample in samples)
        {
            var scores = sample.Scores!;
            Assert.Equal("C", scores[HveScorers.ExecCheckName].Text);
            Assert.Equal("C", scores[HveScorers.ArtefactReportedName].Text);
            Assert.Equal("C", scores[HveScorers.ArtefactQualityName].Text);
            Assert.DoesNotContain(sample.Events.OfType<InfoEvent>(), e => e.Source == CopilotCliEvents.Source);

            // The first tool call reads the plugin's skill files and the workspace instruction files with bash, before anything is written...
            var toolEvents = sample.Events.OfType<ToolEvent>().ToList();
            var read = toolEvents[0];
            Assert.Equal("bash", read.Function);
            var cmd = read.Arguments["cmd"]!.GetValue<string>();
            Assert.StartsWith("cat '", cmd);
            Assert.Contains("/SKILL.md", cmd);
            Assert.Contains(".instructions.md", cmd);
            // ... and the fake sandbox served /opt/hve-core/... from the provisioned plugin copy (the SKILL.md front matter came back).
            Assert.Null(read.Error);
            Assert.DoesNotContain("No such file", read.Result ?? "");
            Assert.Contains("name:", read.Result ?? "");
            Assert.Contains(toolEvents.Skip(1), e => e.Function == "bash" && e.Arguments["cmd"]!.GetValue<string>().StartsWith("mkdir -p", StringComparison.Ordinal));
        }

        // The RPI sample's briefing embeds the rpi-agent body (front matter stripped, the H1 on its own line), then basic_agent's own
        // system message, then the sample input; its read names every RPI skill and the tracking instructions.
        var modelEvent = rpi.Events.OfType<ModelEvent>().First();
        var briefing = Assert.IsType<ChatMessageSystem>(modelEvent.Input[0]);
        Assert.Contains(HveBriefing.AgentInstructionsOpen, briefing.Text);
        Assert.True(HveScorers.ContainsLine(briefing.Text, "# RPI Agent"));
        Assert.Contains("submit", Assert.IsType<ChatMessageSystem>(modelEvent.Input[1]).Text);
        Assert.IsType<ChatMessageUser>(modelEvent.Input[2]);
        var rpiRead = rpi.Events.OfType<ToolEvent>().First().Arguments["cmd"]!.GetValue<string>();
        Assert.Contains("/rpi-research/SKILL.md", rpiRead);
        Assert.Contains("/rpi-plan/SKILL.md", rpiRead);
        Assert.Contains("/rpi-implement/SKILL.md", rpiRead);
        Assert.Contains("/rpi-review/SKILL.md", rpiRead);
        Assert.Contains("copilot-tracking.instructions.md", rpiRead);
        var evidence = rpi.Scores![HveScorers.ArtefactUsedName];
        Assert.Equal(1.0, evidence.Metadata!["agent/rpi-agent"]);

        // generic evidence rules: these two components are recognised from Inspect's tool events
        Assert.Equal(1.0, evidence.Metadata["skill/rpi-research"]);
        Assert.Equal(1.0, evidence.Metadata["instructions/python-script"]);
        Assert.All(samples, sample => Assert.Equal(1.0, sample.Scores![HveScorers.ArtefactUsedName].AsFloat()));
    }

    [Fact]
    public async Task copilot_none_runs_the_cli_without_plugin_or_agent()
    {
        var (sandbox, script) = HveFake.Register();
        var task = HveTasks.Build("review", new HveVariant(HveHarness.Copilot, HveFramework.None), sandbox, new HveSolverOptions { CopilotVersion = HveFake.CopilotVersion });

        var log = await Eval.RunAsync(task, new EvalOptions { Model = FakeHveModel.Create(), LogDir = _logDir, LogFormat = LogFormat.Eval, Limit = 1, MaxSamples = 1 });

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("review-functional-cart", sample.Id);
        Assert.Null(sample.Error);
        Assert.Equal("C", sample.Scores![HveScorers.ExecCheckName].Text);
        Assert.Equal("C", sample.Scores[HveScorers.ArtefactReportedName].Text);
        Assert.False(sample.Scores.ContainsKey(HveScorers.ArtefactUsedName));

        // The CLI was launched with neither --plugin-dir nor --agent, although the sample names an agent.
        var launches = script.Calls.Where(FakeCopilotCli.IsLaunch).ToList();
        Assert.NotEmpty(launches);
        Assert.All(launches, call =>
        {
            Assert.DoesNotContain("--plugin-dir", call.Cmd);
            Assert.DoesNotContain("--agent", call.Cmd);
        });

        // So the CLI announced no skills, listed none and embedded no agent, and offered every tool.
        var cli = sample.Events.OfType<InfoEvent>().Where(e => e.Source == CopilotCliEvents.Source).Select(e => (JsonObject)e.Data!).ToList();
        var loaded = cli.First(line => line["type"]!.GetValue<string>() == "session.skills_loaded");
        Assert.Empty((JsonArray)loaded["data"]!["skills"]!);
        var modelEvents = sample.Events.OfType<ModelEvent>().ToList();
        Assert.Contains("GitHub Copilot CLI", modelEvents[0].Input[0].Text);
        Assert.DoesNotContain("<available_skills>", modelEvents[0].Input[0].Text);
        Assert.DoesNotContain("<agent_instructions>", modelEvents[0].Input[0].Text);
        Assert.All(modelEvents.Where(e => e.Tools.Count > 0), e => Assert.Equal(["bash", "create", "edit", "skill", "view"], e.Tools.Select(t => t.Name).Order()));

        // The plain briefing reached the model inside the first prompt; the model never asked for a skill and did create the artefact.
        var firstUser = modelEvents[0].Input.OfType<ChatMessageUser>().First().Text;
        Assert.Contains("You are working inside a sandboxed repository. No plugin, skill library or custom agent is installed", firstUser);
        Assert.DoesNotContain(HveBriefing.FrameworkMarker, firstUser);
        var toolStarts = cli.Where(line => line["type"]!.GetValue<string>() == "tool.execution_start").Select(line => line["data"]!["toolName"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("skill", toolStarts);
        Assert.Contains("create", toolStarts);
    }

    [Fact]
    public async Task the_program_runs_the_fake_suite_and_exits_zero()
    {
        var options = Program.Options.Parse(["--fake", "--task", "implement", "--limit", "1", "--log-dir", _logDir]);

        var (exit, output, error) = await TestSupport.RunProgramAsync(options);

        Assert.Equal(0, exit);
        Assert.Contains("status   : success (1/1 samples completed)", output);
        Assert.Contains("implement-slugify", output);
        Assert.Contains("hve_check            accuracy                     1.000", output);
        Assert.Contains("components:", output);

        // The default cell is copilot+hve, printed on both axes and in the legend, with the evidence scorer in the list and no deprecation note.
        Assert.Contains("harness  : copilot", output);
        Assert.Contains("framework: hve", output);
        Assert.Contains("solver   copilot+hve:", output);
        Assert.Contains("hve_artefact_used (transcript evidence", output);
        Assert.DoesNotContain("note: --solver", output);
        // stderr carries only the CLI installer's ProviderLogger line ("[INFO] Using copilot cli installed in sandbox: ..."), never the note.
        Assert.DoesNotContain("note: --solver", error);
        Assert.All(error.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.StartsWith("[INFO]", line));
    }

    [Theory]
    [InlineData("generic", "hve")]
    [InlineData("generic", "none")]
    [InlineData("copilot", "none")]
    public async Task the_program_runs_each_other_cell_and_prints_the_axes(string harness, string framework)
    {
        var options = Program.Options.Parse(["--fake", "--task", "skill", "--limit", "1", "--harness", harness, "--framework", framework, "--log-dir", _logDir]);

        var (exit, output, _) = await TestSupport.RunProgramAsync(options);

        Assert.Equal(0, exit);
        Assert.Contains($"harness  : {harness}", output);
        Assert.Contains($"framework: {framework}", output);
        Assert.Contains("status   : success (1/1 samples completed)", output);
        Assert.Contains($"solver   {harness}+{framework}:", output);
        Assert.Equal(framework == "hve", output.Contains("hve_artefact_used", StringComparison.Ordinal));

        if (harness == "generic" && framework == "hve")
        {
            // generic evidence rules: the mean comes from HveScorers reading Inspect's tool events
            var mean = output.Split('\n').Single(line => line.StartsWith("hve_artefact_used", StringComparison.Ordinal) && line.Contains(" mean ", StringComparison.Ordinal));
            Assert.EndsWith("1.000", mean.TrimEnd());
        }
    }

    [Fact]
    public async Task the_solver_alias_prints_a_deprecation_note()
    {
        var options = Program.Options.Parse(["--fake", "--task", "skill", "--limit", "1", "--solver", "basic", "--log-dir", _logDir]);

        var (exit, output, error) = await TestSupport.RunProgramAsync(options);

        Assert.Equal(0, exit);
        Assert.Equal(["note: --solver basic is deprecated; use --harness generic --framework none"], error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("harness  : generic", output);
        Assert.Contains("framework: none", output);
    }
}
