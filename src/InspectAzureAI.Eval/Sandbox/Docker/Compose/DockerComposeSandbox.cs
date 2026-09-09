using InspectAzureAI.Eval.Context;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Sandbox.Docker.Compose;

/// <summary>
/// Port of the compose-backed lifecycle classmethods of <c>util/_sandbox/docker/docker.py</c>
/// (<c>task_init</c>, <c>sample_init</c>, <c>sample_cleanup</c>, <c>task_cleanup</c>) with the project tracking of
/// <c>cleanup.py</c> (<c>project_startup</c>, <c>project_cleanup</c>, <c>project_cleanup_shutdown</c>): one compose
/// project per task for the build and pulls, one per sample for its containers (both named by
/// <see cref="ComposeFiles.TaskProjectName"/>), every running service an environment with the default service
/// first, <c>compose down --volumes</c> per sample and the auto-generated compose files removed at task end.
/// Deviation: Python's <c>validate_prereqs</c> version checks and its internal-image builds (Dockerfiles shipped
/// inside the Python package) are not ported — an internal image is pulled like any other. Running projects are
/// tracked per task name on this instance rather than in Python's per-run context variables.
/// </summary>
internal sealed class DockerComposeSandbox(DockerCli docker, ComposeCli compose)
{
    private readonly object _sync = new();

    private readonly List<ComposeProject> _running = [];

    private readonly List<(string TaskName, string File)> _autoComposeFiles = [];

