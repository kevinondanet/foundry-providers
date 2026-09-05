using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Agents;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of the <c>Compact.compact_input(messages, force)</c> result of <c>model/_compaction.py</c>: the
/// messages to send to the model and, for strategies that summarise, the message describing the compaction
/// (Python appends it to the conversation when it is not already the last compacted message).
/// </summary>
public sealed record CompactedInput(IReadOnlyList<ChatMessage> Messages, ChatMessage? Message = null);

/// <summary>
/// Port of <c>Compact.compact_input</c>: returns the (possibly compacted) input for the next generate.
/// <paramref name="force"/> is the overflow-recovery call, which must compact even below the usual threshold.
/// </summary>
public delegate Task<CompactedInput> CompactInput(IReadOnlyList<ChatMessage> messages, bool force, CancellationToken cancellationToken);

/// <summary>Port of <c>Compact.record_output</c>: lets the strategy update its token baseline from the generate that used <paramref name="input"/>.</summary>
public delegate Task RecordCompactionOutput(IReadOnlyList<ChatMessage> input, ModelOutput output, CancellationToken cancellationToken);

/// <summary>
/// The surface of the <c>Compact</c> protocol that the react agent uses. The compaction port supplies
/// implementations through <see cref="CreateAgentCompaction"/>; the react loop only calls these two delegates.
/// </summary>
public sealed record AgentCompaction(CompactInput CompactInput, RecordCompactionOutput? RecordOutput = null);

/// <summary>
/// Port of <c>_agent_compact</c>'s call to <c>compaction(strategy, prefix, tools, model)</c>: builds the
/// <see cref="AgentCompaction"/> for one agent run. <paramref name="prefix"/> is the always-preserved system
/// plus sample-input messages, <paramref name="tools"/> the agent's tools and <paramref name="model"/> the
/// agent's model when one was given explicitly (null means the active model, resolved by the strategy).
/// </summary>
public delegate AgentCompaction CreateAgentCompaction(IReadOnlyList<ChatMessage> prefix, IReadOnlyList<ToolDef> tools, Model? model);
