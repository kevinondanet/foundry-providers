# Web Surfer

A C# port of `examples/surfer.py` of the inspect_ai repository. A `react` agent with the built-in `web_search()` tool is asked "What were the scores of last night's NHL games?". The file also ports the stateful `web_surfer` tool, which (as in Python) the task does not use: a per-instance conversation kept in a `StoreModel` (`WebSurferState.Messages`) that drives `get_model().generate_loop()` with `web_search()` and returns the completion, so a caller can ask follow-up questions about earlier research or clear the history.

What it demonstrates:

- `BuiltinTools.WebSearch()` (`web_search()`) as a react agent's tool.
- `StoreModel` / `Store.StoreAs<WebSurferState>(instance)` (`store_as`): typed, namespaced state in the sample store, visible in the log.
- `Model.GenerateLoopAsync(messages, tools: [...])` (`Model.generate_loop`): a tool-calling loop run from inside a tool with the sample's active model (`SampleContext.Require().ActiveModel`, Python's `get_model()`).
- A custom `ToolDef` with the Python tool's name, description, parameters and default.

## Running it

### Offline

```bash
dotnet run --project examples -- surfer --fake --sandbox none
```

The scripted model searches once (`web_search(query="NHL scores last night")`) and submits the answer. The search tool is the real `web_search("tavily")` whose HTTP client is given a handler that answers every query with a canned Tavily response (`FakeWebSearch`), so the answer and its citations flow through the provider's parsing without any network. Add `--display conversation` to watch it.

### Against a Foundry deployment

The Python calls `web_search()` with no provider, which means the model's own search: on this engine that is available on the Anthropic route, where Claude's server-side web search executes the tool:

```bash
dotnet run --project examples -- surfer --model <claude deployment> --route anthropic
```

On the Azure chat route the bare `web_search()` fails on its first call with `No valid provider found`, so name an external provider (`tavily` or `exa`, with `TAVILY_API_KEY` / `EXA_API_KEY` set):

```bash
TAVILY_API_KEY=... dotnet run --project examples -- surfer --model <deployment> -T search_provider=tavily
```

Requirements: `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"), a tool-calling deployment, and outbound network access to the search API. No sandbox.

### The `inspectai` CLI

The task is marked `[Task("surfer")]`, so the CLI can discover it in the built assembly:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval surfer \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model anthropic/<claude deployment>
```

## The task

```csharp
[Task("surfer")]
public static EvalTask SurferTask() => Build(BuiltinTools.WebSearch());

public static EvalTask Build(ToolDef webSearch) => new()
{
    Name = "surfer",
    Dataset = new MemoryDataset([new Sample("What were the scores of last night's NHL games?")]),
    Solver = Agents.AsSolver(Agents.React(tools: [webSearch])),
};
```

## The web_surfer tool

```csharp
public sealed class WebSurferState : StoreModel
{
    public List<ChatMessage> Messages { get => Get(new List<ChatMessage>()); set => Set(value); }
}

public static ToolDef Create(string? instance = null, Func<ToolDef>? webSearch = null)
{
    instance ??= ShortUuid.Generate();
    ...
    return new ToolDef("web_surfer", Description, parameters, async (arguments, cancellationToken) =>
    {
        var surferState = Store.StoreAs<WebSurferState>(instance);
        var messages = surferState.Messages;
        if (clearHistory) messages.Clear();
        if (messages.Count == 0) messages.Add(new ChatMessageSystem(SystemPrompt));
        messages.Add(new ChatMessageUser(input));
        var model = SampleContext.Require().ActiveModel;
        var (newMessages, output) = await model.GenerateLoopAsync(messages, tools: [webSearch()], cancellationToken: cancellationToken);
        messages.AddRange(newMessages);
        surferState.Messages = messages;
        return output.Completion;
    });
}
```

## Deviations from Python

- `web_search()` with no provider (the Python call) is executed server-side only on the Anthropic route; on the Azure chat route the first search fails with "No valid provider found", so `-T search_provider=tavily|exa` (an addition) selects an external provider (its API key must be set).
- Under `--fake` the tool is `web_search("tavily")` whose HTTP client answers every query with a canned Tavily response (`FakeWebSearch`); `TAVILY_API_KEY` is given a placeholder value when unset because the provider requires it.
- `web_surfer` is ported (`WebSurfer.Create`) with the Python's name, description, parameters and store-backed history, but as in Python the task does not use it; its optional `webSearch` factory parameter is an addition for testing offline.
- The scripted model searches once and submits the answer; a message limit of 20 guards the fake run.
