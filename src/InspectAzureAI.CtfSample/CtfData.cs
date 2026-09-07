using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.CtfSample;

/// <summary>
/// Where the sample's data lives at run time: the project copies <c>ctf/**</c> (dataset, setup scripts, Dockerfile)
/// next to the executable. A missing file is a <see cref="PrerequisiteError"/> so the fix (build the project) is
/// reported up front rather than as a failure on every sample.
/// </summary>
internal static class CtfData
{
    /// <summary>The <c>ctf</c> directory next to the executable. It doubles as the Docker build context because it holds the Dockerfile.</summary>
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "ctf");

    public static string DatasetPath => Require(Path.Combine(Directory, "dataset.json"));

    public static string SandboxDirectory
    {
        get
        {
            Require(Path.Combine(Directory, "Dockerfile"));
            return Directory;
        }
    }

    private static string Require(string path) =>
        File.Exists(path)
            ? path
            : throw new PrerequisiteError($"{path} not found. Build InspectAzureAI.CtfSample so that ctf/** is copied next to the executable.");
}
