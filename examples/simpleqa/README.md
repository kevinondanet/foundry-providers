# simpleqa

Port of [`examples/simpleqa.py`](https://github.com/UKGovernmentBEIS/inspect_ai/blob/main/examples/simpleqa.py): the
Hugging Face [`codelion/SimpleQA-Verified`](https://huggingface.co/datasets/codelion/SimpleQA-Verified) train split
(`problem` as the input, `answer` as the target) answered with a plain `generate()` and graded by the built-in
`model_graded_qa()` scorer, which asks the model under evaluation whether the submission meets the criterion and reads
its `GRADE: C` / `GRADE: I` verdict. Metrics: `accuracy`, `stderr`.

## Offline

```sh
dotnet run --project examples -- simpleqa --fake
```

`--fake` never touches the network: the dataset is the first five rows of the real train split
(`simpleqa-verified-train-sample.jsonl`, as the datasets-server returned them) served to the real `hf_dataset` loader
through a canned datasets-server handler (`CannedHfHub`), cached in a private temp directory so the user's Hub cache is
untouched. The model (`FakeSimpleqaModel`) answers the first, second and fourth questions with the reference answer and
the third and fifth wrongly; asked to grade, it answers `GRADE: C` when the submission contains the criterion and
`GRADE: I` otherwise. The run therefore scores 3/5 (`accuracy 0.600`, `stderr 0.245`).

## Live

```sh
az login
export AZUREAI_BASE_URL=https://<resource>.services.ai.azure.com
dotnet run --project examples -- simpleqa --model <deployment> --limit 20
```

Requirements: network access to `huggingface.co` on the first run (1000 rows, then read from the cache under the
inspect cache directory), and a Foundry deployment used twice per sample (to answer, and as the `model_graded_qa`
judge), so `--limit` bounds the cost. The dataset is public (MIT); `HF_TOKEN` is not needed.

## CLI

```sh
dotnet build examples
inspectai eval simpleqa --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll --model <deployment>
```

## The task in C#

```csharp
[Task("simpleqa")]
public static EvalTask SimpleqaTask() =>
    Build(Datasets.Hf("codelion/SimpleQA-Verified", "train", sampleFields: new FieldSpec(Input: "problem", Target: "answer")));

public static EvalTask Build(IDataset dataset) => new()
{
    Name = "simpleqa",
    Dataset = dataset,
    Solver = Solvers.Generate(),
    Scorers = [Scorers.ModelGradedQa()],
};
```

## Deviations from Python

- `hf_dataset` reads the split through the Hugging Face datasets-server REST API (`Datasets.Hf`) rather than the
  `datasets` package; the rows are cached under the inspect cache directory as `rows.jsonl`.
- `--fake` serves the first five rows of the real train split (`simpleqa-verified-train-sample.jsonl`) through a canned
  datasets-server handler and plays the model with a script: the reference answer for rows 1, 2 and 4, a wrong answer
  for rows 3 and 5; its grader answers `GRADE: C` when the submission contains the criterion, else `GRADE: I`.
