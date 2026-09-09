using System.Globalization;
using System.Text.Json;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Sandbox.Docker.Compose;

/// <summary>
/// Port of the <c>docker compose</c> CLI wrappers of <c>util/_sandbox/docker/compose.py</c>
/// (<c>compose_command</c>, <c>compose_up</c>, <c>compose_down</c>, <c>compose_ps</c>, <c>compose_check_running</c>,
/// <c>compose_build</c>, <c>compose_pull</c>, <c>compose_cp</c>, <c>compose_cleanup_images</c>). Each call is one argv
/// <c>docker compose [--ansi never] --project-name NAME [-f FILE] ...</c> (never a shell string) run through the
/// injected <see cref="IProcessRunner"/>, under Python's docker CLI concurrency limit and its retry of commands
/// that hang past their timeout. Results are returned raw where Python returns them raw (<c>up</c>, <c>pull</c>);
/// commands Python turns into exceptions surface as <see cref="SandboxUnavailableException"/> /
/// <see cref="InvalidOperationException"/> carrying the CLI's stderr.
/// </summary>
internal sealed class ComposeCli(IProcessRunner? runner = null, string executable = "docker")
{
    /// <summary>Port of <c>COMPOSE_WAIT</c>: how long <c>compose up --wait</c> may take when no service has a healthcheck, in seconds.</summary>
    public const int ComposeWait = 600;

    /// <summary>Port of <c>MAX_RETRIES</c>: how many times a compose command that hangs past its timeout is retried (with a shorter timeout).</summary>
    public const int MaxRetries = 2;

    /// <summary>Port of <c>INSPECT_DOCKER_CLI_CONCURRENCY</c>: how many docker CLI invocations may be in flight at once (default <c>max(2 * cpus, 4)</c>).</summary>
    public const string ConcurrencyVar = "INSPECT_DOCKER_CLI_CONCURRENCY";

    /// <summary>Port of the <c>TIMEOUT</c> of <c>compose_down</c> (shared by the image cleanup that follows it).</summary>
    public static readonly TimeSpan DownTimeout = TimeSpan.FromSeconds(300);

    /// <summary>Port of the 10-minute timeout of <c>compose_cp</c>.</summary>
    public static readonly TimeSpan CopyTimeout = TimeSpan.FromSeconds(600);

    /// <summary>Port of the default timeout of <c>compose_ps</c>.</summary>
    public static readonly TimeSpan PsTimeout = TimeSpan.FromSeconds(300);

    private const long ManagementOutputLimit = 1024 * 1024;

    private static readonly Lazy<SemaphoreSlim> CliGate = new(() =>
    {
        var limit = Concurrency();
        return new SemaphoreSlim(limit, limit);
    });

    private readonly IProcessRunner _runner = runner ?? new ProcessRunner();

    public string Executable { get; } = executable;

