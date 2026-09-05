using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Tests;

/// <summary>Port of the stall-scope tests in <c>tests/model/test_model_stream.py</c> (<c>design/stream-idle-timeout.md</c>) at the observer level.</summary>
public class StallScopeTests
{
    [Fact]
    public void first_bump_arms_and_bumps_within_the_interval_are_throttled()
    {
        using var scope = new StallScope(100);
        Assert.Null(scope.Deadline);
        Assert.False(scope.Armed);

        scope.Bump();
        var armed = scope.Deadline;
        Assert.NotNull(armed);
        Assert.InRange(armed.Value - StallScope.Now, 99, 100.5);

        scope.Bump();
        Assert.Equal(armed, scope.Deadline);
    }

    [Fact]
    public void bump_interval_is_capped_at_a_tenth_of_the_timeout()
    {
        using var small = new StallScope(1);
        using var large = new StallScope(100);

        Assert.Equal(0.1, small.BumpInterval, 6);
        Assert.Equal(StallScope.StallDeadlineBumpInterval, large.BumpInterval);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StallScope(0));
    }

    [Fact]
    public async Task silence_after_the_first_report_fires_the_scope()
    {
        using var scope = new StallScope(0.2);
        scope.Bump();

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.True(scope.Fired);
        Assert.True(scope.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task reports_faster_than_the_timeout_keep_the_scope_alive()
    {
        using var scope = new StallScope(0.3);
        scope.Bump();
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            scope.Bump();
        }

        Assert.False(scope.Fired);
    }

    [Fact]
    public async Task an_armed_scope_never_fires_without_a_report()
    {
        using var scope = new StallScope(0.1);
        var observer = new ModelStreamObserver("m", null);
        observer.ArmStallScope(scope);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.False(scope.Armed);
        Assert.False(scope.Fired);
    }

    [Fact]
    public async Task observer_reports_bump_the_deadline_and_request_streaming()
    {
        using var scope = new StallScope(100);
        var observer = new ModelStreamObserver("m", null);
        observer.ArmStallScope(scope);
        Assert.Same(scope, observer.Stall);
        Assert.False(ModelStreamObserver.ModelStreamRequested());

        using (ModelStreamObserver.Install(observer))
        {
            Assert.True(ModelStreamObserver.ModelStreamRequested());
            ModelStreamObserver.ReportModelStreamStart();
            Assert.True(scope.Armed);

            await ModelStreamObserver.ReportModelStreamDeltaAsync(new StreamTextEvent("x"));   // degrades to a heartbeat without a handler
            ModelStreamObserver.ReportModelStreamProgress(3);
            Assert.Equal(2, observer.ProgressReports);
            Assert.False(observer.Delivered);
        }

        Assert.False(ModelStreamObserver.ModelStreamRequested());
    }

    [Fact]
    public void a_nested_observer_inherits_the_outer_stall_scope()
    {
        using var scope = new StallScope(100);
        var outer = new ModelStreamObserver("m", null);
        outer.ArmStallScope(scope);
        var inner = new ModelStreamObserver("m", _ => Task.CompletedTask);

        using (ModelStreamObserver.Install(outer))
        using (ModelStreamObserver.Install(inner))
        {
            Assert.Same(scope, inner.Stall);
            Assert.Same(inner, ModelStreamObserver.Current);
            ModelStreamObserver.ReportModelStreamStart();
            Assert.True(scope.Armed);
        }

        Assert.Null(ModelStreamObserver.Current);
        var standalone = new ModelStreamObserver("m", null);
        using (ModelStreamObserver.Install(standalone))
        {
            Assert.Null(standalone.Stall);
        }
    }
}
