// A narrated walk through the eval primitives - Dataset, Solver, Scorer, Task -
// and how they combine into a DETERMINISTIC eval: the same samples, the same
// answers, the same scores, every run.
//
//   dotnet run --project src/InspectAzureAI.EvalPrimitivesDemo            # all six lessons
//   dotnet run --project src/InspectAzureAI.EvalPrimitivesDemo -- 3       # one lesson
//   dotnet run --project src/InspectAzureAI.EvalPrimitivesDemo -- 1 6     # a selection, run in order
//   dotnet run --project src/InspectAzureAI.EvalPrimitivesDemo -- list    # lesson titles
//
// Everything is the real InspectAzureAI.Eval API. The only stand-in is the
// model: ScriptedModelApi (the port's mockllm) replays answers from
// AnswerKey.cs, so no network, no keys, and no randomness anywhere.
//
// The lessons go bottom-up: the pieces first (1-4), then the Task that
// assembles them (5), then the runner (6).

using System.Globalization;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Model.Cache;
using InspectAzureAI.Eval.Runner;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Eval.Solvers;
using InspectAzureAI.Eval.Tasks;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.EvalPrimitivesDemo;
using InspectAzureAI.Provider.Core;

// The static classes share names with their namespaces; every file in this repo aliases them.
using Datasets = InspectAzureAI.Eval.Dataset.Datasets;
using Eval = InspectAzureAI.Eval.Runner.Eval;
using Metrics = InspectAzureAI.Eval.Scorers.Metrics;
using Model = InspectAzureAI.Eval.Model.Model;
using Scorers = InspectAzureAI.Eval.Scorers.Scorers;
using Solvers = InspectAzureAI.Eval.Solvers.Solvers;

(int Number, string Title, Func<Task> Run)[] lessons =
[
    (1, "Datasets - Sample, Target, MemoryDataset, Datasets.Json", Lesson1Async),
    (2, "Solvers - TaskState in, TaskState out", Lesson2Async),
    (3, "Scorers - the same answer, judged by different rules", Lesson3Async),
    (4, "Metrics and reducers - from many Scores to one number", Lesson4Async),
    (5, "Tasks - assembling the pieces", Lesson5Async),
    (6, "Running it - Eval.RunAsync against a scripted model, twice", Lesson6Async),
];

if (args.Any(a => a is "list" or "--list" or "-h" or "--help" or "help"))
{
    PrintUsage(Console.Out);
    return 0;
}

var selected = new SortedSet<int>();
foreach (var arg in args)
{
    if (int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && lessons.Any(l => l.Number == n))
    {
        selected.Add(n);
    }
    else
    {
        Console.Error.WriteLine($"Unknown lesson '{arg}'.");
        Console.Error.WriteLine();
        PrintUsage(Console.Error);
        return 2;
    }
}

if (selected.Count == 0)
{
    selected.UnionWith(lessons.Select(l => l.Number));
}

foreach (var (number, title, run) in lessons.Where(l => selected.Contains(l.Number)))
{
    Console.WriteLine();
    Console.WriteLine($"== Lesson {number}: {title}");
    Console.WriteLine();
    await run();
}

Console.WriteLine();
Say(selected.Count == lessons.Length
    ? "Done. Next: examples/ (35 ported inspect_ai examples) and docs/ARCHITECTURE.md."
    : $"Done. Run with no arguments for all {lessons.Length} lessons, or `list` for the titles.");
return 0;

