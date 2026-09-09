# scorer

Port of [`examples/scorer.py`](https://github.com/UKGovernmentBEIS/inspect_ai/blob/main/examples/scorer.py): a custom
model-assisted scorer. The `math` task takes the Hugging Face
[`HuggingFaceH4/MATH-500`](https://huggingface.co/datasets/HuggingFaceH4/MATH-500) test split (`problem` as the input,
the worked `solution` as the target), asks the model to solve each problem step by step and finish with a line of the
form `ANSWER: $ANSWER`, then scores with `expression_equivalence`: the answer is extracted with `AnswerPattern.LINE`
and the model under evaluation (`get_model()`) is asked whether it is equivalent to the target, answering only `Yes`
or `No`. A completion without an `ANSWER:` line is `INCORRECT` with the explanation
`Answer not found in model output: ...`. Metrics: `accuracy`, `stderr`. Generation uses `temperature=0.5`.

## Offline

```sh
dotnet run --project examples -- scorer --fake
dotnet run --project examples -- scorer --fake -T shuffle=false   # keep the dataset order
```

`--fake` never touches the network: the dataset is the first five rows of the real test split
(`math500-test-sample.jsonl`, as the datasets-server returned them) served to the real `hf_dataset` loader through a
canned datasets-server handler (`CannedHfHub`), cached in a private temp directory so the user's Hub cache is untouched.
The model (`FakeMathModel`) answers the first, second and fifth problems correctly, gives a wrong answer for the third and
no `ANSWER:` line for the fourth, and, when asked to judge, says `Yes` exactly when the reference solution contains the
extracted answer. The run therefore scores 3/5 (`accuracy 0.600`, `stderr 0.245`) and exercises all three branches of
the scorer.

## Live

```sh
az login
export AZUREAI_BASE_URL=https://<resource>.services.ai.azure.com
dotnet run --project examples -- scorer --model <deployment> --limit 20
```

Requirements: network access to `huggingface.co` on the first run (500 rows, then read from the cache under the
inspect cache directory), and a Foundry deployment used twice per sample (to solve at temperature 0.5, and to judge
equivalence), so `--limit` bounds the cost. `HF_TOKEN` (or a `huggingface-cli login`) is only needed for gated repos;
MATH-500 is public.

## CLI

```sh
dotnet build examples
inspectai eval math --assembly examples/bin/Debug/net10.0/InspectAzureAI.Examples.dll --model <deployment> -T shuffle=false
```

## The task in C#

```csharp
[Task("math")]
public static EvalTask MathTask(bool shuffle = true) =>
    Build(Datasets.Hf("HuggingFaceH4/MATH-500", "test", sampleFields: new FieldSpec(Input: "problem", Target: "solution"), shuffle: shuffle));

public static EvalTask Build(IDataset dataset) => new()
{
    Name = "math",
    Dataset = dataset,
    Solver = Solvers.Chain(Solvers.PromptTemplate(PromptTemplate), Solvers.Generate()),
    Scorers = [ExpressionEquivalence()],
    Config = new GenerateConfig { Temperature = 0.5 },
};

// @scorer(metrics=[accuracy(), stderr()])
public static ScorerDef ExpressionEquivalence() =>
    Scorers.Custom("expression_equivalence", ScoreExpressionEquivalence, Metrics.Accuracy(), Metrics.Stderr());

private static async Task<Score> ScoreExpressionEquivalence(TaskState state, Target target, CancellationToken cancellationToken)
{
    var match = Regex.Match(state.Output.Completion, AnswerPattern.Line);
    if (match.Success)
    {
        var answer = match.Groups[1].Value;
        var prompt = EquivalencePrompt(target.Text, answer);                       // EQUIVALENCE_TEMPLATE % {...}
        var result = await SampleContext.Require().ActiveModel                     // get_model()
            .GenerateAsync(prompt, cancellationToken: cancellationToken);
        var correct = result.Completion.ToLowerInvariant() == "yes";
        return new Score(correct ? ScoreConstants.Correct : ScoreConstants.Incorrect) { Answer = answer, Explanation = state.Output.Completion };
    }

    return new Score(ScoreConstants.Incorrect) { Explanation = "Answer not found in model output: " + state.Output.Completion };
}
```

`PromptTemplate` and `EquivalenceTemplate` are the Python `PROMPT_TEMPLATE` / `EQUIVALENCE_TEMPLATE` verbatim.

## Deviations from Python

- `hf_dataset` reads the split through the Hugging Face datasets-server REST API (`Datasets.Hf`) rather than the
  `datasets` package; the shuffle permutation is .NET's, and the rows are cached under the inspect cache directory as
  `rows.jsonl`.
- `--fake` serves the first five rows of the real test split (`math500-test-sample.jsonl`) through a canned
  datasets-server handler and plays the model with a script: correct answers for rows 1, 2 and 5, a wrong answer for
  row 3 and no `ANSWER:` line for row 4; its equivalence judge answers `Yes` when the reference solution contains the
  extracted answer.
- The Python task argument `shuffle=True` is the `-T shuffle=true|false` task argument (and the `shuffle` parameter of
  the CLI task method).
- The `EQUIVALENCE_TEMPLATE` placeholders are filled by string replacement rather than Python's `%` formatting (there
  are no other `%` directives in the template).
