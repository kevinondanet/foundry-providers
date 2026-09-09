using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;

/// <summary><c>Task(model=...)</c>: <c>EvalTask.Model</c> and its resolution against the eval-level model (<c>_eval/loader.py</c> <c>ResolvedTask.model = task.model or model</c>).</summary>
public sealed class TaskModelTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));

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

    private static Model Scripted(string name, string reply, GenerateConfig? config = null) =>
        new(new ScriptedModelApi([ScriptedTurn.Text(reply)], name), config);

    private static EvalTask QuizTask(Model? model = null) => new()
    {
        Name = "task model",
        Dataset = new MemoryDataset([new Sample("What is 2 + 2?") { Target = "4" }], name: "unit"),
        Scorers = [Scorers.Includes()],
        Model = model,
    };

    private EvalOptions Options(Model? model = null) => new() { Model = model, LogDir = _logDir, MaxSamples = 1, LogFormat = LogFormat.Json };

    [Fact]
    public void eval_task_model_defaults_to_null()
    {
        Assert.Null(QuizTask().Model);
        Assert.Null(new EvalOptions().Model);
    }

    [Fact]
    public async Task task_model_is_used_when_the_options_have_none()
    {
        var log = await Eval.RunAsync(QuizTask(Scripted("task-model", "4")), Options());

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal("task-model", log.Eval.Model);
        var sample = Assert.Single(log.Samples!);
        Assert.Equal("4", sample.Output.Completion);
        Assert.Equal("C", sample.Scores!["includes"].Text);
    }

    [Fact]
    public async Task task_model_wins_over_the_eval_level_model()
    {
        // Python: ResolvedTask.model = task.model or model — the task's own model is used when it has one
        var log = await Eval.RunAsync(QuizTask(Scripted("task-model", "4")), Options(Scripted("eval-model", "5")));

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal("task-model", log.Eval.Model);
        Assert.Equal("4", Assert.Single(log.Samples!).Output.Completion);
    }

    [Fact]
    public async Task eval_level_model_is_used_when_the_task_has_none()
    {
        var log = await Eval.RunAsync(QuizTask(), Options(Scripted("eval-model", "5")));

        Assert.Equal(EvalStatus.Success, log.Status);
        Assert.Equal("eval-model", log.Eval.Model);
        Assert.Equal("5", Assert.Single(log.Samples!).Output.Completion);
    }

    [Fact]
    public async Task no_model_anywhere_is_an_error()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Eval.RunAsync(QuizTask(), Options()));

        Assert.StartsWith("No model specified", ex.Message);
    }

    [Fact]
    public async Task task_model_config_is_recorded_and_the_task_config_is_layered_over_it()
    {
        var task = QuizTask(Scripted("task-model", "4", new GenerateConfig { Temperature = 0.5 })) with
        {
            Config = new GenerateConfig { TopP = 0.9, Temperature = 0.1 },
        };

        var log = await Eval.RunAsync(task, Options());

        // eval.model_generate_config is the resolved model's own config; the plan config layers the task's over it
        // (Python's Model.generate: model.config.merge(task config), so the task's temperature wins)
        Assert.Equal(0.5, log.Eval.ModelGenerateConfig.Temperature);
        Assert.Null(log.Eval.ModelGenerateConfig.TopP);
        Assert.Equal(0.9, log.Plan.Config.TopP);
        Assert.Equal(0.1, log.Plan.Config.Temperature);
    }
}
