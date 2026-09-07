using System.Security.Cryptography;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Sandbox.Docker;

namespace InspectAzureAI.Eval.Tests;

/// <summary>One default-image container shared by every live docker test (created only when Docker answers, removed afterwards).</summary>
public sealed class DockerContainerFixture : IAsyncLifetime
{
    private SandboxEnvironments? _environments;

    public DockerSandboxProvider Provider { get; } = new();

    public DockerSandboxEnvironment Sandbox =>
        (DockerSandboxEnvironment)(_environments ?? throw new InvalidOperationException("Docker is not available.")).Default;

    public async Task InitializeAsync()
    {
        if (DockerProbe.Available)
        {
            _environments = await Provider.SampleInitAsync("docker-tests", null, new Dictionary<string, string>());
        }
    }

    public async Task DisposeAsync()
    {
        if (_environments?.Cleanup is { } cleanup)
        {
            await cleanup(true);
        }
    }
}

/// <summary>Live behaviour of the docker provider (port of <c>util/_sandbox/docker/docker.py</c>) against a real <c>python:3.12-slim-bookworm</c> container.</summary>
public sealed class DockerSandboxTests(DockerContainerFixture fixture) : IClassFixture<DockerContainerFixture>
{
    private static readonly string[] Sh = ["sh", "-c"];

    private static string[] Script(string script) => [.. Sh, script];

    private DockerSandboxEnvironment Sandbox => fixture.Sandbox;

    [DockerFact]
    public async Task exec_echo_captures_stdout_stderr_and_exit_code()
    {
        var echo = await Sandbox.ExecAsync(["echo", "hello"]);
        var failing = await Sandbox.ExecAsync(Script("echo out; echo err 1>&2; exit 3"));

        Assert.True(echo.Success);
        Assert.Equal("hello\n", echo.Stdout);
        Assert.Equal("", echo.Stderr);
        Assert.False(failing.Success);
        Assert.Equal(3, failing.ReturnCode);
        Assert.Equal("out\n", failing.Stdout);
        Assert.Equal("err\n", failing.Stderr);
    }

    [DockerFact]
    public async Task exec_defaults_to_the_image_workdir_and_honours_absolute_and_relative_cwd()
    {
        var root = await Sandbox.ExecAsync(["pwd"]);
        var absolute = await Sandbox.ExecAsync(["pwd"], cwd: "/tmp");
        var relative = await Sandbox.ExecAsync(["pwd"], cwd: "usr/lib");

        Assert.Equal("/", Sandbox.WorkingDirectory);
        Assert.Equal("/\n", root.Stdout);
        Assert.Equal("/tmp\n", absolute.Stdout);
        Assert.Equal("/usr/lib\n", relative.Stdout);
    }

    [DockerFact]
    public async Task exec_passes_environment_variables_and_stdin()
    {
        var env = await Sandbox.ExecAsync(Script("echo \"$INSPECT_TEST_VAR|$OTHER\""), env: new Dictionary<string, string> { ["INSPECT_TEST_VAR"] = "bar", ["OTHER"] = "two words" });
        var stdin = await Sandbox.ExecAsync(["cat"], input: "hello ünïcode\nline2");

        Assert.Equal("bar|two words\n", env.Stdout);
        Assert.Equal("hello ünïcode\nline2", stdin.Stdout);
    }

    [DockerFact]
    public async Task exec_runs_as_the_requested_user()
    {
        var root = await Sandbox.ExecAsync(["id", "-u"], user: "root");
        var nobody = await Sandbox.ExecAsync(["id", "-un"], user: "nobody");

        Assert.Equal("0\n", root.Stdout);
        Assert.Equal("nobody\n", nobody.Stdout);
    }

    [DockerFact]
    public async Task exec_timeout_kills_the_process_tree_inside_the_container_and_reports_partial_output()
    {
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<SandboxTimeoutException>(() =>
            Sandbox.ExecAsync(Script("echo partial; sleep 20; echo late"), timeout: TimeSpan.FromSeconds(1)));
        var processes = await Sandbox.ExecAsync(Script("for p in /proc/[0-9]*; do tr '\\0' ' ' < $p/cmdline 2>/dev/null; echo; done"));

        Assert.Contains("partial", ex.TruncatedOutput);
        Assert.DoesNotContain("late", ex.TruncatedOutput);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the command was not killed promptly");
        Assert.DoesNotContain("sleep 20", processes.Stdout);
    }

    [DockerFact]
    public async Task exec_missing_executable_is_a_failed_result()
    {
        var result = await Sandbox.ExecAsync(["definitely-not-a-real-command-xyz"]);

        Assert.False(result.Success);
        Assert.Equal(127, result.ReturnCode);
        Assert.Contains("definitely-not-a-real-command-xyz", result.Stdout + result.Stderr);
    }

    [DockerFact]
    public async Task exec_reads_large_stdout_and_stderr_without_deadlocking_and_caps_the_tail()
    {
        var large = await Sandbox.ExecAsync(Script("head -c 300000 /dev/zero | tr '\\0' a; head -c 300000 /dev/zero | tr '\\0' b 1>&2"));
        ExecResult capped;
        using (new EnvVarScope().Set(SandboxLimits.MaxExecOutputSizeVar, "100"))
        {
            capped = await Sandbox.ExecAsync(Script("for i in $(seq 1 100); do echo line$i; done"));
        }

        Assert.True(large.Success);
        Assert.Equal(300000, large.Stdout.Length);
        Assert.Equal(300000, large.Stderr.Length);
        Assert.True(capped.Stdout.Length <= 100);
        Assert.EndsWith("line99\nline100\n", capped.Stdout);
        Assert.DoesNotContain("line1\n", capped.Stdout);
    }