    /// <summary>Port of <c>task_init</c>: resolve the compose file, <c>compose build</c>, remove the images the task project built, then pull what the services still need. A failed build or an explicit <c>container_name</c> is a <see cref="PrerequisiteError"/>, as in Python.</summary>
    public async Task TaskInitAsync(string taskName, string? config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskName);
        var project = ComposeProject.Create(ComposeFiles.TaskProjectName(taskName), config, taskName);
        RecordAutoCompose(project);
        try
        {
            var services = ComposeConfig.Load(project.ConfigFile!);

            // build containers which are out of date, then cleanup images created during build
            await compose.BuildAsync(project, cancellationToken).ConfigureAwait(false);
            await compose.CleanupImagesAsync(project, project.ConfigDirectory, ComposeCli.DownTimeout, cancellationToken).ConfigureAwait(false);

            foreach (var (name, service) in services.Services)
            {
                // an explicit container_name cannot work with epochs > 1 (every sample is its own project)
                if (!string.IsNullOrEmpty(service.ContainerName))
                {
                    throw new PrerequisiteError(
                        $"ERROR: Docker service '{name}' includes an explicitly configured container_name ('{service.ContainerName}'). This is not permitted, as container names should be provisioned by Docker compose and an explicit container_name will not work with epochs > 1.");
                }

                // pull any remote images (buildable and x-local services are never pulled)
                if (service.Build is not null || service.XLocal)
                {
                    continue;
                }

                // skip the pull if the image is already available locally (avoids noisy errors for images loaded via 'docker load')
                if (service.Image is not null && await docker.ImageExistsAsync(service.Image, cancellationToken).ConfigureAwait(false))
                {
                    ProviderLogger.Info($"Service {name}: using local image '{service.Image}'.");
                    continue;
                }

                var pull = await compose.PullAsync(name, project, cancellationToken).ConfigureAwait(false);
                if (!pull.Success)
                {
                    ProviderLogger.Warning(
                        $"Failed to pull docker image '{service.Image ?? "(unknown)"}' from remote registry. If this is a locally built image add 'x-local: true' to the the service definition to prevent this error.");
                }
            }
        }
        catch
        {
            await ShutdownAsync(taskName, cleanup: true, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Port of <c>sample_init</c>: a fresh project for the sample, <c>compose up --wait</c>, the running-services
    /// check, one <see cref="DockerComposeSandboxEnvironment"/> per running service (default first), and a cleanup
    /// delegate that brings the project down — or keeps it, logging how to reach and remove it. Any failure before
    /// the delegate exists brings the project down here. Deviation: a file with exactly one service is accepted as
    /// the default even without <c>default</c>/<c>x-default</c> (Python raises "No 'default' service found").
    /// </summary>
    public async Task<SandboxEnvironments> SampleInitAsync(string taskName, string? config, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskName);
        ArgumentNullException.ThrowIfNull(metadata);
        var sample = SampleContext.Current?.SampleState;
        var project = ComposeProject.Create(ComposeFiles.TaskProjectName(taskName), config, taskName, sample?.SampleId?.ToString(), sample?.Epoch);
        project = project with { Env = ResolveConfigEnvironment(project.ConfigFile, metadata) };
        Startup(project);
        try
        {
            // enumerate the services that will be created, start them, and check that they are running
            var services = ComposeConfig.Load(project.ConfigFile!);
            var up = await compose.UpAsync(project, services.Services.Values, cancellationToken).ConfigureAwait(false);
            var running = await compose.CheckRunningAsync(services.Services.Keys.ToList(), project, cancellationToken).ConfigureAwait(false);
            if (running.Count == 0)
            {
                throw new InvalidOperationException($"No services started.\nCompose up stderr: {up.StderrText}");
            }

            // create sandbox environments for all running services
            string? defaultService = null;
            var environments = new List<KeyValuePair<string, ISandboxEnvironment>>();
            foreach (var (service, definition) in services.Services)
            {
                var container = running.FirstOrDefault(entry => entry.Service == service);
                if (container is null)
                {
                    continue;
                }

                var workingDir = await ContainerWorkingDirAsync(service, container.Name, cancellationToken).ConfigureAwait(false);
                environments.Add(new(service, new DockerComposeSandboxEnvironment(docker, compose, project, service, container.Name, workingDir)));
                if (definition.XDefault)
                {
                    defaultService = service;
                }
            }

            // confirm that we have a 'default' environment and put it first
            defaultService ??= environments.Any(entry => entry.Key == "default")
                ? "default"
                : environments.Count == 1 && services.Services.Count == 1
                    ? environments[0].Key
                    : throw new InvalidOperationException(
                        "No 'default' service found in Docker compose file. You should either name a service 'default' or add 'x-default: true' to one of your service definitions.");
            var index = environments.FindIndex(entry => entry.Key == defaultService);
            if (index < 0)
            {
                throw new InvalidOperationException($"The default service '{defaultService}' of the Docker compose file is not running.\nCompose up stderr: {up.StderrText}");
            }

            var ordered = new List<KeyValuePair<string, ISandboxEnvironment>>(environments.Count) { environments[index] };
            ordered.AddRange(environments.Where((_, position) => position != index));
            return SandboxEnvironments.Create(ordered, cleanup => SampleCleanupAsync(project, ordered, cleanup));
        }
        catch
        {
            await ProjectCleanupAsync(project).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Port of <c>task_cleanup</c> / <c>project_cleanup_shutdown</c> for <paramref name="taskName"/>: projects a
    /// sample kept are brought down when <paramref name="cleanup"/> is true, otherwise listed with their
    /// containers and the command that removes them; the task's auto-generated compose files are removed either way.
    /// </summary>
    public async Task TaskCleanupAsync(string taskName, bool cleanup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskName);
        await ShutdownAsync(taskName, cleanup, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The projects started for <paramref name="taskName"/> that have not been brought down.</summary>
    public IReadOnlyList<ComposeProject> RunningProjects(string taskName)
    {
        lock (_sync)
        {
            return _running.Where(project => project.TaskName == taskName).ToList();
        }
    }

    /// <summary>
    /// Port of <c>resolve_config_environment</c>: the <c>SAMPLE_METADATA_&lt;KEY&gt;</c> variables (key upper-cased,
    /// spaces to underscores) the compose file text mentions, valued from the sample's metadata, for compose's
    /// <c>${...}</c> interpolation. Deviation: read from the resolved compose file rather than the raw config
    /// string, so a directory config interpolates too.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ResolveConfigEnvironment(string? configFile, IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (configFile is null || metadata.Count == 0 || !File.Exists(configFile))
        {
            return env;
        }

        var text = File.ReadAllText(configFile);
        foreach (var (key, value) in metadata)
        {
            var name = $"SAMPLE_METADATA_{key.Replace(' ', '_').ToUpperInvariant()}";
            if (text.Contains(name, StringComparison.Ordinal))
            {
                env[name] = value;
            }
        }

        return env;
    }

    private async Task SampleCleanupAsync(ComposeProject project, IReadOnlyList<KeyValuePair<string, ISandboxEnvironment>> environments, bool cleanup)
    {
        if (cleanup)
        {
            await ProjectCleanupAsync(project).ConfigureAwait(false);
            return;
        }

        var containers = environments.Select(entry => ((DockerComposeSandboxEnvironment)entry.Value).ContainerName).ToList();
        ProviderLogger.Info(
            $"Sandbox project {project.Name} was kept for inspection (containers: {string.Join(", ", containers)}; "
            + $"docker exec -it {containers[0]} bash -l; docker compose --project-name {project.Name} down --volumes to remove).");
    }

    /// <summary>Port of <c>project_cleanup</c>: <c>compose down --volumes</c> (never cancelled: it runs on failure paths and must not orphan containers), then the project is no longer running.</summary>
    private async Task ProjectCleanupAsync(ComposeProject project)
    {
        await compose.DownAsync(project, CancellationToken.None).ConfigureAwait(false);
        lock (_sync)
        {
            _running.RemoveAll(running => running.Name == project.Name);
        }
    }

    private async Task ShutdownAsync(string taskName, bool cleanup, CancellationToken cancellationToken)
    {
        var projects = RunningProjects(taskName);
        if (projects.Count > 0)
        {
            if (cleanup)
            {
                ProviderLogger.Info("Cleaning up Docker environments (please do not interrupt this operation!)");
                await Task.WhenAll(projects.Select(CleanupProjectReportingErrorsAsync)).ConfigureAwait(false);
            }
            else
            {
                var lines = new List<string> { "Docker Sandbox Environments (not yet cleaned up):" };
                foreach (var project in projects)
                {
                    string containers;
                    try
                    {
                        containers = string.Join(", ", (await compose.PsAsync(project, all: true, cancellationToken: cancellationToken).ConfigureAwait(false)).Select(container => container.Name));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or SandboxUnavailableException or SandboxTimeoutException)
                    {
                        containers = $"(unknown: {ex.Message.Trim()})";
                    }

                    lines.Add($"  sample {project.SampleId ?? ""} epoch {project.Epoch?.ToString() ?? ""} ({project.Name}): {containers}");
                }

                lines.Add("Cleanup a project: docker compose --project-name <name> down --volumes");
                ProviderLogger.Info(string.Join("\n", lines));
            }
        }

        // remove auto-compose files
        List<string> files;
        lock (_sync)
        {
            files = _autoComposeFiles.Where(entry => entry.TaskName == taskName).Select(entry => entry.File).ToList();
            _autoComposeFiles.RemoveAll(entry => entry.TaskName == taskName);
        }

        foreach (var file in files)
        {
            ComposeFiles.SafeCleanupAutoCompose(file);
        }
    }

    private async Task CleanupProjectReportingErrorsAsync(ComposeProject project)
    {
        try
        {
            await ProjectCleanupAsync(project).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProviderLogger.Warning($"Error cleaning up Docker environment {project.Name}: {ex.Message}");
        }
    }

    /// <summary>Port of <c>container_working_dir</c> over <c>docker inspect</c> rather than an exec of <c>pwd</c> ("" is "/", as the image's WORKDIR is the container's cwd).</summary>
    private async Task<string> ContainerWorkingDirAsync(string service, string container, CancellationToken cancellationToken)
    {
        try
        {
            return await docker.InspectAsync(container, "{{.Config.WorkingDir}}", cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxUnavailableException ex)
        {
            ProviderLogger.Warning($"Failed to get working directory for docker container '{service}': {ex.Message}");
            return "/";
        }
    }

    private void Startup(ComposeProject project)
    {
        lock (_sync)
        {
            _running.Add(project);
        }

        RecordAutoCompose(project);
    }

    private void RecordAutoCompose(ComposeProject project)
    {
        if (!project.IsAutoCompose)
        {
            return;
        }

        lock (_sync)
        {
            if (!_autoComposeFiles.Contains((project.TaskName, project.ConfigFile!)))
            {
                _autoComposeFiles.Add((project.TaskName, project.ConfigFile!));
            }
        }
    }
}
