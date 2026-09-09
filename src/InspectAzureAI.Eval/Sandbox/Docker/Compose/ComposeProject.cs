using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Sandbox.Docker.Compose;

/// <summary>
/// Port of <c>util/_sandbox/docker/util.py</c> <c>ComposeProject</c>: one <c>docker compose</c> project
/// (a task-level one for builds, one per sample for containers), its compose file and the environment
/// forwarded to the compose CLI (the <c>SAMPLE_METADATA_*</c> interpolation variables).
/// </summary>
public sealed record ComposeProject(
    string Name,
    string? ConfigFile,
    string TaskName,
    string? SampleId = null,
    int? Epoch = null,
    IReadOnlyDictionary<string, string>? Env = null)
{
    /// <summary>Directory of the compose file (the cwd of <c>compose down</c>), or null without one.</summary>
    public string? ConfigDirectory => ConfigFile is null ? null : Path.GetDirectoryName(ConfigFile);

    /// <summary>Whether <see cref="ConfigFile"/> is a file this port generated (removed at task cleanup).</summary>
    public bool IsAutoCompose => ConfigFile is not null && ComposeFiles.IsAutoComposeFile(ConfigFile);

    /// <summary>
    /// Port of <c>ComposeProject.create</c> + <c>config.py</c> <c>resolve_compose_file</c> for the config forms
    /// <see cref="SandboxSpec.Config"/> allows: a compose file, a Dockerfile (auto-compose around it), a
    /// directory (its compose file, else its Dockerfile), null (the same search in the cwd, else the generic
    /// image) or an image reference (auto-compose around the image; Python reaches this through a
    /// <c>ComposeConfig</c> object rather than a string).
    /// </summary>
    public static ComposeProject Create(string name, string? config, string taskName, string? sampleId = null, int? epoch = null, IReadOnlyDictionary<string, string>? env = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var configFile = ComposeFiles.ResolveConfig(config, name);
        return new ComposeProject(name, configFile, taskName, sampleId, epoch, env);
    }
}

/// <summary>
/// Port of <c>util/_sandbox/docker/config.py</c> (compose file discovery and the auto-generated compose
/// files) and the naming helpers of <c>util.py</c> (<c>task_project_name</c>, <c>is_inspect_project</c>).
/// </summary>
public static partial class ComposeFiles
{
    /// <summary>Port of <c>COMPOSE_FILES</c>: the file names searched, in order.</summary>
    public static readonly IReadOnlyList<string> ComposeFileNames = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    public const string Dockerfile = "Dockerfile";

    /// <summary>Port of <c>AUTO_COMPOSE_YAML</c>: the legacy auto-compose file written next to a Dockerfile.</summary>
    public const string AutoComposeYaml = ".compose.yaml";

    /// <summary>Port of <c>AUTO_COMPOSE_SUBDIR</c>: the data-directory subfolder auto-compose files live in.</summary>
    public const string AutoComposeSubdir = "docker-compose";

    /// <summary>Port of <c>COMPOSE_COMMENT</c>.</summary>
    public const string ComposeComment = "# inspect auto-generated docker compose file\n# (will be removed when task is complete)";

    /// <summary>Port of <c>COMPOSE_GENERIC_YAML</c> (the image is <see cref="GenericImage"/>).</summary>
    public const string GenericImage = "aisiuk/inspect-tool-support";

    private const string ServiceBody = "    command: \"tail -f /dev/null\"\n    init: true\n    network_mode: none\n    stop_grace_period: 1s\n";

    [GeneratedRegex(@"[-.]compose\.ya?ml$")]
    private static partial Regex ComposePattern();

    [GeneratedRegex(@"^inspect-[a-z\d\-_]*-i[a-z\d]{6,}$")]
    private static partial Regex InspectProjectPattern();

    [GeneratedRegex(@"[^a-z\d\-_]")]
    private static partial Regex NotProjectChar();

    [GeneratedRegex("-+")]
    private static partial Regex Dashes();

    /// <summary>Port of <c>COMPOSE_GENERIC_YAML</c> for <paramref name="image"/>.</summary>
    public static string GenericYaml(string image = GenericImage) =>
        $"{ComposeComment}\nservices:\n  default:\n    image: {Quote(image)}\n{ServiceBody}";

    /// <summary>Port of <c>COMPOSE_DOCKERFILE_YAML</c> with the build context already absolute (Python rewrites it in <c>_update_build_context</c>).</summary>
    public static string DockerfileYaml(string contextDirectory, string dockerfile = Dockerfile) =>
        $"{ComposeComment}\nservices:\n  default:\n    build:\n      context: {Quote(contextDirectory)}\n      dockerfile: {Quote(dockerfile)}\n{ServiceBody}";

