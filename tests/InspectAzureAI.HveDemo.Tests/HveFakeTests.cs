using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.HveDemo;
using InspectAzureAI.HveDemo.Components;
using InspectAzureAI.HveDemo.Fake;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.HveDemo.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The offline pieces on their own, without the bridge or an eval run: how the fake sandbox maps sandbox paths inside a
/// <c>bash -c</c> command string, and the turn plan <see cref="FakeHveModel"/> plays for each cell of the matrix, driven
/// with synthetic conversations built from the real briefings.
/// </summary>
public sealed class HveFakeTests
{
    private static readonly IReadOnlyList<ToolInfo> GenericTools = [new ToolInfo("bash", "Run a bash command."), new ToolInfo("submit", "Submit an answer.")];

    private static readonly IReadOnlyList<ToolInfo> CliTools = FakeCopilotCli.ToolNames.Select(name => new ToolInfo(name, name)).ToList();

    [Fact]
    public void map_command_text_rewrites_provisioned_sandbox_paths_inside_a_command()
    {
        using var env = new ScriptedSandboxEnvironment(new FakeSandboxScript().WithFile("/opt/hve-core/x", "x").WithFile("/tmp/setup.sh", "true"));

        var mapped = env.MapCommandText("cat '/opt/hve-core/x' && bash /tmp/setup.sh && ls /workspace/a && cd /workspace");

        Assert.Equal(
            $"cat '{env.HostPath("/opt/hve-core/x")}' && bash {env.HostPath("/tmp/setup.sh")} && ls {env.HostPath("/workspace/a")} && cd {env.HostPath("/workspace")}",
            mapped);
        foreach (var untouched in new[] { "http://host/opt/x", "./opt/x", "/usr/bin/env", "/optional", "/tmpfile", "/workspaces", "/opt/absent", "/tmp/absent" })
        {
            Assert.Equal(untouched, env.MapCommandText(untouched));
        }
    }

    [Fact]
    public async Task heredoc_bodies_keep_unmapped_sandbox_paths_verbatim()
    {
        using var env = new ScriptedSandboxEnvironment(new FakeSandboxScript { RunUnmatchedLocally = true }.WithFile("/opt/hve-core/x", "x"));

        var write = await env.ExecAsync(["bash", "-c", "cat > note.txt <<'EOF'\nsee /tmp/x and /optional\nEOF\n"]);
        Assert.True(write.Success, write.Stderr);
        Assert.Equal("see /tmp/x and /optional\n", env.FileText("/workspace/note.txt"));

        // A provisioned plugin path inside the command string, on the other hand, is served from the mirror.
        var read = await env.ExecAsync(["bash", "-c", "cat /opt/hve-core/x"]);
        Assert.True(read.Success, read.Stderr);
        Assert.Equal("x", read.Stdout);
    }

    [Theory]
    [InlineData("copilot", "hve")]
    [InlineData("copilot", "none")]
    [InlineData("generic", "hve")]
    [InlineData("generic", "none")]
    public async Task fake_model_plans_each_cell(string harness, string framework)
    {
        var model = FakeHveModel.Create();
        var sample = Slugify();
        var generic = harness == "generic";
        var briefing = (generic, framework == "hve") switch
        {
            (true, true) => HveSolvers.GenericHveBriefing(HveData.PluginSandboxPath, null, HveData.PluginDirectory),
            (true, false) => HveSolvers.GenericPlainBriefing,
            (false, true) => HveSolvers.CopilotHveBriefing,
            (false, false) => HveSolvers.CopilotPlainBriefing,
        };
        var (messages, tools) = Conversation(sample, generic, briefing);

        var first = await model.GenerateAsync(messages, tools);
        var call = Assert.Single(first.Message.ToolCalls!);
        switch (harness, framework)
        {
            case ("generic", "hve"):
                Assert.Equal("bash", call.Function);
                var read = call.Arguments["cmd"]!.GetValue<string>();
                Assert.StartsWith("cat '", read);
                Assert.Contains("coding-standards/python-foundational/SKILL.md'", read);
                Assert.Contains("'.github/instructions/python-script.instructions.md'", read);
                break;
            case ("generic", "none"):
                Assert.Equal("bash", call.Function);
                Assert.StartsWith("mkdir -p 'textkit' && cat > 'textkit/slug.py' <<'HVE_EOF'", call.Arguments["cmd"]!.GetValue<string>());
                break;
            case ("copilot", "hve"):
                Assert.Equal("skill", call.Function);
                Assert.Equal("python-foundational", call.Arguments["skill"]!.GetValue<string>());
                break;
            default:
                Assert.Equal("view", call.Function);
                Assert.Equal("/workspace/.github/instructions/python-script.instructions.md", call.Arguments["path"]!.GetValue<string>());
                break;
        }

        // The turn after a read writes the artefact; generic+none has no read, so its first turn was the write and the second is the check.
        var second = await model.GenerateAsync([.. messages, first.Message, new ChatMessageTool("ok", call.Id, call.Function)], tools);
        var next = Assert.Single(second.Message.ToolCalls!);
        switch (harness, framework)
        {
            case ("generic", "hve"):
                Assert.Equal("bash", next.Function);
                Assert.StartsWith("mkdir -p 'textkit' && cat > 'textkit/slug.py' <<'HVE_EOF'", next.Arguments["cmd"]!.GetValue<string>());
                break;
            case ("generic", "none"):
                Assert.Equal("bash", next.Function);
                Assert.Equal(HveDataset.Check(sample.Metadata), next.Arguments["cmd"]!.GetValue<string>());
                break;
            default:
                Assert.Equal("create", next.Function);
                Assert.Equal("/workspace/textkit/slug.py", next.Arguments["path"]!.GetValue<string>());
                break;
        }
    }

