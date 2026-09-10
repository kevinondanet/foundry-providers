using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Agents.Human;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

/// <summary>
/// Centaur mode: the golden <c>.bashrc</c> texts of both CLIs (ports of <c>_run_codex_cli_centaur</c> and
/// <c>run_claude_code_centaur</c>, with deviation D-S1's export escaping) and argument pass-through of
/// <c>run_centaur</c> through the agent-factory seam. The human CLI agent itself is covered by Eval.Tests.
/// </summary>
public class CentaurTests
{
    [Fact]
    public void options_default_to_python_centaur_options()
    {
        var options = new CentaurOptions();

        Assert.True(options.Answer);
        Assert.Null(options.AnswerPattern);
        Assert.False(options.IntermediateScoring);
        Assert.True(options.RecordSession);
        Assert.Null(options.View);
        Assert.Null(options.PollingInterval);
    }

    [Fact]
    public void instructions_are_verbatim()
    {
        Assert.Equal("Codex CLI:\n\n - You may also use Codex CLI via the 'codex' command.\n - Use 'codex resume' if you need to resume a previous codex session.", Centaur.CodexInstructions);
        Assert.Equal("Claude Code:\n\n - You may also use Claude Code via the 'claude' command.\n - Use 'claude --resume' if you need to resume a previous claude session.", Centaur.ClaudeInstructions);
    }

    [Fact]
    public void codex_bashrc_matches_the_golden_text()
    {
        IReadOnlyList<string> cmd = ["/opt/codex/bin/codex", "--model", "gpt-5.4", "--dangerously-bypass-approvals-and-sandbox", "-c", "web_search=\"live\"", "-c", "features.goals=true", "it's"];
        var env = new Dictionary<string, string>
        {
            ["CODEX_HOME"] = "/workspace/.codex",
            ["OPENAI_API_KEY"] = "tok\"en",
            ["OPENAI_BASE_URL"] = "http://127.0.0.1:5555/v1",
            ["NOTE"] = "cost $5 `date` \\ end",
        };

        const string expected = """
            export CODEX_HOME="/workspace/.codex"
            export OPENAI_API_KEY="tok\"en"
            export OPENAI_BASE_URL="http://127.0.0.1:5555/v1"
            export NOTE="cost $5 \`date\` \\ end"

            alias codex='/opt/codex/bin/codex --model gpt-5.4 --dangerously-bypass-approvals-and-sandbox -c '\''web_search="live"'\'' -c features.goals=true '\''it'\''"'\''"'\''s'\'''
            """;
        Assert.Equal(expected, Centaur.CodexBashrc(cmd, env));
    }

    [Fact]
    public void claude_bashrc_matches_the_golden_text()
    {
        IReadOnlyList<string> cmd = ["/opt/claude/claude", "--dangerously-skip-permissions", "--model", "claude-sonnet-4-6", "--append-system-prompt", "Don't stop"];
        var env = new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = "http://127.0.0.1:5555",
            ["ANTHROPIC_AUTH_TOKEN"] = "t$k`\"",
        };

        const string expected = """
            export ANTHROPIC_BASE_URL="http://127.0.0.1:5555"
            export ANTHROPIC_AUTH_TOKEN="t$k\`\""
            mkdir -p "$HOME/.local/bin"
            export PATH="$HOME/.local/bin:$PATH"
            ln -sf /opt/claude/claude "$HOME/.local/bin/claude"

            echo '{"hasCompletedOnboarding":true,"bypassPermissionsModeAccepted":true}' > "$HOME"/.claude.json

            alias claude='/opt/claude/claude --dangerously-skip-permissions --model claude-sonnet-4-6 --append-system-prompt '\''Don'\''"'\''"'\''t stop'\'''
            """;
        Assert.Equal(expected, Centaur.ClaudeBashrc(cmd, env));
    }

    [Fact]
    public void claude_bashrc_quotes_an_unsafe_binary_path_in_the_link()
    {
        var bashrc = Centaur.ClaudeBashrc(["/opt/my tools/claude"], new Dictionary<string, string>());

        var lines = bashrc.Split('\n');
        Assert.Equal("mkdir -p \"$HOME/.local/bin\"", lines[0]);
        Assert.Contains("ln -sf '/opt/my tools/claude' \"$HOME/.local/bin/claude\"", lines);
        Assert.Equal("alias claude=''\\''/opt/my tools/claude'\\'''", lines[^1]);
        Assert.Throws<ArgumentException>(() => Centaur.ClaudeBashrc([], new Dictionary<string, string>()));
    }

    [Fact]
    public void export_lines_keep_order_and_dollar_expansion()
    {
        var lines = Centaur.ExportLines(new Dictionary<string, string>
        {
            ["B"] = "$HOME/bin",
            ["A"] = "back\\slash \"quoted\" `tick`",
        });

        Assert.Equal(["export B=\"$HOME/bin\"", "export A=\"back\\\\slash \\\"quoted\\\" \\`tick\\`\""], lines);
        Assert.Equal("alias codex='codex exec'", Centaur.AliasLine("codex", ["codex", "exec"]));
    }

    [Fact]
    public async Task run_async_hands_every_option_to_the_agent_factory_and_runs_its_agent()
    {
        var view = new ConsoleHumanAgentView(TextWriter.Null);
        var options = new CentaurOptions
        {
            Answer = false,
            AnswerPattern = "^[0-9]+$",
            IntermediateScoring = true,
            RecordSession = false,
            View = view,
            PollingInterval = TimeSpan.FromMilliseconds(5),
        };
        var state = new AgentState([new ChatMessageUser("fix the bug")]);
        HumanCliArgs? captured = null;

        var result = await Centaur.RunAsync(
            options,
            Centaur.CodexInstructions,
            "bashrc text",
            state,
            args =>
            {
                captured = args;
                return new AgentDef("human_cli", "fake human", (s, _) =>
                {
                    s.Messages.Add(new ChatMessageAssistant("submitted"));
                    return Task.FromResult(s);
                });
            },
            CancellationToken.None);

        Assert.Equal(new HumanCliArgs(false, "^[0-9]+$", true, false, Centaur.CodexInstructions, "bashrc text", view, TimeSpan.FromMilliseconds(5)), captured);
        Assert.Null(result.LimitError);
        Assert.Equal(["fix the bug", "submitted"], result.State.Messages.Select(m => m.Text));
        Assert.Single(state.Messages);
    }

    [Fact]
    public void default_agent_factory_builds_the_human_cli_agent()
    {
        var agent = Centaur.DefaultAgentFactory(new HumanCliArgs(true, null, false, true, Centaur.ClaudeInstructions, "", null, null));

        Assert.Equal(HumanCli.AgentName, agent.Name);
    }
}
