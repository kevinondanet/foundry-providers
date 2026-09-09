using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.HveDemo;
using InspectAzureAI.HveDemo.Components;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.CopilotCli;

namespace InspectAzureAI.HveDemo.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Each of the four scorers in isolation, over a fake sandbox and a synthetic transcript.</summary>
public sealed class HveScorersTests
{
    [Fact]
    public async Task exec_check_scores_the_check_commands_exit_code()
    {
        var sandbox = new FakeSandboxEnvironment(cmd => cmd is ["bash", "-c", "python3 -m unittest discover -s tests -q"] ? FakeSandboxEnvironment.Ok("OK\n") : FakeSandboxEnvironment.Fail(1, "FAIL: nope"));
        var (_, scope) = TestSupport.Begin(sandbox);
        using (scope)
        {
            var pass = await HveScorers.ExecCheck().Score(TestSupport.State(TestSupport.Metadata()), new Target("t"), CancellationToken.None);
            var fail = await HveScorers.ExecCheck().Score(TestSupport.State(TestSupport.Metadata(check: "bash tests/test_rotate.sh")), new Target("t"), CancellationToken.None);
            var none = await HveScorers.ExecCheck().Score(TestSupport.State(TestSupport.Metadata(check: "")), new Target("t"), CancellationToken.None);

            Assert.Equal("C", TestSupport.ScoreText(pass));
            Assert.Equal("textkit/slug.py", pass.Answer);
            Assert.Contains("exited 0", pass.Explanation);
            Assert.Equal("I", TestSupport.ScoreText(fail));
            Assert.Contains("FAIL: nope", fail.Explanation);
            Assert.Equal(1, fail.Metadata!["exit_code"]);
            Assert.True(none.IsUnscored);
        }

        Assert.Contains(sandbox.Calls, call => call.Cmd.SequenceEqual(["bash", "-c", "bash tests/test_rotate.sh"]) && call.Timeout == HveScorers.CheckTimeout);
    }

