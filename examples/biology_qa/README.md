# Biology QA

## Introduction

A C# port of the `examples/biology_qa.py` example of the inspect_ai repository, run on this repository's eval engine. The task asks the 20 biology trivia questions of the `biology_qa` example dataset bundled with the engine (`Datasets.Example("biology_qa", new FieldSpec(Input: "question", Target: "answer"))`, the port of `example_dataset(..., sample_fields=FieldSpec(input="question", target="answer"))`). The model gets the `web_search()` tool, configured for five providers at once exactly as the Python does — `grok`, `openai` (with `search_context_size` and `user_location` options), `anthropic`, `tavily` (with `max_results` and `max_connections`) and `gemini` (with a `time_range_filter` of the last 365 days) — and `generate()` runs the tool-call loop; answers are graded by `model_graded_qa()`.

On Foundry only two of the five providers actually execute: a `claude-*` deployment on the anthropic route uses Claude's own server-side web search, and every other deployment searches through Tavily. The grok, openai and gemini options are recorded in the tool's options exactly as Python writes them but are inert (see "Deviations from Python").

## Running it

### The examples runner

The example is `biology_qa` in the examples project (`BiologyQaExample`, see [examples/README.md](../README.md) for the runner and its flags). Offline, with a scripted model that searches once per question and then answers from the result, and a scripted Tavily service behind the real Tavily provider, so every sample completes without a network:

```bash
dotnet run --project examples -- biology_qa --fake
```

Against a Foundry deployment, which needs `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables") and a deployment that supports tool calling. With a non-Claude deployment the `tavily` provider does the searching, so `TAVILY_API_KEY` must be set and `api.tavily.com` reachable; with a `claude-*` deployment (the anthropic route) Claude's built-in web search is used and no Tavily key is needed. Each sample costs the model's tool-call loop plus one grader call; no Docker or sandbox:

```bash
export TAVILY_API_KEY=...
dotnet run --project examples -- biology_qa --model <deployment>
dotnet run --project examples -- biology_qa --model claude-sonnet-4-5   # Claude's own web search, no Tavily key
```

`dotnet run --project examples -- biology_qa --help` prints the flags, the task and the deviations. The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

The task is marked with `[Task("biology_qa")]`, so the CLI can discover it in the built assembly, exactly as `inspect eval biology_qa.py` does for the Python module:

```bash
dotnet build examples
TAVILY_API_KEY=... dotnet run --project src/InspectAzureAI.Cli -- eval biology_qa \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

## Task

The C# task mirrors the Python one:

```csharp
public static JsonObject OpenaiOptions() => new()
{
    ["search_context_size"] = "high",
    ["user_location"] = new JsonObject { ["type"] = "approximate", ["country"] = "US", ["city"] = "Boston" },
};

public static JsonObject TavilyOptions() => new() { ["max_results"] = 5, ["max_connections"] = 8 };

public static JsonObject GeminiOptions(DateTimeOffset? now = null)   // time_range_filter: the last 365 days, whole seconds, UTC
{
    ...
    return new JsonObject { ["time_range_filter"] = new JsonObject { ["start_time"] = IsoFormat(start), ["end_time"] = IsoFormat(end) } };
}

[Task("biology_qa")]
public static EvalTask BiologyQa() => new()
{
    Name = "biology_qa",
    Dataset = Datasets.Example(name: "biology_qa", fields: new FieldSpec(Input: "question", Target: "answer")),
    Solver = Solvers.Chain(
        Solvers.UseTools(BuiltinTools.WebSearch(new WebSearchProviders
        {
            ["grok"] = true,
            ["openai"] = OpenaiOptions(),
            ["anthropic"] = true,
            ["tavily"] = TavilyOptions(),
            ["gemini"] = GeminiOptions(),
        })),
        Solvers.Generate()),
    Scorers = [Scorers.ModelGradedQa()],
};
```

`BuiltinTools.WebSearch` is the port of `web_search(providers=...)`: the provider dictionary is normalised and carried in the tool's `options` (with the `__internal_tool_type__ = web_search` marker) exactly as Python does, and the external provider is created on first use, so a missing `TAVILY_API_KEY` surfaces as a prerequisite error on the first search rather than when the task is built. `BiologyQaExample.Build(HttpMessageHandler? handler)` is the same task over an injected Tavily HTTP transport, which is how the offline run works.

## The scripted model and search service

Under `--fake` two scripts stand in for the network. `FakeTavilyHandler` is an `HttpMessageHandler` handed to the real Tavily provider: it answers every `POST https://api.tavily.com/search` with a canned response in Tavily's shape whose `answer` is the dataset's target for the queried question and whose single result cites `https://example.com/biology/<question id>`, so the bearer header, the request body and the citation parsing of the provider run for real. `FakeBiologyQaModel` computes each turn from the conversation: on the question it calls `web_search` with the question as the query; once the tool result is in the conversation it answers with what the search returned; and the `model_graded_qa` prompt is graded for real (`GRADE: C` when the submission contains the criterion, `GRADE: I` otherwise). A `--fake` run therefore reports an accuracy of 1.0, with one `web_search` tool event and one citation-bearing tool result per sample in the log.

The Tavily provider validates `TAVILY_API_KEY` before its first call, so a `--fake` run sets the variable to a placeholder for the process when it is not already set; the scripted transport never sends it anywhere.

## Deviations from Python

- Only the anthropic and tavily providers execute on Foundry: a `claude-*` deployment on the anthropic route uses Claude's server-side web search, every other deployment searches through Tavily (`TAVILY_API_KEY`, outbound HTTPS to `api.tavily.com`). The grok, openai (`search_context_size`, `user_location`) and gemini (`time_range_filter`) options are recorded in the tool's options exactly as Python writes them but are inert, because the Azure chat completions route has no server-side search.
- `gemini_options.time_range_filter` holds ISO-8601 strings (Python's `datetime.now(timezone.utc).replace(microsecond=0)` `isoformat`: `2026-01-01T00:00:00+00:00`) instead of datetime objects, and is computed when the task is built rather than when the module is imported.
- The `--fake` scripted model (one `web_search` call per question, then the answer the search returned) and the scripted Tavily transport (a canned `https://api.tavily.com/search` response whose answer is the dataset's target, with one citation) are additions for running the example offline; `--fake` sets `TAVILY_API_KEY` to a placeholder for the process when it is not set, because the Tavily provider validates the variable before its first call.
