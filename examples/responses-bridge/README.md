# Responses Bridge

A C# port of `examples/responses-bridge.py` of the inspect_ai repository: the smallest possible `agent_bridge()` agent. The Python agent opens the bridge, makes a single `client.responses.create(model="inspect", input=user_prompt(state.messages).text)` call with the stock OpenAI client (the bridge redirects the request to the eval model) and returns `bridge.state`; a one-sample task asks the model to print the word `hello` and scores it with `includes()`.

**The Python framework has no C# form.** There is no OpenAI Python client to monkey-patch in .NET, and this engine's bridge does not speak the Responses API wire format (it serves chat completions and Anthropic messages). The eval model behind the bridge can now be a Responses-route deployment (`--model gpt-5.6-sol`); that changes what the provider sends to Foundry, not what the bridge accepts from the agent. What stands in is the in-process bridge of `src/InspectAzureAI.Maf`: `InspectChatClient` is an `IChatClient` over an `AgentBridge`, and a Microsoft Agent Framework `ChatClientAgent` with no tools makes the one call. The request still names the model `inspect`, and the bridge tracks the conversation into its state exactly as Python's does.

| File | Description |
|---|---|
| [ResponsesBridgeExample.cs](ResponsesBridgeExample.cs) | `ResponsesAgent` (the bridged agent), `BridgedTask` (the `[Task("bridged_task")]` method) and the runner entry `ResponsesBridgeExample`. |

## Running it

Offline, with a scripted model that answers `hello` (no network, no sandbox):

```bash
dotnet run --project examples -- responses-bridge --fake --sandbox none
```

Against a Foundry deployment (Python's `eval(bridged_task(), model="openai/gpt-4o")`), which needs `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"):

```bash
dotnet run --project examples -- responses-bridge --model <deployment>
```

Through the `inspectai` CLI, which discovers the `[Task]` method in the built assembly:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval bridged_task \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

`dotnet run --project examples -- responses-bridge --help` prints the flags, the task and the deviations. The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

## The agent and the task

```csharp
public static AgentDef Create() => new(AgentName, "...", ExecuteAsync);

private static async Task<AgentState> ExecuteAsync(AgentState state, CancellationToken cancellationToken)
{
    // async with agent_bridge(state) as bridge:
    var bridge = new AgentBridge(state, SampleContext.Require().ActiveModel);
    using var client = new InspectChatClient(bridge);

    // client = AsyncOpenAI(); await client.responses.create(model="inspect", input=user_prompt(state.messages).text)
    var agent = new ChatClientAgent(client, new ChatClientAgentOptions { ChatOptions = new ChatOptions { ModelId = "inspect" } });
    var session = await agent.CreateSessionAsync(cancellationToken);
    await agent.RunAsync([new ChatMessage(ChatRole.User, UserPrompt(state.Messages).Text)], session, cancellationToken: cancellationToken);

    return bridge.State;
}

[Task("bridged_task")]
public static EvalTask Build() => new()
{
    Name = "bridged_task",
    Dataset = new MemoryDataset([new Sample("Please print the word 'hello'?") { Target = "hello" }]),
    Solver = Agents.AsSolver(ResponsesAgent.Create()),
    Scorers = [Scorers.Includes()],
};
```

## Deviations from Python

- Python patches the OpenAI Python client so `client.responses.create(model="inspect")` reaches the eval model; .NET has no client to patch and this engine's bridge does not speak the Responses wire format, so the agent makes its one call through the in-process `InspectChatClient` (an `IChatClient` over `AgentBridge`) driven by a Microsoft Agent Framework `ChatClientAgent` with no tools. The request still names the model `inspect` and `bridge.State` is returned as in Python.
- The Python script runs `eval(bridged_task(), model="openai/gpt-4o", display="plain")` at import; here the runner supplies the model (a Foundry deployment, or the scripted `hello` model under `--fake`).
- `user_prompt()` is not a public helper in this port; the agent carries its own three-line port (the last user message).
