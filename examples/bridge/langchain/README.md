## LangChain Agent

A C# port of `examples/bridge/langchain` of the inspect_ai repository. The Python example demonstrates using a native [LangChain](https://www.langchain.com/) agent with Inspect to perform Q/A using the [Tavili Search API](https://tavily.com/), through [`agent_bridge()`](https://inspect.aisi.org.uk/agent-bridge.html), which enables integrating arbitrary 3rd party agent frameworks into Inspect.

**LangChain has no C# form.** What stands in is a Microsoft Agent Framework `ChatClientAgent` running over `InspectChatClient`, the in-process bridge of `src/InspectAzureAI.Maf` (see `docs/agent-framework.md`): the framework's tool loop plays `create_agent`'s ReAct graph, its session plays the `MemorySaver` checkpointer keyed by `thread_id`, and the sample's messages seed the run as `messages_to_openai` + `convert_to_messages` do. `TavilySearch(max_results=5)` is the engine's `web_search` tool over the Tavily provider with the same `max_results` option. The dataset, the system prompt, the task and the `model_graded_fact()` scorer are the Python's.

| File | Description |
|---|---|
| [LangchainExample.cs](LangchainExample.cs) | `WebResearchAgent` (the agent), `ResearchTask` (the `[Task("research")]` method) and the runner entry `LangchainExample`. |
| [../WebResearch.cs](../WebResearch.cs) | Shared by the three bridge examples: the dataset loader, the system prompt, the live and canned `web_search` tools and the scripted model. |
| [dataset.json](dataset.json) | Dataset with questions and ideal answers (verbatim). |
| [requirements.txt](requirements.txt) | Dependencies of the Python LangChain example (verbatim, for reference). |

## Running it

Offline, with a scripted model that searches once and answers from a canned `web_search` result, and the same scripted model grading (`GRADE: C`); no network, no key, no sandbox:

```bash
dotnet run --project examples -- bridge/langchain --fake --sandbox none
```

Against a Foundry deployment, which needs `az login`, `AZUREAI_BASE_URL` (see the root README's "Environment variables") and, as for the Python example, a [Tavili](https://tavily.com/) account with `TAVILY_API_KEY` set. The deployment plays both the agent and the `model_graded_fact` grader:

```bash
dotnet run --project examples -- bridge/langchain --model <deployment>
dotnet run --project examples -- bridge/langchain --model <deployment> -T max_results=3
dotnet run --project examples -- bridge/langchain --model <deployment> -T provider=exa   # EXA_API_KEY instead
```

Through the `inspectai` CLI, which discovers the `[Task]` method in the built assembly (the task reads `bridge/langchain/dataset.json` next to the assembly). The three bridge examples all name their task `research`, as the Python files do, so `inspectai eval research` is ambiguous in the one examples assembly; the task is tagged `bridge=langchain` so `list tasks -F` tells them apart, and the examples runner is the way to run one:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- list tasks examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll -F bridge=langchain
```

`dotnet run --project examples -- bridge/langchain --help` prints the flags, the task and the deviations. The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

## The agent and the task

```csharp
public static AgentDef Create(int maxResults = 5, ToolDef? searchTool = null) =>
    AgentFramework.Agent(new MafAgentOptions
    {
        Name = "web_research_agent",
        Description = "LangChain Tavily search agent.",
        Instructions = "You help users find information by searching the web.",
        Tools = [MafTools.FromToolDef(searchTool ?? WebResearch.LiveSearchTool("tavily", maxResults))],
        // the answer is the agent's final message, as with LangChain: no submit tool
        Submit = false,
    });

[Task("research", "bridge=langchain")]
public static EvalTask Research() => Build(DefaultDatasetPath, WebResearchAgent.Create());

public static EvalTask Build(string datasetPath, AgentDef agent) => new()
{
    Name = "research",
    Dataset = Datasets.Json(datasetPath),
    Solver = Agents.AsSolver(agent),
    Scorers = [Scorers.ModelGradedFact()],
};
```

## Deviations from Python

- LangChain, LangGraph, `ChatOpenAI` and `MemorySaver` have no C# form: the agent is a Microsoft Agent Framework `ChatClientAgent` over `InspectChatClient` (the in-process `agent_bridge`). Its tool loop stands in for `create_agent`'s ReAct graph and its session for the `MemorySaver`/`thread_id` checkpointer; the transcript, limits and log are Inspect's as in Python.
- `TavilySearch(max_results)` is the engine's `web_search` tool over the Tavily provider with the same `max_results` option (`TAVILY_API_KEY`); `-T provider=exa` selects Exa instead. Tavily's tool returns a JSON results object to LangChain, this tool returns the provider's answer text with citations.
- Under `--fake` the search is a canned `web_search` tool (no key, no network) and one scripted model plays both the agent and the `model_graded_fact` grader; the Python example only runs live.
- Python resolves `dataset.json` relative to `task.py`; the `[Task]` method reads the copy next to the built assembly (`bridge/langchain/dataset.json`).
- The three bridge examples share the Python task name `research`, so in the one examples assembly `inspectai eval research` is ambiguous (the CLI disambiguates by assembly file, not by folder); `inspectai list tasks -F bridge=langchain` tells them apart and the examples runner (`-- bridge/langchain`) runs this one.
