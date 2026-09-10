using InspectAzureAI.Swe.CodexCli;

namespace InspectAzureAI.Swe.Tests;

/// <summary>The Codex CLI argv and file locations (<c>_codex_cli/codex_cli.py:248-308, 367-388</c>).</summary>
public class CodexCliCommandTests
{
    private const string Binary = "/var/tmp/.5c95f967ca830048/codex-0.154.0-linux-arm64/bin/codex";

    [Fact]
    public void headless_argv_is_exec_model_bypass_then_user_and_explicit_overrides()
    {
        var cmd = CodexCliCommand.Base(
            Binary,
            "gpt-5.4",
            centaur: false,
            autoReview: false,
            [KeyValuePair.Create("model_reasoning_effort", "\"high\""), KeyValuePair.Create("web_search", "\"disabled\"")],
            CodexCliConfig.CliOverrides(CodexWebSearch.Live, goals: true, autoReview: null));

        Assert.Equal(
            [
                Binary, "exec", "--color", "never", "--skip-git-repo-check", "--model", "gpt-5.4", "--dangerously-bypass-approvals-and-sandbox",
                "-c", "model_reasoning_effort=\"high\"", "-c", "web_search=\"disabled\"",
                "-c", "web_search=\"live\"", "-c", "features.goals=true",
            ],
            cmd);
    }

    [Fact]
    public void auto_review_drops_the_bypass_flag_and_adds_the_approval_overrides()
    {
        var cmd = CodexCliCommand.Base(Binary, "gpt-5.5", centaur: false, autoReview: true, null, CodexCliConfig.CliOverrides(CodexWebSearch.Cached, false, new CodexAutoReview { Policy = "Deny network." }));

        Assert.DoesNotContain(CodexCliCommand.BypassFlag, cmd);
        Assert.Equal(
            [
                Binary, "exec", "--color", "never", "--skip-git-repo-check", "--model", "gpt-5.5",
                "-c", "web_search=\"cached\"", "-c", "features.goals=false",
                "-c", "approval_policy=\"on-request\"", "-c", "sandbox_mode=\"workspace-write\"",
                "-c", "approvals_reviewer=\"auto_review\"", "-c", "features.guardian_approval=true",
            ],
            cmd);
    }

    [Fact]
    public void centaur_mode_has_no_exec_subcommand()
    {
        var cmd = CodexCliCommand.Base(Binary, "inspect-generic", centaur: true, autoReview: false, null, []);

        Assert.Equal([Binary, "--model", "inspect-generic", CodexCliCommand.BypassFlag], cmd);
    }

    [Fact]
    public void the_prompt_is_appended_and_resume_last_follows_it()
    {
        IReadOnlyList<string> baseCmd = [Binary, "exec", "--model", "m"];

        Assert.Equal([Binary, "exec", "--model", "m", "fix it"], CodexCliCommand.WithPrompt(baseCmd, "fix it", resume: false));
        Assert.Equal([Binary, "exec", "--model", "m", "try again", "resume", "--last"], CodexCliCommand.WithPrompt(baseCmd, "try again", resume: true));
        Assert.Equal(4, baseCmd.Count);
    }

    [Fact]
    public void launch_wraps_the_command_in_bash_with_stdin_closed()
    {
        Assert.Equal("exec 0</dev/null; \"$@\"", CodexCliCommand.LaunchScript);
        Assert.Equal(["bash", "-c", CodexCliCommand.LaunchScript, "bash", Binary, "exec", "go"], CodexCliCommand.Launch([Binary, "exec", "go"]));
    }

    [Theory]
    [InlineData("/workspace", "/workspace/.codex", false, "/workspace/AGENTS.md", "/workspace/.codex/config.toml")]
    [InlineData("/workspace/", "/workspace/.codex", false, "/workspace/AGENTS.md", "/workspace/.codex/config.toml")]
    [InlineData("/workspace", "/home/agent/.codex", true, "/home/agent/.codex/AGENTS.md", "/home/agent/.codex/config.toml")]
    public void agents_md_and_config_toml_follow_the_home_dir(string cwd, string codexHome, bool homeDirSet, string agentsMd, string configToml)
    {
        Assert.Equal(agentsMd, CodexCliCommand.AgentsMdPath(cwd, codexHome, homeDirSet));
        Assert.Equal(configToml, CodexCliCommand.ConfigTomlPath(cwd, codexHome, homeDirSet));
    }
}
