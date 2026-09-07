using System.Text;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Sandbox.Local;

namespace InspectAzureAI.Eval.Tests;

/// <summary>Behaviour of the local sandbox provider (port of <c>util/_sandbox/local.py</c>): exec, files, timeouts and output caps.</summary>
public class LocalSandboxTests
{
    private static readonly string[] Bash = ["bash", "-c"];

    private static string[] Sh(string script) => [.. Bash, script];

    [Fact]
    public async Task exec_captures_stdout_stderr_and_exit_code()
    {
        using var sandbox = new LocalSandboxEnvironment();

        var result = await sandbox.ExecAsync(Sh("echo out; echo err 1>&2; exit 3"));

        Assert.False(result.Success);
        Assert.Equal(3, result.ReturnCode);
        Assert.Equal("out\n", result.Stdout);
        Assert.Equal("err\n", result.Stderr);
    }

    [Fact]
    public async Task exec_runs_in_the_sample_directory_and_honours_relative_cwd()
    {
        using var sandbox = new LocalSandboxEnvironment();
        await sandbox.WriteFileAsync("marker.txt", "root");
        await sandbox.WriteFileAsync("sub/inner.txt", "inner");

        var root = await sandbox.ExecAsync(["cat", "marker.txt"]);
        var inner = await sandbox.ExecAsync(["cat", "inner.txt"], cwd: "sub");

        Assert.Equal("root", root.Stdout);
        Assert.Equal("inner", inner.Stdout);
        Assert.True(Directory.Exists(sandbox.WorkingDirectory));
    }

    [Fact]
    public async Task exec_passes_environment_variables_and_stdin()
    {
        using var sandbox = new LocalSandboxEnvironment();

        var env = await sandbox.ExecAsync(Sh("echo $INSPECT_TEST_VAR"), env: new Dictionary<string, string> { ["INSPECT_TEST_VAR"] = "bar" });
        var stdin = await sandbox.ExecAsync(["cat"], input: "hello ünïcode");

        Assert.Equal("bar\n", env.Stdout);
        Assert.Equal("hello ünïcode", stdin.Stdout);
    }

