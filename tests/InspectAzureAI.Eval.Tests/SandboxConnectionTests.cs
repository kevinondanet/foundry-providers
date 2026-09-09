using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Sandbox.Docker;
using InspectAzureAI.Eval.Sandbox.Local;
using InspectAzureAI.Eval.Testing;

namespace InspectAzureAI.Eval.Tests;

/// <summary>
/// The connection seam (port of <c>SandboxConnection</c> and <c>DockerSandboxEnvironment.connection</c>): the
/// docker login command, ports and VS Code command through a scripted process runner, the local login shell,
/// the extension over plain environments, the port parser and shlex-style quoting.
/// </summary>
public class SandboxConnectionTests
{
    private const string Container = "inspect-swe-0123456789ab";

    private const string PortsJson = """{"5900/tcp":[{"HostIp":"0.0.0.0","HostPort":"54023"},{"HostIp":"::","HostPort":"54023"}],"8080/tcp":[{"HostIp":"127.0.0.1","HostPort":"54024"}],"53/udp":null}""";

    private static (ScriptedProcessRunner Runner, DockerSandboxEnvironment Sandbox) Docker(bool running = true, string ports = PortsJson)
    {
        var runner = new ScriptedProcessRunner
        {
            Responder = request => request.Arguments[0] != "inspect"
                ? ProcessResult.Ok()
                : request.Arguments[2] == "{{.State.Running}}" ? ProcessResult.Ok(running ? "true\n" : "false\n") : ProcessResult.Ok(ports + "\n"),
        };
        return (runner, new DockerSandboxEnvironment(new DockerCli(runner), Container, "/work"));
    }

    [Fact]
    public async Task docker_connection_carries_the_exec_command_ports_and_vscode_command()
    {
        var (runner, sandbox) = Docker();

        var connection = await sandbox.ConnectionAsync();

        Assert.Equal("docker", connection.Type);
        Assert.Equal($"docker exec -it {Container} bash -l", connection.Command);
        Assert.Equal([DockerSandboxEnvironment.VscodeAttachCommand, Container], connection.VscodeCommand);
        Assert.Equal(Container, connection.Container);
        Assert.NotNull(connection.Ports);
        Assert.Equal(2, connection.Ports.Count);
        Assert.Equal(5900, connection.Ports[0].ContainerPort);
        Assert.Equal("tcp", connection.Ports[0].Protocol);
        Assert.Equal([new HostMapping("0.0.0.0", 54023), new HostMapping("::", 54023)], connection.Ports[0].Mappings);
        Assert.Equal(8080, connection.Ports[1].ContainerPort);
        Assert.Equal(["inspect", "--format", "{{.State.Running}}", Container], runner.Requests[0].Arguments);
        Assert.Equal(["inspect", "--format", "{{json .NetworkSettings.Ports}}", Container], runner.Requests[1].Arguments);
    }

    [Fact]
    public async Task docker_connection_as_a_user_quotes_the_user_and_drops_the_vscode_command()
    {
        var (_, sandbox) = Docker(ports: "{}");

        var connection = await sandbox.ConnectionAsync(user: "agent user");

        Assert.Equal($"docker exec -it --user 'agent user' {Container} bash -l", connection.Command);
        Assert.Null(connection.VscodeCommand);
        Assert.Null(connection.Ports);
    }

    [Fact]
    public async Task docker_connection_fails_when_the_container_is_not_running()
    {
        var (_, sandbox) = Docker(running: false);

        var ex = await Assert.ThrowsAsync<SandboxUnavailableException>(() => sandbox.ConnectionAsync());

        Assert.Contains(Container, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task local_connection_is_a_login_shell_in_the_working_directory()
    {
        using var sandbox = new LocalSandboxEnvironment();

        var connection = await ((ISandboxEnvironment)sandbox).ConnectionAsync(user: "ignored");

        Assert.Equal("local", connection.Type);
        Assert.Equal($"cd {ShellWords.Quote(sandbox.WorkingDirectory)} && bash -l", connection.Command);
        Assert.Null(connection.Container);
        Assert.Null(connection.VscodeCommand);
    }

    [Fact]
    public async Task environments_without_a_provider_do_not_support_connections()
    {
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => new FakeSandboxEnvironment().ConnectionAsync());

        Assert.Contains("FakeSandboxEnvironment", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void docker_ports_parse_skips_unpublished_entries_and_returns_null_for_none()
    {
        Assert.Null(DockerPorts.Parse("{}"));
        Assert.Null(DockerPorts.Parse("null"));
        Assert.Null(DockerPorts.Parse("""{"80/tcp":null}"""));

        var parsed = DockerPorts.Parse(PortsJson)!;
        Assert.Equal(["5900/tcp", "8080/tcp"], parsed.Select(port => $"{port.ContainerPort}/{port.Protocol}"));
        Assert.Equal(54024, parsed[1].Mappings[0].HostPort);
        Assert.Equal("127.0.0.1", parsed[1].Mappings[0].HostIp);
    }

    [Fact]
    public void shell_words_quote_like_shlex()
    {
        Assert.Equal("docker exec -it name bash -l", ShellWords.Join(["docker", "exec", "-it", "name", "bash", "-l"]));
        Assert.Equal("'a b'", ShellWords.Quote("a b"));
        Assert.Equal("''", ShellWords.Quote(""));
        Assert.Equal("'it'\"'\"'s'", ShellWords.Quote("it's"));
        Assert.Equal("/usr/local/bin:x_y@z%1+2=3,4.5-6", ShellWords.Quote("/usr/local/bin:x_y@z%1+2=3,4.5-6"));
        Assert.Equal("'$HOME'", ShellWords.Quote("$HOME"));
    }
}
