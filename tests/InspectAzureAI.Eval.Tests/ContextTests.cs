using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>The ambient sample services: <c>Store</c>, limits, transcript spans and <c>SampleContext</c> scoping.</summary>
public class ContextTests
{
    private static SampleContext NewContext(SandboxEnvironments? sandboxes = null, Limits? limits = null) =>
        new() { ActiveModel = new Model(new ScriptedModelApi()), Sandboxes = sandboxes, Limits = limits ?? new Limits() };

    [Fact]
    public void store_get_set_contains_and_default_initialisation()
    {
        var store = new Store();

        store.Set("a", 1);
        var defaulted = store.Get("b", "fallback");

        Assert.Equal(1, store.Get("a"));
        Assert.Equal(1, store.Get("a", 0));
        Assert.Equal("fallback", defaulted);
        Assert.True(store.Contains("b"));
        Assert.Null(store.Get("missing"));
        Assert.Equal(["a", "b"], store.Keys.Order());
        store.Delete("a");
        Assert.False(store.Contains("a"));
        Assert.Equal("fallback", store.ToDictionary()["b"]);
    }

    [Fact]
    public void message_limit_reports_reached_and_exceeded_like_python()
    {
        var limits = new Limits { MessageLimit = 5 };

        limits.CheckMessageLimit(4);
        limits.CheckMessageLimit(5, raiseForEqual: false);
        var reached = Assert.Throws<LimitExceededException>(() => limits.CheckMessageLimit(5));
        var exceeded = Assert.Throws<LimitExceededException>(() => limits.CheckMessageLimit(6, raiseForEqual: false));

        Assert.Equal("Message limit reached. count: 5; limit: 5", reached.Message);
        Assert.Equal("Message limit exceeded. count: 6; limit: 5", exceeded.Message);
        Assert.Equal("message", reached.Type);
        Assert.Equal(5, reached.Value);
        new Limits().CheckMessageLimit(int.MaxValue);
    }

    [Fact]
    public void token_limit_accumulates_usage_and_raises_once_exceeded()
    {
        var limits = new Limits { TokenLimit = 1000 };

        limits.AddUsage(new ModelUsage(400, 100, 500), "m1");
        limits.AddUsage(new ModelUsage(400, 100, 500), "m2");
        var ex = Assert.Throws<LimitExceededException>(() => limits.AddUsage(new ModelUsage(1, 1, 2), "m1"));

        Assert.Equal("token", ex.Type);
        Assert.Equal("1,000", ex.LimitStr);
        Assert.Equal("Token limit exceeded. value: 1,002; limit: 1,000", ex.Message);
        Assert.Equal(1002, limits.TotalUsage.TotalTokens);
        Assert.Equal(new ModelUsage(401, 101, 502), limits.UsageByModel["m1"]);
    }