// ───────────────────────────────────────────────────────────────────────────
static Task Lesson1Async()
// ───────────────────────────────────────────────────────────────────────────
{
    // A Sample is the unit of evaluation: the Input a solver will see and the Target a scorer will judge
    // against. Target is a record holding one OR MORE accepted values (implicit from string and string[]).
    var paris = new Sample("What is the capital of France?") { Id = "fr", Target = "Paris" };
    var colour = new Sample("Name a primary colour.") { Id = "colour", Target = new[] { "red", "blue", "yellow" } };
    var planet = new Sample("Which planet is known as the Red Planet?")
    {
        Id = "planet",
        Choices = ["Venus", "Mars", "Jupiter"],         // for multiple-choice solvers/scorers
        Target = "B",
        Metadata = new Dictionary<string, object?> { ["topic"] = "space" }, // free-form; metrics can group by it
    };
    Show("paris.Target.Text ", paris.Target.Text);
    Show("colour.Target     ", string.Join(" | ", colour.Target.Values));
    Show("planet.Choices    ", string.Join(", ", planet.Choices!));

    // MemoryDataset wraps samples built in code. IDataset is an IReadOnlyList<Sample> plus Filter, Shuffle,
    // Slice and Sort.
    var byHand = new MemoryDataset([paris, colour, planet], name: "by-hand");
    Show("byHand.Count      ", byHand.Count);

    // Datasets.Json loads the same shape from a .jsonl file. FieldSpec maps YOUR column names onto Sample's,
    // and autoId numbers the samples 1..n. (Datasets.Csv and Datasets.Hf follow the same pattern.)
    var quiz = LoadQuiz();
    Show("quiz.Count        ", quiz.Count);
    foreach (var sample in quiz)
    {
        Say($"    #{sample.Id}  {sample.Input.Text,-72} -> [{string.Join("|", sample.Target.Values)}]  topic={sample.Metadata?["topic"]}");
    }

    // Filter and Slice return new datasets and are obviously deterministic. Shuffle mutates in place (as in
    // Python) and takes a seed, so even a "random" order is reproducible run after run.
    Show("Filter(arithmetic)", Ids(quiz.Filter(s => Equals(s.Metadata?["topic"]?.ToString(), "arithmetic"), "arithmetic")));
    Show("Slice(..2)        ", Ids(quiz.Slice(..2)));
    var shuffled = LoadQuiz();
    shuffled.Shuffle(42);
    Show("Shuffle(seed: 42) ", Ids(shuffled));
    var shuffledAgain = LoadQuiz();
    shuffledAgain.Shuffle(42);
    Show("Shuffle(seed: 42) ", Ids(shuffledAgain));
    return Task.CompletedTask;
}

// ───────────────────────────────────────────────────────────────────────────
static async Task Lesson2Async()
// ───────────────────────────────────────────────────────────────────────────
{
    // A Solver is a delegate: (TaskState, Generate, CancellationToken) -> TaskState. It reads and mutates
    // the state: messages in, Output out. `generate` is the model call, handed IN, so a solver never picks a
    // model - which is why the runner can swap in a scripted one. Here every call gets ScriptedGenerate.
    var state = NewState("What is the capital of France?", target: "Paris");
    Say($"  start:               {Describe(state.Messages)}");

    // system_message() prepends a system prompt.
    state = await Solvers.SystemMessage("Answer in one word.")(state, ScriptedGenerate, CancellationToken.None);
    Say($"  after SystemMessage: {Describe(state.Messages)}");

    // prompt_template() rewrites the user prompt; {prompt} is the original text.
    state = await Solvers.PromptTemplate("Question: {prompt}\nReply with just the answer.")(state, ScriptedGenerate, CancellationToken.None);
    Say($"  after PromptTemplate: user = {Quote(state.UserPrompt.Text)}");

    // generate() calls the model and stores the result in state.Output.
    state = await Solvers.Generate()(state, ScriptedGenerate, CancellationToken.None);
    Say($"  after Generate:      {Describe(state.Messages)}");
    Show("Output.Completion  ", state.Output.Completion);

    // chain() runs solvers in turn. A custom solver is any lambda of the right shape - this one post-processes
    // the completion (see UppercaseOutput below).
    var chained = NewState("What is 7 * 6?", target: "42");
    var chain = Solvers.Chain(Solvers.SystemMessage("Be terse."), Solvers.Generate(), UppercaseOutput());
    chained = await chain(chained, ScriptedGenerate, CancellationToken.None);
    Show("chain -> Completion", chained.Output.Completion);

    // Setting Completed = true ends the chain early: later solvers (here, Generate) never run.
    var stopped = NewState("What is 12 - 5?", target: "7");
    stopped = await Solvers.Chain(GiveUp("skipped by policy"), Solvers.Generate())(stopped, ScriptedGenerate, CancellationToken.None);
    Show("Completed          ", stopped.Completed);
    Show("Completion         ", stopped.Output.Completion);
}

