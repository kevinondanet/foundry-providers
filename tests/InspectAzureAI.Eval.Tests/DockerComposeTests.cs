using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Sandbox.Docker;
using InspectAzureAI.Eval.Sandbox.Docker.Compose;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

/// <summary>
/// Answers the docker and compose commands a compose-backed sample issues the way the CLIs would: <c>ps</c> lists
/// the configured services as running containers named <c>PROJECT-SERVICE-1</c>, <c>image inspect</c> knows the
/// configured local images, <c>inspect</c> answers a workdir or the published ports, everything else succeeds.
/// </summary>
internal sealed class ComposeScript
{
    public List<string> Services { get; } = ["default"];

    public HashSet<string> LocalImages { get; } = [];

    public string PortsJson { get; set; } = "{}";

    /// <summary>What <c>config --images</c> prints for a project name (nothing by default).</summary>
    public Func<string, string>? ConfigImages { get; set; }

    public ProcessResult? UpResult { get; set; }

    public ProcessResult? BuildResult { get; set; }

    public bool NoneRunning { get; set; }

    public ProcessResult Respond(ProcessRequest request)
    {
        var args = request.Arguments;
        if (args[0] == "compose")
        {
            var (project, _, command) = DockerComposeTests.ComposeArgs(request);
            switch (command[0])
            {
                case "ps":
                    var status = command.Contains("--status") ? command[command.IndexOf("--status") + 1] : null;
                    if (status == "exited" || NoneRunning)
                    {
                        return ProcessResult.Ok("");
                    }

                    return ProcessResult.Ok(string.Join("\n", Services.Select(service =>
                        $$"""{"ID":"{{service}}id","Name":"{{project}}-{{service}}-1","Service":"{{service}}","State":"running","Health":"","ExitCode":0}""")) + "\n");
                case "up":
                    return UpResult ?? ProcessResult.Ok();
                case "build":
                    return BuildResult ?? ProcessResult.Ok();
                case "config":
                    return ProcessResult.Ok(ConfigImages?.Invoke(project) ?? "");
                default:
                    return ProcessResult.Ok();
            }
        }

        return args switch
        {
            ["image", "inspect", .., var image] => LocalImages.Contains(image) ? ProcessResult.Ok("sha256:abc\n") : ProcessResult.Failed(1, stderr: "Error: No such image: " + image),
            ["inspect", "--format", "{{.Config.WorkingDir}}", _] => ProcessResult.Ok("/workspace\n"),
            ["inspect", "--format", "{{json .NetworkSettings.Ports}}", _] => ProcessResult.Ok(PortsJson + "\n"),
            ["images", "-q", _] => ProcessResult.Ok("deadbeef\n"),
            ["exec", ..] => ProcessResult.Ok("hi\n"),
            _ => ProcessResult.Ok(),
        };
    }
}

/// <summary>
/// The compose path of the docker provider (port of <c>util/_sandbox/docker/{compose,config,docker,service,util}.py</c>)
/// exercised through a scripted process runner: the exact compose argv for auto-composed images and Dockerfiles,
/// real compose files (multi-service, internal networks, ports, x-default), cleanup, and the parsing of the
/// examples' compose files.
/// </summary>
public sealed class DockerComposeTests : IDisposable
{
    private const string Image = "aisiuk/inspect-tool-support";

    private readonly string _root;

    private readonly string _autoDir;

    private readonly EnvVarScope _env;

