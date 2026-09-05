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
        Choices = new Choices(choices ?? []);
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

    public int? MessageLimit { get; set; }

    public int? TokenLimit { get; set; }

    /// <summary>Port of <c>token_usage</c>: total tokens recorded against the current sample.</summary>
    public int TokenUsage => SampleContext.Current?.Limits.TotalUsage.TotalTokens ?? 0;

    public bool Completed { get; set; }

    public Target Target { get; }

    public Dictionary<string, Score>? Scores { get; set; }

    public string Uuid { get; }

    /// <summary>Port of <c>TaskState.choices</c>: the sample's choices (empty when the sample has none), marked and shuffled by the multiple choice solver.</summary>
    public Choices Choices { get; private set; }

    /// <summary>
    /// Port of the copy <c>score(AgentState)</c> makes of <c>sample_state()</c>: the same sample identity, input,
    /// target, choices, limits, metadata, store, tools and scores, with <paramref name="messages"/> and
    /// <paramref name="output"/> swapped in.
    /// </summary>
    public TaskState WithMessages(IEnumerable<ChatMessage> messages, ModelOutput output)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(output);
        return new TaskState(Model, SampleId, Epoch, Input, messages, Target, null, output, MessageLimit, TokenLimit, Metadata, Store, Uuid)
        {
            Choices = Choices,
            Tools = Tools.ToList(),
            ToolChoice = ToolChoice,
            Completed = Completed,
            Scores = Scores,
        };
    }

    /// <summary>
    /// Port of <c>deepcopy(state)</c> as <c>fork()</c> uses it: an independent state with its own message list,
    /// metadata, tools, choices, scores and a copy of the store (same uuid). Messages, output and store values are
    /// immutable records or shared as-is.
    /// </summary>
    public TaskState Copy() =>
        new TaskState(Model, SampleId, Epoch, Input, Messages.ToList(), Target, null, Output, MessageLimit, TokenLimit, Metadata, new Store(Store.ToDictionary()), Uuid)
        {
            Choices = Choices.Clone(),
            Tools = Tools.ToList(),
            ToolChoice = ToolChoice,
            Completed = Completed,
            Scores = Scores is null ? null : new Dictionary<string, Score>(Scores, StringComparer.Ordinal),
        };
}
