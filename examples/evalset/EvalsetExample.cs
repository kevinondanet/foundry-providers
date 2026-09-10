using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Evalset;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/evalset.py</c> as an <see cref="IExample"/>. The Python file is a click script around
/// <c>eval_set</c>, not a task, so the runner's task <c>evalset</c> is a one-sample wrapper whose solver runs the
/// eval set (<see cref="EvalsetRun.RunAsync"/>) on the runner's model plus <c>-T model2=&lt;deployment&gt;</c>, in the
/// directory <c>-T log_dir=&lt;dir&gt;</c> (default <c>logs/evalset</c>; Python's required <c>--log-dir</c>), with
/// <c>-T max_tasks</c> and <c>-T retry_attempts</c> (default 10); the sample errors when the set did not succeed, so
/// the run exits 0 only when every task completed, as the script does. Deviation: without <c>model2</c> the set
/// runs on one model (the same deployment twice is not a distinct task); offline, two scripted models stand in
/// and the first one's first call fails so the run shows a retry.
/// </summary>
public sealed class EvalsetExample : IExample
{
    /// <summary>The wrapper task's name (the Python file's name).</summary>
    public const string TaskName = "evalset";

    /// <summary>Where the eval set's logs go when <c>-T log_dir</c> is not given.</summary>
    public const string DefaultLogDir = "logs/evalset";

    /// <summary>The wrapper sample's input: the click command's docstring.</summary>
    public const string SampleInput = "Run 2 tasks on 2 models, retrying as required if errors occur.";

    public string Name => TaskName;