    [DockerFact]
    public async Task write_and_read_text_preserves_newlines_and_resolves_relative_paths()
    {
        var text = "a\r\nb\nc\n\n";

        await Sandbox.WriteFileAsync("inspect-tests/dir/text.txt", text);
        var back = await Sandbox.ReadFileAsync("inspect-tests/dir/text.txt");
        var cat = await Sandbox.ExecAsync(["cat", "/inspect-tests/dir/text.txt"]);

        Assert.Equal(text, back);
        Assert.Equal(text, cat.Stdout);
    }

    [DockerFact]
    public async Task write_and_read_bytes_round_trips_a_3mb_binary_blob()
    {
        var blob = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        var expected = Convert.ToHexStringLower(SHA256.HashData(blob));

        await Sandbox.WriteFileAsync("/inspect-tests/blob.bin", blob);
        var back = await Sandbox.ReadFileBytesAsync("/inspect-tests/blob.bin");
        var sum = await Sandbox.ExecAsync(["sha256sum", "/inspect-tests/blob.bin"]);

        Assert.Equal(blob, back);
        Assert.StartsWith(expected, sum.Stdout);
    }

    [DockerFact]
    public async Task read_missing_file_and_directory_raise_the_python_error_types()
    {
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => Sandbox.ReadFileAsync("/inspect-tests/nope.txt"));
        var directory = await Assert.ThrowsAsync<IOException>(() => Sandbox.ReadFileAsync("/tmp"));

        Assert.Equal("/inspect-tests/nope.txt", missing.FileName);
        Assert.Contains("is a directory", directory.Message);
    }

    [DockerFact]
    public async Task read_file_beyond_the_limit_raises_output_limit_exceeded()
    {
        await Sandbox.WriteFileAsync("/inspect-tests/big.txt", new string('x', 11));
        using var env = new EnvVarScope().Set(SandboxLimits.MaxReadFileSizeVar, "10");

        var ex = await Assert.ThrowsAsync<OutputLimitExceededException>(() => Sandbox.ReadFileAsync("/inspect-tests/big.txt"));

        Assert.Equal("10 bytes", ex.LimitDescription);
    }

    [DockerFact]
    public async Task copy_to_container_places_a_host_file_byte_exact()
    {
        var blob = RandomNumberGenerator.GetBytes(1024 * 1024);
        var host = Path.Combine(Path.GetTempPath(), $"inspect-cp-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(host, blob);
        try
        {
            await Sandbox.CopyToContainerAsync(host, "/inspect-tests/copied/blob.bin");
        }
        finally
        {
            File.Delete(host);
        }

        Assert.Equal(blob, await Sandbox.ReadFileBytesAsync("/inspect-tests/copied/blob.bin"));
    }

    [DockerFact]
    public async Task host_docker_internal_resolves_inside_the_container()
    {
        var result = await Sandbox.ExecAsync(["getent", "hosts", "host.docker.internal"]);

        Assert.True(result.Success, result.Stderr);
        Assert.Contains("host.docker.internal", result.Stdout);
        Assert.Equal("host.docker.internal", Sandbox.HostAddress);
    }

    [DockerFact]
    public async Task cleanup_removes_the_container()
    {
        var environments = await fixture.Provider.SampleInitAsync("docker-tests", null, new Dictionary<string, string>());
        var sandbox = (DockerSandboxEnvironment)environments.Default;
        var cli = new DockerCli();
        var before = await cli.InspectAsync(sandbox.ContainerName, "{{.State.Running}}");

        await environments.Cleanup!(true);

        Assert.Equal("true", before);
        await Assert.ThrowsAsync<SandboxUnavailableException>(() => cli.InspectAsync(sandbox.ContainerName, "{{.State.Running}}"));
        await Assert.ThrowsAsync<SandboxUnavailableException>(() => sandbox.ExecAsync(["echo", "hi"]));
    }

    [DockerFact]
    public async Task task_init_builds_a_dockerfile_directory_and_the_workdir_becomes_the_default()
    {
        var context = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(context);
        await File.WriteAllTextAsync(
            Path.Combine(context, "Dockerfile"),
            $"FROM {DockerSandboxProvider.DefaultImage}\nRUN echo built > /built.txt\nWORKDIR /workspace\n");
        var tag = $"{DockerImages.BuildRepository}:{DockerImages.ContentHash(context)}";
        SandboxEnvironments? environments = null;
        try
        {
            await fixture.Provider.TaskInitAsync("docker-tests", context);
            environments = await fixture.Provider.SampleInitAsync("docker-tests", context, new Dictionary<string, string>());
            var sandbox = (DockerSandboxEnvironment)environments.Default;

            var built = await sandbox.ExecAsync(["cat", "/built.txt"]);
            var pwd = await sandbox.ExecAsync(["pwd"]);
            await sandbox.WriteFileAsync("relative.txt", "rel");
            var relative = await sandbox.ExecAsync(["cat", "/workspace/relative.txt"]);

            Assert.Equal("/workspace", sandbox.WorkingDirectory);
            Assert.Equal("built\n", built.Stdout);
            Assert.Equal("/workspace\n", pwd.Stdout);
            Assert.Equal("rel", relative.Stdout);
            Assert.True(await new DockerCli().ImageExistsAsync(tag));
        }
        finally
        {
            if (environments?.Cleanup is { } cleanup)
            {
                await cleanup(true);
            }

            await new ProcessRunner().RunAsync(new ProcessRequest("docker", ["rmi", "-f", tag]), CancellationToken.None);
            Directory.Delete(context, recursive: true);
        }
    }
}
