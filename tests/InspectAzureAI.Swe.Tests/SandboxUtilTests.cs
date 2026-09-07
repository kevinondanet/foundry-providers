using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

/// <summary>Port of inspect_swe <c>_util/sandbox.py</c> against the in-memory fake sandbox.</summary>
public class SandboxUtilTests
{
    private static FakeSandboxEnvironment Scripted(params (string Cmd, string Stdout)[] answers) =>
        new(cmd => answers.FirstOrDefault(a => a.Cmd == cmd[2]) is { Cmd: not null } hit
            ? FakeSandboxEnvironment.Ok(hit.Stdout)
            : FakeSandboxEnvironment.Fail(1, $"no answer for {cmd[2]}"));

    [Fact]
    public async Task exec_runs_bash_and_returns_trimmed_stdout()
    {
        var sandbox = Scripted(("pwd", "  /workspace\n"));

        var result = await SandboxUtil.ExecAsync(sandbox, "pwd", user: "agent", cwd: "/tmp");

        Assert.Equal("/workspace", result);
        var call = Assert.Single(sandbox.Calls);
        Assert.Equal(["bash", "-c", "pwd"], call.Cmd);
        Assert.Equal("agent", call.User);
        Assert.Equal("/tmp", call.Cwd);
    }

    [Fact]
    public async Task exec_failure_raises_with_the_python_message()
    {
        var sandbox = new FakeSandboxEnvironment(_ => FakeSandboxEnvironment.Fail(2, "boom"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SandboxUtil.ExecAsync(sandbox, "false"));

        Assert.Equal("Error executing sandbox command false: boom", ex.Message);
    }

    [Theory]
    [InlineData("x86_64", "glibc", "linux-x64")]
    [InlineData("amd64", "musl", "linux-x64-musl")]
    [InlineData("aarch64", "glibc", "linux-arm64")]
    [InlineData("arm64", "musl", "linux-arm64-musl")]
    public async Task platform_detection_combines_arch_and_libc(string arch, string libc, string expected)
    {
        var sandbox = new FakeSandboxEnvironment(cmd => FakeSandboxEnvironment.Ok(cmd[2] switch
        {
            "uname -s" => "Linux\n",
            "uname -m" => arch + "\n",
            _ => libc + "\n",
        }));

        Assert.Equal(expected, await SandboxUtil.DetectPlatformAsync(sandbox));
    }

    [Fact]
    public async Task unsupported_os_and_architecture_are_rejected()
    {
        var mac = Scripted(("uname -s", "Darwin"));
        var odd = Scripted(("uname -s", "Linux"), ("uname -m", "riscv64"));

        var os = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => SandboxUtil.DetectPlatformAsync(mac));
        var arch = await Assert.ThrowsAsync<PlatformNotSupportedException>(() => SandboxUtil.DetectPlatformAsync(odd));

        Assert.Equal("Unsupported OS: Darwin", os.Message);
        Assert.Equal("Unsupported architecture: riscv64", arch.Message);
    }

    [Fact]
    public async Task explicit_absolute_cwd_is_returned_without_touching_the_sandbox()
    {
        var sandbox = new FakeSandboxEnvironment();

        Assert.Equal("/srv/app", await SandboxUtil.ResolveAgentCwdAsync(sandbox, null, "/srv/app"));
        Assert.Empty(sandbox.Calls);
    }

    [Fact]
    public async Task relative_cwd_is_canonicalised_inside_the_sandbox()
    {
        var sandbox = Scripted(("pwd", "/workspace/src\n"));

        var cwd = await SandboxUtil.ResolveAgentCwdAsync(sandbox, "agent", "src");

        Assert.Equal("/workspace/src", cwd);
        var call = Assert.Single(sandbox.Calls);
        Assert.Equal("src", call.Cwd);
        Assert.Equal("agent", call.User);
    }

    [Fact]
    public async Task default_cwd_is_the_sandbox_working_directory_unless_it_is_root()
    {
        var workdir = Scripted(("pwd", "/workspace"));
        var rootWithHome = Scripted(("pwd", "/"), ("cd ~ 2>/dev/null && pwd || echo \"/\"", "/root"));
        var rootNoHome = Scripted(("pwd", "/"), ("cd ~ 2>/dev/null && pwd || echo \"/\"", "/"));

        Assert.Equal("/workspace", await SandboxUtil.ResolveAgentCwdAsync(workdir, null, null));
        Assert.Equal("/root", await SandboxUtil.ResolveAgentCwdAsync(rootWithHome, null, null));
        Assert.Equal("/", await SandboxUtil.ResolveAgentCwdAsync(rootNoHome, null, null));
        Assert.Single(workdir.Calls);
        Assert.Equal(2, rootWithHome.Calls.Count);
    }

    [Fact]
    public async Task fake_sandbox_stores_files_in_memory()
    {
        var sandbox = new FakeSandboxEnvironment();

        await sandbox.WriteFileAsync("/w/a.txt", "hi\r\n");

        Assert.Equal("hi\r\n", await sandbox.ReadFileAsync("/w/a.txt"));
        Assert.Equal("127.0.0.1", sandbox.HostAddress);
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => sandbox.ReadFileAsync("/w/none"));
        Assert.Equal("/w/none", missing.FileName);
        Assert.Equal(SandboxUtil.BashCommand("ls"), new[] { "bash", "-c", "ls" });
    }
}