// ───────────────────────────────────────────────────────────────────────────
static async Task Lesson3Async()
// ───────────────────────────────────────────────────────────────────────────
{
    // A Scorer is a delegate: (TaskState, Target, CancellationToken) -> Score. A ScorerDef bundles it with a
    // name and the metrics that summarise it. The built-ins return "C" (correct) or "I" (incorrect); yours can
    // return any ScoreValue. Every scorer here is a pure function of (completion, target): deterministic.
    Say("  completion = \"The capital of France is Paris.\", target = \"Paris\"");
    var sentence = Answered("What is the capital of France?", "The capital of France is Paris.", target: "Paris");
    await ScoreWith(Scorers.Includes(), sentence);                 // target appears anywhere
    await ScoreWith(Scorers.Match("end"), sentence, "match(end)");     // target at the end (case/punctuation tolerant)
    await ScoreWith(Scorers.Match("begin"), sentence, "match(begin)"); // ...or at the beginning
    await ScoreWith(Scorers.ExactMatch(), sentence);               // the WHOLE completion is the target
    await ScoreWith(Scorers.Pattern(@"is (\w+)\.?$"), sentence, @"pattern(is (\w+))"); // a regex capture group is the target
    Say("");

    // Numbers: match(numeric: true) compares as numbers, so "42.0" counts for "42".
    Say("  completion = \"42.0\", target = \"42\"");
    var dec = Answered("What is 7 * 6?", "42.0", target: "42");
    await ScoreWith(Scorers.Match(numeric: false), dec, "match(end)");
    await ScoreWith(Scorers.Match(numeric: true), dec, "match(numeric)");
    Say("");

    // Several accepted targets: any one of them satisfies the scorer.
    Say("  completion = \"Blue\", target = [red | blue | yellow]");
    var colour = Answered("Name a primary colour.", "Blue", target: new[] { "red", "blue", "yellow" });
    await ScoreWith(Scorers.Includes(), colour);
    await ScoreWith(Scorers.ExactMatch(), colour);
    Say("");

    // answer("letter") looks for the conventional "ANSWER: X" line, ignoring the chatter around it.
    Say("  completion = \"ANSWER: B\\nMars is the red planet.\", target = \"B\"");
    var mc = Answered("Which planet is the Red Planet? A) Venus B) Mars C) Jupiter", "ANSWER: B\nMars is the red planet.", target: "B");
    await ScoreWith(Scorers.Answer("letter"), mc, "answer(letter)");
    await ScoreWith(Scorers.Match("end"), mc, "match(end)");
    Say("");

    // Your own: Scorers.Custom(name, scorer, metrics...) is the @scorer(metrics=[...]) decorator. Lenient
    // (below) accepts a number within a tolerance, or text containing a target. Answer and Explanation are
    // free text for the log.
    Say("  completion = \"3.1416\" and \"3\", target = \"3.14159\"");
    await ScoreWith(Lenient(0.01), Answered("Estimate pi.", "3.1416", target: "3.14159"));
    await ScoreWith(Lenient(0.01), Answered("Estimate pi.", "3", target: "3.14159"));
}

