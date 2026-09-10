using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Port of inspect_swe <c>tests/test_claude_code_env.py</c>: the Claude Code subprocess environment, including the inverted-polarity MCP flag.</summary>
public class ClaudeCodeEnvTests
{
    private static ClaudeCodeModels Models() => ClaudeCodeModels.Resolve(new Model(new ScriptedModelApi([], "model")));

    private static IReadOnlyDictionary<string, string> Env(IReadOnlyDictionary<string, string>? overrides = null) =>
        ClaudeCodeEnv.Build("http://host.docker.internal:13337", "tok-abc", Models(), overrides);

    [Fact]
    public void bridge_base_url_and_token_are_wired_in()
    {
        var env = Env();

        Assert.Equal("http://host.docker.internal:13337", env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("tok-abc", env["ANTHROPIC_AUTH_TOKEN"]);
    }

    [Fact]
    public void presented_model_identities_are_populated()
    {
        var env = Env();

        Assert.Equal("model", env["ANTHROPIC_MODEL"]);
        Assert.Equal("model", env["ANTHROPIC_DEFAULT_OPUS_MODEL"]);
        Assert.Equal("model", env["ANTHROPIC_DEFAULT_SONNET_MODEL"]);
        Assert.Equal("model", env["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
        Assert.Equal("model", env["CLAUDE_CODE_SUBAGENT_MODEL"]);
        Assert.Equal("model", env["ANTHROPIC_SMALL_FAST_MODEL"]);
    }

    [Fact]
    public void role_models_set_their_own_env_names()
    {
        var models = ClaudeCodeModels.Resolve(
            new Model(new ScriptedModelApi([], "model")),
            haikuModel: new Model(new ScriptedModelApi([], "small")),
            subagentModel: new Model(new ScriptedModelApi([], "helper")));

        var env = ClaudeCodeEnv.Build("http://h", "t", models);

        Assert.Equal("model", env["ANTHROPIC_MODEL"]);
        Assert.Equal("model", env["ANTHROPIC_DEFAULT_OPUS_MODEL"]);
        Assert.Equal("model", env["ANTHROPIC_DEFAULT_SONNET_MODEL"]);
        Assert.Equal("small", env["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
        Assert.Equal("small", env["ANTHROPIC_SMALL_FAST_MODEL"]);
        Assert.Equal("helper", env["CLAUDE_CODE_SUBAGENT_MODEL"]);
    }

    [Fact]
    public void mcp_connection_is_blocking_by_default()
    {
        var value = Env()["MCP_CONNECTION_NONBLOCKING"];

        // Inverted polarity: only an explicitly falsy token makes connection block, so membership is what matters.
        Assert.Contains(value, ClaudeCodeEnv.FalsyValues);
        Assert.DoesNotContain(value, ClaudeCodeEnv.TruthyValues);
    }

    [Fact]
    public void mcp_startup_budgets_cover_a_slow_sandbox()
    {
        var env = Env();

        foreach (var key in new[] { "MCP_TIMEOUT", "MCP_CONNECT_TIMEOUT_MS" })
        {
            Assert.True(int.Parse(env[key], System.Globalization.CultureInfo.InvariantCulture) >= 120_000, $"{key}={env[key]} is too small");
        }
    }

    [Fact]
    public void caller_env_overrides_defaults_and_adds_new_keys()
    {
        var env = Env(new Dictionary<string, string> { ["IS_SANDBOX"] = "0", ["CUSTOM"] = "x" });

        Assert.Equal("0", env["IS_SANDBOX"]);
        Assert.Equal("x", env["CUSTOM"]);
        Assert.Equal("CUSTOM", env.Keys.Last());
    }

    [Fact]
    public void caller_can_override_the_mcp_defaults()
    {
        var env = Env(new Dictionary<string, string> { ["MCP_CONNECTION_NONBLOCKING"] = "1" });

        Assert.Equal("1", env["MCP_CONNECTION_NONBLOCKING"]);
    }

    [Fact]
    public void blocking_mcp_env_is_applied_verbatim()
    {
        var env = Env();

        foreach (var (key, value) in ClaudeCodeEnv.BlockingMcpEnv)
        {
            Assert.Equal(value, env[key]);
        }
    }

    [Fact]
    public void auto_memory_is_disabled_by_default()
    {
        var value = Env()["CLAUDE_CODE_DISABLE_AUTO_MEMORY"];

        Assert.Contains(value, ClaudeCodeEnv.TruthyValues);
    }

    [Fact]
    public void caller_can_re_enable_auto_memory()
    {
        var env = Env(new Dictionary<string, string> { ["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "0" });

        Assert.Equal("0", env["CLAUDE_CODE_DISABLE_AUTO_MEMORY"]);
    }

    [Fact]
    public void variables_keep_python_source_order()
    {
        var keys = Env().Keys.ToArray();

        Assert.Equal(
            [
                "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL",
                "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "CLAUDE_CODE_SUBAGENT_MODEL",
                "ANTHROPIC_SMALL_FAST_MODEL", "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS",
                "IS_SANDBOX", "MCP_CONNECTION_NONBLOCKING", "MCP_TIMEOUT", "MCP_CONNECT_TIMEOUT_MS", "CLAUDE_CODE_DISABLE_AUTO_MEMORY",
            ],
            keys);
        Assert.Equal("1", Env()["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        Assert.Equal("1", Env()["CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS"]);
        Assert.Equal("1", Env()["IS_SANDBOX"]);
    }
}
