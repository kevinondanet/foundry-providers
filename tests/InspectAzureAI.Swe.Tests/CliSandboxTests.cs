using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Swe.CodexCli;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

/// <summary>Pins the default command table of the shared <see cref="CliSandbox"/> that the fake CLIs build on.</summary>
public class CliSandboxTests
{
    [Fact]
    public async Task which_fails_by_default_and_answers_configured_binaries()
    {
        var sandbox = new CliSandbox();

        var miss = await sandbox.ExecAsync(SandboxUtil.BashCommand("which codex"));
        sandbox.WhichPaths["claude"] = "/usr/local/bin/claude";
        var hit = await sandbox.ExecAsync(SandboxUtil.BashCommand("which claude"));

        Assert.False(miss.Success);
        Assert.Equal(1, miss.ReturnCode);
        Assert.Equal("", miss.Stdout);
        Assert.Equal("", miss.Stderr);
        Assert.True(hit.Success);
        Assert.Equal("/usr/local/bin/claude\n", hit.Stdout);
    }

    [Fact]
    public async Task test_x_fails_unless_the_path_is_marked_installed()
    {
        var sandbox = new CliSandbox();

        var before = await sandbox.ExecAsync(["test", "-x", "/opt/codex/bin/codex"]);
        sandbox.MarkInstalled("/opt/codex/bin/codex");
        var after = await sandbox.ExecAsync(["test", "-x", "/opt/codex/bin/codex"]);
        var other = await sandbox.ExecAsync(["test", "-x", "/opt/other"]);

        Assert.False(before.Success);
        Assert.True(after.Success);
        Assert.False(other.Success);
    }

    [Fact]
    public async Task version_probe_fails_so_the_installed_version_is_unknown()
    {
        var sandbox = new CliSandbox();

        var result = await sandbox.ExecAsync(["/usr/local/bin/codex", "--version"]);

        Assert.False(result.Success);
        Assert.Null(await CodexCliBinary.InstalledVersionAsync(sandbox, "/usr/local/bin/codex", null));
    }

    [Fact]
    public async Task platform_probes_and_pwd_answer_linux_arm64_glibc_in_workspace()
    {
        var sandbox = new CliSandbox();
        var musl = new CliSandbox { Machine = "x86_64", Libc = "musl", WorkingDirectory = "/srv" };

        Assert.Equal("linux-arm64", await SandboxUtil.DetectPlatformAsync(sandbox));
        Assert.Equal("/workspace", await SandboxUtil.ResolveAgentCwdAsync(sandbox, "agent", null));
        Assert.Equal("linux-x64-musl", await SandboxUtil.DetectPlatformAsync(musl));
        Assert.Equal("/srv", await SandboxUtil.ResolveAgentCwdAsync(musl, null, null));
    }

    [Theory]
    [InlineData("mkdir", "-p", "/var/tmp/x")]
    [InlineData("chmod", "+x", "/var/tmp/x/bin/codex")]
    [InlineData("chown", "agent", "/workspace/.codex")]
    [InlineData("tar", "-xzf", "/var/tmp/x/a.tar.gz")]
    [InlineData("rm", "-f", "/var/tmp/x/a.tar.gz")]
    public async Task install_commands_succeed(string executable, string flag, string path)
    {
        var result = await new CliSandbox().ExecAsync([executable, flag, path], user: "root");

        Assert.True(result.Success);
    }

    [Fact]
    public async Task shell_probes_match_on_the_script_and_argv_commands_on_the_executable()
    {
        var sandbox = new CliSandbox();

        var shellUname = await sandbox.ExecAsync(["bash", "-c", "uname -s"]);
        var argvUname = await sandbox.ExecAsync(["uname", "-s"]);
        var shellMkdir = await sandbox.ExecAsync(SandboxUtil.BashCommand("mkdir -p /x"));

        Assert.Equal("Linux\n", shellUname.Stdout);
        Assert.False(argvUname.Success);
        Assert.Equal("CliSandbox: unexpected command uname -s", argvUname.Stderr);
        Assert.False(shellMkdir.Success);
        Assert.Equal("CliSandbox: unexpected command bash -c mkdir -p /x", shellMkdir.Stderr);
        Assert.Equal("uname -s", CliSandbox.ShellScript(sandbox.Calls[0]));
        Assert.Null(CliSandbox.ShellScript(sandbox.Calls[1]));
    }

    [Fact]
    public async Task unexpected_commands_fail_visibly()
    {
        var result = await new CliSandbox().ExecAsync(["curl", "-s", "https://example.test"]);

        Assert.False(result.Success);
        Assert.Equal(1, result.ReturnCode);
        Assert.Equal(CliSandbox.UnexpectedCommandPrefix + "curl -s https://example.test", result.Stderr);
    }

    [Fact]
    public async Task on_exec_answers_first_and_a_null_result_falls_through_to_the_defaults()
    {
        var sandbox = new CliSandbox
        {
            OnExec = async (call, cancellationToken) =>
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return call.Cmd[0] == "codex" ? CliSandbox.Ok("ran " + FakeCliEnv.Require(call, "CODEX_HOME")) : null;
            },
        };
        var env = new Dictionary<string, string> { ["CODEX_HOME"] = "/workspace/.codex" };

        var answered = await sandbox.ExecAsync(["codex", "exec"], cwd: "/workspace", env: env, user: "agent", timeout: TimeSpan.FromMinutes(1));
        var fallthrough = await sandbox.ExecAsync(SandboxUtil.BashCommand("uname -m"));

        Assert.Equal("ran /workspace/.codex", answered.Stdout);
        Assert.Equal("aarch64\n", fallthrough.Stdout);
        var first = sandbox.Calls[0];
        Assert.Equal(["codex", "exec"], first.Cmd);
        Assert.Equal("/workspace", first.Cwd);
        Assert.Equal("agent", first.User);
        Assert.Equal(TimeSpan.FromMinutes(1), first.Timeout);
        Assert.Same(env, first.Env);
        Assert.Equal(2, sandbox.Calls.Count);
    }

    [Fact]
    public async Task files_are_kept_in_memory()
    {
        var sandbox = new CliSandbox();

        await sandbox.WriteFileAsync("/workspace/.codex/config.toml", "model_provider = \"openai-proxy\"\n");
        await sandbox.WriteFileAsync("/var/tmp/a.tar.gz", new byte[] { 1, 2, 3 });

        Assert.Equal("model_provider = \"openai-proxy\"\n", await sandbox.ReadFileAsync("/workspace/.codex/config.toml"));
        Assert.Equal("model_provider = \"openai-proxy\"\n", sandbox.TextOf("/workspace/.codex/config.toml"));
        Assert.Equal(new byte[] { 1, 2, 3 }, await sandbox.ReadFileBytesAsync("/var/tmp/a.tar.gz"));
        Assert.Null(sandbox.TextOf("/nowhere"));
        Assert.Equal("/nowhere", (await Assert.ThrowsAsync<FileNotFoundException>(() => sandbox.ReadFileAsync("/nowhere"))).FileName);
        Assert.Equal("127.0.0.1", sandbox.HostAddress);
        Assert.IsAssignableFrom<ISandboxEnvironment>(sandbox);
    }
}
