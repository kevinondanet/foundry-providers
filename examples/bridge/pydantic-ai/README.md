## Pydantic AI

A C# port of `examples/bridge/pydantic-ai` of the inspect_ai repository. The Python example demonstrates using a native [Pydantic AI](https://ai.pydantic.dev/) agent with Inspect, through [`agent_bridge()`](https://inspect.aisi.org.uk/agent-bridge.html), which enables integrating arbitrary 3rd party agent frameworks into Inspect: an agent with the `WebSearch()` capability, a typed output (`AnswerToQueryOutput`, `retries={"output": 3}`) and a model id chosen per active provider, whose typed result is written back as `bridge.state.output.completion`.

**Pydantic AI has no C# form.** What stands in is a Microsoft Agent Framework `ChatClientAgent` running over `InspectChatClient`, the in-process bridge of `src/InspectAzureAI.Maf` (see `docs/agent-framework.md`), whose `ChatOptions.ResponseFormat` is `AnswerToQueryOutput`'s JSON schema (the bridge forwards it to the model as a `ResponseSchema`, the port of `output_type`); the output retries are a small loop that feeds validation feedback back to the agent as Pydantic AI does; `WebSearch()` is the engine's `web_search` tool. The dataset, the system prompt, the task and the `model_graded_fact()` scorer are the Python's.

| File | Description |
|---|---|
| [PydanticAiExample.cs](PydanticAiExample.cs) | `AnswerToQueryOutput` (the typed output), `WebResearchAgent` (the agent with its output retries), `ResearchTask` (the `[Task("research")]` method) and the runner entry `PydanticAiExample`. |
| [../WebResearch.cs](../WebResearch.cs) | Shared by the three bridge examples: the dataset loader, the system prompt, the live and canned `web_search` tools and the scripted model. |
| [dataset.json](dataset.json) | Dataset with questions and ideal answers (verbatim). |

## Running it

Offline, with a scripted model that searches once and answers with the typed output's JSON from a canned `web_search` result, and the same scripted model grading (`GRADE: C`); no network, no key, no sandbox:

```bash
dotnet run --project examples -- bridge/pydantic-ai --fake --sandbox none
```

Against a Foundry deployment that supports `json_schema` response formats (GPT-4o-class or newer; a `claude-*` deployment on the Anthropic route uses its output format), which needs `az login`, `AZUREAI_BASE_URL` (see the root README's "Environment variables") and `TAVILY_API_KEY` for the search tool (`-T provider=exa` with `EXA_API_KEY` instead). The deployment plays both the agent and the `model_graded_fact` grader:

```bash
dotnet run --project examples -- bridge/pydantic-ai --model <deployment>
```

Through the `inspectai` CLI, which discovers the `[Task]` method in the built assembly (the task reads `bridge/pydantic-ai/dataset.json` next to the assembly). The three bridge examples all name their task `research`, as the Python files do, so `inspectai eval research` is ambiguous in the one examples assembly; the task is tagged `bridge=pydantic-ai` so `list tasks -F` tells them apart, and the examples runner is the way to run one:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- list tasks examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll -F bridge=pydantic-ai
```

`dotnet run --project examples -- bridge/pydantic-ai --help` prints the flags, the task and the deviations. The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

## The output, the agent and the task

```csharp
public sealed record AnswerToQueryOutput(
    [property: JsonPropertyName("answer")] [property: Description("The answer to the query")] string Answer);

public static AgentDef Create(ToolDef? searchTool = null, int outputRetries = 3)
{
    var framework = AgentFramework.Agent(new MafAgentOptions
    {
        Name = "web_research_agent",
        Instructions = "You help users find information by searching the web.",
        Tools = [MafTools.FromToolDef(searchTool ?? WebResearch.LiveSearchTool("tavily"))],
        Submit = false,
        AgentFactory = (client, tools) => new ChatClientAgent(client, new ChatClientAgentOptions
        {
            ChatOptions = new ChatOptions { Instructions = ..., Tools = tools, ResponseFormat = AnswerToQueryOutput.ResponseFormat },
        }),
    });
    return new AgentDef("web_research_agent", "Pydantic AI Web Research Agent.", (state, ct) => ExecuteAsync(framework, state, outputRetries, ct));
}

private static async Task<AgentState> ExecuteAsync(AgentDef framework, AgentState state, int outputRetries, CancellationToken ct)
{
    // result = await agent.run(user_prompt(state.messages).text)
    var run = new AgentState([new ChatMessageUser(UserPrompt(state.Messages).Text)]);
    for (var attempt = 0; ; attempt++)
    {
        run = await framework.Execute(run, ct);
        if (AnswerToQueryOutput.TryParse(run.Output.Completion, out var output, out var error))
        {
            // bridge.state.output.completion = result.output.model_dump_json()
            run.Output = run.Output with { Completion = output!.ToJson() };
            return run;
        }
        if (attempt >= outputRetries)
            throw new InvalidOperationException($"Exceeded maximum retries ({outputRetries}) for output validation: {error}");
        run.Messages.Add(new ChatMessageUser($"Validation feedback:\n{error}\n\nFix the errors and try again."));
    }
}

[Task("research", "bridge=pydantic-ai")]
public static EvalTask Research() => Build(DefaultDatasetPath, WebResearchAgent.Create());
```

## Deviations from Python

- Pydantic AI (`Agent`, `capabilities.WebSearch`, `output_type`, `retries`) has no C# form: the agent is a Microsoft Agent Framework `ChatClientAgent` over `InspectChatClient` (the in-process `agent_bridge`) whose `ChatOptions.ResponseFormat` is `AnswerToQueryOutput`'s JSON schema, forwarded to the model as a `ResponseSchema`; the typed result is still written back as the completion (`{"answer": ...}`).
- `retries={"output": 3}` is a hand-written loop: a final message that is not valid `AnswerToQueryOutput` JSON is answered with a `Validation feedback: ... Fix the errors and try again.` user message and the agent runs again, up to three times, then the sample errors (Pydantic AI raises `UnexpectedModelBehavior`). Each run starts from a new `AgentState` holding only the user prompt and the returned state is that run's conversation (Python's bridge writes the run into the sample's own state object; the retry there happens inside one `agent.run`).
- `_pydantic_bridge_model()` picks `openai-responses:`/`anthropic:`/`google:` so Pydantic AI's HTTP client matches the patched library; the in-process client is provider-agnostic, so there is no switch.
- `WebSearch()` (the provider's hosted search) is the engine's `web_search` tool over an external provider (Tavily by default, `TAVILY_API_KEY`; `-T provider=exa` for Exa): the Agent Framework bridge carries no internal-provider marker.
- Under `--fake` the search is a canned `web_search` tool (no key, no network), the scripted model writes the output JSON by hand (it ignores the response schema) and the same scripted model grades; the Python example only runs live.
- Python resolves `dataset.json` relative to `task.py`; the `[Task]` method reads the copy next to the built assembly (`bridge/pydantic-ai/dataset.json`).
- The three bridge examples share the Python task name `research`, so in the one examples assembly `inspectai eval research` is ambiguous (the CLI disambiguates by assembly file, not by folder); `inspectai list tasks -F bridge=pydantic-ai` tells them apart and the examples runner (`-- bridge/pydantic-ai`) runs this one.
