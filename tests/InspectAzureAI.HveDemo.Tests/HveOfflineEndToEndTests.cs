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
using InspectAzureAI.Swe.CopilotCli;

namespace InspectAzureAI.HveDemo.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;

/// <summary>
/// The whole demo offline: the suite task through the fake sandbox, with <see cref="FakeCopilotCli"/> playing the CLI
/// against the real sandbox agent bridge and <see cref="FakeHveModel"/> (a <c>ScriptedModelApi</c>) producing the tool
/// calls that make every check pass. Needs <c>python3</c>, <c>bash</c> and <c>git</c> on this host, as the checks and the
/// commit-message setup script run for real in the sample's mirror directory.
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
        var task = HveTasks.Build(null, HveSolvers.CopilotName, sandbox, options);

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
    public async Task the_baseline_solver_passes_the_checks_without_touching_the_plugin()
    {
        var (sandbox, _) = HveFake.Register();
        var task = HveTasks.Build("skill", HveSolvers.BasicName, sandbox, new HveSolverOptions());

        var log = await Eval.RunAsync(task, new EvalOptions { Model = FakeHveModel.Create(), LogDir = _logDir, LogFormat = LogFormat.Eval, Limit = 1, MaxSamples = 1 });

        Assert.Equal(EvalStatus.Success, log.Status);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("skill-commit-message", sample.Id);
        Assert.Equal("C", sample.Scores![HveScorers.ExecCheckName].Text);
        Assert.Equal("C", sample.Scores[HveScorers.ArtefactReportedName].Text);
        Assert.Equal(0.0, sample.Scores[HveScorers.ArtefactUsedName].AsFloat());
        Assert.DoesNotContain(sample.Events.OfType<InfoEvent>(), e => e.Source == CopilotCliEvents.Source);
        Assert.Contains(sample.Events.OfType<ToolEvent>(), e => e.Function == "bash");
    }

    [Fact]
    public async Task the_program_runs_the_fake_suite_and_exits_zero()
    {
        var options = Program.Options.Parse(["--fake", "--task", "implement", "--limit", "1", "--log-dir", _logDir]);
        var stdout = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        int exit;
        try
        {
            exit = await Program.RunAsync(options, CancellationToken.None);
        }
        finally
        {
            Console.SetOut(stdout);
        }

        var output = writer.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("status   : success (1/1 samples completed)", output);
        Assert.Contains("implement-slugify", output);
        Assert.Contains("hve_check            accuracy                     1.000", output);
        Assert.Contains("components:", output);
    }
}
