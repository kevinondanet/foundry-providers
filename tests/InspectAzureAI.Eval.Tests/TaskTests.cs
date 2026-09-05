using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

/// <summary>The <c>EvalTask</c> record and <c>Epochs</c> mirror Python's <c>Task</c> defaults.</summary>
public class TaskTests
{
    private static MemoryDataset Dataset() => new([new Sample("hi") { Target = "there" }], name: "unit");

    [Fact]
    public void eval_task_defaults_follow_python_task()
    {
        var task = new EvalTask { Name = "t", Dataset = Dataset() };

        Assert.Equal("t", task.Name);
        Assert.Single(task.Dataset);
        Assert.Null(task.Setup);
        Assert.NotNull(task.Solver);
        Assert.Empty(task.Scorers);
        Assert.Null(task.Metrics);
        Assert.Equal(new GenerateConfig(), task.Config);
        Assert.Null(task.Sandbox);
        Assert.Null(task.Epochs);
        Assert.Equal(FailOnError.Always, task.FailOnError);
        Assert.Null(task.MessageLimit);
        Assert.Null(task.TokenLimit);
        Assert.Null(task.TimeLimit);
        Assert.Equal("0", task.Version);
        Assert.Null(task.Metadata);
    }

    [Fact]
    public void eval_task_with_expression_overrides_settings()
    {
        var task = new EvalTask { Name = "t", Dataset = Dataset() };
        ScoreReducer max = scores => scores.MaxBy(s => s.AsFloat())!;

        var configured = task with
        {
            Epochs = new Epochs(3, [max]),
            Sandbox = new SandboxSpec("docker", "Dockerfile"),
            MessageLimit = 10,
            TimeLimit = TimeSpan.FromMinutes(5),
            FailOnError = false,
            Version = "2",
        };

        Assert.Equal(3, configured.Epochs!.Count);
        Assert.Same(max, Assert.Single(configured.Epochs.Reducers!));
        Assert.Equal(new SandboxSpec("docker", "Dockerfile"), configured.Sandbox);
        Assert.Equal(10, configured.MessageLimit);
        Assert.Equal(TimeSpan.FromMinutes(5), configured.TimeLimit);
        Assert.Equal(FailOnError.Never, configured.FailOnError);
        Assert.Equal("2", configured.Version);
        Assert.Equal(FailOnError.Always, task.FailOnError);
    }

    [Fact]
    public void epochs_requires_a_positive_count_and_defaults_reducers_to_null()
    {
        var epochs = new Epochs(2);

        Assert.Equal(2, epochs.Count);
        Assert.Null(epochs.Reducers);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Epochs(0));
        Assert.Equal(new Epochs(2), epochs);
    }
}
