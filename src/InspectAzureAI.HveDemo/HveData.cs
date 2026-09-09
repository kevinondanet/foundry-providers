using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.HveDemo;

/// <summary>
/// Where the demo's data lives at run time: the project copies <c>hve/**</c> (dataset, workspaces, the vendored
/// HVE Core plugin, reference solutions, Dockerfile) next to the executable, the way <c>CtfData</c> does for the CTF
/// sample. A missing file is a <see cref="PrerequisiteError"/> so the fix (build the project) is reported up front
/// rather than as a failure on every sample.
/// </summary>
public static class HveData
{
    /// <summary>Where the plugin is copied inside every sample's sandbox and what <c>--plugin-dir</c> receives.</summary>
    public const string PluginSandboxPath = "/opt/hve-core";

    /// <summary>The sandbox working directory (the image's <c>WORKDIR</c>); the sample's workspace files land here.</summary>
    public const string SandboxWorkingDirectory = "/workspace";

    /// <summary>The <c>hve</c> directory next to the executable.</summary>
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "hve");

    public static string DatasetPath => Require(Path.Combine(Directory, "dataset.json"));

    /// <summary>One directory per sample id with that sample's workspace files.</summary>
    public static string WorkspaceRoot => RequireDirectory(Path.Combine(Directory, "workspace"));

    /// <summary>The <c>.github</c> overlay (repository instructions) every sample's workspace starts with.</summary>
    public static string SharedWorkspace => RequireDirectory(Path.Combine(WorkspaceRoot, "_shared"));

    /// <summary>The vendored HVE Core subset: a Copilot CLI plugin directory (<c>plugin.json</c> at its root).</summary>
    public static string PluginDirectory => RequireDirectory(Path.Combine(Directory, "plugin"));

    /// <summary>One directory per sample id with a known-good artefact; the fake model writes these.</summary>
    public static string ReferenceRoot => RequireDirectory(Path.Combine(Directory, "reference"));

    /// <summary>
    /// One directory per sample id with the files its <c>check</c> command depends on (checker scripts, answer keys,
    /// pristine copies of the tests the agent was shown), kept on the host and written into the sandbox by the
    /// check scorer right before the check runs, so an agent cannot read the answer key or edit its own grader.
    /// </summary>
    public static string ChecksRoot => RequireDirectory(Path.Combine(Directory, "checks"));

    /// <summary>The Docker build context (it holds the Dockerfile).</summary>
    public static string SandboxDirectory
    {
        get
        {
            Require(Path.Combine(Directory, "sandbox", "Dockerfile"));
            return Path.Combine(Directory, "sandbox");
        }
    }

    private static string Require(string path) =>
        File.Exists(path) ? path : throw new PrerequisiteError($"{path} not found. Build InspectAzureAI.HveDemo so that hve/** is copied next to the executable.");

    private static string RequireDirectory(string path) =>
        System.IO.Directory.Exists(path) ? path : throw new PrerequisiteError($"{path} not found. Build InspectAzureAI.HveDemo so that hve/** is copied next to the executable.");
}
