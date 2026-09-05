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

/// <summary>Port of inspect_swe <c>tests/test_claude_code_model.py</c> and <c>test_claude_code_effort.py</c>: presented identities, aliases and host-side effort.</summary>
public class ClaudeCodeModelsTests
{
    private static Model Served(string name = "model") => new(new ScriptedModelApi([], name));

    [Fact]
    public void defaults_present_served_model_and_share_one_alias()
    {
        var served = Served();

        var models = ClaudeCodeModels.Resolve(served);

        Assert.Equal("model", models.Presented);
        Assert.Equal("model", models.Opus);
        Assert.Equal("model", models.Sonnet);
        Assert.Equal("model", models.Haiku);
        Assert.Equal("model", models.Subagent);
        Assert.Same(served, Assert.Single(models.Aliases).Value);
        Assert.Same(served, models.Served);
    }

    [Fact]
    public void model_config_overrides_presented_identity()
    {
        var served = Served();

        var models = ClaudeCodeModels.Resolve(served, "claude-sonnet-4-5");

        Assert.Equal("claude-sonnet-4-5", models.Presented);
        Assert.Equal("claude-sonnet-4-5", models.Haiku);
        Assert.Same(served, models.Aliases["claude-sonnet-4-5"]);
    }

    [Fact]
    public void caller_model_aliases_take_precedence()
    {
        var served = Served();
        var overridden = Served("override");

        var models = ClaudeCodeModels.Resolve(served, modelAliases: new Dictionary<string, Model> { ["model"] = overridden, ["extra"] = overridden });

        Assert.Same(overridden, models.Aliases["model"]);
        Assert.Same(overridden, models.Aliases["extra"]);
        Assert.Equal(["model", "extra"], models.Aliases.Keys);
    }

    [Fact]
    public void effort_sets_reasoning_effort_on_a_copy_of_the_served_model()
    {
        var served = Served();

        var models = ClaudeCodeModels.Resolve(served, effort: "max");

        var alias = models.Aliases[models.Presented];
        Assert.Equal("max", alias.Config.ReasoningEffort);
        Assert.NotSame(served, alias);
        Assert.Same(served.Api, alias.Api);
        Assert.Null(served.Config.ReasoningEffort);
        Assert.Same(alias, models.Served);
    }

    [Fact]
    public void unconfigured_effort_leaves_served_model_config_untouched()
    {
        var served = Served();

        var models = ClaudeCodeModels.Resolve(served, effort: null);

        Assert.Null(models.Aliases[models.Presented].Config.ReasoningEffort);
        Assert.Same(served, models.Aliases[models.Presented]);
    }

    [Fact]
    public void effort_does_not_override_caller_supplied_model_aliases()
    {
        var overridden = Served("override");

        var models = ClaudeCodeModels.Resolve(Served(), effort: "high", modelAliases: new Dictionary<string, Model> { ["model"] = overridden });

        Assert.Same(overridden, models.Aliases["model"]);
        Assert.Null(overridden.Config.ReasoningEffort);
        Assert.Equal("high", models.Served.Config.ReasoningEffort);
    }

    [Fact]
    public void effort_merges_over_the_existing_config()
    {
        var served = new Model(new ScriptedModelApi([], "model"), new GenerateConfig { Temperature = 0.2, ReasoningEffort = "low" });

        var models = ClaudeCodeModels.Resolve(served, effort: "xhigh");

        Assert.Equal("xhigh", models.Served.Config.ReasoningEffort);
        Assert.Equal(0.2, models.Served.Config.Temperature);
    }
}