// ───────────────────────────────────────────────────────────────────────────
static Task Lesson4Async()
// ───────────────────────────────────────────────────────────────────────────
{
    // A Metric folds every sample's Score into one number: (IReadOnlyList<SampleScore>) -> ScoreValue.
    // accuracy() maps C -> 1, I -> 0 and averages; stderr()/std() are the usual statistics over the same 0/1s.
    var scores = new[] { "C", "C", "I", "C" }
        .Select((value, i) => new SampleScore(new Score(value), SampleId: i + 1, Scorer: "includes"))
        .ToList();
    Say("  scores = [C, C, I, C]");
    Show("accuracy", Metrics.Accuracy().Compute(scores));
    Show("stderr  ", Metrics.Stderr().Compute(scores));
    Show("std     ", Metrics.Std().Compute(scores));

    // Numeric scores (partial credit) use mean().
    var partial = new[] { 0.5, 1.0, 0.25 }.Select((value, i) => new SampleScore(new Score(value), SampleId: i + 1)).ToList();
    Say("  scores = [0.5, 1.0, 0.25]");
    Show("mean    ", Metrics.Mean().Compute(partial));

    // Epochs run every sample N times. A ScoreReducer then collapses one sample's N scores into ONE score
    // BEFORE the metrics see it: (IReadOnlyList<Score>) -> Score.
    Score[] epochs = [new("C"), new("I"), new("C")];
    Say("  one sample over 3 epochs = [C, I, C]");
    Show("Reducers.Mean()     ", Reducers.Mean()(epochs));
    Show("Reducers.Mode()     ", Reducers.Mode()(epochs));
    Show("Reducers.Max()      ", Reducers.Max()(epochs));
    Show("Reducers.AtLeast(1) ", Reducers.AtLeast(1)(epochs));
    Show("Reducers.AtLeast(3) ", Reducers.AtLeast(3)(epochs));
    return Task.CompletedTask;
}

// ───────────────────────────────────────────────────────────────────────────
static Task Lesson5Async()
// ───────────────────────────────────────────────────────────────────────────
{
    // An EvalTask is a record that names the pieces: a Dataset, a Solver (default: generate()), Scorers,
    // optional Epochs (count + reducers), limits, config. It does nothing by itself; Eval.RunAsync drives it.
    // See BuildTask() below - the same task lesson 6 runs.
    var task = BuildTask();
    Show("Name    ", task.Name);
    Show("Version ", task.Version);
    Show("Dataset ", $"{task.Dataset.Name} ({task.Dataset.Count} samples)");
    Show("Solver  ", "Chain(SystemMessage(...), Generate())");
    foreach (var scorer in task.Scorers)
    {
        Say($"  Scorer   = {scorer.Name,-9} metrics [{string.Join(", ", scorer.Metrics.Select(m => m.Name))}]");
    }

    Show("Epochs  ", task.Epochs is null ? "1 (default)" : "set");

    // The same task with epochs: three runs per sample, reduced by mean before the metrics.
    var repeated = task with { Epochs = new Epochs(3, [Reducers.Mean()]) };
    Show("Epochs  ", repeated.Epochs is null ? "1" : "3, reducer mean");

    // [Task("name")] on a public static method returning EvalTask is only for CLI discovery
    // (`inspectai eval <name> --assembly ...`); Eval.RunAsync takes the object directly.
    return Task.CompletedTask;
}

