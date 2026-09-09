# Images

## Introduction

A C# port of the `examples/images` example of the inspect_ai repository (`images.py`, `images.jsonl`, `ballons.png`, `bike.png`), run on this repository's eval engine. It is a small vision QA task: `images.jsonl` holds two samples whose input is a user message with a text part and an image part referencing a PNG beside the dataset —

```json
{ "input": [ { "role": "user", "content": [{ "type": "text", "text": "How many ballons are in this picture?"}, { "type": "image", "image": "ballons.png"} ]}], "target": "3" }
{ "input": [ { "role": "user", "content": [{ "type": "text", "text": "What is this a picture of?"}, { "type": "image", "image": "bike.png"} ]}], "target": ["bike", "bicycle"] }
```

— a system message demands a single bracketed value (`[22]`, `[house]`), `generate()` asks the model, and `match()` scores the completion against the target (a list target matches any of its values). The data files are copied verbatim from the Python folder. `Datasets.Json` (the port of `json_dataset`) parses the message-list inputs with their content parts into `ContentText` / `ContentImage` and, as `resolve_sample_files` does, turns the relative image paths into absolute ones next to the dataset. In Python the eval then materialises those files as `data:image/png;base64,...` URIs before the run (`_eval/task/images.py`, the default `trusted_pre_run` policy); this repository's engine does not port that step yet and its Foundry routes refuse a bare file path, so the task inlines its own dataset's PNGs when it is built (`SampleImages.Materialize`, see "Deviations from Python").

## Running it

### The examples runner

The example is `images` in the examples project (`ImagesExample`, see [examples/README.md](../README.md) for the runner and its flags). Offline, with a scripted model that answers `[3]` and `[bike]` from the image part of each message (without looking at the pixels):

```bash
dotnet run --project examples -- images --fake
```

Against a Foundry deployment, which must be vision-capable (the gpt-4o / gpt-4.1 / gpt-5 families, or a `claude-*` deployment on the anthropic route) and needs `az login` and `AZUREAI_BASE_URL` (see the root README's "Environment variables"); no API keys, Docker or sandbox:

```bash
dotnet run --project examples -- images --model <vision deployment>
```

`dotnet run --project examples -- images --help` prints the flags, the task and the deviations. The exit code is 0 when the log reports success, 1 when it does not, 2 for a usage or prerequisite error, and 3 on cancellation or an unexpected error.

### The `inspectai` CLI

The task is marked with `[Task("images")]`, so the CLI can discover it in the built assembly, exactly as `inspect eval images.py` does for the Python module. The task reads `images.jsonl` from the `images/` folder beside the assembly (the project copies the data files there), so the CLI can be run from any directory:

```bash
dotnet build examples
dotnet run --project src/InspectAzureAI.Cli -- eval images \
  --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll \
  --model azureai/<vision deployment>
```

## Task

The C# task mirrors the Python one:

```csharp
public const string SystemMessage =
    "\nFor the following exercise, it is important that you answer with only a single word or numeric value in brackets. For example, [22] or [house]. Do not include any discussion, narrative, or rationale, just a single value in brackets.\n";

[Task("images")]
public static EvalTask Images() => Build(DefaultDatasetPath());

public static EvalTask Build(string datasetPath) => new()
{
    Name = "images",
    Dataset = SampleImages.Materialize(Datasets.Json(datasetPath)),   // the PNGs as data URIs, as Python's pre-run step does
    Solver = Solvers.Chain(Solvers.SystemMessage(SystemMessage), Solvers.Generate()),
    Scorers = [Scorers.Match()],
};
```

`DefaultDatasetPath()` is `images/images.jsonl` beside the built assembly when that file exists, else `images.jsonl` in the working directory as in Python; the runner passes `ExampleContext.DataPath("images.jsonl")`, the same file. `SampleImages.Materialize` is a local port of `sample_with_base64_content` over `materialize_media` for this example: every `ContentImage` of a user message that names an existing local file becomes a `data:image/<type>;base64,...` URI; data URIs, URLs and missing files are left alone.

## Deviations from Python

- Python opens `images.jsonl` relative to the working directory (`inspect eval images.py` runs from the example folder); here the task reads the copy beside the built assembly (`images/images.jsonl`), falling back to the working directory, so the relative image paths of the dataset resolve wherever the run starts.
- The eval engine has no port of the pre-run media step of `_eval/task/images.py` (Python's default `trusted_pre_run` policy, which turns each sample's image files into data URIs before the run), and the Foundry routes refuse a bare file path; so the task inlines its own dataset's PNGs as `data:image/png;base64` URIs when it is built, and the log's sample inputs carry those data URIs rather than file paths.
- The `--fake` scripted model is an addition for running the example offline: it answers `[3]` for `ballons.png` and `[bike]` for `bike.png`, by the image file name when the message still carries one and by the question text once the image is inlined, without looking at the pixels.
