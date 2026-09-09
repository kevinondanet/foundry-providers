using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Examples.Surfer;

/// <summary>Port of <c>examples/surfer.py</c> <c>WebSurferState</c>: the web surfer's conversation, kept in the sample store per tool instance.</summary>
public sealed class WebSurferState : StoreModel
{
    /// <summary><c>messages: list[ChatMessage] = Field(default_factory=list)</c>.</summary>
    public List<ChatMessage> Messages
    {
        get => Get(new List<ChatMessage>());
        set => Set(value);
    }
}