// ───────────────────────────────────────────────────────────────────────────
static async Task Lesson6Async()
// ───────────────────────────────────────────────────────────────────────────
{
    // The one non-deterministic component of any eval is the model. Replace it with ScriptedModelApi, whose
    // every turn is a FUNCTION of the conversation (AnswerKey.Reply), so even samples running concurrently
    // get the same answers regardless of order. Everything downstream - solvers, scorers, metrics - is pure.
    var options = new EvalOptions
    {
        Model = ScriptedModel(),
        LogDir = LogDir(),               // a log is always written; put it somewhere harmless
        LogFormat = LogFormat.Json,      // human-readable, for teaching (default is the .eval zip)
        MaxSamples = 4,                  // run four samples concurrently: still deterministic
    };

    var log = await Eval.RunAsync(BuildTask(), options);
    Show("Status ", log.Status);
    Show("Log    ", log.Location);
    Say("");

    // Per sample: what the model said, and what each scorer made of it. Note where the scorers disagree.
    Say($"  {"id",-3} {"target",-16} {"completion",-42} {"includes",-9} {"match",-6} lenient");
    foreach (var sample in log.Samples!.OrderBy(s => Convert.ToInt32(s.Id, CultureInfo.InvariantCulture)))
    {
        var scores = sample.Scores!;
        Say($"  {sample.Id,-3} {string.Join("|", sample.Target.Values),-16} {Quote(sample.Output.Completion),-42} {scores["includes"].Text,-9} {scores["match"].Text,-6} {scores["lenient"].Text}");
    }

    Say("");
    foreach (var score in log.Results!.Scores)
    {
        Say($"  {score.Name,-9} " + string.Join("  ", score.Metrics.Select(m => $"{m.Key}={m.Value.Value.ToString("F3", CultureInfo.InvariantCulture)}")));
    }

    // Determinism, demonstrated: run the whole thing again and compare every score and every metric.
    Say("");
    var again = await Eval.RunAsync(BuildTask(), options with { Model = ScriptedModel() });
    Show("identical scores and metrics on re-run?", Fingerprint(log) == Fingerprint(again));
}

// ---------------------------------------------------------------------------
// The task and its parts, shared by lessons 5 and 6.
// ---------------------------------------------------------------------------
static string QuizPath() => Path.Combine(AppContext.BaseDirectory, "data", "quiz.jsonl");

static string LogDir() => Path.Combine(Path.GetTempPath(), "inspect-eval-primitives-demo");

static IDataset LoadQuiz() =>
    Datasets.Json(QuizPath(), new FieldSpec(Input: "question", Target: "answer", Metadata: ["topic"]), name: "quiz", autoId: true);

static EvalTask BuildTask() => new()
{
    Name = "quiz",
    Version = "1",
    Dataset = LoadQuiz(),
    Solver = Solvers.Chain(Solvers.SystemMessage("Answer as briefly as possible."), Solvers.Generate()),
    Scorers = [Scorers.Includes(), Scorers.Match(numeric: true), Lenient(0.01)],
};

// The scripted model: ScriptedModelApi replays turns; ScriptedTurn.From(func) computes each turn from the
// conversation. 100 turns is plenty for 6 samples x 2 runs.
static Model ScriptedModel() =>
    new(new ScriptedModelApi(
        Enumerable.Repeat(ScriptedTurn.From((messages, _) => ModelOutput.FromContent("scripted", AnswerKey.Reply(messages))), 100),
        "scripted"));

// A hand-rolled Generate for lessons 2-3: what the runner passes to solvers, minus the real model. It does
// the two things generate() does - set state.Output and append the assistant message.
static Task<TaskState> ScriptedGenerate(TaskState state, ToolCallsMode toolCalls = ToolCallsMode.Loop, GenerateConfig? config = null, CachePolicy? cache = null, CancellationToken cancellationToken = default)
{
    var reply = AnswerKey.Reply(state.Messages);
    state.Output = ModelOutput.FromContent("scripted", reply);
    state.Messages.Add(new ChatMessageAssistant(reply));
    return Task.FromResult(state);
}

// A custom solver: post-processes the completion.
static Solver UppercaseOutput() => (state, _, _) =>
{
    state.Output = ModelOutput.FromContent("scripted", state.Output.Completion.ToUpperInvariant());
    return Task.FromResult(state);
};

// A custom solver that ends the chain.
static Solver GiveUp(string reason) => (state, _, _) =>
{
    state.Output = ModelOutput.FromContent("scripted", reason);
    state.Completed = true;
    return Task.FromResult(state);
};

