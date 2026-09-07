# Inspect components: Tasks, Datasets, Solvers and Scorers

This guide is a plain-language introduction to the four building blocks of an Inspect AI evaluation (tasks, datasets, solvers and scorers) and to how they fit together. The examples are Python, and each component chapter ends with a note on how the same piece appears in the .NET port in this repository.

## Contents

- [What an evaluation is](#what-an-evaluation-is)
- [Tasks](#tasks)
- [Datasets](#datasets)
- [Solvers](#solvers)
- [Scorers](#scorers)
- [How the components fit together](#how-the-components-fit-together)
- [Where to read next](#where-to-read-next)

## What an evaluation is

Evaluating a language model means giving it a fixed set of questions you already know something about, recording what it produces, and grading those answers the same way every time. The result is a number you can compare across models, prompts, and dates. Inspect splits that job into four parts, and the easiest way to hold them in your head is an exam.

The **dataset** is the question sheet with the answer key attached. Each entry is a sample: the question (the *input*), the expected answer or a grading hint (the *target*), and sometimes extras such as multiple-choice options, sandbox files, or metadata.

The **solver** is how the student works through each question: answer immediately, think step by step first, or use tools such as a shell and a Python interpreter over many turns. The solver is the only part that talks to the model being evaluated.

The **scorer** is the grader. It compares what the model produced with the target and writes down a score. Some graders check for an exact match, some look for a substring, and some ask a second model to judge.

The **task** is the whole exam package: the question sheet, the working method, the grading rules, and practical settings such as time limits, attempts, and sandbox.

**Metrics** are the final grade: a scorer marks individual answers, and a metric summarizes those marks into a few numbers such as accuracy and its standard error.

The **eval log** is the marked-up exam paper you keep. It records every question, the full conversation with the model, every score with its explanation, and the final metrics, so you can review, re-grade, or compare the run later.

In code terms:

- A **task** is a `Task` object that bundles a dataset, a solver, and a scorer (plus options) into one runnable evaluation, normally returned from a function decorated with `@task`.
- A **dataset** is an ordered collection of `Sample` objects, each carrying an `input`, an optional `target`, and optional `choices`, `id`, `metadata`, and `files`.
- A **solver** is an async function that receives a `TaskState`, does some work (usually calling the model), and returns the updated `TaskState`.
- A **scorer** is an async function that receives the finished `TaskState` and the sample's `Target` and returns a `Score`.

## Tasks

### What a task is

A task is the unit of evaluation in Inspect: one `Task` object that bundles a dataset, a solver, a scorer, and the settings that govern the run, such as epochs, per-attempt limits, sandbox, and model generation options. You define a task with a function decorated with `@task` that returns a `Task`, and you run it with the `eval()` function or the `inspect eval` command. Every run writes an eval log.

The point of bundling is repeatability: you write the recipe once, give it a name, and can then run it against any model, share it, and compare results across runs, sure that every run used the same recipe.

### The @task decorator

`@task` marks a function that returns a `Task`. It registers the function under a name (by default the function name) so that `inspect eval my_file.py` can discover and run it, and it records the function's parameters so they can be set from the command line. Those parameters are called task parameters, and they are how one definition becomes a family of variants. `@task(name="other")` overrides the registered name, and extra keyword attributes on the decorator become task attributes that `inspect list tasks` can filter on.

### The Task constructor, field by field

The `Task` constructor accepts many keyword arguments. Most tasks use only the first three.

- `dataset`: a `Dataset` or a plain list of `Sample` objects; an empty dataset raises an error.
- `solver`: a solver, a list of solvers (joined with `chain()` and run in order), or an `Agent`. The default is `generate()`, one plain call to the model.
- `scorer`: one or more scorers. Optional; without one the task runs but reports no metrics.
- `metrics`: replaces the metrics the scorer declares (usually `accuracy()` and `stderr()`) with your own list.
- `setup`: solver(s) that always run before the main solver, even when that solver is swapped at run time.
- `cleanup`: an async function called with the `TaskState` after each sample, even one that raised an error.
- `model`: the default model, usually left unset so that `eval()` or `--model` supplies it.
- `config`: a `GenerateConfig` with generation options such as `temperature` and `max_tokens`.
- `model_roles`: named models for particular jobs, such as a `"grader"`, which scorers look up with `get_model(role="grader")`.
- `sandbox`: an isolated environment, such as `"docker"`, where tools like `bash()` run code safely.
- `epochs`: how many times to run each sample: an integer, or an `Epochs` object that also names the reducer(s) for combining repeated scores.
- `message_limit`, `token_limit`, `turn_limit`, `time_limit`, `working_limit`, `cost_limit`: caps applied to each sample. `time_limit` is wall clock seconds, `working_limit` counts only time spent generating and running tools, and `cost_limit` is in dollars.
- `fail_on_error`, `continue_on_fail`, `score_on_error`: what to do when a sample raises an error; by default the first error stops the task.
- `name`, `display_name`, `version`, `metadata`, `tags`: identity and bookkeeping. The names default to the registered function name; bump `version` when a change makes old results incomparable; `metadata` and `tags` are recorded in the eval log.
- Less common: `approval` (tool-call policies), `early_stopping` (stop early based on scores so far), `viewer` and `headline_metric` (log viewer presentation).

### A minimal example

Here is the smallest useful task: one question, one plain model call, and a scorer that checks whether the answer ends with the target.

```python
from inspect_ai import Task, task
from inspect_ai.dataset import Sample
from inspect_ai.scorer import match
from inspect_ai.solver import generate

@task
def capitals():
    return Task(
        dataset=[Sample(input="What is the capital of France?", target="Paris")],
        solver=generate(),
        scorer=match(),
    )
```

Save this as `capitals.py` and run `inspect eval capitals.py --model openai/gpt-4o`: Inspect calls the model, compares the output with the target, prints a summary, and writes an eval log.

### A richer example with parameters

This version reads a shipped dataset, adds a system prompt, grades with a model, repeats each question three times, and caps each attempt at two minutes; the function arguments are task parameters with defaults.

```python
from inspect_ai import Task, task
from inspect_ai.dataset import example_dataset
from inspect_ai.scorer import model_graded_fact
from inspect_ai.solver import generate, system_message

@task
def security_guide(system="devops.txt", epochs=3):
    return Task(
        dataset=example_dataset("security_guide"),
        solver=[system_message(system), generate()],
        scorer=model_graded_fact(),
        epochs=epochs,
        time_limit=120,
        version=1,
    )
```

### Running a task and passing task arguments

`eval()` takes one or more tasks, a model, and any run-time overrides, and returns a list of `EvalLog` objects, one per task.

```python
from inspect_ai import eval

logs = eval(
    security_guide(system="researcher.txt"),
    model="openai/gpt-4o",
    limit=20,
)
log = logs[0]
if log.status == "success":
    print(log.results)
```

On the command line, task parameters are set with `-T`; other overrides have their own flags:

```bash
inspect eval security.py -T system="researcher.txt" -T epochs=5 \
    --model openai/gpt-4o --limit 20
```

Values after `-T` are parsed like YAML, so `epochs=5` arrives as an integer. Settings resolve in layers, each overriding the last: the `Task` definition, then `task_with()`, then environment variables such as `INSPECT_EVAL_MODEL`, then `eval()` or the CLI.

### What epochs mean

A model's output is partly random, so a single attempt at a question is a noisy measurement. An epoch is one complete pass over the dataset: `epochs=5` runs every sample five times, giving five scores per sample, which a reducer combines before metrics are computed. The default is `"mean"`, so a sample answered correctly three times out of five contributes 0.6. `Epochs(5, "mode")` takes the most common score instead, and `Epochs(5, ["mean", "pass_at_5"])` reports metrics under several reducers at once. Epochs must be at least one.

### What an eval log holds

Each run writes one eval log file, by default under `./logs`, in the compact `.eval` format. The `EvalLog` object has a `status` (`"started"`, `"success"`, `"cancelled"`, or `"error"`), an `eval` section describing the task, model, and arguments, a `plan` listing the solvers, `results` holding the metrics, `stats` with token usage, and `samples`: one entry per sample and epoch with its input, output, target, score, and a full transcript. `inspect view` opens a browser viewer, and `read_eval_log()` loads an eval log back into Python.

### Common variations and gotchas

- Swap the solver without editing the task using `--solver name` or `eval(solver=...)`. The `setup` step still runs, which is why it exists.
- The scorer cannot be changed from the CLI; use `task_with(my_task(), scorer=...)` in Python, or expose it as a task parameter.
- `task_with()` modifies the task in place; for several variants, call the `@task` function once per variant.
- Limits are per sample, not per task. A `time_limit` of 120 gives every sample two minutes.
- Passing `metrics` replaces the scorer's metrics rather than adding to them, so include `stderr()` if you still want it.
- To run several tasks or models with retries and resumption, use `eval_set()` with a `log_dir` rather than looping over `eval()`.

### How it connects to the other components

The task is the frame that holds the other three. The dataset supplies the samples and the task decides how many epochs of them to run; the solver receives each sample as a `TaskState` and leaves its answer there, bounded by the task's limits and sandbox; the scorer reads that state and the sample's target and produces a `Score`; and the task's reducer and `metrics` turn many scores into the numbers in the eval log.

### In the .NET port

The port mirrors `Task` with the `EvalTask` record in `src/InspectAzureAI.Eval/Tasks/EvalTask.cs`, whose properties (`Dataset`, `Setup`, `Solver`, `Scorers`, `Metrics`, `Config`, `Sandbox`, `Epochs`, the six limits, `Version`, `Metadata`, `TaskArgs`, `ModelRoles`, `Approval`) follow the Python names. `Epochs` in `Epochs.cs` carries a `Count` and optional `Reducers`, and the `[Task]` attribute in `TaskAttribute.cs` plays the role of `@task`: it marks a public static method returning an `EvalTask`, whose parameters bind to `-T` arguments in the `inspectai` CLI. `Eval.RunAsync(EvalTask, EvalOptions)` is the port of `eval()` and returns a single `EvalLog` rather than a list. Notable differences: `Name` is required because there is no registry to infer it from, the model lives on `EvalOptions` rather than the task, `TimeLimit` is a `TimeSpan`, and there is no `Cleanup`, `DisplayName`, `Tags`, or `Viewer` property.

## Datasets

A dataset is the list of samples that a task runs over. Keeping the questions separate from the answering and the marking means the same exam paper can be sat by many students, and whether your data lives in CSV, JSON, Hugging Face or a Python list, Inspect turns it into one uniform sequence of samples. The rest of the dataset API (loaders, field mapping, filtering, shuffling) exists to get your data into that list.

### The Sample object: one question on the paper

`Sample` (in `inspect_ai.dataset`) has one required field and several optional ones:

- `input`: the question, and the only required field. Either a plain string or a list of chat messages (see below).
- `target`: the expected answer: a string, or a list of strings when several are acceptable. For an exact-match scorer this is the literal answer; for a model-graded scorer it can be a sentence describing a good answer.
- `choices`: the options for a multiple-choice question; the `target` is then the capital letter of the correct option (`"A"`, `"B"` and so on).
- `id`: a unique integer or string. Without an id column, Inspect numbers the samples from 1.
- `metadata`: a free-form dictionary of extra information (difficulty, category, source) that solvers and scorers can read and that is written to the eval log.
- `sandbox`: the isolated environment (for example `"docker"`) this sample needs, optionally with a config file. Most tasks set it once at the `Task` level instead.
- `files`: a dictionary mapping a sandbox path to a file to copy there: a local path, a URL, or the contents as a string.
- `setup`: a bash script (path or inline text) that runs inside the sandbox before the solver starts, with a five minute timeout.

### What the input can be

The simplest input is a string, which Inspect wraps in a `ChatMessageUser`, a single "user" turn. The input can also be a list of `ChatMessage` objects, each with a `role` (`system`, `user`, `assistant` or `tool`) and `content`, which lets you open with a system prompt, prime the conversation with earlier assistant turns, or include images. In JSON data this is a list of `{"role": ..., "content": ...}` objects.

### Loading a dataset

All loaders live in `inspect_ai.dataset` and return a `Dataset`.

- `csv_dataset(path)`: reads a CSV file whose first row names the columns (or pass `fieldnames`). Paths can be local, `s3://` or `https://`.
- `json_dataset(path)`: reads a JSON array of objects, or a JSON Lines file (`.jsonl`) with one object per line.
- `hf_dataset(path, split=...)`: reads a named `split` from Hugging Face using the `datasets` package. Pass `trust=True` only for repositories whose loading code you have read.
- `example_dataset(name)`: loads one of the small datasets bundled with Inspect (`security_guide`, `theory_of_mind`, `popularity`, `biology_qa`, `bias_detection`).
- `MemoryDataset(samples)`: wraps a Python list of `Sample` objects you built yourself, the escape hatch for any other source.

The file loaders share optional arguments: `sample_fields` (the mapping, described next), `auto_id`, `shuffle` and `seed`, `shuffle_choices`, `limit`, and `name`.

### Mapping your own columns

If your file already has columns named `input`, `target` and so on, the loaders need no extra arguments. Otherwise there are two ways to map them.

`FieldSpec` is a declarative mapping: one attribute per sample field, holding the name of the matching column in your data; you only set the ones that differ from the defaults. Its `metadata` attribute takes a list of column names to gather into the metadata dictionary, or a frozen Pydantic model class for typed, validated metadata.

`record_to_sample` is the programmatic option: a function that receives one raw record (a `dict` of the row as read from the file) and returns a `Sample`, or a list of them. Use it when a value needs converting, columns need combining, or one record should produce several samples. Either option is passed as `sample_fields`; the type alias for the function form is `RecordToSample`.

### A worked example

Suppose `security.csv` has columns `question`, `answer`, `qid` and `topic`. A `FieldSpec` maps it in a few lines:

```python
from inspect_ai.dataset import FieldSpec, csv_dataset

dataset = csv_dataset(
    "security.csv",
    sample_fields=FieldSpec(
        input="question",
        target="answer",
        id="qid",
        metadata=["topic"],
    ),
)
```

For `popularity.jsonl`, where the answer has stray whitespace and you want to shape the metadata yourself, a `record_to_sample` function handles it:

```python
from inspect_ai.dataset import Sample, json_dataset

def record_to_sample(record):
    return Sample(
        input=record["question"],
        target=record["answer_matching_behavior"].strip(),
        id=record["question_id"],
        metadata={"label_confidence": record["label_confidence"]},
    )

dataset = json_dataset("popularity.jsonl", record_to_sample)
```

The tutorial's HellaSwag task does the same, converting an integer answer index to a letter with `chr(ord("A") + int(record["label"]))` and putting the candidate endings in `choices`.

Finally, a hand-built `MemoryDataset` plugged into a task:

```python
from inspect_ai import Task, task
from inspect_ai.dataset import MemoryDataset, Sample
from inspect_ai.scorer import model_graded_fact
from inspect_ai.solver import generate

@task
def security_guide():
    return Task(
        dataset=MemoryDataset([
            Sample(input="What cookie attributes should I use for strong security?",
                   target="secure samesite and httponly"),
        ]),
        solver=generate(),
        scorer=model_graded_fact(),
    )
```

### Filtering, shuffling and limiting

A `Dataset` is a Python sequence: you can index it, take its `len()`, and slice it (`dataset[0:100]` is the first hundred samples). It also has:

- `filter(predicate)` returns a new dataset holding only the samples your function accepts, for example `dataset.filter(lambda s: s.metadata["category"] == "advanced")`.
- `shuffle(seed=None)` randomizes the order in place; pass a seed for a repeatable order.
- `shuffle_choices(seed=None)` randomizes each sample's `choices` and rewrites the `target` letter to match, so a model cannot learn that the answer is usually C.
- `sort(key=...)` sorts in place, by input length unless you supply a key.

At run time, `inspect eval task.py --limit 50` runs the first fifty samples, `--sample-id 22,23` picks specific ids (glob patterns like `*_advanced` work too), and `--sample-shuffle 42` shuffles with a seed. `eval()` takes the same options as keyword arguments.

### From sample to solver

When the task runs, each sample becomes the starting point of a `TaskState` (described in the Solvers chapter). The sample itself is never modified, so every epoch starts from the same place.

### Common variations and gotchas

- Multiple acceptable answers: give `target` a list of strings. Scorers such as `match()` and `includes()` accept any of them.
- Choices in CSV: a single cell holding `"A, B, C"` is split on commas (or whitespace if there are none).
- Relative paths in `files` and `setup` are resolved relative to the dataset file, not the current directory.
- Typed metadata: pass a Pydantic model class as `FieldSpec.metadata` and read it back with `sample.metadata_as(MyModel)` or `state.metadata_as(MyModel)`; the class must be `frozen=True`.

### How it connects to the other components

At run time the task turns each sample into a `TaskState` and gives it to the solver, which produces the model's answer; the scorer then compares that answer with the sample's `target` (and `choices`, for multiple choice) to produce a `Score`. The sample's `id` and `metadata` travel through the pipeline into the eval log, so you can trace any result back to its row.

### In the .NET port

The C# port keeps the same shapes under `InspectAzureAI.Eval.Dataset`. `Sample` is a record holding a `SampleInput` (text or a message list), a `Target`, and the same optional `Choices`, `Id`, `Metadata`, `Sandbox`, `Files` and `Setup` properties. `IDataset` mirrors the Python `Dataset` interface with `Filter`, `Shuffle`, `ShuffleChoices`, `Sort` and `Slice`; `MemoryDataset` is its in-memory implementation; `FieldSpec` is a record with the same default column names; and `RecordToSample` is a delegate returning one or more samples. The static `Datasets.Json` and `Datasets.Csv` methods are the loaders, taking `fields` and `recordToSample` as separate parameters rather than one `sample_fields` union. The notable gaps: only local paths are supported (no S3 or HTTPS), there is no Hugging Face or example loader, and a seeded shuffle orders differently from Python because .NET's `Random` differs.

## Solvers

A solver is the part of an evaluation that produces the model's answer: an async function that receives a `TaskState` (the working state of one sample) plus a `generate` function, changes the state in some way, and returns it. Most solvers either edit the conversation (adding a system prompt, rewriting the question) or call the model (which appends the reply and records it as the output). A task has exactly one top-level solver, but that solver is very often a chain of smaller solvers run in order.

Solvers exist because an evaluation needs to say not only *what* questions to ask but *how* to ask them: with a system prompt or without, thinking step by step or not, with tools and retries or neither. These choices change results a great deal, so Inspect keeps them separate from the data and the grading, and each solver is one small step that you can swap in or out without touching the questions or the marking.

### TaskState: the sample's working memory

`TaskState` is the object every solver reads and writes. Inspect creates one per sample per epoch, carrying everything the sample knows so far:

- `messages`: the chat history, a list of `ChatMessage` objects that starts from the sample's `input` and grows as solvers add messages and the model replies.
- `output`: a `ModelOutput` holding the model's most recent reply; `output.completion` is its plain text. Scorers usually read this field.
- `input` and `input_text`: the original sample input, kept unchanged.
- `user_prompt`: the first user message, so a solver can rewrite the question with `state.user_prompt.text = ...`.
- `target`: the expected answer, wrapped in a `Target`, which solvers normally leave alone.
- `metadata`: the sample's metadata dictionary, which template solvers substitute into prompts automatically.
- `store`: a `Store`, a key-value bag shared by solvers, tools and scorers during one sample.
- `tools` and `tool_choice`: the tools the model may call, and whether it must call one.
- `completed`: a flag; once a solver sets it to `True`, remaining solvers in the chain are skipped.
- `choices` and `scores`: multiple-choice options from the sample, and scores a solver chooses to record itself (merged with the scorer's).

The state also carries limits (`message_limit`, `token_limit`, `cost_limit`). When one is hit, solving stops and the sample goes on to scoring with whatever it has.

### The generate function

The second argument to every solver is `generate`, a helper that sends `state.messages` (and `state.tools`) to the model being evaluated, appends the reply to `messages`, and sets `output`. Its `tool_calls` option controls what happens if the model asks to use a tool: `"loop"` (the default) runs the tools and calls the model again until it stops asking, `"single"` runs one round, and `"none"` leaves the calls to you. It also accepts generation settings such as `max_tokens` or `temperature`. Solvers that only edit prompts never call it.

### Built-in solvers

All of these live in `inspect_ai.solver`.

- `system_message(template)`: inserts a system message (after any existing ones), filling the template from `metadata`, the store and any extra keyword parameters.
- `user_message(template)`: appends a user message, with the same substitution.
- `prompt_template(template)`: rewrites the user prompt. The template must contain a `{prompt}` placeholder that receives the current question.
- `chain_of_thought()`: rewrites the user prompt to ask for step-by-step reasoning ending with a line of the form `ANSWER: ...`, which makes scoring easier.
- `generate()`: simply calls `generate(state)`; the default solver if a task does not specify one.
- `multiple_choice()`: formats the sample's `choices` as lettered options, calls the model itself (so do not add another `generate()`), and records which letters it picked. Pair it with the `choice()` scorer; options include `cot` and `multiple_correct`.
- `self_critique()`: sends the question and answer to a critic model (the same model unless you pass `model`), plays the critique back as a user message, and calls `generate` again for a revised answer.
- `use_tools(*tools)`: puts tools into `state.tools` for later `generate` calls, replacing existing tools unless you pass `append=True`.
- `basic_agent()`: a complete tool-using agent. It installs a system message and a `submit` tool, then calls the model and runs requested tools until the model calls `submit`. With `max_attempts` above one it scores each submission and, if wrong, tells the model to try again.

### Chaining solvers

You can pass a list of solvers to a `Task`, or wrap them with `chain()`. Solvers run in order, each receiving the state the previous one returned, and the chain stops early if `state.completed` becomes `True`; nested chains are flattened. Here is a composite solver from the Inspect docs, used with `solver=critique()`:

```python
from inspect_ai.solver import (
    chain, generate, prompt_template, self_critique, solver, system_message
)

@solver
def critique(system_prompt="system.txt", user_prompt="prompt.txt"):
    return chain(
        system_message(system_prompt),
        prompt_template(user_prompt),
        generate(),
        self_critique(),
    )
```

As a recipe: set the model's role, rewrite the question, get an answer, critique it and answer again.

### Writing your own solver

A custom solver is a function decorated with `@solver` that returns an async `solve` function: the outer function holds the parameters, the inner one does the work, and the decorator registers the solver so Inspect can log its name and arguments. This example appends a hint stored in the sample's metadata:

```python
from inspect_ai.solver import Generate, Solver, TaskState, solver

@solver
def add_hint(prefix: str = "Hint: ") -> Solver:
    async def solve(state: TaskState, generate: Generate) -> TaskState:
        hint = state.metadata.get("hint")
        if hint:
            state.user_prompt.text += f"\n\n{prefix}{hint}"
        return state

    return solve
```

Use it like any built-in: `solver=[add_hint(), generate()]`. A solver may also call `get_model()` to use another model, or `score(state)` to get a list of intermediate scores from the task's scorers.

### Solvers and agents

An agent is a model working towards a goal over many turns: it calls tools, looks at the results, and decides what to do next. In Inspect an `Agent` is a close cousin of a solver, a function that takes and returns a state, but its `AgentState` holds only `messages` and `output`, so one agent can serve as a solver, a tool, or a member of a multi-agent system. `Task` and `chain()` accept agents directly (via `as_solver()`). The built-in `react()` agent is the recommended tool-calling loop; here it is as a task solver, adapted from the tutorial:

```python
from inspect_ai import Task, task
from inspect_ai.agent import react
from inspect_ai.scorer import includes
from inspect_ai.tool import bash, python

@task
def ctf(attempts=3):
    return Task(
        dataset=read_dataset(),  # helper defined alongside the tutorial task
        solver=react(tools=[bash(), python()], attempts=attempts),
        scorer=includes(),
        sandbox="docker",
        message_limit=30,
    )
```

`basic_agent()` is the older, solver-flavoured version of the same idea.

### Common variations and gotchas

- Always `await` calls to `generate` and to model methods; a missing `await` breaks Inspect's scheduling of model calls.
- `user_prompt` raises if there is no user message, so solvers that use it assume a chat-shaped input.
- Template solvers format with Python's `str.format`, so a literal `{` in a template needs to be doubled.
- Setting `state.completed = True` ends the chain early; a message, token or cost limit does the same, and the sample is still scored.

### How it connects to the other components

For each sample in the dataset, Inspect builds a `TaskState`, runs the task's solver on it, and then hands the finished state (with its `output` and `messages`) and the sample's `Target` to the scorer. Solvers sit in the middle: they consume what the dataset provides and produce what the scorer grades, without knowing how either is implemented.

### In the .NET port

`Solver` and `Generate` are delegates in `src/InspectAzureAI.Eval/Solvers/SolverDelegates.cs`, with `ToolCallsMode` standing in for the `"loop"`, `"single"` and `"none"` strings. `TaskState` is a class in `TaskState.cs` with the same members (`Messages`, `Output`, `UserPrompt`, `Target`, `Metadata`, `Store`, `Tools`, `ToolChoice`, `Completed`, `Choices`, `Scores`). The built-ins are static methods on a `Solvers` class: `Chain`, `Generate`, `UseTools`, `SystemMessage`, `UserMessage`, `PromptTemplate`, `ChainOfThought`, `SelfCritique`, `MultipleChoice`, `Fork` and `BasicAgent`, while the `react` agent lives under `Agents` and is attached to a task with `Agents.AsSolver`. Notable differences: every solver takes a `CancellationToken`; the per-sample `Generate` is built explicitly by `GenerateLoop.Create(model)`; there is no `@solver` registry, so the CLI resolves solvers by their Python names against the static `Solvers` factories; and `UseTools` leaves `tool_choice` untouched by default where Python sets it to `"auto"`.

## Scorers

A scorer is the part of an evaluation that marks the model's work. When the solver finishes, Inspect hands the scorer the finished `TaskState` (including the model's final `output`) and the `Target` (the expected answer or grading guidance from the dataset), and the scorer compares them and returns a `Score`. Scorers run once per sample; metrics then combine those scores into headline numbers.

A model's raw output is just text, and the scorer is the consistent rule for turning "here is what the model said" into "was that right, and how right". Sometimes the mark scheme is one word ("Paris"); sometimes it is a rubric ("should mention salting and a slow hash") that needs judgement. Inspect ships examiners for both, and lets you write your own.

### The key pieces

**Score.** Every scorer returns a `Score` object with these fields:

- `value`: the mark itself: a string, number, or boolean, or a list or dictionary of those. Most scorers use the constants `CORRECT` (`"C"`), `INCORRECT` (`"I"`), `PARTIAL` (`"P"`), or `NOANSWER` (`"N"`), or a float from 0 to 1.
- `answer`: the text the scorer extracted from the output. Optional but strongly recommended, since it shows in the log viewer and exposes scoring mistakes.
- `explanation`: why the score was given, often the full model output or the grader's reasoning.
- `metadata`: a dictionary of extra details worth keeping in the eval log.

A `Score` also has an optional `reason`, a machine-readable label for abnormal cases such as `"grader_failed"`; a scorer that cannot produce a mark at all returns `Score.unscored(reason=...)`, which metrics skip rather than count as zero.

**Target.** The `Target` is a sequence of one or more strings (`target.text` joins them), since a dataset can list several acceptable answers and the text-matching scorers accept any of them.

**Built-in scorers.** Pick by how the answer needs to be checked:

- `match()`: the target appears at a known position: `"end"` (the default), `"begin"`, `"any"`, or `"exact"` (the whole output must equal the target). `numeric=True` compares numbers rather than text.
- `includes()`: the target appears anywhere in the output. Good for flags and keywords.
- `exact()`: normalizes both sides and requires the whole output to equal a target. Reports `mean` and `stderr`.
- `pattern()`: a regular expression capture group extracts the answer; if nothing matches, the score is `INCORRECT` with `reason="invalid_response_format"`.
- `answer()`: for prompts that tell the model to end with `ANSWER: X`. Pass `"letter"`, `"word"`, or `"line"` to say how much to extract.
- `choice()`: the partner of the `multiple_choice()` solver. It reads the letters the model picked, undoes any shuffling, and compares with the target (`"A,B"` allows several correct answers).
- `model_graded_qa()`: asks a second model (the grader) whether the output meets the criterion in the target, for open-ended answers where no string match will do.
- `model_graded_fact()`: the same machinery, but asking only whether the output contains the fact in the target, for answers buried in a longer response.
- `f1()`: word overlap between output and target as an F1 score (a balance of precision and recall) from 0 to 1, for short free-text answers.

**Scorers versus metrics.** A scorer marks one sample. A metric summarizes marks across all samples: it receives a list of `SampleScore` objects (a `Score` plus its sample id) and returns a number. The main ones:

- `accuracy()`: the proportion of correct answers, converting values with `value_to_float()` (`"C"` is 1, `"I"` and `"N"` are 0, `"P"` is 0.5).
- `mean()`: the average of numeric scores, for scorers such as `f1()` that return a continuum.
- `stderr()`: the standard error of the mean, how much the headline number might move with different questions.
- `bootstrap_stderr()`: the same idea estimated by resampling, 1000 times by default.

Each scorer declares its own default metrics (most use `accuracy()` and `stderr()`); as the Tasks chapter noted, `metrics=[...]` on the `Task` replaces them entirely.

**Reducers.** With several epochs (see "What epochs mean" in the Tasks chapter), a reducer folds each sample's scores into one before metrics run. The default is `"mean"`; others are `"median"`, `"mode"` (most common), `"majority"` (more than half agree, else unscored), `"max"`, `"at_least_{k}"`, `"pass_at_{k}"` (at least one of k attempts succeeds), `"pass_k_{k}"` (all k succeed), and `"collect"` (keep every value as a list). A reducer keeps `answer` and `explanation` only when they are identical across epochs.

**Custom scorers.** A custom scorer is an async function taking a `TaskState` and a `Target` and returning a `Score`, wrapped in a factory decorated with `@scorer(metrics=[...])`, which registers it by name so eval logs record which scorer produced each score.

### Worked examples

The simplest case is the `capitals` task from the Tasks chapter: `scorer=match()` checks that the model's answer ends with the target.

When the target is a rubric rather than a literal answer, use a model grader; here the dataset's `criterion` column is the target and a named model grades:

```python
from inspect_ai import Task, task
from inspect_ai.dataset import FieldSpec, csv_dataset
from inspect_ai.scorer import model_graded_qa
from inspect_ai.solver import generate

@task
def graded_geography():
    # CSV columns "question" and "criterion" (e.g. "The answer should name Paris")
    return Task(
        dataset=csv_dataset("geography.csv",
            sample_fields=FieldSpec(input="question", target="criterion")),
        solver=generate(),
        scorer=model_graded_qa(model="openai/gpt-4o"),
    )
```

When neither fits, write your own. This scorer takes the last number in the output and accepts it if within one percent of the target:

```python
import re
from inspect_ai.scorer import CORRECT, INCORRECT, Score, Target, accuracy, scorer, stderr
from inspect_ai.solver import TaskState

@scorer(metrics=[accuracy(), stderr()])
def close_enough(rel_tol: float = 0.01):
    async def score(state: TaskState, target: Target) -> Score:
        numbers = re.findall(r"-?\d+(?:\.\d+)?", state.output.completion)
        if not numbers:
            return Score(value=INCORRECT, explanation="No number found in output.")
        expected = float(target.text)
        correct = abs(float(numbers[-1]) - expected) <= rel_tol * abs(expected)
        return Score(value=CORRECT if correct else INCORRECT, answer=numbers[-1])
    return score
```

### Model-graded scoring and its tradeoffs

A model grader is the only practical option for open-ended answers, but it is a judge with quirks:

- **Who grades.** By default the grader is the model bound to the `"grader"` role, or else the model under test, which can flatter itself. Pass `model=` for an independent grader.
- **How the grade is read.** The grader is asked to reason step by step and end with `GRADE: C` or `GRADE: I`; Inspect reads the last such line. An unparseable grade leaves the sample unscored with `reason="grader_failed"`. `partial_credit=True` adds `GRADE: P`, worth 0.5.
- **Consistency and cost.** Graders run at the provider's default temperature, so borderline grades can flip between runs; build the grader with `get_model(..., config=GenerateConfig(temperature=0))` for stability. Each graded sample costs a model call; `cascade()` runs a cheap scorer first and calls the grader only when needed.
- **Panels.** Pass a list of models and each grades independently; `"majority"` decides and the votes are kept in the score's `metadata`.
- **Injection.** The grader sees model-controlled text, so Inspect neutralizes fake `[BEGIN DATA]` and `[END DATA]` markers in the answer, question, and criterion.

### Common variations and gotchas

- **Several scorers.** Pass a list to `scorer=` for one score per scorer on each sample. A scorer can also return a dictionary value, with metrics declared per key.
- **Value types must match metrics.** If you return your own strings, tell the metric how to read them: `accuracy(to_float=value_to_float(correct="pass", incorrect="fail"))`.
- **Unscored is not zero.** `Score.unscored()` samples are excluded from metrics and reported separately as `unscored_samples`.

### How it connects to the other components

The dataset supplies each sample's `target`, so the dataset and the scorer must agree on what it means (a literal answer for `match()`, a rubric for `model_graded_qa()`). The solver leaves the model's `output` in the `TaskState` for the scorer to read. The task's reducers then fold the scorer's per-sample results, and its metrics turn them into the numbers in the eval log.

### In the .NET port

The C# port keeps the same shapes under `InspectAzureAI.Eval.Scorers`. A `Scorer` is a delegate `(TaskState, Target, CancellationToken) -> Task<Score>`; `ScorerDef(Name, Score, Metrics)` replaces the `@scorer` decorator, and hand-written scorers go through `Scorers.Custom(name, scorer, metrics)`. `Score` is a record with `Value` (a `ScoreValue` union of `Str`, `Num`, `Bool`, `List`, and `Dict`), `Answer`, `Explanation`, `Reason`, and `Metadata`; `ScoreConstants.Correct` and friends replace `CORRECT` and `INCORRECT`. The built-ins are `Scorers.Includes`, `Match`, `Pattern`, `Answer`, `Choice`, `F1`, `Exact`, `ModelGradedQa`, `ModelGradedFact`, `MultiScorer`, and `Cascade`; metrics live on `Metrics` (`Accuracy`, `Mean`, `Stderr`, `BootstrapStderr`) and reducers on `Reducers`. Two differences stand out: the C# `Scorer` cannot return null, so an unscored result is a `Score.Unscored` value (its value is NaN), which `MultiScorer` and `Cascade` skip; and `ModelGradedQa` takes one optional `Model` rather than a list or role, so a grading panel is assembled with `Scorers.MultiScorer`.

## How the components fit together

The clearest way to see the pipeline is to follow one sample from dataset to eval log.

**1. Sample becomes TaskState.** For each sample and each epoch, Inspect builds a `TaskState` from the sample's `input`, `target`, `choices` and `metadata`, with `messages` seeded from the input, empty slots for the solver to fill, and the task's per-sample limits.

**2. Setup and solver run.** Inspect runs the task's `setup` solvers first, if any, then the main solver, giving each the `TaskState` and the `generate` function; a list of solvers is chained, stopping early if `state.completed` becomes true. A solver never grades or writes the eval log, and it should treat `state.target` as the answer key, not something to show the model.

**3. Model output lands in the state.** When the solver returns, `state.output` holds the last `ModelOutput` and `state.messages` the whole conversation. A limit that was hit ends the solver but not the sample, which continues to scoring with whatever it has.

**4. Scorer produces a Score.** Inspect marks the state completed and calls each scorer as `await scorer(state, Target(sample.target))`. The returned `Score` is stored under the scorer's name in `state.scores`, a `ScoreEvent` goes into the sample's transcript, and a `SampleScore` (the score plus sample id, metadata, and scorer name) is collected for the task. A scorer never sees other samples.

**5. Reducer and metrics.** With more than one epoch, the reducer named by `Epochs(count, reducer)` (mean by default) first collapses each sample id's scores into one. Then each scorer's default metrics, or the task's `metrics` override, turn the list of `SampleScore` objects into values: one set per scorer, and per reducer if several are configured.

**6. The eval log is written.** Each sample is written as it finishes, with its input, messages, output, scores, store, and transcript events; metrics and run statistics are added when the task ends. `inspect view` opens the eval log in the browser, `inspect score` re-grades it, and `samples_df()` and `evals_df()` load it into Pandas.

In diagram form:

```
Task = { dataset, setup, solver, scorer, metrics, options }

  Dataset
    |  one Sample, repeated once per epoch
    v
  TaskState (input, target, messages, limits, ...)
    |
    v
  setup -> solver(s)  --generate()-->  Model
    |  TaskState now has output and full messages
    v
  Scorer(s) --> Score --> SampleScore   (one per sample per epoch)
    |
    v
  Reducer (per sample id, across epochs)
    |
    v
  Metrics (accuracy, stderr, ...)
    |
    v
  Eval log (samples, scores, transcript, metrics)
```

Each piece can be replaced without touching the others. The most common swaps are:

- **Swap the dataset.** Keep the solver and scorer and point the task at different questions, usually through a task parameter or a new `record_to_sample()` mapping.
- **Swap the solver.** Pass `--solver` on the command line or `solver=` to `eval()`; the task's `setup` solvers still run. This is how you compare `generate()` against a `react()` agent on the same questions.
- **Add a scorer.** Give the task a list, such as `scorer=[includes(), model_graded_fact()]`; each scorer gets its own scores and metrics in the eval log, and `inspect score` can re-score an existing eval log.
- **Run several epochs.** Pass `--epochs 5`, or set `epochs=Epochs(5, "max")` in the task, to reduce variance or to measure success in any of several attempts.
- **Swap the model.** `--model` is not one of the four components, but it is the change people make most often, and none of them need to change for it.

### In the .NET port

The pipeline runs the same way. `Eval.RunAsync` fans out one run per sample and epoch; `SampleRunner` builds the `TaskState`, runs setup and solver, marks the state completed, and runs each `ScorerDef`; `EvalResultsBuilder` reduces epochs per sample id before computing metrics; and `EvalLogWriter` writes an eval log the Python tools can read.

## Where to read next

- Inspect docs: [Tasks](https://inspect.aisi.org.uk/tasks.html), [Datasets](https://inspect.aisi.org.uk/datasets.html), [Solvers](https://inspect.aisi.org.uk/solvers.html), [Scorers](https://inspect.aisi.org.uk/scorers.html), and the [Tutorial](https://inspect.aisi.org.uk/tutorial.html) with complete worked examples.
- This repository: [docs/ARCHITECTURE.md](ARCHITECTURE.md) on how the .NET port lays out the same components in C#.