    [Fact]
    public async Task exec_timeout_kills_the_process_tree_and_reports_captured_output()
    {
        using var sandbox = new LocalSandboxEnvironment();
        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<SandboxTimeoutException>(() =>
            sandbox.ExecAsync(Sh("echo partial; sleep 20; echo late"), timeout: TimeSpan.FromMilliseconds(500)));

        Assert.Contains("partial", ex.TruncatedOutput);
        Assert.DoesNotContain("late", ex.TruncatedOutput);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "the process tree was not killed promptly");
    }

    [Fact]
    public async Task exec_missing_executable_is_a_failed_result()
    {
        using var sandbox = new LocalSandboxEnvironment();

        var result = await sandbox.ExecAsync(["definitely-not-a-real-command-xyz"]);

        Assert.False(result.Success);
        Assert.Equal(127, result.ReturnCode);
        Assert.Contains("definitely-not-a-real-command-xyz", result.Stderr);
    }

    [Fact]
    public async Task exec_reads_large_stdout_and_stderr_without_deadlocking()
    {
        using var sandbox = new LocalSandboxEnvironment();

        var result = await sandbox.ExecAsync(Sh("head -c 300000 /dev/zero | tr '\\0' a; head -c 300000 /dev/zero | tr '\\0' b 1>&2"));

        Assert.True(result.Success);
        Assert.Equal(300000, result.Stdout.Length);
        Assert.Equal(300000, result.Stderr.Length);
    }

    [Fact]
    public async Task exec_output_is_capped_keeping_the_tail()
    {
        using var env = new EnvVarScope().Set(SandboxLimits.MaxExecOutputSizeVar, "100");
        using var sandbox = new LocalSandboxEnvironment();

        var result = await sandbox.ExecAsync(Sh("for i in $(seq 1 100); do echo line$i; done"));

        Assert.True(result.Stdout.Length <= 100);
        Assert.EndsWith("line99\nline100\n", result.Stdout);
        Assert.DoesNotContain("line1\n", result.Stdout);
    }

    [Fact]
    public async Task write_and_read_text_preserves_newlines_and_bytes_round_trip()
    {
        using var sandbox = new LocalSandboxEnvironment();
        var text = "a\r\nb\nc";
        byte[] bytes = [0, 1, 2, 255, 254];

        await sandbox.WriteFileAsync("dir/text.txt", text);
        await sandbox.WriteFileAsync("dir/blob.bin", bytes);

        Assert.Equal(text, await sandbox.ReadFileAsync("dir/text.txt"));
        Assert.Equal(bytes, await sandbox.ReadFileBytesAsync("dir/blob.bin"));
        Assert.Equal(Encoding.UTF8.GetBytes(text), await sandbox.ReadFileBytesAsync(Path.Combine(sandbox.WorkingDirectory, "dir/text.txt")));
    }

    [Fact]
    public async Task read_missing_file_and_directory_raise_the_python_error_types()
    {
        using var sandbox = new LocalSandboxEnvironment();
        Directory.CreateDirectory(Path.Combine(sandbox.WorkingDirectory, "adir"));

        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => sandbox.ReadFileAsync("nope.txt"));
        var directory = await Assert.ThrowsAsync<IOException>(() => sandbox.ReadFileAsync("adir"));

        Assert.Equal("nope.txt", missing.FileName);
        Assert.Contains("is a directory", directory.Message);
    }

    [Fact]
    public async Task read_file_beyond_the_limit_raises_output_limit_exceeded()
    {
        using var env = new EnvVarScope().Set(SandboxLimits.MaxReadFileSizeVar, "10");
        using var sandbox = new LocalSandboxEnvironment();
        await sandbox.WriteFileAsync("big.txt", new string('x', 11));

        var ex = await Assert.ThrowsAsync<OutputLimitExceededException>(() => sandbox.ReadFileAsync("big.txt"));

        Assert.Equal("10 bytes", ex.LimitDescription);
        Assert.Equal("The sandbox output stream limit of 10 bytes was exceeded.", ex.Message);
    }

    [Fact]
    public async Task invalid_utf8_text_read_is_a_decoding_error()
    {
        using var sandbox = new LocalSandboxEnvironment();
        await sandbox.WriteFileAsync("bad.txt", new byte[] { 0xff, 0xfe, 0x41 });

        await Assert.ThrowsAsync<DecoderFallbackException>(() => sandbox.ReadFileAsync("bad.txt"));
        Assert.Equal(3, (await sandbox.ReadFileBytesAsync("bad.txt")).Length);
    }

    [Fact]
    public async Task provider_creates_a_default_environment_and_cleanup_removes_the_directory()
    {
        var provider = SandboxRegistry.Get("local");
        var environments = await provider.SampleInitAsync("task", null, new Dictionary<string, string>());
        var sandbox = Assert.IsType<LocalSandboxEnvironment>(environments.Default);

        Assert.Equal("local", provider.Type);
        Assert.Equal("127.0.0.1", sandbox.HostAddress);
        Assert.Equal("default", environments.Environments.Keys.Single());
        Assert.True(Directory.Exists(sandbox.WorkingDirectory));

        await environments.Cleanup!(true);

        Assert.False(Directory.Exists(sandbox.WorkingDirectory));
    }

    [Fact]
    public void registry_rejects_unknown_types_naming_the_known_ones()
    {
        var ex = Assert.Throws<ArgumentException>(() => SandboxRegistry.Get("nope"));

        Assert.Contains("local", ex.Message);
        Assert.Contains("nope", ex.Message);
    }

    [Theory]
    [InlineData(100L * 1024 * 1024, "100 MiB")]
    [InlineData(8L * 1024, "8 KiB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GiB")]
    [InlineData(12L, "12 bytes")]
    public void human_readable_size_matches_python(long bytes, string expected) => Assert.Equal(expected, SandboxLimits.HumanReadableSize(bytes));

    [Fact]
    public void tail_buffer_keeps_only_the_last_bytes()
    {
        var buffer = new TailByteBuffer(5);
        buffer.Write("abc"u8);
        buffer.Write("defg"u8);

        Assert.Equal("cdefg", Encoding.ASCII.GetString(buffer.ToArray()));
        Assert.True(buffer.Truncated);
        Assert.Equal(7, buffer.TotalBytes);
    }

    [Fact]
    public async Task exec_returns_once_the_command_exits_even_when_a_background_child_keeps_the_pipes_open()
    {
        using var sandbox = new LocalSandboxEnvironment();
        var started = DateTime.UtcNow;

        var result = await sandbox.ExecAsync(Sh("sleep 30 & echo started"));

        Assert.True(result.Success);
        Assert.Equal("started\n", result.Stdout);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "the exec waited for the background child's pipe");
    }

    [Fact]
    public async Task exec_timeout_returns_even_when_an_orphaned_child_keeps_the_pipes_open()
    {
        using var sandbox = new LocalSandboxEnvironment();
        var started = DateTime.UtcNow;

        // The subshell's child outlives the killed tree (it is re-parented once the subshell exits) and holds stdout.
        var ex = await Assert.ThrowsAsync<SandboxTimeoutException>(() =>
            sandbox.ExecAsync(Sh("echo partial; (sleep 30 &); sleep 20"), timeout: TimeSpan.FromMilliseconds(500)));

        Assert.Contains("partial", ex.TruncatedOutput);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "the timeout waited for the orphan's pipe");
    }
}
