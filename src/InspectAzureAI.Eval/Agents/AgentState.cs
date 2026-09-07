using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Agents;

/// <summary>
/// Port of <c>agent/_agent.py</c> <c>AgentState</c>: the conversation an agent works on. <see cref="Output"/>
/// is synthesized from the last assistant message until it is set explicitly, as in Python.
/// </summary>
public sealed class AgentState
{
    private ModelOutput? _output;

    public AgentState(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        Messages = messages.ToList();
    }

    public List<ChatMessage> Messages { get; set; }

    public ModelOutput Output
    {
        get
        {
            if (_output is not null)
            {
                return _output;
            }

            var last = Messages.OfType<ChatMessageAssistant>().LastOrDefault();
            return last is null
                ? new ModelOutput()
                : new ModelOutput { Model = last.Model ?? "", Choices = [new ChatCompletionChoice(last, StopReason.Stop)] };
        }
        set => _output = value ?? throw new ArgumentNullException(nameof(value));
    }
}
