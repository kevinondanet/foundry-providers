using InspectAzureAI.HveDemo;
using InspectAzureAI.HveDemo.Components;

namespace InspectAzureAI.HveDemo.Tests;

/// <summary>The dataset component: the eight samples, their metadata contract, and the sandbox provisioning attached to each.</summary>
public sealed class HveDatasetTests
{
    [Fact]
    public void loads_eight_samples_of_three_kinds()
    {
        var dataset = HveDataset.Load();

        Assert.Equal(8, dataset.Count);
        Assert.Equal("hve", dataset.Name);
        Assert.Equal(["implement", "review", "skill"], HveDataset.KindsOf(dataset));
        Assert.Equal(4, dataset.Count(s => HveDataset.Kind(s) == "implement"));
        Assert.Equal(2, dataset.Count(s => HveDataset.Kind(s) == "review"));
        Assert.Equal(2, dataset.Count(s => HveDataset.Kind(s) == "skill"));
    }

    [Fact]
    public void every_sample_carries_the_metadata_contract()
    {
        foreach (var sample in HveDataset.Load())
        {
            Assert.False(string.IsNullOrEmpty(sample.Input.Text), $"{sample.Id} has no input");
            Assert.False(string.IsNullOrEmpty(sample.Target.Text), $"{sample.Id} has no target");
            Assert.NotEmpty(HveDataset.Artefact(sample.Metadata));
            Assert.NotEmpty(HveDataset.Check(sample.Metadata));
            Assert.NotEmpty(HveDataset.Rubric(sample.Metadata));
            Assert.NotEmpty(HveDataset.Components(sample.Metadata));
            Assert.All(HveDataset.Components(sample.Metadata), component => Assert.Matches("^(agent|skill|instructions|prompt)/[a-z0-9-]+$", component));
        }
    }

    [Fact]
    public void review_samples_name_the_hve_code_review_sub_agents()
    {
        var reviews = HveDataset.Load("review").ToList();

        Assert.Equal(["hve-core:code-review-functional", "hve-core:code-review-standards"], reviews.Select(s => HveDataset.Agent(s.Metadata)).ToList());
        Assert.Null(HveDataset.Agent(HveDataset.Load("skill")[0].Metadata));
    }

    [Fact]
    public void files_cover_the_shared_overlay_the_workspace_and_the_plugin()
    {
        var sample = HveDataset.Load().First(s => Equals(s.Id, "implement-slugify"));
        var files = sample.Files!;

        Assert.True(files.ContainsKey(".github/copilot-instructions.md"));
        Assert.True(files.ContainsKey(".github/instructions/python-script.instructions.md"));
        Assert.True(files.ContainsKey("tests/test_slugify.py"));
        Assert.True(files.ContainsKey("textkit/__init__.py"));
        Assert.DoesNotContain(files.Keys, key => key.Contains("__pycache__", StringComparison.Ordinal));
        Assert.All(files.Where(pair => pair.Key != HveData.PluginSandboxPath), pair => Assert.True(File.Exists(pair.Value), pair.Value));
        Assert.Equal(HveData.PluginDirectory, files[HveData.PluginSandboxPath]);
        Assert.True(File.Exists(Path.Combine(files[HveData.PluginSandboxPath], "plugin.json")));
    }

    [Fact]
    public void the_plugin_can_be_left_out_or_relocated()
    {
        var without = HveDataset.Load(kind: null, pluginSandboxPath: null).First();
        var elsewhere = HveDataset.Load(kind: null, pluginSandboxPath: "/srv/plugin").First();

        Assert.DoesNotContain(HveData.PluginSandboxPath, without.Files!.Keys);
        Assert.True(elsewhere.Files!.ContainsKey("/srv/plugin"));
    }

    [Fact]
    public void the_commit_message_sample_has_a_setup_script_and_the_others_do_not()
    {
        var dataset = HveDataset.Load();

        Assert.Equal("bash setup.sh", dataset.First(s => Equals(s.Id, "skill-commit-message")).Setup);
        Assert.All(dataset.Where(s => !Equals(s.Id, "skill-commit-message")), s => Assert.Null(s.Setup));
    }

    [Fact]
    public void filtering_by_kind_is_validated()
    {
        Assert.Equal(2, HveDataset.Load("skill").Count);
        Assert.Equal("hve[skill]", HveDataset.Load("skill").Name);
        Assert.Throws<ArgumentException>(() => HveDataset.Load("benchmark"));
    }

    [Fact]
    public void every_sample_has_a_reference_solution_for_the_fake_model()
    {
        foreach (var sample in HveDataset.Load())
        {
            Assert.True(File.Exists(Fake.FakeHveModel.ReferencePath(HveData.ReferenceRoot, sample)), $"{sample.Id} has no reference artefact");
        }
    }

    [Fact]
    public void every_sample_has_check_assets_outside_the_workspace_and_pristine_copies_match_what_the_agent_sees()
    {
        foreach (var sample in HveDataset.Load())
        {
            var id = Convert.ToString(sample.Id, System.Globalization.CultureInfo.InvariantCulture)!;
            var checks = Path.Combine(HveData.ChecksRoot, id);
            Assert.True(Directory.Exists(checks), $"{id} has no hve/checks directory");
            foreach (var file in Directory.EnumerateFiles(checks, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(checks, file).Replace('\\', '/');
                if (sample.Files!.TryGetValue(relative, out var visible))
                {
                    Assert.Equal(File.ReadAllText(file), File.ReadAllText(visible));
                }
            }

            // the answer keys and grader scripts are not provisioned into the sandbox
            Assert.DoesNotContain(sample.Files!.Keys, key => key.EndsWith("planted.json", StringComparison.Ordinal) || Path.GetFileName(key).StartsWith("check_", StringComparison.Ordinal));
        }

        var rpi = HveDataset.Load().First(s => Equals(s.Id, "implement-config-loader-rpi"));
        Assert.True(rpi.Files!.ContainsKey(".github/instructions/copilot-tracking.instructions.md"), "the RPI sample lists instructions/copilot-tracking, so the overlay must ship it");
    }

    [Fact]
    public void agent_front_matter_tool_lists_parse_in_block_and_flow_style()
    {
        Assert.Equal(["search/codebase", "read/readFile", "edit/createFile"], Fake.FakeCopilotCli.ToolIds("name: x\ntools:\n  - search/codebase\n  - read/readFile\n  - edit/createFile\nuser-invocable: false"));
        Assert.Equal(["execute/runInTerminal", "read", "edit"], Fake.FakeCopilotCli.ToolIds("tools: [execute/runInTerminal, read, 'edit']\nagents: []"));
        Assert.Null(Fake.FakeCopilotCli.ToolIds("name: RPI Agent\ndescription: x"));
    }

    [Fact]
    public void the_vendored_plugin_keeps_its_licence_and_manifest()
    {
        Assert.True(File.Exists(Path.Combine(HveData.PluginDirectory, "LICENSE")));
        Assert.True(File.Exists(Path.Combine(HveData.PluginDirectory, "NOTICE.md")));
        Assert.Contains("\"name\": \"hve-core\"", File.ReadAllText(Path.Combine(HveData.PluginDirectory, "plugin.json")));
        Assert.Equal(7, Directory.EnumerateFiles(Path.Combine(HveData.PluginDirectory, ".github", "agents"), "*.agent.md", SearchOption.AllDirectories).Count());
        Assert.Equal(9, Directory.EnumerateFiles(Path.Combine(HveData.PluginDirectory, ".github", "skills"), "SKILL.md", SearchOption.AllDirectories).Count());
    }
}
