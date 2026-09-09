# Structured Output

A C# port of `examples/structured.py` of the inspect_ai repository: four tasks that ask a model for the RGB colour of white and black in a constrained shape, run on this repository's eval engine.

## What it demonstrates

Two ways of constraining a model's output:

- `rgb_color` asks for JSON through a response schema: `GenerateConfig(response_schema=ResponseSchema(name="color", json_schema=json_schema(Color)))`, where `Color` has integer `red`, `green` and `blue` fields. The `score_json_color` scorer parses the completion as a `Color` and compares `red,green,blue` with the target (`C` / `I`), or scores `I` with the explanation `Error parsing response: ...` when the completion is not a colour. Both Foundry routes forward the schema: the models route as a chat-completions `response_format` of type `json_schema`, the anthropic route as `output_format` under the structured-outputs beta.
- `rgb_color_regex`, `rgb_color_choice` and `rgb_color_grammar` constrain the output with vLLM / SGLang guided decoding (a regex, a list of choices, an EBNF grammar) passed as provider-specific fields in `extra_body`, and score with `score_regex_color`, which reads the first `RGB: r,g,b` in the completion. They are marked `vllm=True` / `sglang=True` and raise `Unsupported provider` for any other provider, so **they are not runnable on Foundry** (see the deviations).

## Running it

### Offline (the examples runner)

The example is `structured` in the examples project (`StructuredExample`, see [examples/README.md](../README.md) for the runner and its flags). With `--fake` a scripted model answers `rgb_color` with the JSON a model honouring the schema returns, so both samples score `C`:

```bash
dotnet run --project examples -- structured --fake
```

`-T reply=malformed` makes it answer in a sentence instead, which shows the scorer's `Error parsing response` branch; `-T reply=rgb` answers `RGB: r,g,b`. The three guided tasks are skipped under `--fake` (a prerequisite error, exit 2), because nothing can fake guided decoding:

```bash
dotnet run --project examples -- structured --fake -T reply=malformed
dotnet run --project examples -- structured --fake --task rgb_color_regex   # skipped: needs vLLM or SGLang
```

### Against a Foundry deployment

Needs `az login`, `AZUREAI_BASE_URL` (see the root README's "Environment variables") and a deployment with structured outputs (the gpt-4o / gpt-5 families on the models route, or a `claude-*` deployment on the anthropic route):

```bash
dotnet run --project examples -- structured --model <deployment>
```

`--task rgb_color_regex|rgb_color_choice|rgb_color_grammar` against a deployment fails with `Unsupported provider: azureai` (or `anthropic`), exactly where the Python raises for any provider but vllm / sglang.

### The `inspectai` CLI

The tasks are marked with `[Task("rgb_color")]`, `[Task("rgb_color_regex", "vllm=true", "sglang=true")]`, `[Task("rgb_color_choice", "vllm=true")]` and `[Task("rgb_color_grammar", "vllm=true", "sglang=true")]`, so the CLI discovers them in the built assembly (`inspectai list tasks -F vllm=true` filters on the attributes, as `inspect list tasks -F vllm=true` does):

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval rgb_color \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<deployment>
```

The exit code of the runner is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

## The tasks

`StructuredTasks.cs` is the port of the Python module. `class Color(BaseModel)` becomes a System.Text.Json record whose `required` members and `JsonPropertyName`s give `JsonSchemaGenerator.JsonSchemaOf<Color>()` (the port of `json_schema(Color)`) the same schema, and make `JsonSerializer.Deserialize<Color>` (the port of `Color.model_validate_json`) reject a missing field:

```csharp
public sealed record Color
{
    [JsonPropertyName("red")] public required int Red { get; init; }
    [JsonPropertyName("green")] public required int Green { get; init; }
    [JsonPropertyName("blue")] public required int Blue { get; init; }
}

[Task("rgb_color")]
public static EvalTask RgbColor() => new()
{
    Name = "rgb_color",
    Dataset = new MemoryDataset(
    [
        new Sample("What is the RGB color for white?") { Target = "255,255,255" },
        new Sample("What is the RGB color for black?") { Target = "0,0,0" },
    ]),
    Solver = Solvers.Generate(),
    Scorers = [ScoreJsonColor()],
    Config = new GenerateConfig
    {
        ResponseSchema = new ResponseSchema(name: "color", jsonSchema: JsonSchemaGenerator.JsonSchemaOf<Color>()),
    },
};

public static ScorerDef ScoreJsonColor() => Scorers.Custom("score_json_color", ScoreJsonColorAsync, Metrics.Accuracy(), Metrics.Stderr());
```

The guided tasks evaluate `ModelName(get_model()).api` when they are built; `StructuredTasks.ModelApi(model)` is that lookup, and each task has an overload taking the api so the body can be exercised without a model:

```csharp
[Task("rgb_color_regex", "vllm=true", "sglang=true")]
public static EvalTask RgbColorRegex() => RgbColorRegex(ModelApi());

public static EvalTask RgbColorRegex(string api)
{
    var guidedName = api switch
    {
        "vllm" => "guided_regex",
        "sglang" => "regex",
        _ => throw new PrerequisiteError($"Unsupported provider: {api}"),
    };

    return new EvalTask
    {
        Name = "rgb_color_regex",
        Dataset = ColorDataset(),
        Solver = Solvers.Generate(),
        Scorers = [ScoreRegexColor()],
        Config = new GenerateConfig { ExtraBody = new JsonObject { [guidedName] = @"RGB: (\d{1,3}),(\d{1,3}),(\d{1,3})" } },
    };
}
```

`rgb_color_choice` passes `guided_choice: ["RGB: 255,255,255", "RGB: 0,0,0"]` (vLLM only: `Choice is only supported for vLLM` otherwise) and `rgb_color_grammar` the EBNF grammar as `guided_grammar` (vLLM) or `ebnf` (SGLang).

## Deviations from Python

- `rgb_color_regex`, `rgb_color_choice` and `rgb_color_grammar` are not runnable on Foundry: they need a vLLM or SGLang server (guided decoding through `extra_body`) and this port has only the azureai and anthropic Foundry apis, so against a deployment they raise `Unsupported provider: azureai` (a `PrerequisiteError` in place of Python's `ValueError`, exit 2) exactly where the Python does for any other provider, and under `--fake` they are skipped with a prerequisite error because nothing can fake guided decoding. `GenerateConfig.ExtraBody` is carried on the config but never written into a Foundry request.
- Python's `get_model()` at task-build time has no equivalent under the `inspectai` CLI, which builds a task before it binds the model: the guided tasks read the api from the model the examples runner resolved, else the active sample's model, else `INSPECT_EVAL_MODEL` (`api/name`), else fail with Python's `No model specified` error.
- `Color` is a System.Text.Json record (required `red`/`green`/`blue` integers) in place of the pydantic model, and `JsonSerializer.Deserialize<Color>` stands in for `Color.model_validate_json`, so the `Error parsing response: ...` explanation carries a `JsonException` message rather than pydantic's `ValidationError` text. The response schema it produces (integer properties, all required, `additionalProperties: false`) is the one `json_schema(Color)` gives.
- The scripted `--fake` model and `-T reply=json|rgb|malformed` are additions for running the demonstration offline (the Python example only runs through `inspect eval`): `json` is what a model honouring the response schema returns, `rgb` what guided decoding would force, `malformed` a sentence that shows both scorers' error branches.
