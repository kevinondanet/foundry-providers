using System.Text;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Sandbox.Docker;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tests;

/// <summary>Records every process the docker CLI wrapper would run and answers from a queue (then a responder), so argv and error mapping are checked without Docker.</summary>
internal sealed class ScriptedProcessRunner : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = [];

    public Queue<ProcessResult> Queued { get; } = new();

    /// <summary>Answers requests once the queue is empty; defaults to a successful, silent process.</summary>
    public Func<ProcessRequest, ProcessResult> Responder { get; set; } = _ => ProcessResult.Ok();

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Queued.Count > 0 ? Queued.Dequeue() : Responder(request));
    }

    public static ProcessResult ByVerb(ProcessRequest request, string verb, ProcessResult result) =>
        request.Arguments[0] == verb ? result : ProcessResult.Ok();
}

/// <summary>Argv construction and result classification of the docker provider (port of <c>util/_sandbox/docker/</c>), exercised through a scripted process runner without Docker.</summary>
public class DockerCliTests
{
    private const string Container = "inspect-swe-0123456789ab";

    private static (ScriptedProcessRunner Runner, DockerCli Cli) Cli()
    {
        var runner = new ScriptedProcessRunner();
        return (runner, new DockerCli(runner));
    }

    private static (ScriptedProcessRunner Runner, DockerSandboxEnvironment Sandbox) Sandbox(string workingDirectory = "/work")
    {
        var (runner, cli) = Cli();
        // Exec paths that end in a signal exit confirm the container is alive with `docker inspect`.
        runner.Responder = request => ScriptedProcessRunner.ByVerb(request, "inspect", ProcessResult.Ok("true\n"));
        return (runner, new DockerSandboxEnvironment(cli, Container, workingDirectory));
    }

    [Fact]
    public async Task run_builds_the_detached_argv_with_the_host_gateway_and_returns_the_id()
    {
        var (runner, cli) = Cli();
        runner.Queued.Enqueue(ProcessResult.Ok("abcdef123456\n"));

        var id = await cli.RunDetachedAsync("python:3.12-slim-bookworm", Container, ["sleep", "infinity"]);

        Assert.Equal("abcdef123456", id);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("docker", request.FileName);
        Assert.Equal(
            ["run", "-d", "--init", "--name", Container, "--add-host", "host.docker.internal:host-gateway", "python:3.12-slim-bookworm", "sleep", "infinity"],
            request.Arguments);
        Assert.Null(request.Input);
        // docker run may pull the image implicitly, so it gets no host-side guard.
        Assert.Null(request.Timeout);
    }

    [Fact]
    public async Task exec_builds_user_cwd_env_and_stdin_flags_in_docker_order()
    {
        var (runner, cli) = Cli();
        var env = new Dictionary<string, string> { ["A"] = "1", ["B"] = "two words" };

        await cli.ExecAsync(Container, ["echo", "hi there"], input: "in"u8.ToArray(), cwd: "/tmp", env: env, user: "root", hostTimeout: TimeSpan.FromSeconds(5));

        var request = Assert.Single(runner.Requests);
        // values travel through the CLI's environment (a bare -e NAME forwards them), never on the command line
        Assert.Equal(["exec", "-u", "root", "-w", "/tmp", "-e", "A", "-e", "B", "-i", Container, "echo", "hi there"], request.Arguments);
        Assert.Equal(env, request.Environment);
        Assert.Equal("in", Encoding.UTF8.GetString(request.Input!.Value.Span));
        Assert.Equal(TimeSpan.FromSeconds(5), request.Timeout);
        Assert.False(request.AbortOnOutputLimit);
    }

    [Fact]
    public async Task exec_without_options_is_the_bare_argv_and_returns_the_raw_result()
    {
        var (runner, cli) = Cli();
        runner.Queued.Enqueue(ProcessResult.Failed(3, stderr: "boom"));

        var result = await cli.ExecAsync(Container, ["false"]);

        Assert.Equal(["exec", Container, "false"], runner.Requests[0].Arguments);
        Assert.Null(runner.Requests[0].Input);
        Assert.Null(runner.Requests[0].Timeout);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("boom", result.StderrText);
    }