    /// <summary>Port of <c>is_compose_yaml</c>: a standard compose file name, a <c>*-compose.yaml</c> / <c>.compose.yaml</c> alike, or a file in the auto-compose directory.</summary>
    public static bool IsComposeFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var name = Path.GetFileName(path);
        if (ComposeFileNames.Contains(name, StringComparer.Ordinal) || ComposePattern().IsMatch(name))
        {
            return true;
        }

        return IsAutoComposeFile(path);
    }

    /// <summary>Port of <c>is_dockerfile</c>: <c>Dockerfile</c>, <c>name.Dockerfile</c> or <c>Dockerfile.name</c>.</summary>
    public static bool IsDockerfile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var name = Path.GetFileName(path);
        return Path.GetFileNameWithoutExtension(name) == Dockerfile || name.EndsWith("." + Dockerfile, StringComparison.Ordinal);
    }

    /// <summary>Port of <c>find_compose_file</c>: the first standard compose file name present in <paramref name="parent"/>, or null.</summary>
    public static string? FindComposeFile(string parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        foreach (var file in ComposeFileNames)
        {
            if (File.Exists(Path.Combine(parent, file)))
            {
                return file;
            }
        }

        return null;
    }

    public static bool HasDockerfile(string parent) => File.Exists(Path.Combine(parent, Dockerfile));

    public static bool HasAutoComposeFile(string parent) => File.Exists(Path.Combine(parent, AutoComposeYaml));

    /// <summary>
    /// Port of <c>inspect_data_dir(AUTO_COMPOSE_SUBDIR)</c> over <c>platformdirs.user_data_path</c>:
    /// <c>~/Library/Application Support/inspect_ai/docker-compose</c> on macOS, <c>$XDG_DATA_HOME/inspect_ai/docker-compose</c>
    /// (default <c>~/.local/share/...</c>) elsewhere on Unix, <c>%LOCALAPPDATA%\inspect_ai\inspect_ai\docker-compose</c> on Windows.
    /// <c>INSPECT_AUTO_COMPOSE_DIR</c> overrides the location (tests point it at a temp directory). Created when missing.
    /// </summary>
    public static string AutoComposeDir()
    {
        var dir = Environment.GetEnvironmentVariable("INSPECT_AUTO_COMPOSE_DIR");
        if (string.IsNullOrWhiteSpace(dir))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string root;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                root = Path.Combine(home, "Library", "Application Support", "inspect_ai");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "inspect_ai", "inspect_ai");
            }
            else
            {
                var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                root = Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg, "inspect_ai");
            }

            dir = Path.Combine(root, AutoComposeSubdir);
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Port of <c>is_auto_compose_file</c>: a file in the auto-compose directory, or the legacy <c>.compose.yaml</c> name.</summary>
    public static bool IsAutoComposeFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (parent is not null && string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(AutoComposeDir()), StringComparison.Ordinal))
        {
            return true;
        }

        return Path.GetFileName(full) == AutoComposeYaml;
    }

    /// <summary>Port of <c>auto_compose_file</c>: writes <paramref name="contents"/> as <c>&lt;projectName&gt;.yaml</c> in the auto-compose directory and returns its path.</summary>
    public static string AutoComposeFile(string contents, string projectName)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentException.ThrowIfNullOrEmpty(projectName);
        var path = Path.Combine(AutoComposeDir(), $"{projectName}.yaml");
        File.WriteAllText(path, contents);
        return Path.GetFullPath(path);
    }

    /// <summary>Port of <c>safe_cleanup_auto_compose</c>: removes an auto-compose file, warning rather than throwing.</summary>
    public static void SafeCleanupAutoCompose(string? file)
    {
        if (string.IsNullOrEmpty(file))
        {
            return;
        }

        try
        {
            if (IsAutoComposeFile(file) && File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProviderLogger.Warning($"Error cleaning up compose file: {ex.Message}");
        }
    }

    /// <summary>
    /// Port of <c>resolve_compose_file(parent, project_name)</c>: an existing compose file in
    /// <paramref name="parent"/> wins, then a legacy <c>.compose.yaml</c>, then a Dockerfile (auto-compose
    /// around it), else the generic auto-compose. Returns the absolute path to use with <c>-f</c>.
    /// </summary>
    public static string ResolveComposeFile(string parent, string projectName)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var directory = parent.Length == 0 ? Directory.GetCurrentDirectory() : Path.GetFullPath(parent);
        var compose = FindComposeFile(directory);
        if (compose is not null)
        {
            return Path.Combine(directory, compose);
        }

        if (HasAutoComposeFile(directory))
        {
            return Path.Combine(directory, AutoComposeYaml);
        }

        if (HasDockerfile(directory))
        {
            return AutoComposeFile(DockerfileYaml(directory), projectName);
        }

        return AutoComposeFile(GenericYaml(), projectName);
    }

    /// <summary>
    /// Port of the config branch of <c>ComposeProject.create</c>: which compose file a
    /// <see cref="SandboxSpec.Config"/> denotes for <paramref name="projectName"/> (see <see cref="ComposeProject.Create"/>).
    /// Deviation: a null config looks in the process cwd rather than the task's directory (see <see cref="IsComposeConfig"/>).
    /// </summary>
    public static string ResolveConfig(string? config, string projectName)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectName);
        if (string.IsNullOrWhiteSpace(config))
        {
            return ResolveComposeFile("", projectName);
        }

        if (Directory.Exists(config))
        {
            var directory = Path.GetFullPath(config);
            var compose = FindComposeFile(directory);
            if (compose is not null)
            {
                return Path.Combine(directory, compose);
            }

            if (HasAutoComposeFile(directory))
            {
                return Path.Combine(directory, AutoComposeYaml);
            }

            if (HasDockerfile(directory))
            {
                return AutoComposeFile(DockerfileYaml(directory), projectName);
            }

            throw new FileNotFoundException($"Sandbox directory '{config}' contains no compose file ({string.Join(", ", ComposeFileNames)}) or Dockerfile.", Path.Combine(directory, ComposeFileNames[0]));
        }

        if (File.Exists(config))
        {
            var full = Path.GetFullPath(config);
            if (IsDockerfile(full))
            {
                return AutoComposeFile(DockerfileYaml(Path.GetDirectoryName(full)!, Path.GetFileName(full)), projectName);
            }

            return full;
        }

        if (LooksLikePath(config))
        {
            throw new FileNotFoundException($"Sandbox config '{config}' was not found.", config);
        }

        return AutoComposeFile(GenericYaml(config), projectName);
    }

    /// <summary>
    /// Whether a <see cref="SandboxSpec.Config"/> is compose-shaped: a compose file (by name or extension),
    /// a directory holding one, a legacy <c>.compose.yaml</c>, or null with such a file in the cwd. Image
    /// references and Dockerfiles are not (the bare docker path handles those unless compose is forced).
    /// Deviation: a null config is resolved against the process cwd, not the task's directory (Python's
    /// <c>inspect eval</c> chdirs to the task file before <c>resolve_compose_file</c>); pass
    /// <c>SandboxSpec("docker", taskDirectory)</c> to find the compose file next to a task.
    /// </summary>
    public static bool IsComposeConfig(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
        {
            var cwd = Directory.GetCurrentDirectory();
            return FindComposeFile(cwd) is not null || HasAutoComposeFile(cwd);
        }

        if (Directory.Exists(config))
        {
            return FindComposeFile(config) is not null || HasAutoComposeFile(config);
        }

        if (File.Exists(config))
        {
            if (IsDockerfile(config))
            {
                return false;
            }

            var extension = Path.GetExtension(config);
            return IsComposeFile(config) || string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Port of <c>task_project_name</c>: <c>inspect-&lt;task slug (12 chars)&gt;-i&lt;6 random chars&gt;</c>, a name
    /// that satisfies docker's project constraints; the suffix comes from shortuuid's alphabet lowercased.
    /// </summary>
    public static string TaskProjectName(string task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var slug = task.ToLowerInvariant();
        slug = NotProjectChar().Replace(slug, "-");
        slug = Dashes().Replace(slug, "-");
        if (slug.Length == 0)
        {
            slug = "task";
        }

        slug = slug[..Math.Min(12, slug.Length)].TrimEnd('_');
        return $"inspect-{slug}-i{RandomSuffix(6)}";
    }

    /// <summary>Port of <c>is_inspect_project</c>.</summary>
    public static bool IsInspectProject(string name) => name is not null && InspectProjectPattern().IsMatch(name);

    private const string SuffixAlphabet = "23456789abcdefghjkmnpqrstuvwxyz";

    private static string RandomSuffix(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = SuffixAlphabet[RandomNumberGenerator.GetInt32(SuffixAlphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>A missing config that is clearly a path (not an image reference such as <c>aisiuk/inspect-tool-support</c>).</summary>
    private static bool LooksLikePath(string config) =>
        config.Contains('\\')
        || config.StartsWith('.')
        || config.StartsWith('/')
        || config.StartsWith('~')
        || config.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
        || config.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
        || IsDockerfile(config);

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