    public DockerComposeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inspect-compose-tests", Guid.NewGuid().ToString("N"));
        _autoDir = Path.Combine(_root, "auto-compose");
        Directory.CreateDirectory(_autoDir);
        _env = new EnvVarScope().Set("INSPECT_AUTO_COMPOSE_DIR", _autoDir);
        ProviderLogger.Reset();
    }

    public void Dispose()
    {
        _env.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    internal static (string Project, string? File, List<string> Command) ComposeArgs(ProcessRequest request)
    {
        var args = request.Arguments;
        string? project = null, file = null;
        var i = 1;
        while (i < args.Count)
        {
            switch (args[i])
            {
                case "--ansi":
                    i += 2;
                    continue;
                case "--project-name":
                    project = args[i + 1];
                    i += 2;
                    continue;
                case "-f":
                    file = args[i + 1];
                    i += 2;
                    continue;
            }

            break;
        }

        return (project ?? throw new InvalidOperationException("compose command without --project-name"), file, args.Skip(i).ToList());
    }

    private static (ScriptedProcessRunner Runner, ComposeScript Script, DockerSandboxProvider Provider) Provider(DockerComposeMode mode = DockerComposeMode.Auto, params string[] services)
    {
        var runner = new ScriptedProcessRunner();
        var script = new ComposeScript();
        if (services.Length > 0)
        {
            script.Services.Clear();
            script.Services.AddRange(services);
        }

        runner.Responder = script.Respond;
        return (runner, script, new DockerSandboxProvider(runner, mode));
    }

    private static List<(string Project, string? File, List<string> Command)> Compose(ScriptedProcessRunner runner) =>
        runner.Requests.Where(request => request.Arguments[0] == "compose").Select(ComposeArgs).ToList();

    private static Dictionary<string, string> Metadata() => new();

    private string Dir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string ComposeFile(string name, string yaml, string fileName = "compose.yaml")
    {
        var path = Path.Combine(Dir(name), fileName);
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public async Task image_only_spec_auto_composes_through_build_pull_up_exec_and_down()
    {
        var (runner, _, provider) = Provider(DockerComposeMode.Always);

        await provider.TaskInitAsync("my task", Image);
        var environments = await provider.SampleInitAsync("my task", Image, Metadata());
        var sandbox = Assert.IsType<DockerComposeSandboxEnvironment>(environments.Default);
        var echo = await sandbox.ExecAsync(["echo", "hi"]);
        var files = Directory.GetFiles(_autoDir);
        var taskYaml = File.ReadAllText(Compose(runner)[0].File!);
        await environments.Cleanup!(true);
        await provider.TaskCleanupAsync("my task", Image, cleanup: true);

        var compose = Compose(runner);
        var (taskProject, taskFile, build) = compose[0];
        var (sampleProject, sampleFile, up) = compose[3];
        // task init: build the (auto-composed) task project, drop its build images, pull the image the service needs
        Assert.Matches("^inspect-my-task-i[23456789abcdefghjkmnpqrstuvwxyz]{6}$", taskProject);
        Assert.Equal(Path.Combine(_autoDir, taskProject + ".yaml"), taskFile);
        Assert.Equal(ComposeFiles.GenericYaml(Image), taskYaml);
        Assert.Equal(["build"], build);
        Assert.Null(runner.Requests[0].Timeout);
        Assert.Equal(["config", "--images"], compose[1].Command);
        Assert.Equal(["image", "inspect", "--format", "{{.Id}}", Image], runner.Requests[2].Arguments);
        Assert.Equal(["pull", "--ignore-buildable", "--policy", "missing", "default"], compose[2].Command);
        // sample init: its own project and compose file, up --wait, the running check, the container's workdir
        Assert.NotEqual(taskProject, sampleProject);
        Assert.Equal(Path.Combine(_autoDir, sampleProject + ".yaml"), sampleFile);
        Assert.Equal(["up", "--detach", "--wait", "--wait-timeout", "601"], up);
        Assert.Equal(TimeSpan.FromSeconds(600), runner.Requests.Single(r => r.Arguments.Contains("up")).Timeout);
        Assert.Equal(["ps", "--format", "json", "--status", "running"], compose[4].Command);
        Assert.Equal(["ps", "--format", "json", "--status", "exited"], compose[5].Command);
        Assert.Equal(["inspect", "--format", "{{.Config.WorkingDir}}", $"{sampleProject}-default-1"], runner.Requests.Single(r => r.Arguments[0] == "inspect").Arguments);
        Assert.Equal("default", sandbox.Service);
        Assert.Equal($"{sampleProject}-default-1", sandbox.ContainerName);
        Assert.Equal("/workspace", sandbox.WorkingDirectory);
        Assert.Equal(sampleProject, sandbox.Project.Name);
        Assert.Equal(["default"], environments.Environments.Keys);
        // exec goes to the service's container through bare docker exec
        Assert.Equal(["exec", $"{sampleProject}-default-1", "echo", "hi"], runner.Requests.Single(r => r.Arguments[0] == "exec").Arguments);
        Assert.Equal("hi\n", echo.Stdout);
        // cleanup: down --volumes from the compose file's directory, then the built images; the auto files go at task cleanup
        var down = runner.Requests.Single(r => r.Arguments.Contains("down"));
        Assert.Equal(["compose", "--ansi", "never", "--project-name", sampleProject, "-f", sampleFile!, "down", "--volumes"], down.Arguments);
        Assert.Equal(_autoDir, down.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(300), down.Timeout);
        Assert.Equal(["config", "--images"], compose[^1].Command);
        Assert.Equal(sampleProject, compose[^1].Project);
        Assert.Equal(2, files.Length);
        Assert.Empty(Directory.GetFiles(_autoDir));
    }

    [Fact]
    public async Task dockerfile_directory_auto_composes_with_an_absolute_build_context_and_removes_the_built_image()
    {
        var context = Dir("build");
        File.WriteAllText(Path.Combine(context, "Dockerfile"), "FROM scratch\n");
        var (runner, script, provider) = Provider(DockerComposeMode.Always);
        script.ConfigImages = project => $"{project}-default\nubuntu:24.04\n";

        await provider.TaskInitAsync("build-task", context);

        var compose = Compose(runner);
        var (project, file, _) = compose[0];
        Assert.Equal(Path.Combine(_autoDir, project + ".yaml"), file);
        Assert.Equal(ComposeFiles.DockerfileYaml(context), File.ReadAllText(file!));
        Assert.Contains($"context: \"{context}\"", File.ReadAllText(file!));
        Assert.Contains("dockerfile: \"Dockerfile\"", File.ReadAllText(file!));
        Assert.Equal(["build"], compose[0].Command);
        Assert.Equal(["config", "--images"], compose[1].Command);
        Assert.Equal(2, compose.Count);
        // a buildable service is never pulled or looked up
        Assert.DoesNotContain(runner.Requests, r => r.Arguments[0] == "image" || r.Arguments.Contains("pull"));
        // only the images this project built are removed
        Assert.Equal(["images", "-q", $"{project}-default"], runner.Requests.Single(r => r.Arguments[0] == "images").Arguments);
        Assert.Equal(["rmi", $"{project}-default"], runner.Requests.Single(r => r.Arguments[0] == "rmi").Arguments);
        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("ubuntu:24.04"));
    }

    [Fact]
    public async Task compose_file_with_two_services_internal_network_and_ports_maps_each_service_with_the_default_first()
    {
        var path = ComposeFile("two", """
            services:
              default:
                image: aisiuk/inspect-computer-tool
                init: true
                ports:
                  - "127.0.0.1::5900"
                  - "127.0.0.1::6080"
                networks:
                  - sandbox
              helper:
                image: ubuntu:24.04
                command: tail -f /dev/null
                networks:
                  - sandbox

            networks:
              sandbox:
                internal: true
            """);
        var (runner, script, provider) = Provider(DockerComposeMode.Auto, "default", "helper");
        script.LocalImages.Add("ubuntu:24.04");
        script.PortsJson = """{"5900/tcp":[{"HostIp":"127.0.0.1","HostPort":"61029"}],"6080/tcp":[{"HostIp":"127.0.0.1","HostPort":"61030"}]}""";

        var config = ComposeConfig.Load(path);
        await provider.TaskInitAsync("two", path);
        var environments = await provider.SampleInitAsync("two", path, Metadata());
        var helper = Assert.IsType<DockerComposeSandboxEnvironment>(environments.Environments["helper"]);
        await helper.ExecAsync(["id"]);
        var connection = await ((DockerComposeSandboxEnvironment)environments.Default).ConnectionAsync();
        var asRoot = await helper.ConnectionAsync(user: "root");

        // the parsed file
        Assert.True(config.Networks["sandbox"].Internal);
        Assert.Equal(["127.0.0.1::5900", "127.0.0.1::6080"], config.Services["default"].Ports);
        Assert.Equal(["sandbox"], config.Services["default"].Networks);
        Assert.True(config.Services["default"].Init);
        Assert.Equal(["tail", "-f", "/dev/null"], config.Services["helper"].Command);
        Assert.Equal("default", config.DefaultService);
        // the file itself is passed to compose (nothing auto-generated) and the local image is not pulled
        var compose = Compose(runner);
        Assert.All(compose, c => Assert.Equal(path, c.File));
        Assert.Empty(Directory.GetFiles(_autoDir));
        Assert.Equal(["pull", "--ignore-buildable", "--policy", "missing", "default"], Assert.Single(compose, c => c.Command[0] == "pull").Command);
        Assert.Contains(ProviderLogger.Infos, m => m == "Service helper: using local image 'ubuntu:24.04'.");
        Assert.Equal(["up", "--detach", "--wait", "--wait-timeout", "601"], compose.Single(c => c.Command[0] == "up").Command);
        // one environment per service, the default first, each on its own container
        var project = compose.Single(c => c.Command[0] == "up").Project;
        Assert.Equal(["default", "helper"], environments.Environments.Keys);
        Assert.Equal($"{project}-default-1", ((DockerComposeSandboxEnvironment)environments.Default).ContainerName);
        Assert.Equal($"{project}-helper-1", helper.ContainerName);
        Assert.Equal(["exec", $"{project}-helper-1", "id"], runner.Requests.Single(r => r.Arguments[0] == "exec").Arguments);
        // connection info: compose ps finds the container, docker inspect the ports
        Assert.Equal("docker", connection.Type);
        Assert.Equal($"docker exec -it {project}-default-1 bash -l", connection.Command);
        Assert.Equal(["remote-containers.attachToRunningContainer", $"{project}-default-1"], connection.VscodeCommand);
        Assert.Equal($"{project}-default-1", connection.Container);
        Assert.Equal([5900, 6080], connection.Ports!.Select(p => p.ContainerPort));
        Assert.Equal(new HostMapping("127.0.0.1", 61029), connection.Ports![0].Mappings.Single());
        Assert.Equal($"docker exec -it --user root {project}-helper-1 bash -l", asRoot.Command);
        Assert.Null(asRoot.VscodeCommand);
        Assert.Contains(compose, c => c.Command.SequenceEqual(["ps", "--format", "json"]));
        Assert.Equal(["inspect", "--format", "{{json .NetworkSettings.Ports}}", $"{project}-default-1"], runner.Requests.First(r => r.Arguments.Contains("{{json .NetworkSettings.Ports}}")).Arguments);
    }

    [Fact]
    public async Task x_default_marks_the_default_service_and_a_file_without_one_fails_and_is_brought_down()
    {
        var marked = ComposeFile("marked", "services:\n  web:\n    image: nginx\n    x-default: true\n  worker:\n    image: busybox\n");
        var unmarked = ComposeFile("unmarked", "services:\n  web:\n    image: nginx\n  worker:\n    image: busybox\n");
        var (runner, _, provider) = Provider(DockerComposeMode.Auto, "web", "worker");

        var environments = await provider.SampleInitAsync("marked", marked, Metadata());
        runner.Requests.Clear();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SampleInitAsync("unmarked", unmarked, Metadata()));

        Assert.Equal("web", ComposeConfig.Load(marked).DefaultService);
        Assert.False(ComposeConfig.Load(unmarked).TryGetDefaultService(out _));
        Assert.Equal(["web", "worker"], environments.Environments.Keys);
        Assert.Equal("web", ((DockerComposeSandboxEnvironment)environments.Default).Service);
        Assert.Contains("No 'default' service found", ex.Message);
        Assert.Contains(Compose(runner), c => c.Command.SequenceEqual(["down", "--volumes"]));
    }

    [Fact]
    public async Task cleanup_false_keeps_the_project_and_task_cleanup_lists_then_downs_it()
    {
        var (runner, _, provider) = Provider(DockerComposeMode.Always);
        var environments = await provider.SampleInitAsync("keep", Image, Metadata());
        var sandbox = (DockerComposeSandboxEnvironment)environments.Default;
        var project = sandbox.Project;

        await environments.Cleanup!(false);
        var downsAfterKeep = runner.Requests.Count(r => r.Arguments.Contains("down"));
        await provider.TaskCleanupAsync("keep", Image, cleanup: false);
        var downsAfterList = runner.Requests.Count(r => r.Arguments.Contains("down"));
        var fileAfterList = File.Exists(project.ConfigFile!);
        await provider.TaskCleanupAsync("keep", Image, cleanup: true);

        Assert.Equal(0, downsAfterKeep);
        Assert.Contains(ProviderLogger.Infos, m => m.Contains(sandbox.ContainerName, StringComparison.Ordinal) && m.Contains("kept", StringComparison.Ordinal));
        Assert.Equal(0, downsAfterList);
        Assert.Contains(Compose(runner), c => c.Command.SequenceEqual(["ps", "--format", "json", "--all"]));
        Assert.Contains(ProviderLogger.Infos, m => m.Contains("not yet cleaned up", StringComparison.Ordinal) && m.Contains(sandbox.ContainerName, StringComparison.Ordinal));
        Assert.False(fileAfterList);
        Assert.Equal(["compose", "--ansi", "never", "--project-name", project.Name, "-f", project.ConfigFile!, "down", "--volumes"], runner.Requests.Single(r => r.Arguments.Contains("down")).Arguments);
    }

    [Fact]
    public async Task no_services_started_reports_compose_stderr_and_downs_the_project()
    {
        var (runner, script, provider) = Provider(DockerComposeMode.Always);
        script.UpResult = ProcessResult.Failed(1, stderr: "service \"default\" didn't complete successfully\n");
        script.NoneRunning = true;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SampleInitAsync("fail", Image, Metadata()));

        Assert.StartsWith("No services started.", ex.Message);
        Assert.Contains("didn't complete successfully", ex.Message);
        Assert.Equal(["down", "--volumes"], Compose(runner)[^2].Command);
        Assert.Equal(["config", "--images"], Compose(runner)[^1].Command);
    }

    [Fact]
    public async Task auto_mode_routes_only_compose_shaped_configs_never_refuses_them_and_always_takes_everything()
    {
        var composeDir = Path.GetDirectoryName(ComposeFile("routed", "services:\n  default:\n    image: busybox\n"))!;
        var dockerfileDir = Dir("plain");
        File.WriteAllText(Path.Combine(dockerfileDir, "Dockerfile"), "FROM scratch\n");
        var (runner, _, auto) = Provider(DockerComposeMode.Auto);
        var never = new DockerSandboxProvider(new ScriptedProcessRunner(), DockerComposeMode.Never);
        var always = new DockerSandboxProvider(new ScriptedProcessRunner(), DockerComposeMode.Always);

        await auto.SampleInitAsync("plain", "busybox", Metadata());

        Assert.True(auto.UsesCompose(composeDir));
        Assert.True(auto.UsesCompose(Path.Combine(composeDir, "compose.yaml")));
        Assert.False(auto.UsesCompose("busybox"));
        Assert.False(auto.UsesCompose(dockerfileDir));
        Assert.False(auto.UsesCompose(null));
        Assert.Throws<ArgumentException>(() => never.UsesCompose(composeDir));
        Assert.False(never.UsesCompose("busybox"));
        Assert.True(always.UsesCompose("busybox"));
        Assert.True(always.UsesCompose(null));
        // an image under Auto still takes the bare docker path
        Assert.Contains(runner.Requests, r => r.Arguments[0] == "run");
        Assert.DoesNotContain(runner.Requests, r => r.Arguments[0] == "compose");
    }

    [Fact]
    public void compose_mode_comes_from_the_environment()
    {
        using var env = new EnvVarScope();

        env.Set(DockerComposeModes.EnvironmentVariable, "always");
        var always = DockerComposeModes.FromEnvironment();
        env.Set(DockerComposeModes.EnvironmentVariable, "Never");
        var never = DockerComposeModes.FromEnvironment();
        env.Set(DockerComposeModes.EnvironmentVariable, "bogus");
        var bogus = DockerComposeModes.FromEnvironment();
        env.Set(DockerComposeModes.EnvironmentVariable, null);
        var unset = DockerComposeModes.FromEnvironment();

        Assert.Equal(DockerComposeMode.Always, always);
        Assert.Equal(DockerComposeMode.Never, never);
        Assert.Equal(DockerComposeMode.Auto, bogus);
        Assert.Equal(DockerComposeMode.Auto, unset);
        Assert.Contains(ProviderLogger.Warnings, m => m.Contains("bogus", StringComparison.Ordinal));
    }

    [Fact]
    public async Task sample_metadata_variables_the_compose_file_mentions_are_forwarded_to_compose()
    {
        var path = ComposeFile("meta", "services:\n  default:\n    image: ${SAMPLE_METADATA_IMAGE}\n    environment:\n      - TOKEN=${SAMPLE_METADATA_API_TOKEN}\n");
        var (runner, _, provider) = Provider();
        var metadata = new Dictionary<string, string> { ["image"] = "busybox", ["api token"] = "secret", ["unused"] = "x" };

        var environments = await provider.SampleInitAsync("meta", path, metadata);
        var up = runner.Requests.Single(r => r.Arguments.Contains("up"));

        Assert.Equal(new Dictionary<string, string> { ["SAMPLE_METADATA_IMAGE"] = "busybox", ["SAMPLE_METADATA_API_TOKEN"] = "secret" }, up.Environment);
        Assert.Equal(up.Environment, ((DockerComposeSandboxEnvironment)environments.Default).Project.Env);
        Assert.Empty(DockerComposeSandbox.ResolveConfigEnvironment(path, new Dictionary<string, string> { ["other"] = "y" }));
        Assert.Empty(DockerComposeSandbox.ResolveConfigEnvironment(null, metadata));
    }

    [Fact]
    public async Task task_init_reports_a_failed_build_as_a_prerequisite_error()
    {
        var path = ComposeFile("broken", "services:\n  default:\n    build: .\n");
        var (_, script, provider) = Provider(DockerComposeMode.Auto);
        script.BuildResult = ProcessResult.Failed(1, stderr: "failed to solve: dockerfile parse error\n");

        var ex = await Assert.ThrowsAsync<PrerequisiteError>(() => provider.TaskInitAsync("broken", path));

        Assert.StartsWith("Failed to build docker containers", ex.Message);
        Assert.Contains("dockerfile parse error", ex.Message);
    }

    [Fact]
    public async Task compose_environments_get_the_docker_polling_interval()
    {
        var path = ComposeFile("poll", "services:\n  default:\n    image: busybox\n");
        var (_, _, provider) = Provider(DockerComposeMode.Auto);

        var environments = await provider.SampleInitAsync("poll", path, Metadata());

        Assert.IsType<DockerComposeSandboxEnvironment>(environments.Default);
        Assert.Equal(SandboxService.DockerPollingInterval, SandboxService.DefaultPollingInterval(environments.Default));
        Assert.Equal(SandboxService.DefaultHostPollingInterval, SandboxService.DefaultPollingInterval(new FakeSandboxEnvironment()));
    }

    [Fact]
    public async Task task_init_rejects_an_explicit_container_name()
    {
        var path = ComposeFile("named", "services:\n  default:\n    image: busybox\n    container_name: fixed\n");
        var (_, _, provider) = Provider();

        var ex = await Assert.ThrowsAsync<PrerequisiteError>(() => provider.TaskInitAsync("named", path));

        Assert.Contains("container_name ('fixed')", ex.Message);
    }

    [Fact]
    public async Task copies_go_through_compose_cp_and_map_its_errors()
    {
        var (runner, _, provider) = Provider(DockerComposeMode.Always);
        var environments = await provider.SampleInitAsync("cp", Image, Metadata());
        var sandbox = (DockerComposeSandboxEnvironment)environments.Default;
        var host = Path.Combine(_root, "blob.bin");
        await File.WriteAllBytesAsync(host, [1, 2, 3]);
        runner.Requests.Clear();

        await sandbox.CopyToContainerAsync(host, "opt/blob.bin");
        runner.Queued.Enqueue(ProcessResult.Ok());
        await sandbox.CopyFromContainerAsync("/etc/hostname", Path.Combine(_root, "hostname"));
        runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "Error response from daemon: Could not find the file /nope in container abc\n"));
        runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "Error: permission denied\n"));
        runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "cannot copy directory\n"));
        runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "something else\n"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => sandbox.CopyFromContainerAsync("/nope", host));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sandbox.CopyFromContainerAsync("/etc/shadow", host));
        await Assert.ThrowsAsync<IOException>(() => sandbox.CopyFromContainerAsync("/etc", host));
        var other = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.CopyFromContainerAsync("/x", host));

        var project = sandbox.Project;
        Assert.Equal(["exec", sandbox.ContainerName, "mkdir", "-p", "/workspace/opt"], runner.Requests[0].Arguments);
        Assert.Equal(["compose", "--project-name", project.Name, "-f", project.ConfigFile!, "cp", "-L", "--", host, "default:/workspace/opt/blob.bin"], runner.Requests[1].Arguments);
        Assert.Equal(TimeSpan.FromSeconds(600), runner.Requests[1].Timeout);
        Assert.Equal(["cp", "-L", "--", "default:/etc/hostname", Path.Combine(_root, "hostname")], ComposeArgs(runner.Requests[2]).Command);
        Assert.Equal(SandboxLimits.MaxReadFileSize, runner.Requests[2].OutputLimit);
        Assert.Contains("something else", other.Message);
    }

    [Fact]
    public async Task hung_compose_commands_are_retried_with_shorter_timeouts_then_time_out()
    {
        var runner = new ScriptedProcessRunner();
        var cli = new ComposeCli(runner);
        var project = new ComposeProject("inspect-t-iabcdef", null, "t");
        for (var i = 0; i < 3; i++)
        {
            runner.Queued.Enqueue(new ProcessResult(-1, [], "partial"u8.ToArray(), 0, 7, TimedOut: true));
        }

        var ex = await Assert.ThrowsAsync<SandboxTimeoutException>(() => cli.PsAsync(project));

        Assert.Equal([TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)], runner.Requests.Select(r => r.Timeout!.Value));
        Assert.All(runner.Requests, r => Assert.Equal(["compose", "--project-name", "inspect-t-iabcdef", "ps", "--format", "json"], r.Arguments));
        Assert.Contains("timed out after 300 seconds", ex.Message);
        Assert.Equal("partial", ex.TruncatedOutput);
        Assert.Equal(2, ProviderLogger.Infos.Count(m => m.StartsWith("Retrying docker compose command", StringComparison.Ordinal)));
    }

    [Fact]
    public void compose_ps_json_is_read_in_both_cli_shapes_and_check_running_needs_every_service()
    {
        const string running = """{"ID":"a","Name":"p-default-1","Service":"default","State":"running","Health":"","ExitCode":0}""";
        const string exited = """{"ID":"b","Name":"p-init-1","Service":"init","State":"exited","ExitCode":0}""";

        var lines = ComposeCli.ParsePs(running + "\n" + exited + "\n");
        var array = ComposeCli.ParsePs("[" + running + "," + exited + "]");

        Assert.Equal(lines, array);
        Assert.Equal(new ComposeContainer("a", "p-default-1", "default", "running", 0, ""), lines[0]);
        Assert.Equal(new ComposeContainer("b", "p-init-1", "init", "exited", 0), lines[1]);
        Assert.Empty(ComposeCli.ParsePs("  \n"));
    }

    [Fact]
    public async Task check_running_accepts_services_that_exited_successfully_and_rejects_partial_starts()
    {
        var runner = new ScriptedProcessRunner();
        var cli = new ComposeCli(runner);
        var project = new ComposeProject("inspect-t-iabcdef", null, "t");
        const string running = """{"ID":"a","Name":"p-default-1","Service":"default","State":"running","ExitCode":0}""";
        runner.Queued.Enqueue(ProcessResult.Ok(running + "\n"));
        runner.Queued.Enqueue(ProcessResult.Ok("""{"ID":"b","Name":"p-init-1","Service":"init","State":"exited","ExitCode":0}""" + "\n"));
        runner.Queued.Enqueue(ProcessResult.Ok(running + "\n"));
        runner.Queued.Enqueue(ProcessResult.Ok("""{"ID":"b","Name":"p-init-1","Service":"init","State":"exited","ExitCode":1}""" + "\n"));

        var both = await cli.CheckRunningAsync(["default", "init"], project);
        var failed = await cli.CheckRunningAsync(["default", "init"], project);

        Assert.Equal(["default"], both.Select(c => c.Service));
        Assert.Empty(failed);
    }

    [Theory]
    [InlineData("1h30m", 5400)]
    [InlineData("1.5s", 1.5)]
    [InlineData("500ms", 0.5)]
    [InlineData("", 0)]
    public void durations_parse_like_go(string text, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), ComposeHealthchecks.ParseDuration(text));
    }

    [Fact]
    public void healthcheck_time_follows_docker_defaults_and_the_examples_schedule()
    {
        var defaults = ComposeConfig.Parse("services:\n  a:\n    image: x\n    healthcheck:\n      test: [\"CMD\", \"true\"]\n").Services["a"];
        var dind = ComposeConfig.Parse(Examples.EvalsInEval).Services["docker"];

        Assert.Equal(3 * (30 + 30), ComposeHealthchecks.ServiceHealthcheckTime(defaults));
        Assert.Equal(10 * (5 + 5), ComposeHealthchecks.ServiceHealthcheckTime(dind));
        Assert.Equal(0, ComposeHealthchecks.ServiceHealthcheckTime(ComposeConfig.Parse(Examples.Browser).Services["default"]));
        Assert.Throws<FormatException>(() => ComposeHealthchecks.ParseDuration("soon"));
    }

    [Fact]
    public void task_project_names_satisfy_docker_constraints()
    {
        var name = ComposeFiles.TaskProjectName("My Task_Name!! with a very long name");

        Assert.Matches("^inspect-my-task_name-i[23456789abcdefghjkmnpqrstuvwxyz]{6}$", name);
        Assert.True(ComposeFiles.IsInspectProject(name));
        Assert.StartsWith("inspect-task-i", ComposeFiles.TaskProjectName(""));
        Assert.StartsWith("inspect---i", ComposeFiles.TaskProjectName("!!!"));
        Assert.False(ComposeFiles.IsInspectProject("inspect-swe-0123456789ab"));
    }

    [Fact]
    public void browser_example_parses()
    {
        var config = ComposeConfig.Parse(Examples.Browser);
        var service = config.Services["default"];

        Assert.Equal("aisiuk/inspect-tool-support", service.Image);
        Assert.True(service.Init);
        Assert.Null(service.NetworkMode);
        Assert.Equal("default", config.DefaultService);
    }

    [Theory]
    [InlineData(nameof(Examples.Computer))]
    [InlineData(nameof(Examples.InterventionComputer))]
    public void computer_examples_publish_loopback_vnc_ports(string example)
    {
        var service = ComposeConfig.Parse(Examples.Text(example)).Services["default"];

        Assert.Equal("aisiuk/inspect-computer-tool", service.Image);
        Assert.True(service.Init);
        Assert.Equal(["127.0.0.1::5900", "127.0.0.1::6080"], service.Ports);
    }

    [Fact]
    public void human_and_intervention_shell_examples_build_the_dockerfile_with_limits()
    {
        var human = ComposeConfig.Parse(Examples.Human).Services["default"];
        var shell = ComposeConfig.Parse(Examples.InterventionShell).Services["default"];

        Assert.Equal(new ComposeBuild("."), human.Build);
        Assert.Equal(["tail", "-f", "/dev/null"], human.Command);
        Assert.Equal("none", human.NetworkMode);
        Assert.Equal(new ComposeBuild("."), shell.Build);
        Assert.Equal("1", shell.CpuLimit);
        Assert.Equal("2.0gb", shell.MemoryLimit);
        Assert.Null(shell.NetworkMode);
    }

    [Fact]
    public void skills_example_keeps_an_internal_default_network()
    {
        var config = ComposeConfig.Parse(Examples.Skills);

        Assert.Equal("ubuntu:24.04", config.Services["default"].Image);
        Assert.True(config.Services["default"].Init);
        Assert.True(config.Networks["default"].Internal);
    }

    [Fact]
    public void http_proxy_example_carries_environment_volumes_and_no_network()
    {
        var service = ComposeConfig.Parse(Examples.HttpProxy).Services["default"];

        Assert.Equal(".", service.Build!.Context);
        Assert.Equal("Dockerfile", service.Build.Dockerfile);
        Assert.Equal("none", service.NetworkMode);
        Assert.Equal("http://localhost:8080", service.Environment["HTTP_PROXY"]);
        Assert.Equal("localhost,127.0.0.1", service.Environment["NO_PROXY"]);
        Assert.Equal(["./remap.py:/remap.py:ro"], service.Volumes);
    }

    [Fact]
    public void multi_tool_example_is_the_tool_support_image()
    {
        var service = ComposeConfig.Parse(Examples.InterventionMultiTool).Services["default"];

        Assert.Equal("aisiuk/inspect-tool-support", service.Image);
        Assert.True(service.Init);
    }

    [Fact]
    public void evals_in_eval_example_has_a_privileged_dind_sidecar_with_a_healthcheck()
    {
        var config = ComposeConfig.Parse(Examples.EvalsInEval);
        var agent = config.Services["default"];
        var dind = config.Services["docker"];

        Assert.Equal(["default", "docker"], config.Services.Keys);
        Assert.Equal("Dockerfile", agent.Build!.Dockerfile);
        Assert.Equal("tcp://docker:2375", agent.Environment["DOCKER_HOST"]);
        Assert.Equal("/workspace", agent.WorkingDir);
        Assert.Equal(["docker"], agent.DependsOn);
        Assert.Equal("docker:dind-rootless", dind.Image);
        Assert.True(dind.Privileged);
        Assert.Equal("", dind.Environment["DOCKER_TLS_CERTDIR"]);
        Assert.Equal(["CMD", "docker", "--host=tcp://localhost:2375", "info"], dind.Healthcheck!.Test);
        Assert.Equal("5s", dind.Healthcheck.Interval);
        Assert.Equal(10L, dind.Healthcheck.Retries);
        Assert.Equal("default", config.DefaultService);
    }

    [Fact]
    public async Task evals_in_eval_compose_up_waits_for_the_healthcheck_schedule()
    {
        var path = ComposeFile("dind", Examples.EvalsInEval);
        var (runner, _, provider) = Provider(DockerComposeMode.Auto, "default", "docker");

        await provider.SampleInitAsync("dind", path, Metadata());

        var up = runner.Requests.Single(r => r.Arguments.Contains("up"));
        Assert.Equal(["up", "--detach", "--wait", "--wait-timeout", "101"], ComposeArgs(up).Command);
        Assert.Equal(TimeSpan.FromSeconds(100), up.Timeout);
    }

    /// <summary>The examples' compose files (<c>inspect_ai/examples/*/compose.yaml</c>) verbatim, comments included.</summary>
    private static class Examples
    {
        public static string Text(string name) => name switch
        {
            nameof(Computer) => Computer,
            nameof(InterventionComputer) => InterventionComputer,
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        public const string Browser = """
            services:
              default:
                image: aisiuk/inspect-tool-support
                init: true
                # network_mode is omitted because this example browses external websites.
            """;

        public const string Computer = """
            services:
              default:
                image: aisiuk/inspect-computer-tool
                init: true

                # network_mode is omitted because container networking is required for the
                # VNC port mappings. This also permits Internet access, although the tasks
                # do not require it.
                # You can VNC into each container by using the following port mapping, which
                # dynamically binds to loopback host ports. Find the specific bindings with
                # `docker inspect <container_id_or_name>`. This info is also included in the
                # Running Samples tab. The output will look something like:
                #
                #  service   container   host     url
                #  VNC       5900        61029    vnc://localhost:61029
                #  noVNC     6080        61030    http://localhost:61030?view_only=true&autoconnect=true

                ports:
                  - "127.0.0.1::5900"
                  - "127.0.0.1::6080"
            """;

        public const string Human = """
            services:
              default:
                build: .
                command: tail -f /dev/null
                network_mode: none
            """;

        public const string Skills = """
            services:
              default:
                image: ubuntu:24.04
                command: tail -f /dev/null
                init: true

            # Keep an interface for the network-info task without permitting Internet access.
            networks:
              default:
                internal: true
            """;

        public const string HttpProxy = """
            services:
              default:
                build:
                  context: .
                  dockerfile: Dockerfile
                init: true
                command: tail -f /dev/null
                # Contain the agent by disabling ordinary container network egress.
                # Loopback remains available to mitmproxy and sandbox_agent_bridge.
                network_mode: none
                environment:
                  # Direct compatible HTTP clients through the local mitmproxy.
                  - HTTP_PROXY=http://localhost:8080
                  - HTTPS_PROXY=http://localhost:8080
                  # Don't proxy localhost (used by sandbox_agent_bridge on port 13131)
                  - NO_PROXY=localhost,127.0.0.1
                volumes:
                  - ./remap.py:/remap.py:ro
            """;

        public const string InterventionShell = """
            services:
              default:
                build: .
                command: tail -f /dev/null
                cpus: 1.0
                mem_limit: 2.0gb
                # network_mode is omitted to permit Internet access for this general-purpose
                # agent.
            """;

        public const string InterventionComputer = """
            services:
              default:
                image: aisiuk/inspect-computer-tool
                init: true
                # network_mode is omitted to permit Internet access for this general-purpose
                # agent and to support the VNC port mappings.
                # Dynamically assign loopback host ports for vnc and novnc
                ports:
                  - "127.0.0.1::5900"
                  - "127.0.0.1::6080"
            """;

        public const string InterventionMultiTool = """
            services:
              default:
                image: aisiuk/inspect-tool-support
                init: true
                # network_mode is omitted because this agent uses web browser tools.
            """;

        public const string EvalsInEval = """
            services:
              # Both services intentionally use Compose's default network. They must
              # communicate with each other, and the nested Docker daemon requires Internet
              # access to pull sandbox images used by the inner evaluations.
              default:
                build:
                  context: .
                  dockerfile: Dockerfile
                environment:
                  # Connect to the dind sidecar's Docker daemon
                  - DOCKER_HOST=tcp://docker:2375
                working_dir: /workspace
                command: tail -f /dev/null
                depends_on:
                  docker:
                    condition: service_healthy

              docker:
                # Rootless Docker limits control of the nested daemon to an unprivileged
                # user in this sidecar. The sidecar still requires privileged mode, so this
                # example is not suitable for adversarial or untrusted agents (see README).
                image: docker:dind-rootless
                privileged: true
                environment:
                  # Disable TLS for the connection confined to this Compose network.
                  # The evaluated agent intentionally receives unauthenticated daemon access.
                  - DOCKER_TLS_CERTDIR=
                healthcheck:
                  test: ["CMD", "docker", "--host=tcp://localhost:2375", "info"]
                  interval: 5s
                  timeout: 5s
                  retries: 10
            """;
    }
}
