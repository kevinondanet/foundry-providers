using InspectAzureAI.Eval.Concurrency;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Concurrency = InspectAzureAI.Eval.Concurrency.Concurrency;
using Eval = InspectAzureAI.Eval.Runner.Eval;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

/// <summary>
/// Async-safety of the fan-outs and of the process-global concurrency registry: <c>fork</c> and <c>multi_scorer</c>
/// cancel their siblings on the first failure like Python's <c>tg_collect</c>, and a run's
/// <see cref="DynamicSampleLimiter"/> unsubscribes from the registry and its controller when the run ends.
/// The registries are process-global, so every test starts from a reset.
/// </summary>
public sealed class AsyncSafetyTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly string _logDir = Directory.CreateTempSubdirectory("async-safety-tests").FullName;

    public AsyncSafetyTests() => Concurrency.Init();

    public void Dispose()
    {
        Concurrency.Init();
        Directory.Delete(_logDir, recursive: true);
    }

    private static TaskState State() =>
        new("scripted", 0, 0, "question", [new ChatMessageUser("question")], output: ModelOutput.FromContent("model", ""));

    private static void SaturatedSuccesses(AdaptiveConcurrencyController controller, int count)
    {
        for (var i = 0; i < count; i++)
        {
            controller.MaxBorrowedThisRound = controller.Concurrency;
            controller.NotifySuccess();
        }
    }

    // ---------------------------------------------------------------- tg_collect

    [Fact]
    public async Task tg_collect_returns_results_in_order_and_rethrows_the_first_failure_after_cancelling_the_rest()
    {
        var ordered = await AsyncUtil.TgCollect<int>([async ct => { await Task.Delay(20, ct); return 1; }, _ => Task.FromResult(2)]);
        Assert.Equal([1, 2], ordered);

        var blockedCancelled = false;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AsyncUtil.TgCollect<int>(
        [
            async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    blockedCancelled = true;
                    throw;
                }

                return 1;
            },
            _ => throw new InvalidOperationException("boom"),
        ]).WaitAsync(TestTimeout));
        Assert.Equal("boom", ex.Message);
        Assert.True(blockedCancelled);

        // caller cancellation wins over a branch failure and surfaces as cancellation
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AsyncUtil.TgCollect<int>([_ => throw new InvalidOperationException("never started")], cts.Token));
    }

    // ---------------------------------------------------------------- fork

    [Fact]
    public async Task fork_cancels_the_other_branches_when_one_fails()
    {
        using var scope = new SampleContextScope();
        var generate = GenerateLoop.Create(scope.Model);
        var blockedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedCancelled = false;
        Solver blocked = async (state, _, cancellationToken) =>
        {
            blockedStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                blockedCancelled = true;
                throw;
            }

            return state;
        };
        Solver failing = async (_, _, _) =>
        {
            await blockedStarted.Task;
            throw new InvalidOperationException("boom");
        };

        // without sibling cancellation the blocked branch never returns and fork hangs (WaitAsync would time out)
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Solvers.Fork(State(), [blocked, failing], generate).WaitAsync(TestTimeout));
        Assert.Equal("boom", ex.Message);
        Assert.True(blockedCancelled);

        // every subtask span ends, the cancelled branch's included, and the parent's span stack is intact
        var begins = scope.Transcript.Events.OfType<SpanBeginEvent>().Where(e => e.Type == "subtask").ToList();
        Assert.Equal(2, begins.Count);
        Assert.Equal(scope.Transcript.Events.OfType<SpanBeginEvent>().Select(e => e.Id).Order(), scope.Transcript.Events.OfType<SpanEndEvent>().Select(e => e.Id).Order());
        Assert.Null(scope.Transcript.CurrentSpanId);
    }

    // ---------------------------------------------------------------- multi_scorer

    [Fact]
    public async Task multi_scorer_cancels_the_other_scorers_when_one_fails()
    {
        var blockedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedCancelled = false;
        ScorerDef blocked = new("blocked", async (_, _, cancellationToken) =>
        {
            blockedStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                blockedCancelled = true;
                throw;
            }

            return new Score(1);
        }, []);
        ScorerDef failing = new("failing", async (_, _, _) =>
        {
            await blockedStarted.Task;
            throw new InvalidOperationException("boom");
        }, []);

        var scorer = Scorers.MultiScorer([blocked, failing], Reducers.Mean());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => scorer.Score(State(), new Target("x"), CancellationToken.None).WaitAsync(TestTimeout));
        Assert.Equal("boom", ex.Message);
        Assert.True(blockedCancelled);
    }

    // ---------------------------------------------------------------- DynamicSampleLimiter

    [Fact]
    public void dynamic_sample_limiter_dispose_unsubscribes_from_the_registry_and_its_controller()
    {
        var adaptive = AdaptiveConcurrency.Create(min: 1, max: 80, start: 10);
        var limiter = new DynamicSampleLimiter(adaptive, "k");
        Assert.Equal(1, Concurrency.ControllerCreatedObserverCount);
        var controller = (AdaptiveConcurrencyController)Concurrency.GetOrCreateSemaphore("m", 10, key: "k", adaptive: adaptive);
        Assert.Same(controller, limiter.Controller);
        Assert.Equal(1, controller.ObserverCount);

        limiter.Dispose();

        Assert.Equal(0, Concurrency.ControllerCreatedObserverCount);
        Assert.Equal(0, controller.ObserverCount);
        Assert.Same(controller, limiter.Controller);

        // a scale change no longer reaches the disposed limiter; its last capacity and leases keep working
        SaturatedSuccesses(controller, 10);
        Assert.Equal(20, controller.Concurrency);
        Assert.Equal(15, limiter.Limit);
        Assert.Equal(0, limiter.InUse);
        limiter.Dispose();
        Assert.Equal(0, Concurrency.ControllerCreatedObserverCount);

        // disposed before its controller exists: the controller created later is not adopted
        var early = new DynamicSampleLimiter(adaptive, "k-late");
        Assert.Equal(1, Concurrency.ControllerCreatedObserverCount);
        early.Dispose();
        var late = (AdaptiveConcurrencyController)Concurrency.GetOrCreateSemaphore("late", 10, key: "k-late", adaptive: adaptive);
        Assert.Null(early.Controller);
        Assert.Equal(0, late.ObserverCount);
        Assert.Equal(15, early.Limit);

        // a limiter that is still alive keeps both subscriptions
        var alive = new DynamicSampleLimiter(adaptive, "k-late");
        Assert.Same(late, alive.Controller);
        Assert.Equal(1, late.ObserverCount);
        Assert.Equal(1, Concurrency.ControllerCreatedObserverCount);
        alive.Dispose();
    }

    [Fact]
    public async Task the_runner_disposes_its_sample_limiter_after_each_run()
    {
        for (var run = 1; run <= 2; run++)
        {
            var task = new EvalTask { Name = $"run{run}", Dataset = new MemoryDataset([new Sample("q") { Target = "ok" }], name: "one"), Scorers = [Scorers.Includes()] };
            var log = await Eval.RunAsync(task, new EvalOptions { Model = new Model(new ScriptedModelApi(ScriptedTurn.Text("ok"))), LogDir = _logDir }).WaitAsync(TestTimeout);
            Assert.Equal(EvalStatus.Success, log.Status);
            Assert.Null(log.Eval.Config.MaxSamples);
        }

        // the adaptive default path created the model's controller; neither it nor the registry keeps a finished run's limiter
        var controllers = Concurrency.AdaptiveControllers();
        Assert.NotEmpty(controllers);
        Assert.All(controllers, controller => Assert.Equal(0, controller.ObserverCount));
        Assert.Equal(0, Concurrency.ControllerCreatedObserverCount);
    }
}