    [Fact]
    public async Task build_pull_inspect_rm_and_cp_build_their_argv()
    {
        var (runner, cli) = Cli();
        runner.Queued.Enqueue(ProcessResult.Ok());
        runner.Queued.Enqueue(ProcessResult.Ok());
        runner.Queued.Enqueue(ProcessResult.Ok("/workspace\n"));

        await cli.BuildAsync("inspect-swe-sandbox:abc", "/ctx", "/ctx/Dockerfile");
        await cli.PullAsync("python:3.12-slim-bookworm");
        var workdir = await cli.InspectAsync(Container, "{{.Config.WorkingDir}}");
        await cli.RemoveContainerAsync(Container);
        await cli.CopyAsync("/host/blob.bin", $"{Container}:/opt/blob.bin");
        var exists = await cli.ImageExistsAsync("python:3.12-slim-bookworm");

        Assert.Equal("/workspace", workdir);
        Assert.True(exists);
        Assert.Equal(["build", "-t", "inspect-swe-sandbox:abc", "-f", "/ctx/Dockerfile", "/ctx"], runner.Requests[0].Arguments);
        Assert.Null(runner.Requests[0].Timeout);
        Assert.Equal(["pull", "python:3.12-slim-bookworm"], runner.Requests[1].Arguments);
        Assert.Equal(["inspect", "--format", "{{.Config.WorkingDir}}", Container], runner.Requests[2].Arguments);
        Assert.Equal(["rm", "-f", Container], runner.Requests[3].Arguments);
        Assert.Equal(["cp", "/host/blob.bin", $"{Container}:/opt/blob.bin"], runner.Requests[4].Arguments);
        Assert.Equal(["image", "inspect", "--format", "{{.Id}}", "python:3.12-slim-bookworm"], runner.Requests[5].Arguments);
    }

