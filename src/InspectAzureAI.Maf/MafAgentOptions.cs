using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Solvers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Model = InspectAzureAI.Eval.Model.Model;

namespace InspectAzureAI.Maf;

/// <summary>Options of <see cref="AgentFramework.Agent"/>: the Agent Framework agent to run and how its submissions are scored.</summary>
public sealed record MafAgentOptions
{
    public string Name { get; init; } = "maf-agent";

    public string Description { get; init; } = "Microsoft Agent Framework agent whose model calls are bridged to the Inspect model.";

    /// <summary>System instructions of the agent; <c>{submit}</c> is replaced by the submit tool's name. Defaults to <c>basic_agent</c>'s system message.</summary>
    public string? Instructions { get; init; } = Solvers.BasicAgentSystemMessage;

    /// <summary>Tools the agent may call (see <see cref="MafTools"/> for Inspect tools); the submit tool is added when <see cref="Submit"/> is set.</summary>
    public IReadOnlyList<AITool> Tools { get; init; } = [];

    /// <summary>
    /// Add a submit tool: a call to it ends the turn, the answer alone becomes the completion and the submit call
    /// and its result stay in the messages (as <c>basic_agent</c>, not <c>react</c>'s append-and-remove). Without it
    /// the run ends when the agent stops calling tools.
    /// </summary>
    public bool Submit { get; init; } = true;

    public string SubmitName { get; init; } = Solvers.BasicAgentSubmitName;

    public string SubmitDescription { get; init; } = Solvers.BasicAgentSubmitDescription;

    /// <summary>Scored attempts, as for <c>react</c>: an incorrect submission is answered with the incorrect message until the attempts run out.</summary>
    public AgentAttempts Attempts { get; init; } = new();

    /// <summary>Played back when the agent stops without submitting; <c>{submit}</c> is the submit tool's name.</summary>
    public string ContinueMessage { get; init; } = Solvers.BasicAgentContinueMessage;

    /// <summary>
    /// The model to bridge to (default: the sample's active model). Its own <c>GenerateConfig</c> governs
    /// generation: the framework's <c>ChatOptions</c> parameters are mapped but the bridge strips them, as it does
    /// for Claude Code.
    /// </summary>
    public Model? Model { get; init; }

    public int? RetryRefusals { get; init; }

    public CachePolicy? Cache { get; init; }

    /// <summary>Approval policies for the tool calls the agent is about to run (the eval's own policies apply ambiently).</summary>
    public IReadOnlyList<ApprovalPolicy>? Approval { get; init; }

    /// <summary>
    /// Builds the Agent Framework agent over the Inspect chat client and the resolved tools (the caller's plus the
    /// submit tool). Null builds a <see cref="ChatClientAgent"/> from <see cref="Instructions"/>; supply a factory for
    /// a differently configured <see cref="ChatClientAgent"/> or agent middleware. The client handed in already
    /// performs function invocation, and the agent returned must keep it in its pipeline: the run's
    /// function-invocation middleware (tool events, submit, limits) is attached to it, and an agent that does not
    /// route calls through it (a workflow wrapped as an agent) fails when a tool is invoked.
    /// </summary>
    public Func<IChatClient, IList<AITool>, AIAgent>? AgentFactory { get; init; }

    /// <summary>
    /// Agent Framework's cap on model round-trips within one run (its own default is 40, after which it returns
    /// with the pending tool calls un-invoked). Inspect's message, token and time limits bound a run instead, so
    /// the default is unbounded.
    /// </summary>
    public int MaxToolIterations { get; init; } = int.MaxValue;

    /// <summary>Record a transcript tool event for every framework-side function the framework invokes (Inspect tools record their own through the executor).</summary>
    public bool RecordToolEvents { get; init; } = true;
}
