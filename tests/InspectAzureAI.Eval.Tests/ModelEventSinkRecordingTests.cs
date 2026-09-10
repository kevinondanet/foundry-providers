using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Model;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary><see cref="IModelEventSink.OnRecording"/>: sinks rewrite a model event (and add events before it) just before it lands in the transcript.</summary>
public class ModelEventSinkRecordingTests
{
    private sealed class PlainSink : IModelEventSink
    {
        public List<ModelEvent> Seen { get; } = [];

        public void OnModelEvent(ModelEvent e) => Seen.Add(e);
    }

    /// <summary>Tags the event (span id, metadata, tool-call views) and optionally records an event of its own first.</summary>
    private sealed class RewritingSink(string tag, Func<ModelEvent, ModelEvent>? rewrite = null, bool addSpanEnd = false) : IModelEventSink
    {
        public List<ModelEvent> Recording { get; } = [];

        public List<ModelEvent> Seen { get; } = [];

        public void OnModelEvent(ModelEvent e) => Seen.Add(e);

        public ModelEvent OnRecording(ModelEvent e)
        {
            Recording.Add(e);
            if (addSpanEnd)
            {
                SampleContext.Current?.Transcript.Add(new SpanEndEvent($"span-before-{tag}"));
            }

            var tags = e.Metadata is { } existing && existing.TryGetValue("tags", out var value) && value is string text ? text + "," + tag : tag;
            var tagged = e with { Metadata = new Dictionary<string, object?> { ["tags"] = tags } };
            return rewrite?.Invoke(tagged) ?? tagged;
        }
    }

    private static ModelEvent WithViews(ModelEvent e) => e with
    {
        SpanId = "agent-span",
        Output = e.Output with
        {
            Choices = e.Output.Choices.Select(choice => choice with
            {
                Message = choice.Message with
                {
                    ToolCalls = choice.Message.ToolCalls?.Select(call => call with { View = new ToolCallContent("markdown", "```bash\nls\n```") { Title = "bash" } }).ToArray(),
                },
            }).ToArray(),
        },
    };

    [Fact]
    public void the_default_on_recording_is_the_identity()
    {
        IModelEventSink sink = new PlainSink();
        var e = new ModelEvent { Model = "m", Input = [], ToolChoice = ToolChoice.Auto, Config = new GenerateConfig(), Output = new ModelOutput() };

        Assert.Same(e, sink.OnRecording(e));
    }

    [Fact]
    public async Task a_returned_copy_is_what_the_transcript_records()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.ToolCall("bash", new { cmd = "ls" }, id: "call_1")));
        var sink = new RewritingSink("bound", WithViews);
        var model = scope.Model.WithEventSink(sink);

        await model.GenerateAsync([new ChatMessageUser("list files")]);

        var recorded = Assert.Single(scope.Transcript.Events.OfType<ModelEvent>());
        Assert.Equal("agent-span", recorded.SpanId);
        var call = Assert.Single(recorded.Output.Message.ToolCalls!);
        Assert.Equal("bash", call.View!.Title);
        Assert.Null(Assert.Single(sink.Recording).Output.Message.ToolCalls![0].View);
    }

    [Fact]
    public async Task events_added_while_recording_precede_the_model_event()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("done")));
        var model = scope.Model.WithEventSink(new RewritingSink("bound", addSpanEnd: true));

        await model.GenerateAsync([new ChatMessageUser("hi")]);

        var events = scope.Transcript.Events;
        var spanEnd = events.OfType<SpanEndEvent>().Single();
        var modelEvent = events.OfType<ModelEvent>().Single();
        Assert.True(events.ToList().IndexOf(spanEnd) < events.ToList().IndexOf(modelEvent));
        Assert.Equal("span-before-bound", spanEnd.Id);
    }

    [Fact]
    public async Task the_bound_sink_then_the_ambient_sink_rewrite_and_both_observe_the_final_copy()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("done")));
        var bound = new RewritingSink("bound");
        var ambient = new RewritingSink("ambient");
        var model = scope.Model.WithEventSink(bound);

        using (ModelEventSinks.Install(ambient))
        {
            await model.GenerateAsync([new ChatMessageUser("hi")]);
        }

        var recorded = Assert.Single(scope.Transcript.Events.OfType<ModelEvent>());
        Assert.Equal("bound,ambient", recorded.Metadata!["tags"]);
        Assert.Null(Assert.Single(bound.Recording).Metadata);
        Assert.Equal("bound", Assert.Single(ambient.Recording).Metadata!["tags"]);
        Assert.Equal("bound,ambient", Assert.Single(bound.Seen).Metadata!["tags"]);
        Assert.Equal("bound,ambient", Assert.Single(ambient.Seen).Metadata!["tags"]);
        Assert.Same(recorded.Output, bound.Seen[0].Output);
    }

    [Fact]
    public async Task a_sink_that_throws_while_recording_fails_the_generation()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("done")));
        var model = scope.Model.WithEventSink(new RewritingSink("bound", _ => throw new InvalidOperationException("sink broke")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => model.GenerateAsync([new ChatMessageUser("hi")]));

        Assert.Equal("sink broke", ex.Message);
        Assert.Empty(scope.Transcript.Events.OfType<ModelEvent>());
    }

    [Fact]
    public async Task existing_sinks_without_on_recording_record_unchanged_events()
    {
        using var scope = new SampleContextScope(new ScriptedModelApi(ScriptedTurn.Text("done")));
        var sink = new PlainSink();
        var model = new Model(scope.Api) { EventSink = sink };

        await model.GenerateAsync([new ChatMessageUser("hi")]);

        // the transcript stores its own stamped copy (working start), so compare identity by uuid and content by reference
        var recorded = Assert.Single(scope.Transcript.Events.OfType<ModelEvent>());
        var seen = Assert.Single(sink.Seen);
        Assert.Equal(recorded.Uuid, seen.Uuid);
        Assert.Same(recorded.Output, seen.Output);
        Assert.Null(seen.Metadata);
    }
}
