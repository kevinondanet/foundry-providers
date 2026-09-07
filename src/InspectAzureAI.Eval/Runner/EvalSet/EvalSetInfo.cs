using System.Text;
using System.Text.Json;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Runner.EvalSet;

/// <summary>Port of <c>_eval/evalset.py</c> <c>EvalSetTask</c>: one task of the set as recorded in <c>eval-set.json</c>.</summary>
public sealed record EvalSetTaskInfo
{
    public string? Name { get; init; }

    public required string TaskId { get; init; }

    public string? TaskFile { get; init; }

    public IReadOnlyDictionary<string, object?> TaskArgs { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    public required string Model { get; init; }

    public IReadOnlyDictionary<string, object?> ModelArgs { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Role name → model name(s), a list-valued role comma-joined as <c>--model-role</c> accepts it.</summary>
    public IReadOnlyDictionary<string, string>? ModelRoles { get; init; }

    public int Sequence { get; init; }
}

/// <summary>
/// Port of <c>_eval/evalset.py</c> <c>EvalSet</c> and the <c>eval-set.json</c> manifest (<c>write_eval_set_info</c> /
/// <c>read_eval_set_info</c>) plus the <c>.eval-set-id</c> marker (<c>eval_set_id_for_log_dir</c>) that pins the
/// set's id to its log directory.
/// </summary>
public sealed record EvalSetInfo
{
    /// <summary>The manifest file name.</summary>
    public const string ManifestFileName = "eval-set.json";

    /// <summary>The id marker file name.</summary>
    public const string IdFileName = ".eval-set-id";

    public required string EvalSetId { get; init; }

    public IReadOnlyList<EvalSetTaskInfo> Tasks { get; init; } = [];

    /// <summary>
    /// Port of <c>to_eval_set</c>: the manifest for <paramref name="tasks"/>, each task keeping the <c>task_id</c> of
    /// an existing log with its identifier so retries stay grouped with their earlier attempts.
    /// </summary>
    public static EvalSetInfo Build(string evalSetId, IReadOnlyList<ResolvedTask> tasks, IReadOnlyList<EvalSetLog> logs)
    {
        ArgumentException.ThrowIfNullOrEmpty(evalSetId);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(logs);
        var infos = new List<EvalSetTaskInfo>(tasks.Count);
        foreach (var task in tasks)
        {
            var existingTaskId = logs.FirstOrDefault(log => log.TaskIdentifier == task.Identifier)?.Header.Eval.TaskId;
            infos.Add(new EvalSetTaskInfo
            {
                Name = task.Task.Name,
                TaskId = existingTaskId ?? task.Id,
                TaskArgs = task.Task.TaskArgs ?? new Dictionary<string, object?>(StringComparer.Ordinal),
                Model = task.Model.Name,
                ModelRoles = task.ModelRoles is { Count: > 0 } roles
                    ? roles.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value.Select(model => model.Name)), StringComparer.Ordinal)
                    : null,
                Sequence = task.Sequence,
            });
        }

        return new EvalSetInfo { EvalSetId = evalSetId, Tasks = infos };
    }

    /// <summary>Port of <c>write_eval_set_info</c>: writes <c>eval-set.json</c> into <paramref name="logDir"/>.</summary>
    public void Write(string logDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(logDir);
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, ManifestFileName), JsonSerializer.Serialize(this, EvalLogWriter.Options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Port of <c>read_eval_set_info</c>: the manifest of <paramref name="logDir"/>, or null when there is none.</summary>
    public static EvalSetInfo? Read(string logDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(logDir);
        var manifest = Path.Combine(logDir, ManifestFileName);
        if (!File.Exists(manifest))
        {
            return null;
        }

        return JsonSerializer.Deserialize<EvalSetInfo>(File.ReadAllText(manifest, Encoding.UTF8), EvalLogWriter.Options)
            ?? throw new JsonException($"The eval set manifest '{manifest}' is empty.");
    }

    /// <summary>
    /// Port of <c>eval_set_id_for_log_dir</c>: the id recorded in the directory's <c>.eval-set-id</c> file, which a
    /// different requested id contradicts (<see cref="PrerequisiteError"/>); a directory without one records
    /// <paramref name="evalSetId"/>, or a fresh id.
    /// </summary>
    public static string EvalSetIdForLogDir(string logDir, string? evalSetId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(logDir);
        var idFile = Path.Combine(logDir, IdFileName);
        if (File.Exists(idFile))
        {
            var existing = File.ReadAllText(idFile, Encoding.UTF8).Trim();
            if (!string.IsNullOrEmpty(evalSetId) && evalSetId != existing)
            {
                throw new PrerequisiteError($"The eval set ID '{evalSetId}' is not the same as the existing eval set ID '{existing}'.");
            }

            return existing;
        }

        var id = string.IsNullOrEmpty(evalSetId) ? ShortUuid.Generate() : evalSetId;
        Directory.CreateDirectory(logDir);
        File.WriteAllText(idFile, id, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return id;
    }
}