    [Fact]
    public void time_limit_raises_once_elapsed()
    {
        var expired = new Limits { TimeLimit = TimeSpan.FromSeconds(30), StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var fresh = new Limits { TimeLimit = TimeSpan.FromMinutes(5) };

        var ex = Assert.Throws<LimitExceededException>(expired.CheckTimeLimit);
        fresh.CheckTimeLimit();

        Assert.Equal("time", ex.Type);
        Assert.Equal("Time limit exceeded. limit: 30 seconds", ex.Message);
        Assert.True(ex.Value >= 59);
    }

    [Theory]
    [InlineData(1000, "1,000")]
    [InlineData(5, "5")]
    [InlineData(1.5, "1.50")]
    [InlineData(1234.567, "1,234.57")]
    public void limit_formatting_matches_python(double value, string expected) => Assert.Equal(expected, LimitExceededException.FormatLimit(value));

    [Fact]
    public void transcript_spans_nest_and_stamp_span_ids()
    {
        var transcript = new Transcript();

        transcript.Info("outside", "x");
        using (transcript.Span("solver", "solver"))
        {
            transcript.Info("inner", new { a = 1 });
            using (transcript.Span("tool"))
            {
                transcript.Add(new ErrorEvent("boom"));
            }
        }

        Assert.Null(transcript.CurrentSpanId);
        var events = transcript.Events;
        Assert.Equal(["info", "span_begin", "info", "span_begin", "error", "span_end", "span_end"], events.Select(e => e.Event));
        Assert.Null(events[0].SpanId);
        var outer = Assert.IsType<SpanBeginEvent>(events[1]);
        var inner = Assert.IsType<SpanBeginEvent>(events[3]);
        Assert.Equal("solver", outer.Name);
        Assert.Equal("solver", outer.Type);
        Assert.Null(outer.ParentId);
        Assert.Equal("span", inner.Type);
        Assert.Equal(outer.Id, inner.ParentId);
        Assert.Equal(outer.Id, events[2].SpanId);
        Assert.Equal(inner.Id, events[4].SpanId);
        Assert.Equal(inner.Id, Assert.IsType<SpanEndEvent>(events[5]).Id);
        Assert.Equal(outer.Id, Assert.IsType<SpanEndEvent>(events[6]).Id);
        Assert.Equal(1, Assert.IsType<InfoEvent>(events[2]).Data!["a"]!.GetValue<int>());
        Assert.Equal("x", Assert.IsAssignableFrom<JsonValue>(Assert.IsType<InfoEvent>(events[0]).Data).GetValue<string>());
    }

    [Fact]
    public void transcript_events_serialize_with_the_event_discriminator()
    {
        var json = JsonSerializer.Serialize(new InfoEvent("src", JsonValue.Create("data")) { SpanId = "s1" });
        var node = JsonNode.Parse(json)!;

        Assert.Equal("info", node["Event"]!.GetValue<string>());
        Assert.Equal("src", node["Source"]!.GetValue<string>());
        Assert.Equal("data", node["Data"]!.GetValue<string>());
        Assert.Equal("s1", node["SpanId"]!.GetValue<string>());
        Assert.NotNull(node["Timestamp"]);
    }

    [Fact]
    public async Task sample_context_flows_across_awaits_and_restores_on_dispose()
    {
        var outer = NewContext();
        var inner = NewContext();
        Assert.Null(SampleContext.Current);

        using (SampleContext.Begin(outer))
        {
            await Task.Yield();
            Assert.Same(outer, SampleContext.Current);
            await Task.Run(async () =>
            {
                Assert.Same(outer, SampleContext.Current);
                using (SampleContext.Begin(inner))
                {
                    await Task.Delay(1);
                    Assert.Same(inner, SampleContext.Current);
                }

                Assert.Same(outer, SampleContext.Current);
            });
            Assert.Same(outer, SampleContext.Current);
            Assert.Same(outer, SampleContext.Require());
        }

        Assert.Null(SampleContext.Current);
        Assert.Throws<InvalidOperationException>(SampleContext.Require);
    }

    [Fact]
    public async Task concurrent_flows_keep_their_own_context()
    {
        var a = NewContext();
        var b = NewContext();

        var seen = await Task.WhenAll(
            Task.Run(async () => { using var s = SampleContext.Begin(a); await Task.Delay(5); return SampleContext.Current; }),
            Task.Run(async () => { using var s = SampleContext.Begin(b); await Task.Delay(1); return SampleContext.Current; }));

        Assert.Same(a, seen[0]);
        Assert.Same(b, seen[1]);
        Assert.Null(SampleContext.Current);
    }

    [Fact]
    public void sandbox_resolution_follows_the_python_rules()
    {
        var first = new FakeSandboxEnvironment();
        var second = new FakeSandboxEnvironment();
        var none = NewContext();
        var single = NewContext(SandboxEnvironments.Create([new("only", first)]));
        var multiple = NewContext(SandboxEnvironments.Create([new("default", first), new("db", second)]));

        var ex = Assert.Throws<InvalidOperationException>(() => none.Sandbox());
        Assert.StartsWith("No sandbox environment has been provided for the current sample or task.", ex.Message);
        Assert.Same(first, single.Sandbox());
        Assert.Same(first, single.Sandbox("anything"));
        Assert.Same(first, multiple.Sandbox());
        Assert.Same(first, multiple.Sandbox("default"));
        Assert.Same(second, multiple.Sandbox("db"));
        Assert.Same(first, multiple.Sandboxes!.Default);
        Assert.Contains("'nope' is not a recognized environment name", Assert.Throws<ArgumentException>(() => multiple.Sandbox("nope")).Message);
    }

    [Fact]
    public void sandbox_environments_preserve_insertion_order()
    {
        var first = new FakeSandboxEnvironment();
        var second = new FakeSandboxEnvironment();

        var environments = SandboxEnvironments.Create([new("zeta", first), new("alpha", second)]);

        Assert.Equal(["zeta", "alpha"], environments.Environments.Keys);
        Assert.Same(first, environments.Default);
    }

    [Fact]
    public void suspended_limits_keep_accumulating_usage_without_raising()
    {
        var limits = new Limits { MessageLimit = 2, TokenLimit = 10, TimeLimit = TimeSpan.Zero };

        limits.Suspend();
        limits.AddUsage(new ModelUsage(20, 5, 25), "grader");
        limits.CheckMessageLimit(50);
        limits.CheckTokenLimit();
        limits.CheckTimeLimit();

        Assert.False(limits.Enforced);
        Assert.Equal(25, limits.TotalUsage.TotalTokens);
        Assert.Equal(25, limits.UsageByModel["grader"].TotalTokens);
    }
}