    [Fact]
    public async Task plugin_directory_is_read_from_the_briefing()
    {
        var (messages, tools) = Conversation(Slugify(), generic: true, HveSolvers.GenericHveBriefing("/srv/p", null, HveData.PluginDirectory));

        var first = await FakeHveModel.Create().GenerateAsync(messages, tools);

        var call = Assert.Single(first.Message.ToolCalls!);
        Assert.Equal("bash", call.Function);
        Assert.Contains("'/srv/p/.github/skills/coding-standards/python-foundational/SKILL.md'", call.Arguments["cmd"]!.GetValue<string>());
    }

    [Fact]
    public async Task fake_model_plan_throws_when_the_plugin_lacks_a_named_skill()
    {
        // An empty directory as the plugin: the sample's skill is nowhere under it.
        var empty = Directory.CreateTempSubdirectory("inspect-hve-empty-plugin");
        try
        {
            var model = FakeHveModel.Create(HveDataset.Load(), HveData.ReferenceRoot, empty.FullName);
            var (messages, tools) = Conversation(Slugify(), generic: true, HveSolvers.GenericHveBriefing(HveData.PluginSandboxPath, null, HveData.PluginDirectory));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => model.GenerateAsync(messages, tools));

            Assert.Contains("has no skill 'python-foundational'", error.Message);
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task reference_guard_rejects_sandbox_path_tokens()
    {
        var root = Directory.CreateTempSubdirectory("inspect-hve-references");
        try
        {
            var reference = Path.Combine(root.FullName, "implement-slugify", "textkit", "slug.py");
            Directory.CreateDirectory(Path.GetDirectoryName(reference)!);
            File.WriteAllText(reference, "# see /tmp/x\n");
            var model = FakeHveModel.Create(HveDataset.Load(), root.FullName, HveData.PluginDirectory);
            var (messages, tools) = Conversation(Slugify(), generic: true, HveSolvers.GenericPlainBriefing);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => model.GenerateAsync(messages, tools));

            Assert.Contains("sandbox path token", error.Message);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static Sample Slugify() => HveDataset.Load().First(sample => Equals(sample.Id, "implement-slugify"));

    /// <summary>
    /// The messages and tools the model sees at its first turn of <paramref name="sample"/>: under the generic harness the
    /// briefing and basic_agent's system message, then the input; under copilot the (fake) CLI's own system prompt and the
    /// first prompt with the briefing prepended, as the agent and the CLI arrange it.
    /// </summary>
    private static (IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolInfo> Tools) Conversation(Sample sample, bool generic, string briefing) => generic
        ? ([new ChatMessageSystem(briefing), new ChatMessageSystem("When you have completed the task and have an answer, call the submit() function to report it."), new ChatMessageUser(sample.Input.Text ?? "")], GenericTools)
        : ([new ChatMessageSystem("You are the GitHub Copilot CLI, a terminal assistant built by GitHub."), new ChatMessageUser("<current_datetime>x</current_datetime>\n\n" + briefing + "\n\n" + (sample.Input.Text ?? ""))], CliTools);
}
