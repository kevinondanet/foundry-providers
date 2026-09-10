using System.Reflection;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.HveDemo;
using InspectAzureAI.HveDemo.Components;
using InspectAzureAI.Swe.CopilotCli;

namespace InspectAzureAI.HveDemo.Tests;

/// <summary>The task component: the four registered tasks, the builder behind them, and the solver options they carry.</summary>
public sealed class HveTasksTests
{
    [Theory]
    [InlineData("implement", HveTasks.ImplementName, 4)]
    [InlineData("review", HveTasks.ReviewName, 2)]
    [InlineData("skill", HveTasks.SkillName, 2)]
    [InlineData("suite", HveTasks.SuiteName, 8)]
    [InlineData(null, HveTasks.SuiteName, 8)]
    public void build_filters_the_dataset_and_sets_the_budgets(string? kind, string name, int samples)
    {
        var sandbox = new SandboxSpec("fake");
        var task = HveTasks.Build(kind, HveVariant.Default, sandbox, new HveSolverOptions());

        Assert.Equal(name, task.Name);
        Assert.Equal("1", task.Version);
        Assert.Equal(samples, task.Dataset.Count);
        Assert.Same(sandbox, task.Sandbox);
        Assert.Equal(HveTasks.MessageLimit, task.MessageLimit);
        Assert.Equal(HveTasks.TimeLimit, task.TimeLimit);
        Assert.Equal(FailOnError.Never, task.FailOnError);
        Assert.Equal(4, task.Scorers.Count);
        Assert.Equal(kind is null or "suite" ? "suite" : kind, task.Metadata!["kind"]);
        Assert.Equal("copilot", task.Metadata["harness"]);
        Assert.Equal("hve", task.Metadata["framework"]);
        Assert.Equal("copilot+hve", task.Metadata["solver"]);
    }

    [Fact]
    public void the_suite_groups_each_scorers_own_headline_metric_by_kind_and_the_kind_tasks_do_not()
    {
        var suite = HveTasks.Build(null, HveVariant.Default, new SandboxSpec("fake"), new HveSolverOptions());
        var review = HveTasks.Build("review", HveVariant.Default, new SandboxSpec("fake"), new HveSolverOptions());

        // no task-level override: that would relabel hve_artefact_used's mean as accuracy
        Assert.Null(suite.Metrics);
        Assert.Null(review.Metrics);
        Assert.Equal(["accuracy", "stderr", "grouped"], suite.Scorers[0].Metrics.Select(m => m.Name));
        Assert.Equal(["mean", "stderr", "grouped"], suite.Scorers[3].Metrics.Select(m => m.Name));
        Assert.Equal(["accuracy", "stderr"], review.Scorers[0].Metrics.Select(m => m.Name));
        Assert.Equal(["mean", "stderr"], review.Scorers[3].Metrics.Select(m => m.Name));
    }

    [Fact]
    public void the_four_task_methods_are_registered_with_the_task_attribute()
    {
        var tasks = typeof(HveTasks).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<TaskAttribute>() is not null)
            .ToDictionary(method => method.GetCustomAttribute<TaskAttribute>()!.Name!, method => method);

        Assert.Equal([HveTasks.ImplementName, HveTasks.ReviewName, HveTasks.SkillName, HveTasks.SuiteName], tasks.Keys.Order());
        Assert.All(tasks.Values, method => Assert.Equal(typeof(EvalTask), method.ReturnType));

