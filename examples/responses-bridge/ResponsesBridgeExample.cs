using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Bridge;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Examples.Runner;
using InspectAzureAI.Maf;
using InspectAzureAI.Provider.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatMessage = InspectAzureAI.Provider.Core.ChatMessage;
using MafChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace InspectAzureAI.Examples.ResponsesBridge;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of <c>examples/responses-bridge.py</c> <c>responses_agent</c>: the smallest possible bridged agent. Python
/// opens <c>agent_bridge(state)</c>, makes one <c>client.responses.create(model="inspect", input=user_prompt(...).text)</c>
/// call with the stock OpenAI client (which the bridge redirects to the eval model) and returns <c>bridge.state</c>.
/// Deviation: there is no OpenAI Responses API client to patch in .NET, so the client is the in-process
/// <see cref="InspectChatClient"/> over an <see cref="AgentBridge"/>, driven by a Microsoft Agent Framework
/// <see cref="ChatClientAgent"/> with no tools (one model call, as the Python makes one); the request still names
/// the model <c>inspect</c> and the bridge tracks the conversation into its state exactly as Python's does.
/// </summary>
public static class ResponsesAgent
{
    /// <summary>The Python <c>@agent</c> function's name.</summary>
    public const string AgentName = "responses_agent";

    /// <summary>The model name the agent asks for; the bridge resolves it to the eval model (Python's <c>model="inspect"</c>).</summary>
    public const string BridgedModelName = "inspect";

    public static AgentDef Create() => new(AgentName, "A bridged agent that makes one model call with the sample's user prompt.", ExecuteAsync);

    private static async Task<AgentState> ExecuteAsync(AgentState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        // async with agent_bridge(state) as bridge:
        var bridge = new AgentBridge(state, SampleContext.Require().ActiveModel);
        using var client = new InspectChatClient(bridge);

        // client = AsyncOpenAI(); await client.responses.create(model="inspect", input=user_prompt(state.messages).text)
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions { ChatOptions = new ChatOptions { ModelId = BridgedModelName } });
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        await agent.RunAsync([new MafChatMessage(ChatRole.User, UserPrompt(state.Messages).Text)], session, cancellationToken: cancellationToken).ConfigureAwait(false);

        return bridge.State;
    }

    /// <summary>Port of <c>inspect_ai.model._prompt.user_prompt</c>: the last user message of the conversation (a <see cref="InvalidOperationException"/> when there is none).</summary>
    public static ChatMessageUser UserPrompt(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.OfType<ChatMessageUser>().LastOrDefault() ?? throw new InvalidOperationException("No user prompt in the conversation.");
    }
}

/// <summary>Port of <c>examples/responses-bridge.py</c> <c>bridged_task</c>: one sample, the <c>responses_agent</c> solver and the <c>includes()</c> scorer.</summary>
public static class BridgedTask
{
    public const string TaskName = "bridged_task";

    /// <summary>The single <c>Sample(input=..., target=...)</c>, verbatim.</summary>
    public const string Input = "Please print the word 'hello'?";

    public const string Target = "hello";

    [Task(TaskName)]
    public static EvalTask Build() => new()
    {
        Name = TaskName,
        Dataset = new MemoryDataset([new Sample(Input) { Target = Target }]),
        Solver = Agents.AsSolver(ResponsesAgent.Create()),
        Scorers = [Scorers.Includes()],
    };
}

/// <summary>
/// The <c>responses-bridge</c> example for the runner: <c>bridged_task</c> against a scripted model that answers
/// "hello" (<c>--fake</c>) or a Foundry deployment (Python's <c>eval(bridged_task(), model="openai/gpt-4o")</c>).
/// </summary>
public sealed class ResponsesBridgeExample : IExample
{
    public const string FakeModelName = "responses-scripted";

    public string Name => "responses-bridge";

    public string Description => "The smallest agent_bridge agent: one bridged model call with the user prompt, scored by includes()";

    public IReadOnlyList<ExampleTask> Tasks { get; } =
    [
        new ExampleTask(BridgedTask.TaskName, _ => BridgedTask.Build(), "asks the model to print the word 'hello' through the bridge"),
    ];

    public ExampleDefaults Defaults { get; } = new();

    public IReadOnlyList<string> Deviations { get; } =
    [
        "Python patches the OpenAI Python client so client.responses.create(model=\"inspect\") reaches the eval model; .NET has no client to patch and this engine's bridge does not speak the Responses wire format, so the agent makes its one call through the in-process InspectChatClient (an IChatClient over AgentBridge) driven by a Microsoft Agent Framework ChatClientAgent with no tools. The request still names the model \"inspect\" and bridge.State is returned as in Python.",
        "The Python script runs eval(bridged_task(), model=\"openai/gpt-4o\", display=\"plain\") at import; here the runner supplies the model (a Foundry deployment, or the scripted \"hello\" model under --fake).",
        "user_prompt() is not a public helper in this port; the agent carries its own three-line port (the last user message).",
    ];

    public Model CreateFakeModel(ExampleContext ctx) => new(new ScriptedModelApi([ScriptedTurn.Text("hello")], FakeModelName));

    public FakeSandboxScript? FakeSandbox(ExampleContext ctx) => null;
}
