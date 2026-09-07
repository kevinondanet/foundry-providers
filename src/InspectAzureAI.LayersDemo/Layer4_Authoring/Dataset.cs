// ============================================================================
//  LAYER 4: THE TASK-AUTHORING API (part 1: datasets and the Task object)
//  Python: inspect_ai/dataset, inspect_ai/_eval/task/task.py (exported as inspect_ai.Task)
//
//  Layer 4 is everything an *author* imports: `Task`, `Sample`, datasets,
//  solvers, scorers, tools, agents. It is entirely un-prefixed — the public,
//  stable surface. Authors never touch `_eval` (the engine that runs their
//  task) and the engine never imports the author's module; the `@task`
//  decorator registers the task by name and the engine resolves the name.
//
//  Datasets are loaded through the filesystem abstraction, so
//  `json_dataset("s3://bucket/questions.jsonl")` works exactly like a local
//  path. That is the cross-cutting filesystem concern showing up in layer 4.
// ============================================================================
using System.Text.Json;
using inspect_ai._util.display;
using inspect_ai._util.file;
using inspect_ai._util.registry;

namespace inspect_ai.dataset
{
    /// <summary>One question on the exam paper (Python: Sample).</summary>
    public sealed record Sample(string Id, string Input, string Target, IReadOnlyDictionary<string, string>? Files = null);

    /// <summary>An ordered collection of samples (Python: Dataset / MemoryDataset).</summary>
    public sealed record Dataset(string Name, IReadOnlyList<Sample> Samples);

    public static class Datasets
    {
        /// <summary>Python: `json_dataset("path-or-uri.jsonl")`. One JSON object per line.</summary>
        public static Dataset json_dataset(string uri)
        {
            Display.Step("L4 dataset", $"json_dataset('{uri}') via the filesystem abstraction");
            var text = FileSystems.ReadText(uri);
            var samples = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<SampleDto>(line)!)
                .Select((dto, i) => new Sample(dto.id ?? $"{i + 1}", dto.input, dto.target ?? "", dto.files))
                .ToList();
            Display.Step("L4 dataset", $"loaded {samples.Count} samples");
            return new Dataset(uri, samples);
        }

        // Lower-case property names so System.Text.Json maps the JSONL fields as written.
        private sealed record SampleDto(string? id, string input, string? target, Dictionary<string, string>? files);
    }
}

namespace inspect_ai
{
    using inspect_ai.dataset;
    using inspect_ai.scorer;
    using inspect_ai.solver;

    /// <summary>
    /// The unit of evaluation (Python: inspect_ai.Task). Bundles a dataset, a
    /// solver, a scorer and settings such as the sandbox. Named EvalTask here
    /// only to avoid colliding with System.Threading.Tasks.Task.
    /// </summary>
    public sealed record EvalTask(Dataset dataset, Solver solver, Scorer scorer, string? sandbox = null);

    /// <summary>Python's `@task` decorator. Marks a factory that returns a Task
    /// and registers it under a name that `inspect eval <name>` can resolve.</summary>
    public sealed class TaskAttribute(string name) : RegistryAttribute(RegistryType.Task, name);
}
