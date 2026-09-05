using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Solvers;

/// <summary>Port of <c>solver/_task_state.py</c> <c>TaskState</c>: the mutable state a solver chain works on.</summary>
public sealed class TaskState
{
    private ModelOutput _output;

    public TaskState(
        string model,
        object sampleId,
        int epoch,
        SampleInput input,
        IEnumerable<ChatMessage> messages,
        Target? target = null,
        IReadOnlyList<string>? choices = null,
        ModelOutput? output = null,
        int? messageLimit = null,
        int? tokenLimit = null,
        IDictionary<string, object?>? metadata = null,
        Store? store = null,
        string? sampleUuid = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(sampleId);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(messages);
        Model = model;
        SampleId = sampleId;
        Epoch = epoch;
        Input = input;
        Messages = messages.ToList();
        Target = target ?? Target.Empty;
        Choices = choices ?? [];
        _output = output ?? new ModelOutput { Model = model };
        MessageLimit = messageLimit;
        TokenLimit = tokenLimit;
        Metadata = metadata is null ? new Dictionary<string, object?>(StringComparer.Ordinal) : new Dictionary<string, object?>(metadata, StringComparer.Ordinal);
        Store = store ?? new Store();
        Uuid = sampleUuid ?? ShortUuid.Generate();
    }

    public string Model { get; }

    public object SampleId { get; }

    public int Epoch { get; }

    public SampleInput Input { get; }

    /// <summary>Port of <c>input_text</c>: the string input, or the last user message of a message-list input.</summary>
    public string InputText
    {
        get
        {
            if (Input.Text is { } text)
            {
                return text;
            }

            var user = (Input.Messages ?? []).OfType<ChatMessageUser>().LastOrDefault();
            return user?.Text ?? throw new InvalidOperationException("input_text requested from TaskState but none available");
        }
    }

    /// <summary>Port of <c>user_prompt</c>: the last user message in <see cref="Messages"/>.</summary>
    public ChatMessageUser UserPrompt =>
        Messages.OfType<ChatMessageUser>().LastOrDefault()
        ?? throw new InvalidOperationException("user_prompt requested from TaskState but none available");

    public Dictionary<string, object?> Metadata { get; set; }

    public List<ChatMessage> Messages { get; set; }

    public ModelOutput Output
    {
        get => _output;
        set => _output = value ?? throw new ArgumentNullException(nameof(value));
    }

    public Store Store { get; }

    public List<ToolDef> Tools { get; set; } = [];

    public ToolChoice? ToolChoice { get; set; }

    private int? _messageLimit;

    private int? _tokenLimit;

    /// <summary>Port of <c>message_limit</c>: reads and writes the sample's scoped <see cref="Context.MessageLimit"/> once the runner has attached it.</summary>
    public int? MessageLimit
    {
        get => MessageLimitNode is { } node ? node.Limit : _messageLimit;
        set
        {
            _messageLimit = value;
            if (MessageLimitNode is { } node)
            {
                node.Limit = value;
            }
        }
    }

    /// <summary>Port of <c>token_limit</c>: reads and writes the sample's scoped <see cref="Context.TokenLimit"/> once the runner has attached it.</summary>
    public int? TokenLimit
    {
        get => TokenLimitNode is { } node ? node.Limit : _tokenLimit;
        set
        {
            _tokenLimit = value;
            if (TokenLimitNode is { } node)
            {
                node.Limit = value;
            }
        }
    }

    /// <summary>The sample-level message limit scope (Python's <c>_message_limit</c>), attached by the runner.</summary>
    internal MessageLimit? MessageLimitNode { get; private set; }

    /// <summary>The sample-level token limit scope (Python's <c>_token_limit</c>), attached by the runner.</summary>
    internal TokenLimit? TokenLimitNode { get; private set; }

    /// <summary>Binds the sample-level scopes the runner enters around the solvers so the limit properties read and write them.</summary>
    internal void AttachLimits(MessageLimit messageLimit, TokenLimit tokenLimit)
    {
        MessageLimitNode = messageLimit;
        TokenLimitNode = tokenLimit;
    }

    /// <summary>Port of <c>token_usage</c>: total tokens recorded against the current sample.</summary>
    public int TokenUsage => SampleContext.Current?.Limits.TotalUsage.TotalTokens ?? 0;

    public bool Completed { get; set; }

    public Target Target { get; }

    public Dictionary<string, Score>? Scores { get; set; }

    public string Uuid { get; }

    public IReadOnlyList<string> Choices { get; }

    /// <summary>
    /// Port of the copy <c>score(AgentState)</c> makes of <c>sample_state()</c>: the same sample identity, input,
    /// target, choices, limits, metadata, store, tools and scores, with <paramref name="messages"/> and
    /// <paramref name="output"/> swapped in.
    /// </summary>
    public TaskState WithMessages(IEnumerable<ChatMessage> messages, ModelOutput output)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(output);
        return new TaskState(Model, SampleId, Epoch, Input, messages, Target, Choices, output, MessageLimit, TokenLimit, Metadata, Store, Uuid)
        {
            Tools = Tools.ToList(),
            ToolChoice = ToolChoice,
            Completed = Completed,
            Scores = Scores,
        };
    }
}
