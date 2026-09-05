using InspectAzureAI.Eval.Context;

namespace InspectAzureAI.Eval.Log;

/// <summary>
/// Port of the <c>TimelineContentItem</c> union of <c>event/_timeline.py</c>: an item of a
/// <see cref="TimelineSpan"/>, discriminated by <see cref="Type"/> ("event" or "span").
/// </summary>
public abstract record TimelineNode
{
    /// <summary>The JSON discriminator: "event" or "span".</summary>
    public abstract string Type { get; }
}

/// <summary>
/// Port of <c>event/_timeline.py</c> <c>TimelineEvent</c>: a reference to one transcript event. Python holds the
/// event object and serializes its uuid; this port keeps the uuid (<see cref="Event"/>) and resolves it on demand
/// with <see cref="Resolve"/>, as <c>timeline_load</c> does from the sample's events.
/// </summary>
public sealed record TimelineEvent(string Event) : TimelineNode
{
    public override string Type => "event";

    /// <summary>The referenced event, or null when no event in <paramref name="events"/> carries <see cref="Event"/> as its uuid.</summary>
    public TranscriptEvent? Resolve(IEnumerable<TranscriptEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events.FirstOrDefault(e => e.Uuid == Event);
    }
}

/// <summary>
/// Port of <c>event/_timeline.py</c> <c>TimelineSpan</c>: a span of execution (agent, scorer, tool or root).
/// <see cref="Name"/> is lower-cased, as Python's validator does.
/// </summary>
public sealed record TimelineSpan : TimelineNode
{
    private readonly string _name = "";

    public TimelineSpan(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public override string Type => "span";

    public string Id { get; init; }

    public string Name
    {
        get => _name;
        init => _name = (value ?? throw new ArgumentNullException(nameof(value))).ToLowerInvariant();
    }

    /// <summary>"agent", "init", "scorers", "branch", ... or null for the root.</summary>
    public string? SpanType { get; init; }

    public IReadOnlyList<TimelineNode> Content { get; init; } = [];

    public IReadOnlyList<TimelineSpan> Branches { get; init; } = [];

    /// <summary>The <c>AnchorEvent.anchor_id</c> a branch span forked from.</summary>
    public string? BranchedFrom { get; init; }

    public string? Description { get; init; }

    /// <summary>Whether this is a utility agent (a single-turn helper with a different system prompt).</summary>
    public bool Utility { get; init; }

    /// <summary>Whether this agent span was invoked as a tool (task / as_tool / handoff).</summary>
    public bool ToolInvoked { get; init; }

    public string? AgentResult { get; init; }

    public Outline? Outline { get; init; }
}

/// <summary>Port of <c>event/_timeline.py</c> <c>OutlineNode</c>: an outline entry referencing an event by uuid.</summary>
public sealed record OutlineNode(string Event)
{
    public IReadOnlyList<OutlineNode> Children { get; init; } = [];
}

/// <summary>Port of <c>event/_timeline.py</c> <c>Outline</c>: the hierarchical outline of an agent's events.</summary>
public sealed record Outline
{
    public IReadOnlyList<OutlineNode> Nodes { get; init; } = [];
}

/// <summary>Port of <c>event/_timeline.py</c> <c>Timeline</c>: a named view over a sample's transcript, stored in <see cref="EvalSample.Timelines"/>.</summary>
public sealed record Timeline(string Name, string Description, TimelineSpan Root);
