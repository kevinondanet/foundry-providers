using System.Text.Json;
using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using Microsoft.Extensions.AI;
using MafChatMessage = Microsoft.Extensions.AI.ChatMessage;
using Model = InspectAzureAI.Eval.Model.Model;

namespace InspectAzureAI.Maf.Tests;

/// <summary>The <see cref="IChatClient"/> over an <see cref="AgentBridge"/>, driven directly with a scripted model.</summary>
public class InspectChatClientTests
{
    private const string Prompt = "What is the capital of France?";

    private static (InspectChatClient Client, AgentBridge Bridge, ScriptedModelApi Api) Client(params ScriptedTurn[] turns)
    {
        var api = new ScriptedModelApi(turns);
        var bridge = new AgentBridge(new AgentState([new ChatMessageUser(Prompt)]), new Model(api));
        return (new InspectChatClient(bridge), bridge, api);
    }

    [Fact]
    public async Task requests_are_generated_by_the_bridged_model_and_tracked_in_the_state()
    {
        var (client, bridge, api) = Client(ScriptedTurn.Text("Paris", new ModelUsage(12, 1, 13)));

        var response = await client.GetResponseAsync([new MafChatMessage(ChatRole.User, Prompt)], new ChatOptions { Instructions = "Answer in one word." });

        Assert.Equal("Paris", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(12, response.Usage!.InputTokenCount);
        var request = Assert.Single(api.Requests);
        Assert.Equal("Answer in one word.", Assert.IsType<ChatMessageSystem>(request.Input[0]).Text);
        Assert.Equal(Prompt, Assert.IsType<ChatMessageUser>(request.Input[1]).Text);
        Assert.Empty(request.Tools);
        Assert.Equal("Paris", bridge.State.Output.Completion);
        Assert.Equal("Paris", Assert.IsType<ChatMessageAssistant>(bridge.State.Messages[^1]).Text);
    }

    [Fact]
    public async Task function_tools_are_offered_to_the_model_and_its_calls_come_back_as_function_calls()
    {
        var (client, _, api) = Client(ScriptedTurn.ToolCall("shout", new { text = "hi" }, id: "call_1", text: "Shouting."));
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create((string text) => text.ToUpperInvariant(), "shout", "Upper-cases text.")],
            ToolMode = ChatToolMode.RequireAny,
        };

        var response = await client.GetResponseAsync([new MafChatMessage(ChatRole.User, "Shout hi")], options);

        var call = Assert.Single(response.Messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("shout", call.Name);
        Assert.Equal("hi", Assert.IsType<JsonElement>(call.Arguments!["text"]).GetString());
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        var request = Assert.Single(api.Requests);
        Assert.Equal("shout", Assert.Single(request.Tools).Name);
        Assert.Same(ToolChoice.Any, request.ToolChoice);
    }

    [Fact]
    public async Task streaming_yields_the_finished_response()
    {
        var (client, _, _) = Client(ScriptedTurn.Text("Paris"));

        var response = await client.GetStreamingResponseAsync([new MafChatMessage(ChatRole.User, Prompt)]).ToChatResponseAsync();

        Assert.Equal("Paris", response.Text);
    }

    [Fact]
    public async Task the_requested_model_id_selects_a_bridge_alias()
    {
        var primary = new ScriptedModelApi(ScriptedTurn.Text("primary"));
        var fast = new ScriptedModelApi([ScriptedTurn.Text("fast")], "fast-model");
        var bridge = new AgentBridge(new AgentState([new ChatMessageUser(Prompt)]), new Model(primary), new Dictionary<string, Model> { ["fast"] = new(fast) });
        using var client = new InspectChatClient(bridge);

        var response = await client.GetResponseAsync([new MafChatMessage(ChatRole.User, Prompt)], new ChatOptions { ModelId = "fast" });

        Assert.Equal("fast", response.Text);
        Assert.Empty(primary.Requests);
        Assert.Single(fast.Requests);
    }

    [Fact]
    public void services_expose_the_metadata_and_the_bridge()
    {
        var (client, bridge, _) = Client();

        var metadata = Assert.IsType<ChatClientMetadata>(client.GetService(typeof(ChatClientMetadata)));
        Assert.Equal("inspect", metadata.ProviderName);
        Assert.Equal("scripted", metadata.DefaultModelId);
        Assert.Same(bridge, client.GetService(typeof(AgentBridge)));
        Assert.Same(client, client.GetService(typeof(IChatClient)));
        Assert.Null(client.GetService(typeof(IChatClient), "keyed"));
    }
}