    public string Description => "An eval set: security_guide and popularity on two models with automatic retries, resumable through its log directory (a wrapper task around EvalSet.RunAsync)";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new(TaskName, Build, "runs the set on the run's model and -T model2=<deployment> in -T log_dir=<dir> (default logs/evalset); -T max_tasks=<n>, -T retry_attempts=<n> (default 10)"),
    ];

    public ExampleDefaults Defaults { get; } = new(Sandbox: "none");

    public IReadOnlyList<string> Deviations { get; } =
    [
        "evalset.py is a click script, not a task: here the runner's task evalset is a one-sample wrapper whose solver runs the eval set (EvalsetRun.RunAsync, the port of run()) and errors the sample when the set did not succeed, so the exit code is 0 only when every task completed; the inspectai eval-set command is the direct counterpart.",
        "Python's required --log-dir is the eval set's directory, given as -T log_dir=<dir> (default logs/evalset); the runner's --log-dir holds the wrapper's own log. --max-tasks and --retry-attempts are -T max_tasks and -T retry_attempts.",
        "The models are Foundry deployments — the run's model and -T model2=<deployment> — instead of openai/gpt-4o-mini and anthropic/claude-3-5-haiku-latest; without model2 the set runs on one model, because the same deployment twice is 'not distinct' for an eval set.",
        "security_guide and popularity are local copies of popularity.py and security_guide.py (EvalsetTasks) rather than references to the popularity and security_guide examples, and they carry no [Task] attribute (those examples register the names).",
        "Offline, two scripted models named scripted/gpt-4o-mini and scripted/claude-3-5-haiku-latest stand in; the first one's first call fails so the run shows the eval set retrying that task, and a second run in the same log directory shows the completed logs being reused.",
        "Progress is a compact reporter (retry messages, errors, every 25th sample) rather than Python's live display; the per-task summary is printed by the example.",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => FakeEvalsetModels.First();

    /// <summary>No sandbox: the two tasks only call generate.</summary>
    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;

    /// <summary>The wrapper task for the runner: the run's model, <c>-T model2</c>, and the <c>-T</c> log dir, max tasks and retry attempts.</summary>
    public static EvalTask Build(ExampleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var model2 = ctx.TaskArg("model2");
        var maxTasks = ctx.TaskArg("max_tasks") is null ? (int?)null : ctx.TaskArgInt("max_tasks", 0);
        return Build(
            logDir: ctx.TaskArg("log_dir", DefaultLogDir)!,
            secondModel: ctx.Fake ? FakeEvalsetModels.Second() : model2 is null ? null : InspectAzureAI.Eval.Model.Models.Create(model2),
            maxTasks: maxTasks,
            retryAttempts: ctx.TaskArgInt("retry_attempts", EvalsetRun.DefaultRetryAttempts),
            output: ctx.Out);
    }

    /// <summary>
    /// The wrapper task for the <c>inspectai</c> CLI (<c>-T log_dir=... -T model2=... -T max_tasks=... -T retry_attempts=...</c>);
    /// the first model is the CLI's <c>--model</c>.
    /// </summary>
    [Task(TaskName)]
    public static EvalTask EvalsetTask(string logDir = DefaultLogDir, string? model2 = null, int? maxTasks = null, int retryAttempts = EvalsetRun.DefaultRetryAttempts) =>
        Build(logDir, model2 is null ? null : InspectAzureAI.Eval.Model.Models.Create(model2), maxTasks, retryAttempts, Console.Out);

    /// <summary>
    /// The wrapper task: one sample whose solver runs the eval set on the active model (the run's) and
    /// <paramref name="secondModel"/>, prints <see cref="EvalsetRun.Summarize"/> to <paramref name="output"/>, sets the
    /// summary as the sample's output, and throws (erroring the sample and the log) when the set did not succeed.
    /// </summary>
    public static EvalTask Build(string logDir, Model? secondModel, int? maxTasks, int retryAttempts, TextWriter output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDir);
        ArgumentNullException.ThrowIfNull(output);
        return new EvalTask
        {
            Name = TaskName,
            Dataset = new MemoryDataset([new Sample(SampleInput)]),
            Solver = async (state, _, cancellationToken) =>
            {
                var first = SampleContext.Require().ActiveModel;
                var models = secondModel is not null && secondModel.Name != first.Name ? new[] { first, secondModel } : [first];
                if (secondModel is not null && models.Length == 1)
                {
                    output.WriteLine($"model2 is the same deployment as the run's model ({first.Name}); an eval set needs distinct models, running on one");
                }

                output.WriteLine($"eval set  : security_guide, popularity on {string.Join(", ", models.Select(model => model.Name))} in {Path.GetFullPath(logDir)} (retry_attempts {retryAttempts}{(maxTasks is { } max ? $", max_tasks {max}" : "")})");
                var result = await EvalsetRun.RunAsync(logDir, models, maxTasks, retryAttempts, new ProgressReporter(output), cancellationToken).ConfigureAwait(false);
                var summary = EvalsetRun.Summarize(result, logDir);
                output.WriteLine(summary);
                var reply = new ChatMessageAssistant(summary, model: state.Model, source: "generate");
                state.Messages.Add(reply);
                state.Output = new ModelOutput { Model = state.Model, Choices = [new ChatCompletionChoice(reply, StopReason.Stop)] };
                if (!result.Success)
                {
                    throw new InvalidOperationException($"Did not successfully complete all tasks in '{logDir}'.");
                }

                return state;
            },
        };
    }

    /// <summary>
    /// The eval set's reporter: forwards the runner's messages (the retry notices), reports sample errors as they
    /// happen and prints a line every 25 completed samples, instead of one line per sample start and completion.
    /// </summary>
    public sealed class ProgressReporter(TextWriter output) : IEvalReporter
    {
        /// <summary>Completed samples between progress lines.</summary>
        public const int Every = 25;

        private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));
        private readonly object _sync = new();
        private int _completed;

        public void SampleStarted(object id, int epoch)
        {
        }

        public void SampleCompleted(EvalSample sample)
        {
            ArgumentNullException.ThrowIfNull(sample);
            lock (_sync)
            {
                _completed++;
                if (sample.Error is { } error)
                {
                    _output.WriteLine($"  sample {sample.Id} error: {FirstLine(error.Message)}");
                }
                else if (_completed % Every == 0)
                {
                    _output.WriteLine($"  {_completed} samples completed");
                }
            }
        }

        public void Message(string text)
        {
            lock (_sync)
            {
                _output.WriteLine($"  {text}");
            }
        }

        private static string FirstLine(string text)
        {
            var index = text.IndexOfAny(['\r', '\n']);
            return index < 0 ? text : text[..index];
        }
    }
}
