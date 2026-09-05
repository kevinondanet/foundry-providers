using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

/// <summary>Port of inspect_swe <c>_util/messages.py</c>: which user messages form the next prompt.</summary>
public class AgentPromptTests
{
    [Fact]
    public void all_user_messages_form_the_prompt_when_there_is_no_assistant_response()
    {
        var (prompt, hasAssistant) = AgentPrompt.BuildUserPrompt([new ChatMessageSystem("sys"), new ChatMessageUser("first"), new ChatMessageUser("second")]);

        Assert.Equal("first\n\nsecond", prompt);
        Assert.False(hasAssistant);
    }

    [Fact]
    public void only_user_messages_after_the_last_assistant_response_are_used()
    {
        var messages = new ChatMessage[]
        {
            new ChatMessageUser("old"),
            new ChatMessageAssistant("done"),
            new ChatMessageUser("follow-up"),
            new ChatMessageTool("tool output", toolCallId: "t1"),
            new ChatMessageUser("more"),
        };

        var (prompt, hasAssistant) = AgentPrompt.BuildUserPrompt(messages);
        var turn = AgentPrompt.GetUserTurn(messages);

        Assert.Equal("follow-up\n\nmore", prompt);
        Assert.True(hasAssistant);
        Assert.Equal(2, turn.Messages.Count);
    }

    [Fact]
    public void a_conversation_ending_with_an_assistant_message_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => AgentPrompt.BuildUserPrompt([new ChatMessageUser("q"), new ChatMessageAssistant("a")]));

        Assert.Equal("Messages input ends with an assistant messages.", ex.Message);
    }

    [Fact]
    public void non_text_content_is_dropped_with_a_warning_unless_handled()
    {
        ProviderLogger.Reset();
        var image = new ContentImage("data:image/png;base64,AAAA");
        var messages = new ChatMessage[] { new ChatMessageUser(new Content[] { new ContentText("look"), image }) };

        var (prompt, _) = AgentPrompt.BuildUserPrompt(messages);
        var warned = ProviderLogger.Warnings.ToArray();
        ProviderLogger.Reset();
        AgentPrompt.BuildUserPrompt(messages, ["image"]);

        Assert.Equal("look", prompt);
        Assert.Contains(warned, w => w.Contains("Input contains image content, which this agent does not support; it was dropped from the prompt."));
        Assert.Empty(ProviderLogger.Warnings);
        Assert.Same(image, Assert.Single(AgentPrompt.CollectUserImages(messages)));
    }

    [Fact]
    public void empty_input_yields_an_empty_prompt()
    {
        var (prompt, hasAssistant) = AgentPrompt.BuildUserPrompt([]);

        Assert.Equal("", prompt);
        Assert.False(hasAssistant);
    }
}
