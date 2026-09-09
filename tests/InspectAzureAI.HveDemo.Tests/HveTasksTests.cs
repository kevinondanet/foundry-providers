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
        var task = HveTasks.Build(kind, HveSolvers.CopilotName, sandbox, new HveSolverOptions());

        Assert.Equal(name, task.Name);
        Assert.Equal("1", task.Version);
        Assert.Equal(samples, task.Dataset.Count);
        Assert.Same(sandbox, task.Sandbox);
        Assert.Equal(HveTasks.MessageLimit, task.MessageLimit);
        Assert.Equal(HveTasks.TimeLimit, task.TimeLimit);
        Assert.Equal(FailOnError.Never, task.FailOnError);
        Assert.Equal(4, task.Scorers.Count);
        Assert.Equal(kind is null or "suite" ? "suite" : kind, task.Metadata!["kind"]);
    }

    [Fact]
    public void the_suite_groups_each_scorers_own_headline_metric_by_kind_and_the_kind_tasks_do_not()
    {
        var suite = HveTasks.Build(null, HveSolvers.CopilotName, new SandboxSpec("fake"), new HveSolverOptions());
        var review = HveTasks.Build("review", HveSolvers.CopilotName, new SandboxSpec("fake"), new HveSolverOptions());

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
        Assert.Throws<ArgumentException>(() => HveTasks.Build(null, "swarm", new SandboxSpec("fake"), new HveSolverOptions()));
    }

    [Fact]
    public void copilot_options_carry_the_plugin_the_agent_and_the_hygiene_flags()
    {
        var options = HveSolvers.CopilotOptions(new HveSolverOptions { CopilotVersion = "1.0.83", PluginDir = "/srv/plugin", Provider = CopilotCliProvider.Anthropic, Debug = true }, "hve-core:rpi-agent");

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
    }

    [Fact]
    public void the_system_prompt_names_the_plugin_pieces_and_asks_for_the_summary()
    {
        Assert.Contains("hve-core:rpi-agent", HveSolvers.SystemPrompt);
        Assert.Contains("python-foundational", HveSolvers.SystemPrompt);
        Assert.Contains("git-commit-message.prompt", HveSolvers.SystemPrompt);
        Assert.Contains("names every file you created or changed by its path", HveSolvers.SystemPrompt);
        Assert.DoesNotContain("{", HveSolvers.SystemPrompt);
    }

    [Fact]
    public void program_options_default_to_the_fake_sandbox_under_fake_and_validate_choices()
    {
        var fake = Program.Options.Parse(["--fake", "--task", "review", "--solver", "basic", "--limit", "1"]);
        Assert.True(fake.Fake);
        Assert.Equal("fake", fake.Sandbox);
        Assert.Equal("review", fake.Task);
        Assert.Equal("basic", fake.Solver);
        Assert.Equal(1, fake.Limit);

        var docker = Program.Options.Parse(["--fake", "--sandbox", "docker", "--copilot-version", "1.0.83", "--max-samples", "2"]);
        Assert.Equal("docker", docker.Sandbox);
        Assert.Equal("1.0.83", docker.CopilotVersion);
        Assert.Equal(2, docker.MaxSamples);
        Assert.Null(fake.MaxSamples);
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--max-samples", "0"]));

        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--task", "benchmark"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--solver", "swarm"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--sandbox", "vm"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--model", "gpt-4o", "--sandbox", "fake"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--limit", "0"]));
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--bogus"]));
    }
}
