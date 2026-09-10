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

/// <summary>Each of the four scorers in isolation, over a fake sandbox and a synthetic transcript: the Copilot CLI's JSONL trail and the generic harness's tool events.</summary>
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

    [Fact]
    public void artefact_used_evidence_reads_generic_bash_tool_events()
    {
        // The generic harness leaves no CLI JSONL: its trail is Inspect's own ToolEvents of the bash tool (Arguments["cmd"])
        // and the B.4 briefing in the model events. Sample 1 reads a skill, an instruction file and a prompt command in one
        // command; sample 2 is the heredoc write; sample 3 the submit, whose answer names skills it never read.
        var components = new[] { "skill/python-foundational", "prompt/git-commit-message", "instructions/python-script", "skill/documentation", "skill/rpi-plan", "skill/rpi-plan-critique", "instructions/markdown", "instructions/python-tests" };
        var events = new List<TranscriptEvent>
        {
            Bash("1", "cat '/opt/hve-core/.github/skills/coding-standards/python-foundational/SKILL.md' '.github/instructions/python-script.instructions.md' '/opt/hve-core/.github/prompts/hve-core/git-commit-message.prompt.md'", "---\nname: python-foundational\n---\n"),
            Bash("2", "cat > textkit/slug.py <<'EOF'\n# uses documentation\nEOF\n", ""),
            new ToolEvent("3", "submit", new JsonObject { ["answer"] = "I used the documentation skill and rpi-plan" }, ""),
            SystemPromptEvent("HVE plugin directory: /opt/hve-core\n- skills: .github/skills/**/<name>/SKILL.md (code-review, python-foundational, documentation, rpi-quick, rpi-research, rpi-plan, rpi-plan-critique, rpi-implement, rpi-review)\n- prompt commands: .github/prompts/**/<name>.prompt.md (git-commit-message.prompt)\n- instruction files: .github/instructions/**/<name>.instructions.md (markdown.instructions.md, python-script.instructions.md)"),
        };

        var evidence = HveScorers.Evidence(components, events, HveData.PluginDirectory);

        Assert.Equal(HveScorers.StrongEvidence, evidence["skill/python-foundational"].Weight);
        Assert.Equal("the python-foundational skill files were read by a tool call", evidence["skill/python-foundational"].Note);
        Assert.Equal(HveScorers.StrongEvidence, evidence["prompt/git-commit-message"].Weight);
        Assert.Equal("git-commit-message.prompt.md was read by a tool call", evidence["prompt/git-commit-message"].Note);
        Assert.Equal(HveScorers.StrongEvidence, evidence["instructions/python-script"].Weight);
        Assert.Equal("python-script.instructions.md was read by a tool call", evidence["instructions/python-script"].Note);

        // Listed in the briefing only: weak. submit is not a read tool and a heredoc mention is not a path.
        Assert.Equal(HveScorers.WeakEvidence, evidence["skill/documentation"].Weight);
        Assert.Equal("'documentation' was listed in the briefing but never loaded or read", evidence["skill/documentation"].Note);
        Assert.Equal(HveScorers.WeakEvidence, evidence["skill/rpi-plan"].Weight);
        Assert.Equal(HveScorers.WeakEvidence, evidence["skill/rpi-plan-critique"].Weight);
        Assert.Equal(HveScorers.WeakEvidence, evidence["instructions/markdown"].Weight);
        Assert.Contains("never read", evidence["instructions/markdown"].Note);
        Assert.Equal(0, evidence["instructions/python-tests"].Weight);
        Assert.Equal("no trace of python-tests.instructions.md", evidence["instructions/python-tests"].Note);
    }

    [Fact]
    public void skill_token_match_does_not_credit_a_prefix()
    {
        var evidence = HveScorers.Evidence(["skill/rpi-plan", "skill/rpi-plan-critique"], [SystemPromptEvent("skills: rpi-plan-critique")], HveData.PluginDirectory);

        Assert.Equal(0, evidence["skill/rpi-plan"].Weight);
        Assert.Equal("no trace of 'rpi-plan'", evidence["skill/rpi-plan"].Note);
        Assert.Equal(HveScorers.WeakEvidence, evidence["skill/rpi-plan-critique"].Weight);

        // A path segment is also whole: reading rpi-plan-critique's files is not reading rpi-plan's.
        var read = HveScorers.Evidence(["skill/rpi-plan", "skill/rpi-plan-critique"], [Bash("1", "cat /opt/hve-core/.github/skills/rpi/rpi-plan-critique/SKILL.md /opt/hve-core/.github/skills/rpi/rpi-plan-critique/references/a.md", "")], HveData.PluginDirectory);
        Assert.Equal(0, read["skill/rpi-plan"].Weight);
        Assert.Equal(HveScorers.StrongEvidence, read["skill/rpi-plan-critique"].Weight);
    }

    [Fact]
    public void agent_evidence_from_the_generic_briefing()
    {
        var embedded = HveScorers.Evidence(["agent/rpi-agent"], [SystemPromptEvent("<agent_instructions>\npreamble\n\n# RPI Agent\nbody\n</agent_instructions>")], HveData.PluginDirectory)["agent/rpi-agent"];
        var fallback = HveScorers.Evidence(["agent/rpi-agent"], [SystemPromptEvent(HveBriefing.AgentFallbackPrefix + "hve-core:rpi-agent: read its definition first")], HveData.PluginDirectory)["agent/rpi-agent"];
        var read = HveScorers.Evidence(["agent/rpi-agent"], [Bash("4", "cat /opt/hve-core/.github/agents/hve-core/rpi-agent.agent.md", "")], HveData.PluginDirectory)["agent/rpi-agent"];
        var none = HveScorers.Evidence(["agent/rpi-agent"], [], HveData.PluginDirectory)["agent/rpi-agent"];

        Assert.Equal(HveScorers.StrongEvidence, embedded.Weight);
        Assert.Equal("the hve-core:rpi-agent agent body was embedded in the system prompt", embedded.Note);
        Assert.Equal(HveScorers.WeakEvidence, fallback.Weight);
        Assert.Equal("the briefing asked for hve-core:rpi-agent but its body was neither embedded nor read", fallback.Note);
        Assert.Equal(HveScorers.StrongEvidence, read.Weight);
        Assert.Equal("rpi-agent.agent.md was read by a tool call", read.Note);
        Assert.Equal(0, none.Weight);
        Assert.Equal("no trace of hve-core:rpi-agent", none.Note);

        // The fallback names one agent; a sibling with the same prefix is not credited.
        var sibling = HveScorers.Evidence(["agent/rpi-researcher"], [SystemPromptEvent(HveBriefing.AgentFallbackPrefix + "hve-core:rpi-agent: read its definition first")], HveData.PluginDirectory)["agent/rpi-researcher"];
        Assert.Equal(0, sibling.Weight);
    }

    [Fact]
    public void cli_tool_events_count_like_the_cli_tool_starts()
    {
        // The bridge records the CLI's tool calls as ToolEvents too; the rules read them like tool.execution_start lines.
        var skill = HveScorers.Evidence(["skill/code-review"], [new ToolEvent("5", "skill", new JsonObject { ["skill"] = "code-review" }, "")], HveData.PluginDirectory)["skill/code-review"];
        var view = HveScorers.Evidence(["instructions/bash"], [new ToolEvent("6", "view", new JsonObject { ["path"] = "/workspace/.github/instructions/bash.instructions.md" }, "")], HveData.PluginDirectory)["instructions/bash"];
        var task = HveScorers.Evidence(["agent/code-review-functional"], [new ToolEvent("7", "task", new JsonObject { ["agent_type"] = "hve-core:code-review-functional" }, "")], HveData.PluginDirectory)["agent/code-review-functional"];
        var cliBash = HveScorers.Evidence(["skill/documentation"], [new ToolEvent("8", "bash", new JsonObject { ["command"] = "cat /opt/hve-core/.github/skills/docs/documentation/SKILL.md", ["description"] = "read the skill" }, "")], HveData.PluginDirectory)["skill/documentation"];

        Assert.Equal(HveScorers.StrongEvidence, skill.Weight);
        Assert.Equal("the skill tool loaded 'code-review'", skill.Note);
        Assert.Equal(HveScorers.StrongEvidence, view.Weight);
        Assert.Equal("bash.instructions.md was read by a tool call", view.Note);
        Assert.Equal(HveScorers.WeakEvidence, task.Weight);
        Assert.Equal(HveScorers.StrongEvidence, cliBash.Weight);
    }

    [Fact]
    public void argument_text_joins_string_values_with_newlines_and_ignores_json_escaping()
    {
        Assert.Equal("/x/\nSKILL.md", HveScorers.ArgumentText(new JsonObject { ["command"] = "/x/", ["description"] = "SKILL.md" }));
        Assert.Equal("", HveScorers.ArgumentText(null));
        Assert.Equal("", HveScorers.ArgumentText(new JsonObject()));
        Assert.Equal("a\nb\nc", HveScorers.ArgumentText(new JsonObject { ["outer"] = new JsonObject { ["inner"] = "a", ["n"] = 1 }, ["list"] = new JsonArray("b", new JsonObject { ["deep"] = "c" }), ["flag"] = true }));

        // Two values never join into one path, and an apostrophe stays an apostrophe (ToJsonString would write \u0027).
        var split = HveScorers.Evidence(["skill/x"], [new ToolEvent("7", "bash", new JsonObject { ["command"] = "/x/", ["description"] = "SKILL.md" }, "")], HveData.PluginDirectory)["skill/x"];
        var apostrophe = HveScorers.Evidence(["instructions/it's"], [new ToolEvent("8", "view", new JsonObject { ["path"] = "/workspace/.github/instructions/it's.instructions.md" }, "")], HveData.PluginDirectory)["instructions/it's"];

        Assert.Equal(0, split.Weight);
        Assert.Equal(HveScorers.StrongEvidence, apostrophe.Weight);
    }

    [Fact]
    public async Task artefact_used_scores_a_generic_transcript_from_the_sample_context()
    {
        // The scorer reads the ambient sample transcript, where the engine records the bash ToolEvents and model events.
        var transcript = new Transcript();
        transcript.Add(SystemPromptEvent("HVE plugin directory: /opt/hve-core\n- skills: (python-foundational, documentation)\n- instruction files: (python-script.instructions.md)"));
        transcript.Add(Bash("1", "cat '/opt/hve-core/.github/skills/coding-standards/python-foundational/SKILL.md' '.github/instructions/python-script.instructions.md'", "---\nname: python-foundational\n---\n"));
        transcript.Add(Bash("2", "mkdir -p textkit && cat > textkit/slug.py <<'EOF'\nx = 1\nEOF\n", ""));
        var (_, scope) = TestSupport.Begin(new FakeSandboxEnvironment(), transcript: transcript);
        using (scope)
        {
            var score = await HveScorers.ArtefactUsed().Score(TestSupport.State(TestSupport.Metadata(components: ["skill/python-foundational", "instructions/python-script", "skill/documentation"])), new Target("t"), CancellationToken.None);

            Assert.Equal((1.0 + 1.0 + 0.5) / 3, ((ScoreValue.Num)score.Value).Value, 6);
            Assert.Equal("skill/python-foundational, instructions/python-script, skill/documentation", score.Answer);
            Assert.Equal(1.0, score.Metadata!["skill/python-foundational"]);
            Assert.Equal(1.0, score.Metadata["instructions/python-script"]);
            Assert.Equal(0.5, score.Metadata["skill/documentation"]);
            Assert.Contains("the python-foundational skill files were read by a tool call", score.Explanation);
        }
    }

    [Fact]
    public void a_write_a_description_and_a_failed_read_are_not_reads()
    {
        // Under the generic harness every write is a bash call too, and the plugin's own instructions tell the agent to cite
        // the files it worked from: a research note that names an instruction file and a skill path inside a heredoc is not a
        // read. Neither is the CLI's prose description of a command, nor a cat that came back "No such file or directory".
        var wrote = HveScorers.Evidence(
            ["instructions/python-script", "skill/rpi-plan"],
            [Bash("1", "mkdir -p .copilot-tracking/research && cat > .copilot-tracking/research/notes.md <<'EOF'\nFollowing python-script.instructions.md and /opt/hve-core/.github/skills/rpi/rpi-plan/SKILL.md\nEOF\n", "")],
            HveData.PluginDirectory);

        Assert.Equal(0, wrote["instructions/python-script"].Weight);
        Assert.Equal(0, wrote["skill/rpi-plan"].Weight);

        var described = HveScorers.Evidence(
            ["instructions/python-script"],
            [new ToolEvent("2", "bash", new JsonObject { ["command"] = "ls -la", ["description"] = "read python-script.instructions.md" }, "")],
            HveData.PluginDirectory);
        Assert.Equal(0, described["instructions/python-script"].Weight);

        // A call the engine marked as failed, and a call that succeeded but whose result reports that very path missing.
        var errored = HveScorers.Evidence(
            ["skill/python-foundational"],
            [new ToolEvent("3", "bash", new JsonObject { ["cmd"] = "cat /opt/hve-core/.github/skills/python-foundational/SKILL.md" }, "", new ToolCallError("unknown", "exit code 1"))],
            HveData.PluginDirectory);
        Assert.Equal(0, errored["skill/python-foundational"].Weight);

        // cat a b with b missing: a keeps its credit, b loses it.
        var partial = HveScorers.Evidence(
            ["instructions/python-script", "skill/python-foundational"],
            [Bash("4", "cat '.github/instructions/python-script.instructions.md' '/opt/hve-core/.github/skills/python-foundational/SKILL.md'", "# python scripts\ncat: /opt/hve-core/.github/skills/python-foundational/SKILL.md: No such file or directory")],
            HveData.PluginDirectory);
        Assert.Equal(HveScorers.StrongEvidence, partial["instructions/python-script"].Weight);
        Assert.Equal(0, partial["skill/python-foundational"].Weight);
    }

    [Fact]
    public void a_read_that_never_spelled_the_path_is_recognised_from_the_result()
    {
        // cd + a relative cat, a glob over every skill and a find -exec all read the file without naming its path in one
        // argument; what came back identifies it (the SKILL.md front matter, the agent body's H1).
        var relative = HveScorers.Evidence(
            ["skill/python-foundational"],
            [Bash("1", "cd /opt/hve-core/.github/skills/coding-standards/python-foundational && cat SKILL.md references/*.md", "---\nname: python-foundational\ndescription: python\n---\n# Python")],
            HveData.PluginDirectory)["skill/python-foundational"];
        Assert.Equal(HveScorers.StrongEvidence, relative.Weight);
        Assert.Equal("the python-foundational skill's SKILL.md came back in a tool result", relative.Note);

        var globbed = HveScorers.Evidence(
            ["skill/rpi-plan", "skill/rpi-plan-critique"],
            [Bash("2", "cat \"/opt/hve-core\"/.github/skills/*/*/SKILL.md", "---\nname: rpi-plan\n---\n# RPI Plan\n---\nname: rpi-plan-critique\n---\n# RPI Plan Critique")],
            HveData.PluginDirectory);
        Assert.Equal(HveScorers.StrongEvidence, globbed["skill/rpi-plan"].Weight);
        Assert.Equal(HveScorers.StrongEvidence, globbed["skill/rpi-plan-critique"].Weight);

        var found = HveScorers.Evidence(
            ["agent/rpi-agent"],
            [Bash("3", "find /opt/hve-core -name '*.agent.md' -exec cat {} +", "# RPI Agent\nresearch, plan, implement")],
            HveData.PluginDirectory)["agent/rpi-agent"];
        Assert.Equal(HveScorers.StrongEvidence, found.Weight);
        Assert.Equal("the hve-core:rpi-agent agent body came back in a tool result", found.Note);
    }

    private static ToolEvent Bash(string id, string cmd, string result) => new(id, "bash", new JsonObject { ["cmd"] = cmd }, result);

    private static InfoEvent Cli(string type, JsonObject data) => new(CopilotCliEvents.Source, new JsonObject { ["type"] = type, ["data"] = data });
}
