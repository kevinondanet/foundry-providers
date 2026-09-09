## OpenAI Agents SDK

A C# port of `examples/bridge/agentsdk` of the inspect_ai repository. The Python example demonstrates using a native [OpenAI Agents SDK](https://openai.github.io/openai-agents-python/) agent with Inspect, through [`agent_bridge()`](https://inspect.aisi.org.uk/agent-bridge.html), which enables integrating arbitrary 3rd party agent frameworks into Inspect: an `Agent(name="SearchAssistant", instructions=..., tools=[WebSearchTool()])` run by `Runner.run` with `RunConfig(model="inspect")`.

**The OpenAI Agents SDK has no .NET release**, and this engine's bridge does not speak the Responses API the SDK uses. What stands in is a Microsoft Agent Framework `ChatClientAgent` named `SearchAssistant` running over `InspectChatClient`, the in-process bridge of `src/InspectAzureAI.Maf` (see `docs/agent-framework.md`), with the same instructions. The SDK's hosted `WebSearchTool()` (which the Python bridge maps onto Inspect's `web_search` with the model's own provider) is the engine's `web_search` tool over an external provider (Tavily by default). The dataset, the task and the `model_graded_fact()` scorer are the Python's.

| File | Description |
|---|---|
| [AgentSdkExample.cs](AgentSdkExample.cs) | `WebResearchAgent` (the agent), `ResearchTask` (the `[Task("research")]` method) and the runner entry `AgentSdkExample`. |
| [../WebResearch.cs](../WebResearch.cs) | Shared by the three bridge examples: the dataset loader, the system prompt, the live and canned `web_search` tools and the scripted model. |
| [dataset.json](dataset.json) | Dataset with questions and ideal answers (verbatim). |

## Running it

Offline, with a scripted model that searches once and answers from a canned `web_search` result, and the same scripted model grading (`GRADE: C`); no network, no key, no sandbox:

```bash
dotnet run --project examples -- bridge/agentsdk --fake --sandbox none
```

Against a Foundry deployment, which needs `az login`, `AZUREAI_BASE_URL` (see the root README's "Environment variables") and `TAVILY_API_KEY` for the search tool (`-T provider=exa` with `EXA_API_KEY` instead). The deployment plays both the agent and the `model_graded_fact` grader:

```bash
dotnet run --project examples -- bridge/agentsdk --model <deployment>
```

Through the `inspectai` CLI, which discovers the `[Task]` method in the built assembly (the task reads `bridge/agentsdk/dataset.json` next to the assembly). The three bridge examples all name their task `research`, as the Python files do, so `inspectai eval research` is ambiguous in the one examples assembly; the task is tagged `bridge=agentsdk` so `list tasks -F` tells them apart, and the examples runner is the way to run one:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- list tasks examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll -F bridge=agentsdk
```

`dotnet run --project examples -- bridge/agentsdk --help` prints the flags, the task and the deviations. The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

## The agent and the task

```csharp
public static AgentDef Create(ToolDef? searchTool = null)
{
    var agent = AgentFramework.Agent(new MafAgentOptions
    {
        Name = "SearchAssistant",
        Description = "OpenAI Agents SDK search agent.",
        Instructions = "You help users find information by searching the web.",
        Tools = [MafTools.FromToolDef(searchTool ?? WebResearch.LiveSearchTool("tavily"))],
        // Runner.run ends when the agent produces a final output: no submit tool
        Submit = false,
    });
    // the Inspect agent keeps the Python function's name; the framework agent inside it is SearchAssistant
    return agent with { Name = "web_research_agent" };
}

[Task("research", "bridge=agentsdk")]
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

- The OpenAI Agents SDK (`Agent`, `Runner`, `RunConfig`, `WebSearchTool`) has no .NET release and this engine's bridge does not speak the Responses API the SDK uses: the agent is a Microsoft Agent Framework `ChatClientAgent` named `SearchAssistant` over `InspectChatClient` (the in-process `agent_bridge`), with the same instructions; the Inspect agent keeps the name `web_research_agent`.
- `WebSearchTool()` is OpenAI's hosted web search, which the Python bridge maps onto Inspect's `web_search` with the model's own provider. Here the tool is the engine's `web_search` over an external provider (Tavily by default, `TAVILY_API_KEY`; `-T provider=exa` for Exa): the Agent Framework bridge carries no internal-provider marker, so a deployment's server-side search is not used.
- Under `--fake` the search is a canned `web_search` tool (no key, no network) and one scripted model plays both the agent and the `model_graded_fact` grader; the Python example only runs live.
- Python resolves `dataset.json` relative to `task.py`; the `[Task]` method reads the copy next to the built assembly (`bridge/agentsdk/dataset.json`).
- The three bridge examples share the Python task name `research`, so in the one examples assembly `inspectai eval research` is ambiguous (the CLI disambiguates by assembly file, not by folder); `inspectai list tasks -F bridge=agentsdk` tells them apart and the examples runner (`-- bridge/agentsdk`) runs this one.
