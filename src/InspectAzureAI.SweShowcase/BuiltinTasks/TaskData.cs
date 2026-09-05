using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.SweShowcase.BuiltinTasks;

/// <summary>
/// Locates the task data (<c>tasks/&lt;name&gt;/dataset.json</c> plus its files) and the sandbox Dockerfile, which
/// the project copies next to the executable; a missing file is a <see cref="PrerequisiteError"/> rather than a
/// runtime failure so the fix (build the project) is reported as exit code 2.
/// </summary>
internal static class TaskData
{
    public static string TasksDirectory => Path.Combine(AppContext.BaseDirectory, "tasks");

    public static string SandboxDirectory => Path.Combine(AppContext.BaseDirectory, "sandbox");

    public static string DatasetPath(string task)
    {
        var path = Path.Combine(TasksDirectory, task, "dataset.json");
        return File.Exists(path)
            ? path
            : throw new PrerequisiteError($"Task data not found at {path}. Build InspectAzureAI.SweShowcase so that tasks/** is copied next to the executable.");
    }

    /// <summary>The Docker build context of the showcase image (the directory holding <c>sandbox/Dockerfile</c>).</summary>
    public static string RequireSandboxDirectory()
    {
        var dockerfile = Path.Combine(SandboxDirectory, "Dockerfile");
        return File.Exists(dockerfile)
            ? SandboxDirectory
            : throw new PrerequisiteError($"Sandbox Dockerfile not found at {dockerfile}. Build InspectAzureAI.SweShowcase so that sandbox/** is copied next to the executable.");
    }
}