    [Fact]
    public async Task management_command_failures_and_timeouts_become_sandbox_unavailable_with_stderr()
    {
        var (runner, cli) = Cli();
        runner.Queued.Enqueue(ProcessResult.Failed(125, stderr: "docker: Error response from daemon: pull access denied for nope\n"));
        runner.Queued.Enqueue(new ProcessResult(-1, [], [], 0, 0, TimedOut: true));
        runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "Cannot connect to the Docker daemon"));

        var run = await Assert.ThrowsAsync<SandboxUnavailableException>(() => cli.RunDetachedAsync("nope", Container, ["sleep", "infinity"]));
        var timeout = await Assert.ThrowsAsync<SandboxUnavailableException>(() => cli.RemoveContainerAsync(Container));
        var missing = await cli.ImageExistsAsync("nope");

        Assert.Contains("docker run failed with exit code 125", run.Message);
        Assert.Contains("pull access denied for nope", run.Message);
        Assert.Contains("docker rm did not complete", timeout.Message);
        Assert.False(missing);
    }

    [Fact]
    public async Task missing_docker_binary_is_sandbox_unavailable()
    {
        var cli = new DockerCli(new ProcessRunner(), executable: "definitely-not-docker-xyz");

        var ex = await Assert.ThrowsAsync<SandboxUnavailableException>(() => cli.RemoveContainerAsync(Container));

        Assert.Contains("definitely-not-docker-xyz", ex.Message);
    }

    [Fact]
    public async Task exec_wraps_the_command_in_an_in_container_kill_timeout_with_host_slack()
    {
        var (runner, sandbox) = Sandbox();

        var result = await sandbox.ExecAsync(["bash", "-c", "echo hi"], timeout: TimeSpan.FromSeconds(2.5));

        Assert.True(result.Success);
        var request = runner.Requests[0];
        Assert.Equal(["exec", Container, "timeout", "-s", "KILL", "2.5", "bash", "-c", "echo hi"], request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(2.5) + DockerSandboxEnvironment.HostTimeoutSlack, request.Timeout);
        Assert.Equal(SandboxLimits.MaxExecOutputSize, request.OutputLimit);
    }

    [Fact]
    public async Task exec_resolves_relative_cwd_under_the_working_directory_and_rejects_empty_cmd()
    {
        var (runner, sandbox) = Sandbox("/work");

        await sandbox.ExecAsync(["pwd"], cwd: "sub");
        await sandbox.ExecAsync(["pwd"], cwd: "/abs");
        await Assert.ThrowsAsync<ArgumentException>(() => sandbox.ExecAsync([]));

        Assert.Equal(["exec", "-w", "/work/sub", Container, "pwd"], runner.Requests[0].Arguments);
        Assert.Equal(["exec", "-w", "/abs", Container, "pwd"], runner.Requests[1].Arguments);
    }

    [Fact]
    public async Task exec_maps_timeout_exit_codes_to_sandbox_timeout_with_the_captured_output()
    {
        var (runner, sandbox) = Sandbox();
        runner.Queued.Enqueue(ProcessResult.FromText(124, "partial\n", ""));
        runner.Queued.Enqueue(new ProcessResult(137, "late"u8.ToArray(), "killed"u8.ToArray(), 4, 6, TimedOut: true));

        var inContainer = await Assert.ThrowsAsync<SandboxTimeoutException>(() => sandbox.ExecAsync(["sleep", "20"], timeout: TimeSpan.FromSeconds(1)));
        var onHost = await Assert.ThrowsAsync<SandboxTimeoutException>(() => sandbox.ExecAsync(["sleep", "20"], timeout: TimeSpan.FromSeconds(1)));

        Assert.Equal("partial\n", inContainer.TruncatedOutput);
        Assert.Contains("timed out after 1 seconds", inContainer.Message);
        Assert.Equal("late\nkilled", onHost.TruncatedOutput);
        Assert.Contains("docker exec did not return", onHost.Message);
    }

    [Fact]
    public async Task exec_signal_exit_code_before_the_timeout_elapses_is_an_ordinary_result()
    {
        var (runner, sandbox) = Sandbox();
        runner.Queued.Enqueue(ProcessResult.Failed(137));

        var result = await sandbox.ExecAsync(["python3", "eat-memory.py"], timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(137, result.ReturnCode);
        Assert.False(result.Success);
        Assert.Equal("inspect", runner.Requests[1].Arguments[0]);
    }

    [Fact]
    public async Task exec_silent_signal_exit_of_a_dead_container_is_sandbox_unavailable()
    {
        var (runner, sandbox) = Sandbox();
        runner.Queued.Enqueue(ProcessResult.Failed(137));
        runner.Queued.Enqueue(ProcessResult.Ok("false\n"));

        var ex = await Assert.ThrowsAsync<SandboxUnavailableException>(() => sandbox.ExecAsync(["bash", "-c", "true"]));

        Assert.Contains("has exited", ex.Message);
        Assert.Equal(["inspect", "--format", "{{.State.Running}}", Container], runner.Requests[1].Arguments);
    }

    [Theory]
    [InlineData(1, "", "Error response from daemon: container abc is not running\n")]
    [InlineData(1, "", "Error response from daemon: No such container: inspect-swe-0123456789ab\n")]
    [InlineData(1, "", "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?\n")]
    public async Task exec_daemon_errors_are_sandbox_unavailable(int exitCode, string stdout, string stderr)
    {
        var (runner, sandbox) = Sandbox();
        runner.Queued.Enqueue(ProcessResult.FromText(exitCode, stdout, stderr));

        var ex = await Assert.ThrowsAsync<SandboxUnavailableException>(() => sandbox.ExecAsync(["echo", "hi"]));

        Assert.Contains("The sandbox is not running", ex.Message);
        Assert.Contains(stderr.Trim(), ex.Message);
    }

    [Fact]
    public async Task exec_missing_caller_binary_is_a_failed_result_but_a_missing_timeout_wrapper_is_unavailable()
    {
        const string runcLine = "OCI runtime exec failed: exec failed: unable to start container process: exec: \"{0}\": executable file not found in $PATH\n";
        var (runner, sandbox) = Sandbox();
        runner.Queued.Enqueue(ProcessResult.FromText(127, string.Format(runcLine, "nope"), ""));
        runner.Queued.Enqueue(ProcessResult.FromText(127, string.Format(runcLine, "timeout"), ""));
        runner.Queued.Enqueue(ProcessResult.FromText(1, "Error response from daemon: container x is not running\nmore output\n", ""));

        var missing = await sandbox.ExecAsync(["nope"]);
        var wrapper = await Assert.ThrowsAsync<SandboxUnavailableException>(() => sandbox.ExecAsync(["nope"], timeout: TimeSpan.FromSeconds(1)));
        var twoLines = await sandbox.ExecAsync(["docker", "compose", "exec", "x", "true"]);

        Assert.Equal(127, missing.ReturnCode);
        Assert.Contains("\"nope\"", missing.Stdout);
        Assert.Contains("required execution machinery is unavailable", wrapper.Message);
        Assert.Equal(1, twoLines.ReturnCode);
    }

    [Fact]
    public void permission_denied_launch_failures_are_unauthorized_access()
    {
        var wrapper = new DockerFailures.InjectedWrapper("timeout", "./run.sh");

        var runc = DockerFailures.Classify(ProcessResult.FromText(126, "OCI runtime exec failed: exec failed: unable to start container process: exec: \"./run.sh\": permission denied: unknown\n", ""), null);
        var gnu = DockerFailures.Classify(ProcessResult.FromText(126, "", "timeout: failed to run command ‘./run.sh’: Permission denied\n"), wrapper);
        var other = DockerFailures.Classify(ProcessResult.FromText(126, "", "timeout: failed to run command ‘./other.sh’: Permission denied\n"), wrapper);

        Assert.IsType<UnauthorizedAccessException>(runc);
        Assert.IsType<UnauthorizedAccessException>(gnu);
        Assert.Null(other);
    }

    [Fact]
    public async Task write_file_streams_the_bytes_to_a_shell_that_creates_the_parent_directory()
    {
        var (runner, sandbox) = Sandbox("/work");
        byte[] blob = [0, 1, 2, 255, 254];

        await sandbox.WriteFileAsync("dir/blob.bin", blob);
        await sandbox.WriteFileAsync("/abs/text.txt", "a\r\nb");

        var request = runner.Requests[0];
        Assert.Equal(["exec", "-i", Container, "timeout", "-s", "KILL", "600", "sh", "-c", DockerSandboxEnvironment.WriteFileScript, "sh", "/work/dir/blob.bin"], request.Arguments);
        Assert.Equal(blob, request.Input!.Value.ToArray());
        Assert.Equal("/abs/text.txt", runner.Requests[1].Arguments[^1]);
        Assert.Equal("a\r\nb"u8.ToArray(), runner.Requests[1].Input!.Value.ToArray());
    }

    [Fact]
    public async Task write_file_failures_map_to_the_python_error_types()
    {
        var (runner, sandbox) = Sandbox();
        runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "sh: 1: cannot create /etc/x: Permission denied\n"));
        runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "sh: 1: cannot create /tmp: Is a directory\n"));
        runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "sh: something else\n"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sandbox.WriteFileAsync("/etc/x", "x"));
        await Assert.ThrowsAsync<IOException>(() => sandbox.WriteFileAsync("/tmp", "x"));
        var other = await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.WriteFileAsync("/x", "x"));

        Assert.Contains("something else", other.Message);
    }

    [Fact]
    public async Task read_file_cats_the_resolved_path_and_returns_the_bytes_exactly()
    {
        var (runner, sandbox) = Sandbox("/work");
        byte[] blob = [0, 1, 2, 255, 254];
        runner.Queued.Enqueue(new ProcessResult(0, blob, [], blob.Length, 0));
        runner.Queued.Enqueue(ProcessResult.Ok("a\r\nb\n"));

        var bytes = await sandbox.ReadFileBytesAsync("dir/blob.bin");
        var text = await sandbox.ReadFileAsync("/abs/text.txt");

        Assert.Equal(blob, bytes);
        Assert.Equal("a\r\nb\n", text);
        Assert.Equal(["exec", Container, "cat", "/work/dir/blob.bin"], runner.Requests[0].Arguments);
        Assert.True(runner.Requests[0].AbortOnOutputLimit);
        Assert.Equal(SandboxLimits.MaxReadFileSize, runner.Requests[0].OutputLimit);
        Assert.Equal(["exec", Container, "cat", "/abs/text.txt"], runner.Requests[1].Arguments);
    }

    [Fact]
    public async Task read_file_failures_map_to_the_python_error_types()
    {
        var (runner, sandbox) = Sandbox();
        runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "cat: /work/nope.txt: No such file or directory\n"));
        runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "cat: /etc/shadow: Permission denied\n"));
        runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "cat: /tmp: Is a directory\n"));
        runner.Queued.Enqueue(new ProcessResult(137, [], [], 11, 0, OutputLimitExceeded: true));

        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => sandbox.ReadFileAsync("nope.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sandbox.ReadFileAsync("/etc/shadow"));
        var directory = await Assert.ThrowsAsync<IOException>(() => sandbox.ReadFileAsync("/tmp"));
        var tooBig = await Assert.ThrowsAsync<OutputLimitExceededException>(() => sandbox.ReadFileBytesAsync("/big"));
        var alias = await Assert.ThrowsAsync<IOException>(() => sandbox.ReadFileAsync("dir/.."));

        Assert.Equal("nope.txt", missing.FileName);
        Assert.Contains("is a directory", directory.Message);
        Assert.Equal(SandboxLimits.HumanReadableSize(SandboxLimits.MaxReadFileSize), tooBig.LimitDescription);
        Assert.Contains("is a directory", alias.Message);
        Assert.Equal(4, runner.Requests.Count);
    }

    [Fact]
    public async Task copy_to_container_creates_the_parent_then_runs_docker_cp()
    {
        var (runner, sandbox) = Sandbox("/work");
        var host = Path.Combine(Path.GetTempPath(), $"inspect-cp-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(host, [1, 2, 3]);
        try
        {
            await sandbox.CopyToContainerAsync(host, "opt/blob.bin");
            await Assert.ThrowsAsync<FileNotFoundException>(() => sandbox.CopyToContainerAsync(host + ".missing", "/x"));
        }
        finally
        {
            File.Delete(host);
        }

        Assert.Equal(["exec", Container, "mkdir", "-p", "/work/opt"], runner.Requests[0].Arguments);
        Assert.Equal(["cp", host, $"{Container}:/work/opt/blob.bin"], runner.Requests[1].Arguments);
        Assert.Equal(2, runner.Requests.Count);
    }

    [Fact]
    public void container_path_resolves_relative_paths_under_the_working_directory()
    {
        var (_, root) = Sandbox("/");
        var (_, workspace) = Sandbox("/workspace/");
        var (_, empty) = Sandbox("");

        Assert.Equal("/a/b", root.ContainerPath("a/b"));
        Assert.Equal("/workspace/a", workspace.ContainerPath("a"));
        Assert.Equal("/abs", workspace.ContainerPath("/abs"));
        Assert.Equal("/", empty.WorkingDirectory);
        Assert.Equal("host.docker.internal", root.HostAddress);
    }

    [Fact]
    public async Task sample_init_starts_a_named_container_reads_its_workdir_and_cleanup_removes_it()
    {
        var (runner, cli) = Cli();
        runner.Responder = request => request.Arguments[0] switch
        {
            "run" => ProcessResult.Ok("containerid\n"),
            "inspect" => ProcessResult.Ok("/workspace\n"),
            _ => ProcessResult.Ok(),
        };
        var provider = new DockerSandboxProvider(cli);

        var environments = await provider.SampleInitAsync("task", null, new Dictionary<string, string>());
        var sandbox = Assert.IsType<DockerSandboxEnvironment>(environments.Default);
        await environments.Cleanup!(true);

        Assert.Equal("docker", provider.Type);
        Assert.Equal("default", environments.Environments.Keys.Single());
        Assert.StartsWith(DockerSandboxProvider.ContainerPrefix, sandbox.ContainerName);
        Assert.Equal(DockerSandboxProvider.ContainerPrefix.Length + 12, sandbox.ContainerName.Length);
        Assert.Equal("/workspace", sandbox.WorkingDirectory);
        Assert.Equal(["image", "inspect", "--format", "{{.Id}}", DockerSandboxProvider.DefaultImage], runner.Requests[0].Arguments);
        Assert.Equal(["run", "-d", "--init", "--name", sandbox.ContainerName, "--add-host", DockerCli.HostGatewayMapping, DockerSandboxProvider.DefaultImage, "sleep", "infinity"], runner.Requests[1].Arguments);
        Assert.Equal(["inspect", "--format", "{{.Config.WorkingDir}}", sandbox.ContainerName], runner.Requests[2].Arguments);
        Assert.Equal(["rm", "-f", sandbox.ContainerName], runner.Requests[3].Arguments);
        Assert.Equal(4, runner.Requests.Count);
    }

    [Fact]
    public async Task sample_cleanup_without_cleanup_keeps_the_container_and_logs_its_name()
    {
        var (runner, cli) = Cli();
        var provider = new DockerSandboxProvider(cli);
        var environments = await provider.SampleInitAsync("task", null, new Dictionary<string, string>());
        var name = ((DockerSandboxEnvironment)environments.Default).ContainerName;

        await environments.Cleanup!(false);

        Assert.DoesNotContain(runner.Requests, request => request.Arguments[0] == "rm");
        Assert.Contains(ProviderLogger.Infos, message => message.Contains(name, StringComparison.Ordinal) && message.Contains("kept", StringComparison.Ordinal));
    }

    [Fact]
    public async Task sample_init_falls_back_to_tail_when_the_image_has_no_sleep()
    {
        var (runner, cli) = Cli();
        var provider = new DockerSandboxProvider(cli);
        runner.Responder = request => request.Arguments is ["run", .., "sleep", "infinity"]
            ? ProcessResult.Failed(127, stderr: "docker: Error response from daemon: failed to create task for container: exec: \"sleep\": executable file not found in $PATH\n")
            : ProcessResult.Ok();

        var environments = await provider.SampleInitAsync("task", "busybox", new Dictionary<string, string>());
        var name = ((DockerSandboxEnvironment)environments.Default).ContainerName;

        Assert.Equal("/", environments.Default is DockerSandboxEnvironment env ? env.WorkingDirectory : null);
        Assert.Equal(["rm", "-f", name], runner.Requests[2].Arguments);
        Assert.Equal(["run", "-d", "--init", "--name", name, "--add-host", DockerCli.HostGatewayMapping, "busybox", "tail", "-f", "/dev/null"], runner.Requests[3].Arguments);
    }

    [Fact]
    public async Task sample_init_failure_removes_the_created_container_and_surfaces_stderr()
    {
        var (runner, cli) = Cli();
        var provider = new DockerSandboxProvider(cli);
        runner.Responder = request => request.Arguments[0] == "run"
            ? ProcessResult.Failed(125, stderr: "docker: Error response from daemon: something broke\n")
            : ProcessResult.Ok();

        var ex = await Assert.ThrowsAsync<SandboxUnavailableException>(() => provider.SampleInitAsync("task", null, new Dictionary<string, string>()));

        Assert.Contains("something broke", ex.Message);
        Assert.Equal("rm", runner.Requests[^1].Arguments[0]);
    }

    [Fact]
    public async Task task_init_pulls_a_missing_image_reference_and_skips_a_present_one()
    {
        var (runner, cli) = Cli();
        var provider = new DockerSandboxProvider(cli);
        runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "Error: No such image: ubuntu:24.04"));

        await provider.TaskInitAsync("task", "ubuntu:24.04");
        await provider.TaskInitAsync("task", "ubuntu:24.04");
        await provider.TaskCleanupAsync("task", "ubuntu:24.04", cleanup: true);

        Assert.Equal(["image", "inspect", "--format", "{{.Id}}", "ubuntu:24.04"], runner.Requests[0].Arguments);
        Assert.Equal(["pull", "ubuntu:24.04"], runner.Requests[1].Arguments);
        Assert.Equal(["image", "inspect", "--format", "{{.Id}}", "ubuntu:24.04"], runner.Requests[2].Arguments);
        Assert.Equal(3, runner.Requests.Count);
    }

    [Fact]
    public async Task task_init_builds_a_dockerfile_directory_tagged_by_content_hash()
    {
        var (runner, cli) = Cli();
        var provider = new DockerSandboxProvider(cli);
        var context = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(context, "sub"));
        var dockerfile = Path.Combine(context, "Dockerfile");
        await File.WriteAllTextAsync(dockerfile, "FROM scratch\n");
        await File.WriteAllTextAsync(Path.Combine(context, "sub", "data.txt"), "one");
        try
        {
            var hash = DockerImages.ContentHash(context);
            runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "Error: No such image"));
            await provider.TaskInitAsync("task", context);
            await File.WriteAllTextAsync(Path.Combine(context, "sub", "data.txt"), "two");
            var changed = DockerImages.ContentHash(context);
            runner.Queued.Enqueue(ProcessResult.Failed(1, stderr: "Error: No such image"));
            await provider.TaskInitAsync("task", dockerfile);

            Assert.Matches("^[0-9a-f]{12}$", hash);
            Assert.NotEqual(hash, changed);
            Assert.Equal(changed, DockerImages.Resolve(context).Image[(DockerImages.BuildRepository.Length + 1)..]);
            Assert.Equal(["image", "inspect", "--format", "{{.Id}}", $"inspect-swe-sandbox:{hash}"], runner.Requests[0].Arguments);
            Assert.Equal(["build", "-t", $"inspect-swe-sandbox:{hash}", "-f", dockerfile, context], runner.Requests[1].Arguments);
            Assert.Equal(["build", "-t", $"inspect-swe-sandbox:{changed}", "-f", dockerfile, context], runner.Requests[3].Arguments);
        }
        finally
        {
            Directory.Delete(context, recursive: true);
        }
    }

    [Fact]
    public void image_resolution_rejects_missing_dockerfiles_and_defaults_the_image()
    {
        var empty = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Throws<FileNotFoundException>(() => DockerImages.Resolve(empty));
            Assert.Throws<FileNotFoundException>(() => DockerImages.Resolve(Path.Combine(empty, "nope", "Dockerfile")));
            Assert.Equal(DockerImages.DefaultImage, DockerImages.Resolve(null).Image);
            Assert.Equal(DockerImages.DefaultImage, DockerImages.Resolve("  ").Image);
            Assert.False(DockerImages.Resolve("python:3.12-slim-bookworm").IsBuild);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void provider_registers_under_docker()
    {
        SandboxRegistry.Register(new DockerSandboxProvider());

        Assert.IsType<DockerSandboxProvider>(SandboxRegistry.Get("docker"));
        Assert.Contains("docker", SandboxRegistry.Types);
    }

    [Fact]
    public async Task sample_init_cancelled_after_the_container_was_created_still_removes_it()
    {
        var (runner, cli) = Cli();
        var provider = new DockerSandboxProvider(cli);
        runner.Responder = request => request.Arguments[0] switch
        {
            "run" => ProcessResult.Ok("containerid\n"),
            "inspect" => throw new OperationCanceledException(),
            _ => ProcessResult.Ok(),
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SampleInitAsync("task", null, new Dictionary<string, string>()));

        var name = runner.Requests.Single(r => r.Arguments[0] == "run").Arguments[4];
        Assert.Equal(["rm", "-f", name], runner.Requests[^1].Arguments);
    }

    [Fact]
    public void content_hash_ignores_git_metadata_and_follows_file_changes()
    {
        var context = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(context, ".git"));
        var dockerfile = Path.Combine(context, "Dockerfile");
        try
        {
            File.WriteAllText(dockerfile, "FROM scratch\n");
            var bare = DockerImages.ContentHash(context);
            File.WriteAllText(Path.Combine(context, ".git", "HEAD"), "ref: refs/heads/main\n");
            var withGit = DockerImages.ContentHash(context);
            File.WriteAllText(dockerfile, "FROM scratch\nRUN true\n");
            var changed = DockerImages.ContentHash(context);

            Assert.Equal(bare, withGit);
            Assert.NotEqual(bare, changed);
            Assert.Equal(changed, DockerImages.ContentHash(context));
        }
        finally
        {
            Directory.Delete(context, recursive: true);
        }
    }
}