    /// <summary>
    /// Port of <c>compose_up</c>: <c>up --detach --wait --wait-timeout N</c>, where N is the longest healthcheck
    /// schedule of the services (see <see cref="ComposeHealthchecks"/>) or <see cref="ComposeWait"/>. The result is
    /// returned unchecked: compose exits non-zero under <c>--wait</c> for a service that exits (even successfully),
    /// so <see cref="CheckRunningAsync"/> is the real test.
    /// </summary>
    public Task<ProcessResult> UpAsync(ComposeProject project, IEnumerable<ComposeService> services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var healthcheckTime = ComposeHealthchecks.ServicesHealthcheckTime(services);
        var timeout = healthcheckTime > 0 ? healthcheckTime : ComposeWait;
        string[] command = ["up", "--detach", "--wait", "--wait-timeout", (timeout + 1).ToString(CultureInfo.InvariantCulture)];
        return CommandAsync(project, command, TimeSpan.FromSeconds(timeout), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Port of <c>compose_down</c>: <c>--ansi never ... down --volumes</c> from the compose file's directory, then
    /// <see cref="CleanupImagesAsync"/>. Failures and timeouts are warnings (cleanup must not throw), and the
    /// commands run to completion regardless of <paramref name="cancellationToken"/>'s source (pass
    /// <see cref="CancellationToken.None"/> from a failure path).
    /// </summary>
    public async Task DownAsync(ComposeProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var cwd = project.ConfigDirectory;
        try
        {
            var result = await CommandAsync(project, ["down", "--volumes"], DownTimeout, cwd: cwd, ansi: "never", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                ProviderLogger.Warning($"Failed to stop docker service {result.StderrText}");
            }
        }
        catch (SandboxTimeoutException)
        {
            ProviderLogger.Warning($"Docker compose down for project '{project.Name}' timed out after {DownTimeout.TotalSeconds} seconds.");
        }

        try
        {
            await CleanupImagesAsync(project, cwd, DownTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxTimeoutException)
        {
            ProviderLogger.Warning($"Docker image cleanup for project '{project.Name}' timed out after {DownTimeout.TotalSeconds} seconds.");
        }
    }

    /// <summary>
    /// Port of <c>compose_cleanup_images</c>: the images <c>config --images</c> lists whose names start with the
    /// project name (compose's <c>PROJECT-SERVICE</c> tags for built services) are removed with <c>docker rmi</c>
    /// when <c>docker images -q</c> shows them present.
    /// </summary>
    public async Task CleanupImagesAsync(ComposeProject project, string? cwd, TimeSpan? timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var images = await CommandAsync(project, ["config", "--images"], timeout, cwd: cwd, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!images.Success)
        {
            return;
        }

        foreach (var image in images.StdoutText.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!image.StartsWith(project.Name, StringComparison.Ordinal))
            {
                continue;
            }

            var listed = await DockerAsync(["images", "-q", image], timeout, cancellationToken).ConfigureAwait(false);
            var remove = !listed.Success || listed.StdoutText.Length != 0;
            if (remove)
            {
                var removed = await DockerAsync(["rmi", image], timeout, cancellationToken).ConfigureAwait(false);
                if (!removed.Success)
                {
                    ProviderLogger.Warning($"Failed to cleanup docker image {removed.StderrText}");
                }
            }
        }
    }

    /// <summary>Port of <c>compose_build</c>: <c>build</c> with no timeout; a failure is a <see cref="PrerequisiteError"/> (as in Python) carrying the tail of the build log.</summary>
    public async Task BuildAsync(ComposeProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var result = await CommandAsync(project, ["build"], timeout: null, outputLimit: 8 * ManagementOutputLimit, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new PrerequisiteError($"Failed to build docker containers: {Detail(result)}");
        }
    }

    /// <summary>Port of <c>compose_pull</c>: <c>pull --ignore-buildable --policy missing SERVICE</c> with no timeout; the result is the caller's to judge.</summary>
    public Task<ProcessResult> PullAsync(string service, ComposeProject project, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(service);
        return CommandAsync(project, ["pull", "--ignore-buildable", "--policy", "missing", service], timeout: null, cancellationToken: cancellationToken);
    }

    /// <summary>Port of <c>compose_cp</c>: <c>cp -L -- SRC DEST</c> (either side may be <c>SERVICE:PATH</c>); a failure is an <see cref="InvalidOperationException"/> quoting stderr.</summary>
    public async Task CopyAsync(ComposeProject project, string source, string destination, string? cwd = null, long? outputLimit = null, bool timeoutRetry = true, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        var result = await CommandAsync(project, ["cp", "-L", "--", source, destination], CopyTimeout, timeoutRetry, cwd: cwd, outputLimit: outputLimit, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Failed to copy file from '{source}' to '{destination}': {result.StderrText}");
        }
    }

    /// <summary>
    /// Port of <c>compose_ps</c>: <c>ps --format json [--all] [--status STATUS]</c>. Both output shapes of the
    /// compose CLI are read (one JSON object per line since compose 2.21, a JSON array before).
    /// </summary>
    public async Task<IReadOnlyList<ComposeContainer>> PsAsync(ComposeProject project, string? status = null, bool all = false, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        List<string> command = ["ps", "--format", "json"];
        if (all)
        {
            command.Add("--all");
        }

        if (status is not null)
        {
            command.Add("--status");
            command.Add(status);
        }

        var result = await CommandAsync(project, command, timeout ?? PsTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Error querying for running services: {result.StderrText}");
        }

        return ParsePs(result.StdoutText);
    }

    /// <summary>
    /// Port of <c>compose_check_running</c>: the running containers of the project when every service of the file
    /// is either running or exited successfully, else an empty list (the sample cannot start). Deviation: returns
    /// the containers (name, service, state) rather than service names, so the caller need not query again.
    /// </summary>
    public async Task<IReadOnlyList<ComposeContainer>> CheckRunningAsync(IReadOnlyCollection<string> services, ComposeProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var running = await PsAsync(project, status: "running", cancellationToken: cancellationToken).ConfigureAwait(false);
        var exited = await PsAsync(project, status: "exited", cancellationToken: cancellationToken).ConfigureAwait(false);
        var successful = running.Count + exited.Count(container => container.ExitCode == 0);
        if (successful == 0 || successful != services.Count)
        {
            return [];
        }

        return running;
    }

    /// <summary>Port of <c>compose_ps</c>'s JSON handling for the text of <c>ps --format json</c>.</summary>
    public static IReadOnlyList<ComposeContainer> ParsePs(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var text = output.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        var containers = new List<ComposeContainer>();
        if (text.StartsWith('['))
        {
            using var document = JsonDocument.Parse(text);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                containers.Add(ComposeContainer.FromJson(element));
            }

            return containers;
        }

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            containers.Add(ComposeContainer.FromJson(document.RootElement));
        }

        return containers;
    }

