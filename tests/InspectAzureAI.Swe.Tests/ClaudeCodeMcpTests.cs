using System.Diagnostics;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Swe.Tests;

/// <summary>The Claude Code MCP configuration file, allow rules and flag order (port of <c>claude_code.py:301-339, 600-650</c>).</summary>
public class ClaudeCodeMcpTests
{
    private static readonly McpServerConfigStdio Stdio = new("fs", "npx") { Args = ["-y", "server-fs"], Env = new Dictionary<string, string> { ["ROOT"] = "/ü" } };

    private static readonly McpServerConfigHttp Remote = new("http", "docs", "https://docs.example/mcp") { Tools = ["search", "fetch"] };

    private static readonly McpServerConfigHttp Bridged = new("http", "secrets", "http://127.0.0.1:4321/mcp/secrets", new Dictionary<string, string> { ["Authorization"] = "Bearer tok" });

    [Fact]
    public void config_path_is_per_session_under_tmp()
    {
        Assert.Equal("/tmp/.inspect-claude-code/mcp-abc-123.json", ClaudeCodeMcp.ConfigPath("abc-123"));
        Assert.Throws<ArgumentException>(() => ClaudeCodeMcp.ConfigPath("../etc/x"));
        Assert.Throws<ArgumentException>(() => ClaudeCodeMcp.ConfigPath(""));
    }

    [Fact]
    public void config_json_is_compact_statics_first_then_bridged()
    {
        var json = ClaudeCodeMcp.ConfigJson([Stdio, Remote], [Bridged]);

        Assert.Equal(
            "{\"mcpServers\":{\"fs\":{\"type\":\"stdio\",\"command\":\"npx\",\"args\":[\"-y\",\"server-fs\"],\"env\":{\"ROOT\":\"/ü\"}},"
            + "\"docs\":{\"type\":\"http\",\"url\":\"https://docs.example/mcp\"},"
            + "\"secrets\":{\"type\":\"http\",\"url\":\"http://127.0.0.1:4321/mcp/secrets\",\"headers\":{\"Authorization\":\"Bearer tok\"}}}}",
            json);
    }

    [Fact]
    public void a_later_server_with_the_same_name_replaces_the_earlier_in_place()
    {
        var shadow = new McpServerConfigHttp("sse", "fs", "https://other/sse");

        var json = ClaudeCodeMcp.ConfigJson([Stdio, Remote], [shadow]);

        Assert.Equal("{\"mcpServers\":{\"fs\":{\"type\":\"sse\",\"url\":\"https://other/sse\"},\"docs\":{\"type\":\"http\",\"url\":\"https://docs.example/mcp\"}}}", json);
    }

    [Fact]
    public void config_args_pass_the_path()
    {
        Assert.Equal(["--mcp-config", "/tmp/.inspect-claude-code/mcp-s.json"], ClaudeCodeMcp.ConfigArgs("/tmp/.inspect-claude-code/mcp-s.json"));
    }

    [Fact]
    public async Task the_write_script_creates_the_file_private_and_replaces_a_planted_symlink_without_writing_through_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("inspect-claude-mcp-");
        try
        {
            var victim = Path.Combine(dir.FullName, "victim");
            await File.WriteAllTextAsync(victim, "untouched");
            var path = Path.Combine(dir.FullName, "mcp-s.json");
            File.CreateSymbolicLink(path, victim);

            var (code, stderr) = await BashAsync(ClaudeCodeMcp.WriteConfigScript, path, "{\"mcpServers\":{}}");

            Assert.Equal((0, ""), (code, stderr));
            Assert.Equal("untouched", await File.ReadAllTextAsync(victim));
            Assert.Null(new FileInfo(path).LinkTarget);
            Assert.Equal("{\"mcpServers\":{}}", await File.ReadAllTextAsync(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(["mcp-s.json", "victim"], dir.EnumerateFileSystemInfos().Select(f => f.Name).Order());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task the_directory_script_refuses_a_symlink_and_leaves_its_target_alone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("inspect-claude-mcp-");
        try
        {
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            var target = Directory.CreateDirectory(Path.Combine(dir.FullName, "target"));
            File.SetUnixFileMode(target.FullName, Private);
            var link = Path.Combine(dir.FullName, "link");
            Directory.CreateSymbolicLink(link, target.FullName);

            var (code, stderr) = await BashAsync(ClaudeCodeMcp.PrepareDirectoryScript, link, "");

            Assert.Equal(1, code);
            Assert.Contains("is not a directory owned by root", stderr, StringComparison.Ordinal);
            Assert.Equal(Private, File.GetUnixFileMode(target.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>Runs <c>bash -c script bash arg</c> on the host with <paramref name="input"/> as stdin.</summary>
    private static async Task<(int Code, string Stderr)> BashAsync(string script, string arg, string input)
    {
        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in (string[])["-c", script, "bash", arg])
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        await stdout;
        return (process.ExitCode, await stderr);
    }

    [Fact]
    public void allowed_tools_cover_statics_only_when_allowlisted_and_bridged_always()
    {
        Assert.Equal(["mcp__fs__*", "mcp__docs__search", "mcp__docs__fetch", "mcp__secrets__*"], ClaudeCodeMcp.AllowedTools([Stdio, Remote], [Bridged], allowlistMcpTools: true));
        Assert.Equal(["mcp__secrets__*"], ClaudeCodeMcp.AllowedTools([Stdio, Remote], [Bridged], allowlistMcpTools: false));
        Assert.Empty(ClaudeCodeMcp.AllowedTools([Stdio], [], allowlistMcpTools: false));
    }

    [Fact]
    public void base_flags_place_mcp_args_then_allowed_then_disallowed_tools()
    {
        var flags = ClaudeCodeCommand.BaseFlags(
            "m",
            permissionMode: "auto",
            debug: true,
            disallowedTools: ["WebSearch"],
            mcpConfigArgs: ["--mcp-config", "/tmp/x.json"],
            allowedTools: ["mcp__fs__*", "mcp__secrets__*"]);

        Assert.Equal(
            [
                "--permission-mode", "auto", "--model", "m", "--print", "--output-format", "stream-json", "--verbose", "--debug",
                "--mcp-config", "/tmp/x.json", "--allowed-tools", "mcp__fs__*,mcp__secrets__*", "--disallowed-tools", "WebSearch",
            ],
            flags);
        Assert.DoesNotContain("--allowed-tools", ClaudeCodeCommand.BaseFlags("m", allowedTools: []));
    }

    [Fact]
    public void centaur_base_flags_omit_print_and_debug()
    {
        var flags = ClaudeCodeCommand.BaseFlags("m", debug: true, mcpConfigArgs: ["--mcp-config", "/p"], allowedTools: ["mcp__s__*"], centaur: true);

        Assert.Equal(["--dangerously-skip-permissions", "--model", "m", "--mcp-config", "/p", "--allowed-tools", "mcp__s__*"], flags);
    }

    [Fact]
    public void base_flags_are_unchanged_without_mcp_or_centaur()
    {
        Assert.Equal(
            ClaudeCodeCommand.BaseFlags("m", "plan", true, ["Bash"]),
            ClaudeCodeCommand.BaseFlags("m", "plan", true, ["Bash"], mcpConfigArgs: null, allowedTools: null, centaur: false));
        Assert.Equal(
            ["--permission-mode", "plan", "--model", "m", "--print", "--output-format", "stream-json", "--verbose", "--debug", "--disallowed-tools", "Bash"],
            ClaudeCodeCommand.BaseFlags("m", "plan", true, ["Bash"]));
    }
}