    [Fact]
    public async Task exec_check_restores_the_check_assets_over_what_the_agent_left_before_running()
    {
        var checks = Path.Combine(Path.GetTempPath(), "inspect-hve-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(checks, "s1", "tests"));
        File.WriteAllText(Path.Combine(checks, "s1", "tests", "check.py"), "print('pristine')\n");
        File.WriteAllText(Path.Combine(checks, "s1", "tests", "planted.json"), "{}\n");
        try
        {
            var sandbox = new FakeSandboxEnvironment(_ => FakeSandboxEnvironment.Ok("OK\n"));
            await sandbox.WriteFileAsync("tests/check.py", "import sys; sys.exit(0)  # tampered\n");
            var (_, scope) = TestSupport.Begin(sandbox);
            using (scope)
            {
                var score = await HveScorers.ExecCheck(checks).Score(TestSupport.State(TestSupport.Metadata(check: "python3 tests/check.py")), new Target("t"), CancellationToken.None);

                Assert.Equal("C", TestSupport.ScoreText(score));
                Assert.Equal("print('pristine')\n", await sandbox.ReadFileAsync("tests/check.py"));
                Assert.Equal("{}\n", await sandbox.ReadFileAsync("tests/planted.json"));
                Assert.Equal(["tests/check.py", "tests/planted.json"], (IEnumerable<string>)score.Metadata!["check_files"]!);
                Assert.Equal(["tests/check.py"], (IEnumerable<string>)score.Metadata["check_files_modified"]!);
                Assert.Contains("the agent had modified tests/check.py", score.Explanation);
                Assert.Equal(["bash", "-c", "python3 tests/check.py"], sandbox.Calls.Last().Cmd);

                var other = await HveScorers.ExecCheck(checks).Score(TestSupport.State(TestSupport.Metadata(check: "true"), sampleId: "s2"), new Target("t"), CancellationToken.None);
                Assert.Empty((IEnumerable<string>)other.Metadata!["check_files"]!);
            }
        }
        finally
        {
            Directory.Delete(checks, recursive: true);
        }
    }

    [Fact]
    public void agent_body_markers_match_whole_lines_so_the_orchestrator_heading_does_not_match_its_sub_agents()
    {
        var orchestrator = HveScorers.AgentBodyMarker(HveData.PluginDirectory, "code-review");
        var functional = HveScorers.AgentBodyMarker(HveData.PluginDirectory, "code-review-functional");
        Assert.Equal("# Code Review", orchestrator);
        Assert.Equal("# Code Review Functional", functional);

        var prompt = $"<agent_instructions>\npreamble\n\n{functional}\n\nbody\n</agent_instructions>";
        var evidence = HveScorers.Evidence(["agent/code-review", "agent/code-review-functional"], [SystemPromptEvent(prompt)], HveData.PluginDirectory);

        Assert.Equal(0, evidence["agent/code-review"].Weight);
        Assert.Equal(HveScorers.StrongEvidence, evidence["agent/code-review-functional"].Weight);
        Assert.True(HveScorers.ContainsLine("a\n  # X  \nb", "# X"));
        Assert.False(HveScorers.ContainsLine("a\n# X Y\nb", "# X"));
    }

    private static ModelEvent SystemPromptEvent(string prompt) => new()
    {
        Model = "m",
        ToolChoice = ToolChoice.None,
        Config = new GenerateConfig(),
        Input = [new ChatMessageSystem(prompt), new ChatMessageUser("go")],
        Output = ModelOutput.FromContent("m", "ok"),
    };

    [Fact]
    public async Task artefact_reported_is_the_includes_scorer_with_the_artefact_as_target()
    {
        var scorer = HveScorers.ArtefactReported();
        var mentioned = await scorer.Score(TestSupport.State(TestSupport.Metadata(), completion: "I wrote textkit/slug.py and ran the tests."), new Target("ignored"), CancellationToken.None);
        var silent = await scorer.Score(TestSupport.State(TestSupport.Metadata(), completion: "All done."), new Target("ignored"), CancellationToken.None);
        var noArtefact = await scorer.Score(TestSupport.State(TestSupport.Metadata(artefact: "")), new Target("ignored"), CancellationToken.None);

        Assert.Equal("C", TestSupport.ScoreText(mentioned));
        Assert.Equal("I", TestSupport.ScoreText(silent));
        Assert.True(noArtefact.IsUnscored);
        Assert.Equal([Metrics.Accuracy().Name, Metrics.Stderr().Name], scorer.Metrics.Select(m => m.Name));
    }

    [Fact]
    public async Task artefact_quality_grades_the_artefact_read_from_the_sandbox()
    {
        var sandbox = new FakeSandboxEnvironment();
        sandbox.Files["textkit/slug.py"] = "def slugify(text: str) -> str:\n    return text\n"u8.ToArray();
        var grader = new ScriptedModelApi([ScriptedTurn.Text("The module is present.\n\nGRADE: C"), ScriptedTurn.Text("Missing.\n\nGRADE: I")]);
        var (_, scope) = TestSupport.Begin(sandbox, new Model(grader));
        using (scope)
        {
            var scorer = HveScorers.ArtefactQuality();
            var present = await scorer.Score(TestSupport.State(TestSupport.Metadata(rubric: "Deduct when there are no type hints."), completion: "Wrote textkit/slug.py"), new Target("slug.py exists"), CancellationToken.None);
            var missing = await scorer.Score(TestSupport.State(TestSupport.Metadata(artefact: "textkit/other.py")), new Target("other.py exists"), CancellationToken.None);

            Assert.Equal("C", TestSupport.ScoreText(present));
            Assert.Equal("textkit/slug.py", present.Answer);
            Assert.Equal(true, present.Metadata!["artefact_exists"]);
            Assert.Equal("I", TestSupport.ScoreText(missing));
            Assert.Equal(false, missing.Metadata!["artefact_exists"]);
        }

        // The grader saw the artefact text, the criterion and the rubric inside the default GRADE contract.
        var prompt = grader.Requests[0].Input.Last().Text;
        Assert.Contains("def slugify", prompt);
        Assert.Contains("[Criterion]: slug.py exists", prompt);
        Assert.Contains("Deduct when there are no type hints.", prompt);
        Assert.Contains("GRADE: $LETTER", prompt);
    }

    [Fact]
    public void artefact_used_evidence_weighs_invocations_over_announcements()
    {
        var components = new[] { "skill/python-foundational", "skill/documentation", "prompt/git-commit-message", "agent/rpi-agent", "agent/code-review-functional", "instructions/python-script", "instructions/bash", "instructions/markdown" };
        var marker = HveScorers.AgentBodyMarker(HveData.PluginDirectory, "rpi-agent");
        Assert.False(string.IsNullOrEmpty(marker));
        var events = new List<TranscriptEvent>
        {
            Cli("session.skills_loaded", new JsonObject { ["skills"] = new JsonArray(new JsonObject { ["name"] = "python-foundational" }, new JsonObject { ["name"] = "documentation" }, new JsonObject { ["name"] = "git-commit-message.prompt" }) }),
            Cli("tool.execution_start", new JsonObject { ["toolName"] = "skill", ["arguments"] = new JsonObject { ["skill"] = "python-foundational" } }),
            Cli("tool.execution_start", new JsonObject { ["toolName"] = "skill", ["arguments"] = new JsonObject { ["skill"] = "git-commit-message.prompt" } }),
            Cli("tool.execution_start", new JsonObject { ["toolName"] = "view", ["arguments"] = new JsonObject { ["path"] = "/workspace/.github/instructions/python-script.instructions.md" } }),
            Cli("tool.execution_start", new JsonObject { ["toolName"] = "task", ["arguments"] = new JsonObject { ["agent_type"] = "hve-core:code-review-functional" } }),
            new ModelEvent
            {
                Model = "m",
                ToolChoice = ToolChoice.None,
                Config = new GenerateConfig(),
                Input = [new ChatMessageSystem($"preamble\n| **/*.sh | .github/instructions/bash.instructions.md | shell |\n<agent_instructions>\n{marker}\n</agent_instructions>"), new ChatMessageUser("go")],
                Output = ModelOutput.FromContent("m", "ok"),
            },
        };

        var evidence = HveScorers.Evidence(components, events, HveData.PluginDirectory);

        Assert.Equal(HveScorers.StrongEvidence, evidence["skill/python-foundational"].Weight);
        Assert.Equal(HveScorers.WeakEvidence, evidence["skill/documentation"].Weight);
        Assert.Equal(HveScorers.StrongEvidence, evidence["prompt/git-commit-message"].Weight);
        Assert.Equal(HveScorers.StrongEvidence, evidence["agent/rpi-agent"].Weight);
        Assert.Equal(HveScorers.WeakEvidence, evidence["agent/code-review-functional"].Weight);
        Assert.Equal(HveScorers.StrongEvidence, evidence["instructions/python-script"].Weight);
        Assert.Equal(HveScorers.WeakEvidence, evidence["instructions/bash"].Weight);
        Assert.Equal(0, evidence["instructions/markdown"].Weight);
        Assert.Contains("never read", evidence["instructions/bash"].Note);
    }

    [Fact]
    public async Task artefact_used_scores_the_mean_evidence_of_the_samples_components()
    {
        var transcript = new Transcript();
        transcript.Info(CopilotCliEvents.Source, new JsonObject { ["type"] = "tool.execution_start", ["data"] = new JsonObject { ["toolName"] = "skill", ["arguments"] = new JsonObject { ["skill"] = "code-review" } } });
        transcript.Info("other", new JsonObject { ["type"] = "tool.execution_start", ["data"] = new JsonObject { ["toolName"] = "skill", ["arguments"] = new JsonObject { ["skill"] = "documentation" } } });
        var (_, scope) = TestSupport.Begin(new FakeSandboxEnvironment(), transcript: transcript);
        using (scope)
        {
            var scorer = HveScorers.ArtefactUsed();
            var score = await scorer.Score(TestSupport.State(TestSupport.Metadata(components: ["skill/code-review", "skill/documentation"])), new Target("t"), CancellationToken.None);
            var empty = await scorer.Score(TestSupport.State(TestSupport.Metadata()), new Target("t"), CancellationToken.None);

            Assert.Equal(0.5, ((ScoreValue.Num)score.Value).Value);
            Assert.Equal("skill/code-review", score.Answer);
            Assert.Equal(1.0, score.Metadata!["skill/code-review"]);
            Assert.Equal(0.0, score.Metadata["skill/documentation"]);
            Assert.True(empty.IsUnscored);
            Assert.Equal([Metrics.Mean().Name, Metrics.Stderr().Name], scorer.Metrics.Select(m => m.Name));
        }
    }

    [Fact]
    public void all_runs_the_four_scorers_with_the_check_first()
    {
        var names = HveScorers.All().Select(s => s.Name).ToList();
        Assert.Equal([HveScorers.ExecCheckName, HveScorers.ArtefactReportedName, HveScorers.ArtefactQualityName, HveScorers.ArtefactUsedName], names);
    }

    private static InfoEvent Cli(string type, JsonObject data) => new(CopilotCliEvents.Source, new JsonObject { ["type"] = type, ["data"] = data });
}
