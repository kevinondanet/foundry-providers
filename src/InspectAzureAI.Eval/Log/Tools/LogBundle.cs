using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Log.Json;
using InspectAzureAI.Eval.Runner.Scoring;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Log.Tools;

/// <summary>Port of <c>log/_file.py</c> <c>LogOverview</c>: the thinned manifest entry a viewer bundle's <c>listing.json</c> holds per log.</summary>
public sealed record LogOverview
{
    public required string EvalId { get; init; }

    public required string RunId { get; init; }

    public required string Task { get; init; }

    public required string TaskId { get; init; }

    /// <summary>Port of <c>task_version: int | str</c>: an int when the version is numeric, else the string.</summary>
    public required object TaskVersion { get; init; }

    public int Version { get; init; } = EvalLog.SchemaVersion;

    public EvalStatus Status { get; init; }

    public bool Invalidated { get; init; }

    public EvalError? Error { get; init; }

    public required string Model { get; init; }

    /// <summary>A role bound to several models is shown as their comma-separated names.</summary>
    public IReadOnlyDictionary<string, string>? ModelRoles { get; init; }

    /// <summary>Written as <c>""</c> when unset, as Python does.</summary>
    [JsonConverter(typeof(EmptyStringDateTimeOffsetConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Written as <c>""</c> when unset, as Python does.</summary>
    [JsonConverter(typeof(EmptyStringDateTimeOffsetConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTimeOffset? CompletedAt { get; init; }

    public EvalMetric? PrimaryMetric { get; init; }
}

/// <summary>
/// The viewer's static assets, which Python ships inside its package (<c>inspect_ai/_view/dist</c>: <c>index.html</c>
/// and <c>assets/</c>) and this solution does not. <see cref="ResolveDistDir"/> takes an explicit directory, else
/// <see cref="DistDirEnvironmentVariable"/>; the log schema (<c>inspect_ai/_view/inspect-openapi.json</c>) resolves
/// the same way through <see cref="SchemaPathEnvironmentVariable"/>, else as the dist directory's sibling.
/// </summary>
public static class ViewerAssets
{
    /// <summary>Environment variable naming the viewer dist directory (e.g. <c>&lt;site-packages&gt;/inspect_ai/_view/dist</c>).</summary>
    public const string DistDirEnvironmentVariable = "INSPECT_VIEW_DIST_DIR";

    /// <summary>Environment variable naming the log schema file (e.g. <c>&lt;site-packages&gt;/inspect_ai/_view/inspect-openapi.json</c>).</summary>
    public const string SchemaPathEnvironmentVariable = "INSPECT_VIEW_SCHEMA_PATH";

    /// <summary>The schema's file name inside the Python package's <c>_view</c> directory.</summary>
    public const string SchemaFileName = "inspect-openapi.json";

    /// <summary>Port of <c>_dist_dir</c> / <c>resolve_dist_directory</c>: the directory holding <c>index.html</c>.</summary>
    /// <exception cref="PrerequisiteError">No directory is configured, or it has no <c>index.html</c>.</exception>
    public static string ResolveDistDir(string? configured = null)
    {
        var directory = string.IsNullOrEmpty(configured) ? Environment.GetEnvironmentVariable(DistDirEnvironmentVariable) : configured;
        if (string.IsNullOrEmpty(directory))
        {
            throw new PrerequisiteError(
                $"The viewer assets are not configured. Pass the viewer dist directory or set {DistDirEnvironmentVariable} to the inspect_ai package's _view/dist directory (e.g. <site-packages>/inspect_ai/_view/dist).");
        }

        if (!File.Exists(Path.Combine(directory, "index.html")))
        {
            throw new PrerequisiteError($"The viewer dist directory '{directory}' does not contain index.html.");
        }

        return Path.GetFullPath(directory);
    }

    /// <summary>The log schema file: <paramref name="configured"/>, else <see cref="SchemaPathEnvironmentVariable"/>, else <see cref="SchemaFileName"/> next to the dist directory.</summary>
    /// <exception cref="PrerequisiteError">No schema file can be resolved.</exception>
    public static string ResolveSchemaPath(string? configured = null)
    {
        var path = string.IsNullOrEmpty(configured) ? Environment.GetEnvironmentVariable(SchemaPathEnvironmentVariable) : configured;
        if (string.IsNullOrEmpty(path))
        {
            var dist = Environment.GetEnvironmentVariable(DistDirEnvironmentVariable);
            if (!string.IsNullOrEmpty(dist))
            {
                path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dist)) ?? dist, SchemaFileName);
            }
        }

        if (string.IsNullOrEmpty(path))
        {
            throw new PrerequisiteError(
                $"The log schema is not configured. Pass its path or set {SchemaPathEnvironmentVariable} to the inspect_ai package's _view/{SchemaFileName} (or {DistDirEnvironmentVariable} to its _view/dist).");
        }

        if (!File.Exists(path))
        {
            throw new PrerequisiteError($"The log schema file '{path}' does not exist.");
        }

        return Path.GetFullPath(path);
    }
}

/// <summary>
/// Port of <c>log/_bundle.py</c>: bundling a log directory into a statically deployable viewer, embedding the viewer
/// into a log directory, and the <c>listing.json</c> overview manifest (<c>log/_file.py</c> <c>write_log_listing</c>
/// / <c>to_overview</c>). Local filesystems only; Hugging Face (<c>hf/</c>) targets are not supported.
/// </summary>
public static class LogBundle
{
    /// <summary>Port of the <c>INSPECT_VIEW_BUNDLE_OUTPUT_DIR</c> default for the output directory.</summary>
    public const string OutputDirEnvironmentVariable = "INSPECT_VIEW_BUNDLE_OUTPUT_DIR";

    /// <summary>The name of the logs directory inside a bundle.</summary>
    public const string BundleLogDirName = "logs";

    /// <summary>The listing manifest's file name.</summary>
    public const string ListingFileName = "listing.json";

    private const string EvalSetFileName = "eval-set.json";

    /// <summary>
    /// Port of <c>bundle_log_dir</c>: copies the viewer assets, the log files (with any <c>eval-set.json</c> beside
    /// them, relative paths kept) under <c>logs/</c>, a <c>logs/listing.json</c> overview and a <c>robots.txt</c>
    /// into <paramref name="outputDir"/>, assembled in a temporary directory first. <paramref name="logDir"/>
    /// defaults to <c>INSPECT_LOG_DIR</c> (else <c>./logs</c>), <paramref name="outputDir"/> to
    /// <see cref="OutputDirEnvironmentVariable"/>. The viewer comes from <paramref name="viewerDistDir"/>
    /// (see <see cref="ViewerAssets.ResolveDistDir"/>).
    /// </summary>
    /// <exception cref="PrerequisiteError">No output directory; the output directory is inside the log directory or already exists (without <paramref name="overwrite"/>); the log directory is missing or has no logs; no viewer assets.</exception>
    /// <exception cref="NotSupportedException">An <c>hf/</c> (Hugging Face space) target.</exception>
    public static async Task BundleLogDirAsync(string? logDir = null, string? outputDir = null, bool overwrite = false, string? viewerDistDir = null, CancellationToken cancellationToken = default)
    {
        logDir = ResolveLogDir(logDir);
        if (string.IsNullOrEmpty(outputDir))
        {
            outputDir = Environment.GetEnvironmentVariable(OutputDirEnvironmentVariable) ?? "";
        }

        if (outputDir.Length == 0)
        {
            throw new PrerequisiteError("You must provide an 'output_dir'");
        }

        if (IsHfTarget(outputDir))
        {
            throw new NotSupportedException($"Hugging Face targets ('{outputDir}') are not supported by this port; bundle to a local directory instead.");
        }

        var logDirAbs = WithTrailingSeparator(Path.GetFullPath(logDir));
        var outputDirAbs = WithTrailingSeparator(Path.GetFullPath(outputDir));
        if (outputDirAbs.StartsWith(logDirAbs, StringComparison.Ordinal))
        {
            throw new PrerequisiteError($"The output directory '{outputDir}' cannot be a subdirectory of the log directory '{logDir}'");
        }

        if ((Directory.Exists(outputDir) || File.Exists(outputDir)) && !overwrite)
        {
            throw new PrerequisiteError(
                $"The output directory '{outputDir}' already exists. Choose another output directory or use 'overwrite' to overwrite the directory and contents");
        }

        var dist = ViewerAssets.ResolveDistDir(viewerDistDir);
        var workingDir = CreateWorkingDir();
        try
        {
            PrepareViewer(dist, workingDir, BundleLogDirName, Path.GetFullPath(logDir));
            cancellationToken.ThrowIfCancellationRequested();
            var viewLogsDir = Path.Combine(workingDir, BundleLogDirName);
            Directory.CreateDirectory(viewLogsDir);
            CopyLogFiles(logDir, viewLogsDir, cancellationToken);
            await WriteLogListingAsync(viewLogsDir, cancellationToken: cancellationToken).ConfigureAwait(false);
            MoveOutput(workingDir, outputDir, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(workingDir);
        }
    }

    /// <summary>
    /// Port of <c>embed_log_dir</c>: places the viewer (configured with <c>log_dir="."</c>), <c>listing.json</c> and
    /// <c>robots.txt</c> directly in the log directory beside the logs, which are left untouched. Needs an HTTP
    /// server to view (<c>file://</c> fails on CORS).
    /// </summary>
    /// <exception cref="PrerequisiteError">The log directory does not exist, or no viewer assets.</exception>
    public static async Task EmbedLogDirAsync(string? logDir = null, string? viewerDistDir = null, CancellationToken cancellationToken = default)
    {
        logDir = Path.GetFullPath(ResolveLogDir(logDir));
        if (!Directory.Exists(logDir))
        {
            throw new PrerequisiteError($"The log directory '{logDir}' doesn't exist.");
        }

        var dist = ViewerAssets.ResolveDistDir(viewerDistDir);
        var workingDir = CreateWorkingDir();
        try
        {
            PrepareViewer(dist, workingDir, ".", logDir);
            await WriteLogListingAsync(logDir, outputDir: workingDir, cancellationToken: cancellationToken).ConfigureAwait(false);
            CopyDirContents(workingDir, logDir, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(workingDir);
        }
    }

    /// <summary>
    /// Port of <c>write_log_listing</c>: <paramref name="filename"/> in <paramref name="outputDir"/> (default: the log
    /// directory) mapping each log's name, relative to the log directory with forward slashes, to its
    /// <see cref="LogOverview"/>, in Python's JSON format (2-space indent, nulls omitted).
    /// </summary>
    public static async Task WriteLogListingAsync(string logDir, IReadOnlyList<EvalLogInfo>? logs = null, string filename = ListingFileName, string? outputDir = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(logDir);
        ArgumentException.ThrowIfNullOrEmpty(filename);
        var directory = Path.GetFullPath(logDir);
        logs ??= EvalLogFiles.ListEvalLogs(directory);
        var names = logs.Select(log => EvalLogFiles.ManifestEvalLogName(log, directory, Path.DirectorySeparatorChar.ToString())).ToList();
        var headers = EvalLogFiles.ReadEvalLogHeaders(logs);
        var overviews = new Dictionary<string, LogOverview>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            overviews[names[i]] = ToOverview(headers[i]);
        }

        var output = outputDir ?? directory;
        Directory.CreateDirectory(output);
        var json = JsonSerializer.Serialize(overviews, EvalLogWriter.Options);
        await File.WriteAllTextAsync(Path.Combine(output, filename), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of <c>to_overview</c>: the thinned view of a log header (the headline metric as <see cref="LogOverview.PrimaryMetric"/>).</summary>
    public static LogOverview ToOverview(EvalLog header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var headline = HeadlineMetrics.ForLog(header);
        IReadOnlyDictionary<string, string>? modelRoles = null;
        if (header.Eval.ModelRoles is { Count: > 0 } roles)
        {
            var flat = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (role, configs) in roles)
            {
                flat[role] = string.Join(",", configs.Select(config => config.Model));
            }

            modelRoles = flat;
        }

        return new LogOverview
        {
            EvalId = header.Eval.EvalId,
            RunId = header.Eval.RunId,
            Task = header.Eval.Task,
            TaskId = header.Eval.TaskId,
            TaskVersion = int.TryParse(header.Eval.TaskVersion, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version) ? version : header.Eval.TaskVersion,
            Version = header.Version,
            Status = header.Status,
            Invalidated = header.Invalidated,
            Error = header.Error,
            Model = header.Eval.Model,
            ModelRoles = modelRoles,
            StartedAt = header.Stats.StartedAt,
            CompletedAt = header.Stats.CompletedAt,
            PrimaryMetric = headline?.Metric,
        };
    }

    /// <summary>
    /// Port of <c>inject_configuration</c>: embeds <c>{"log_dir": ..., "abs_log_dir": ...}</c> (Python's
    /// <c>json.dumps</c> form) as a <c>log_dir_context</c> JSON script before the <c>&lt;/head&gt;</c> of
    /// <paramref name="htmlFile"/>, so the viewer loads the logs directly.
    /// </summary>
    public static void InjectConfiguration(string htmlFile, string logDir, string? absLogDir = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(htmlFile);
        ArgumentNullException.ThrowIfNull(logDir);
        var context = new JsonObject { ["log_dir"] = logDir };
        if (absLogDir is not null)
        {
            context["abs_log_dir"] = absLogDir;
        }

        var contents = File.ReadAllText(htmlFile, Encoding.UTF8);
        var injected = contents.Replace(
            "</head>",
            $"  <script id=\"log_dir_context\" type=\"application/json\">{PythonJson.Dumps(context)}</script>\n  </head>",
            StringComparison.Ordinal);
        File.WriteAllText(htmlFile, injected, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Port of <c>write_robots_txt</c>: disallows all crawling of the bundle.</summary>
    public static void WriteRobotsTxt(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        File.WriteAllText(Path.Combine(directory, "robots.txt"), "User-agent: *\nDisallow: /\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Port of <c>is_hf_target</c>: an <c>hf/&lt;user&gt;/&lt;space&gt;</c> target names a Hugging Face space.</summary>
    public static bool IsHfTarget(string outputDir)
    {
        ArgumentNullException.ThrowIfNull(outputDir);
        return outputDir.StartsWith("hf/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Port of <c>copy_log_files</c>: every <c>.json</c> / <c>.eval</c> log under <paramref name="logDir"/> (oldest
    /// first) and each <c>eval-set.json</c> found beside one, copied to <paramref name="targetDir"/> with their paths
    /// relative to the log directory.
    /// </summary>
    /// <exception cref="PrerequisiteError">The log directory does not exist or holds no logs.</exception>
    public static void CopyLogFiles(string logDir, string targetDir, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(logDir);
        ArgumentException.ThrowIfNullOrEmpty(targetDir);
        if (!Directory.Exists(logDir))
        {
            throw new PrerequisiteError($"The log directory {logDir} doesn't exist.");
        }

        var logs = EvalLogFiles.ListEvalLogs(logDir, formats: LogFormats.All, recursive: true, descending: false);
        if (logs.Count == 0)
        {
            throw new PrerequisiteError($"The log directory {logDir} doesn't contain any log files.");
        }

        var evalSetFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var log in logs)
        {
            var evalSet = Path.Combine(Path.GetDirectoryName(log.Name) ?? "", EvalSetFileName);
            if (File.Exists(evalSet))
            {
                evalSetFiles.Add(evalSet);
            }
        }

        var baseLogDir = Path.GetFullPath(logDir);
        foreach (var file in logs.Select(log => log.Name).Concat(evalSetFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyFileToBundle(file, baseLogDir, targetDir);
        }
    }

    /// <summary>Port of <c>_prepare_viewer</c>: the dist copied into <paramref name="workingDir"/>, its <c>index.html</c> configured, plus <c>robots.txt</c>.</summary>
    private static void PrepareViewer(string dist, string workingDir, string logDir, string absLogDir)
    {
        CopyDirContents(dist, workingDir, CancellationToken.None);
        InjectConfiguration(Path.Combine(workingDir, "index.html"), logDir, absLogDir);
        WriteRobotsTxt(workingDir);
    }

    /// <summary>Port of <c>copy_file_to_bundle</c>.</summary>
    private static void CopyFileToBundle(string filePath, string baseLogDir, string targetDir)
    {
        var relative = Path.GetRelativePath(baseLogDir, filePath);
        var output = Path.Combine(targetDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? targetDir);
        File.Copy(filePath, output, overwrite: true);
    }

    /// <summary>Port of <c>copy_dir_contents</c> / <c>_copy_viewer_to_log_dir</c>: a recursive copy that leaves the destination's other files in place.</summary>
    private static void CopyDirContents(string sourceDir, string destDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destDir);
        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destDir, Path.GetRelativePath(sourceDir, file)), overwrite: true);
        }
    }

    /// <summary>Port of <c>move_output</c>: replaces <paramref name="toDir"/> with the contents of <paramref name="fromDir"/>, files copied in modification-time order.</summary>
    private static void MoveOutput(string fromDir, string toDir, CancellationToken cancellationToken)
    {
        if (Directory.Exists(toDir))
        {
            Directory.Delete(toDir, recursive: true);
        }
        else if (File.Exists(toDir))
        {
            File.Delete(toDir);
        }

        Directory.CreateDirectory(toDir);
        foreach (var directory in Directory.EnumerateDirectories(fromDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(toDir, Path.GetRelativePath(fromDir, directory)));
        }

        var files = Directory.EnumerateFiles(fromDir, "*", SearchOption.AllDirectories)
            .Select(file => (File: file, Mtime: File.GetLastWriteTimeUtc(file)))
            .OrderBy(entry => entry.Mtime)
            .ThenBy(entry => entry.File, StringComparer.Ordinal);
        foreach (var (file, _) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(toDir, Path.GetRelativePath(fromDir, file)), overwrite: true);
        }
    }

    private static string ResolveLogDir(string? logDir) =>
        string.IsNullOrEmpty(logDir)
            ? Environment.GetEnvironmentVariable(LogFormats.LogDirEnvironmentVariable) is { Length: > 0 } env ? env : "./logs"
            : logDir;

    private static string WithTrailingSeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static string CreateWorkingDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), "inspect-bundle-" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Python: TemporaryDirectory(ignore_cleanup_errors=True)
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