// A custom scorer via Scorers.Custom (= @scorer(metrics=[accuracy(), stderr()])): a number within
// `tolerance` of a numeric target, or text containing a text target.
static ScorerDef Lenient(double tolerance) => Scorers.Custom("lenient", (state, target, _) =>
{
    var answer = state.Output.Completion.Trim();
    foreach (var want in target.Values)
    {
        if (TryNumber(answer, out var got) && TryNumber(want, out var wanted))
        {
            if (Math.Abs(got - wanted) <= tolerance)
            {
                return Task.FromResult(new Score(ScoreConstants.Correct) { Answer = answer, Explanation = $"|{got} - {wanted}| <= {tolerance}" });
            }
        }
        else if (answer.Contains(want, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new Score(ScoreConstants.Correct) { Answer = answer, Explanation = $"contains '{want}'" });
        }
    }

    return Task.FromResult(new Score(ScoreConstants.Incorrect) { Answer = answer, Explanation = $"no target in [{string.Join(", ", target.Values)}] matched" });
}, Metrics.Accuracy(), Metrics.Stderr());

static bool TryNumber(string text, out double value) =>
    double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

// ---------------------------------------------------------------------------
// TaskState helpers for lessons 2-3 (the runner builds these from a Sample).
// ---------------------------------------------------------------------------
static TaskState NewState(string question, Target target) =>
    new("scripted", sampleId: 1, epoch: 1, input: question, messages: [new ChatMessageUser(question)], target: target);

static TaskState Answered(string question, string completion, Target target)
{
    var state = NewState(question, target);
    state.Output = ModelOutput.FromContent("scripted", completion);
    state.Messages.Add(new ChatMessageAssistant(completion));
    return state;
}

static async Task ScoreWith(ScorerDef scorer, TaskState state, string? label = null)
{
    var score = await scorer.Score(state, state.Target, CancellationToken.None);
    // The built-ins put the raw completion in Explanation; only show it when a scorer says something more.
    var detail = score.Explanation is { Length: > 0 } explanation && explanation != state.Output.Completion
        ? $"  ({Trim(explanation.Replace("\n", "\\n", StringComparison.Ordinal), 60)})"
        : "";
    Say($"  {label ?? scorer.Name,-16} -> {score.Text}  answer={Quote(score.Answer ?? "")}{detail}");
}

static string Fingerprint(EvalLog log)
{
    var samples = log.Samples!
        .OrderBy(s => Convert.ToInt32(s.Id, CultureInfo.InvariantCulture))
        .Select(s => $"{s.Id}:" + string.Join(",", s.Scores!.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value.Text}")));
    var metrics = log.Results!.Scores
        .Select(sc => $"{sc.Name}:" + string.Join(",", sc.Metrics.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key}={m.Value.Value.ToString("R", CultureInfo.InvariantCulture)}")));
    return string.Join(";", samples) + "|" + string.Join(";", metrics);
}

// ---------------------------------------------------------------------------
// Console helpers.
// ---------------------------------------------------------------------------
void PrintUsage(TextWriter writer)
{
    writer.WriteLine("usage: dotnet run --project src/InspectAzureAI.EvalPrimitivesDemo [-- <lesson>...|list]");
    writer.WriteLine();
    writer.WriteLine("  no arguments  run every lesson in order");
    writer.WriteLine("  <lesson>...   run only these lessons (numbers, any order; run ascending)");
    writer.WriteLine("  list          print the lesson titles");
    writer.WriteLine();
    foreach (var (number, title, _) in lessons)
    {
        writer.WriteLine($"  {number}  {title}");
    }
}

static void Say(string text) => Console.WriteLine(text);

static void Show(string label, object? value)
{
    var text = value switch
    {
        string s => Quote(s),
        Score score => score.Text,
        ScoreValue scoreValue => new Score(scoreValue).Text,
        null => "null",
        _ => value.ToString(),
    };
    Console.WriteLine($"  {label} = {text}");
}

static string Ids(IEnumerable<Sample> samples) => string.Join(", ", samples.Select(s => s.Id));

static string Describe(IEnumerable<ChatMessage> messages) =>
    string.Join(" | ", messages.Select(m => $"{m.GetType().Name["ChatMessage".Length..].ToLowerInvariant()}: {Trim(m.Text, 40)}"));

static string Quote(string s) => "\"" + s.Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