    /// <summary>
    /// Port of <c>compose_command</c>: runs <c>docker compose [--ansi ANSI] --project-name NAME [-f FILE] COMMAND...</c>
    /// with the project's environment forwarded (the <c>SAMPLE_METADATA_*</c> interpolation variables) unless
    /// <paramref name="forwardEnv"/> is false. A command with a <paramref name="timeout"/> that hangs past it is retried
    /// up to <see cref="MaxRetries"/> times with a shorter timeout (compose has been seen to hang on busy daemons and
    /// succeed on retry); the last hang is a <see cref="SandboxTimeoutException"/> carrying whatever was captured.
    /// </summary>
    public async Task<ProcessResult> CommandAsync(
        ComposeProject project,
        IReadOnlyList<string> command,
        TimeSpan? timeout,
        bool timeoutRetry = true,
        ReadOnlyMemory<byte>? input = null,
        string? cwd = null,
        bool forwardEnv = true,
        long? outputLimit = null,
        string? ansi = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(command);
        var args = Arguments(project, command, ansi);
        var env = forwardEnv && project.Env is { Count: > 0 } ? project.Env : null;
        if (timeout is null)
        {
            return await RunAsync(args, env, input, cwd, null, outputLimit, cancellationToken).ConfigureAwait(false);
        }

        var retries = 0;
        while (true)
        {
            var commandTimeout = retries == 0
                ? timeout.Value
                : TimeSpan.FromSeconds(Math.Max(Math.Floor(Math.Min(timeout.Value.TotalSeconds, 60) / retries), 1));
            var result = await RunAsync(args, env, input, cwd, commandTimeout, outputLimit, cancellationToken).ConfigureAwait(false);
            if (!result.TimedOut)
            {
                return result;
            }

            retries++;
            if (timeoutRetry && retries <= MaxRetries)
            {
                ProviderLogger.Info($"Retrying docker compose command after TimeoutError: {ShellWords.Join([Executable, .. args])}");
                continue;
            }

            throw new SandboxTimeoutException(
                $"Docker compose command '{string.Join(" ", command)}' timed out after {timeout.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds",
                result.CombinedText);
        }
    }

    /// <summary>The argv (after the executable) <see cref="CommandAsync"/> builds, for callers that only need to describe a command.</summary>
    public static IReadOnlyList<string> Arguments(ComposeProject project, IReadOnlyList<string> command, string? ansi = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        List<string> args = ["compose"];
        if (ansi is not null)
        {
            args.Add("--ansi");
            args.Add(ansi);
        }

        args.Add("--project-name");
        args.Add(project.Name);
        if (project.ConfigFile is not null)
        {
            args.Add("-f");
            args.Add(project.ConfigFile);
        }

        args.AddRange(command);
        return args;
    }

    /// <summary>A bare docker (not compose) command used by the image cleanup; a timeout is a <see cref="SandboxTimeoutException"/> as Python's <c>subprocess</c> raises.</summary>
    private async Task<ProcessResult> DockerAsync(IReadOnlyList<string> args, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var result = await RunAsync(args, null, null, null, timeout, null, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new SandboxTimeoutException($"docker {args[0]} timed out after {timeout?.TotalSeconds ?? 0} seconds", result.CombinedText);
        }

        return result;
    }

    private async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env,
        ReadOnlyMemory<byte>? input,
        string? cwd,
        TimeSpan? timeout,
        long? outputLimit,
        CancellationToken cancellationToken)
    {
        var request = new ProcessRequest(Executable, args)
        {
            Input = input,
            Environment = env,
            WorkingDirectory = cwd,
            Timeout = timeout,
            OutputLimit = outputLimit ?? ManagementOutputLimit,
        };
        var gate = CliGate.Value;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static int Concurrency()
    {
        var configured = Environment.GetEnvironmentVariable(ConcurrencyVar);
        if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit > 0)
        {
            return limit;
        }

        return Math.Max(Environment.ProcessorCount * 2, 4);
    }

    private static string Detail(ProcessResult result)
    {
        const int maxChars = 8 * 1024;
        var text = result.StderrText.Trim();
        if (text.Length == 0)
        {
            text = result.StdoutText.Trim();
        }

        return text.Length > maxChars ? text[^maxChars..] : text;
    }
}

/// <summary>One row of <c>docker compose ps --format json</c>: the container compose created for a service and its state.</summary>
internal sealed record ComposeContainer(string Id, string Name, string Service, string State, int ExitCode, string? Health = null)
{
    internal static ComposeContainer FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Unexpected docker compose ps entry: {element}");
        }

        return new ComposeContainer(
            Text(element, "ID"),
            Text(element, "Name"),
            Text(element, "Service"),
            Text(element, "State"),
            element.TryGetProperty("ExitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number ? exitCode.GetInt32() : 0,
            element.TryGetProperty("Health", out var health) && health.ValueKind == JsonValueKind.String ? health.GetString() : null);
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