        // The registered tasks run in Docker with the Dockerfile as the build context.
        var implement = (EvalTask)tasks[HveTasks.ImplementName].Invoke(null, null)!;
        Assert.Equal("docker", implement.Sandbox!.Type);
        Assert.Equal(HveData.SandboxDirectory, implement.Sandbox.Config);
        Assert.True(File.Exists(Path.Combine(implement.Sandbox.Config!, "Dockerfile")));
    }

    [Fact]
    public void name_for_rejects_unknown_kinds()
    {
        Assert.Equal(HveTasks.SuiteName, HveTasks.NameFor(null));
        Assert.Equal(HveTasks.ImplementName, HveTasks.NameFor("Implement"));
        Assert.Throws<ArgumentException>(() => HveTasks.NameFor("benchmark"));
        Assert.Throws<ArgumentException>(() => HveVariant.Parse("swarm", "hve"));
        Assert.Throws<ArgumentException>(() => HveVariant.Parse("copilot", "all"));
    }

    [Fact]
    public void copilot_options_carry_the_plugin_the_agent_and_the_hygiene_flags()
    {
        var options = HveSolvers.CopilotOptions(new HveSolverOptions { CopilotVersion = "1.0.83", PluginDir = "/srv/plugin", Provider = CopilotCliProvider.Anthropic, Debug = true }, "hve-core:rpi-agent", HveFramework.Hve);

        Assert.Equal("1.0.83", options.Version);
        Assert.Equal(["/srv/plugin"], options.PluginDirs);
        Assert.Equal("hve-core:rpi-agent", options.CustomAgent);
        Assert.Equal(CopilotCliProvider.Anthropic, options.Provider);
        Assert.Equal(CopilotCliPermission.Yolo, options.Permission);
        Assert.True(options.DisableBuiltinMcps);
        Assert.True(options.NoAskUser);
        Assert.True(options.Debug);
        Assert.Equal(1, options.Attempts.Attempts);
        options.Validate();

        Assert.Null(HveSolvers.CopilotOptions(new HveSolverOptions(), null).CustomAgent);

        // Under none the CLI gets neither --plugin-dir nor --agent, whatever the sample's metadata says.
        var none = HveSolvers.CopilotOptions(new HveSolverOptions { PluginDir = "/srv/plugin" }, "hve-core:rpi-agent", HveFramework.None);
        Assert.Empty(none.PluginDirs);
        Assert.Null(none.CustomAgent);
        none.Validate();
    }

    [Fact]
    public void the_briefings_name_the_plugin_pieces_and_ask_for_the_summary()
    {
        Assert.Contains("hve-core:rpi-agent", HveSolvers.CopilotHveBriefing);
        Assert.Contains("python-foundational", HveSolvers.CopilotHveBriefing);
        Assert.Contains("git-commit-message.prompt", HveSolvers.CopilotHveBriefing);
        Assert.Contains("names every file you created or changed by its path", HveSolvers.CopilotHveBriefing);
        Assert.Contains(HveBriefing.FrameworkMarker, HveSolvers.CopilotHveBriefing);
        Assert.DoesNotContain("{", HveSolvers.CopilotHveBriefing);

        // The plain briefings ask for the same summary, go through the template formatter too, and never mention the plugin.
        foreach (var plain in new[] { HveSolvers.CopilotPlainBriefing, HveSolvers.GenericPlainBriefing })
        {
            Assert.Contains("names every file you created or changed by its path", plain);
            Assert.DoesNotContain("{", plain);
            Assert.DoesNotContain(HveBriefing.FrameworkMarker, plain);
        }
    }

    [Fact]
    public void program_options_default_to_the_fake_sandbox_under_fake_and_validate_choices()
    {
        var fake = Program.Options.Parse(["--fake", "--task", "review", "--solver", "basic", "--limit", "1"]);
        Assert.True(fake.Fake);
        Assert.Equal("fake", fake.Sandbox);
        Assert.Equal("review", fake.Task);
        Assert.Equal("generic", fake.Harness);
        Assert.Equal("none", fake.Framework);
        Assert.Equal("basic", fake.SolverAlias);
        Assert.Equal(1, fake.Limit);

        var docker = Program.Options.Parse(["--fake", "--sandbox", "docker", "--copilot-version", "1.0.83", "--max-samples", "2"]);
        Assert.Equal("docker", docker.Sandbox);
        Assert.Equal("1.0.83", docker.CopilotVersion);
        Assert.Equal(2, docker.MaxSamples);
        Assert.Null(fake.MaxSamples);
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--max-samples", "0"]));

        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--task", "benchmark"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--solver", "swarm"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--harness", "swarm"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--framework", "all"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--sandbox", "vm"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--model", "gpt-4o", "--sandbox", "fake"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--limit", "0"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--bogus"]));
    }
}
