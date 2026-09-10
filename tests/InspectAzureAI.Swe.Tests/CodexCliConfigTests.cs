using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools.Mcp;
using InspectAzureAI.Swe.CodexCli;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>Port of <c>tests/test_codex_config.py</c> plus the golden <c>config.toml</c> documents and option validation.</summary>
public class CodexCliConfigTests
{
    private const string BaseUrl = "http://host.docker.internal:54321";

    private const string GoldenDefault = """
        web_search = "live"
        features.goals = true
        preferred_auth_method = "apikey"
        model_provider = "openai-proxy"

        [analytics]
        enabled = false

        [model_providers.openai-proxy]
        name = "OpenAI Proxy"
        base_url = "http://host.docker.internal:54321/v1"
        env_key = "OPENAI_API_KEY"
        wire_api = "responses"
        stream_idle_timeout_ms = 3600000
        """;

    private const string GoldenAutoReview = """
        web_search = "cached"
        features.goals = false
        approval_policy = "on-request"
        sandbox_mode = "workspace-write"
        approvals_reviewer = "auto_review"
        features.guardian_approval = true
        preferred_auth_method = "apikey"
        model_provider = "openai-proxy"

        [analytics]
        enabled = false

        [auto_review]
        policy = "Never allow curl.\nAllow pip."

        [mcp_servers.fs]
        command = "npx"
        args = ["-y", "@modelcontextprotocol/server-filesystem", "/workspace"]
        env = { ROOT = "/workspace" }

        [mcp_servers.secrets]
        url = "http://host.docker.internal:54321/mcp/secrets"
        bearer_token_env_var = "OPENAI_API_KEY"

        [model_providers.openai-proxy]
        name = "OpenAI Proxy"
        base_url = "http://host.docker.internal:54321/v1"
        env_key = "OPENAI_API_KEY"
        wire_api = "responses"
        stream_idle_timeout_ms = 3600000
        """;

    private static Model NewModel(string name) => new(new ScriptedModelApi([], name));

    [Fact]
    public void defaults_match_python()
    {
        var options = new CodexCliOptions();

        Assert.Equal(("codex_cli", CodexWebSearch.Live, true, "auto"), (options.Name, options.WebSearch, options.Goals, options.Version));
        Assert.Null(options.AutoReview);
        Assert.Null(options.Centaur);
        Assert.Null(options.RetryRefusals);
        Assert.Equal(1, options.Attempts.Attempts);
        Assert.Equal("Autonomous coding agent capable of writing, testing, debugging,\nand iterating on code across multiple languages.", options.Description);
        Assert.Equal("0.154.0", CodexCliOptions.TestedVersion);
    }

    [Fact]
    public void config_defaults()
    {
        var config = CodexCliConfig.ConfigOptions(CodexWebSearch.Live, goals: true, autoReview: null);

        Assert.Equal([KeyValuePair.Create<string, object?>("web_search", "live"), KeyValuePair.Create<string, object?>("features.goals", true)], config);
        var toml = Toml.Write(config);
        Assert.Contains("web_search = \"live\"", toml, StringComparison.Ordinal);
        Assert.Contains("features.goals = true", toml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CodexWebSearch.Live, "live")]
    [InlineData(CodexWebSearch.Cached, "cached")]
    [InlineData(CodexWebSearch.Disabled, "disabled")]
    public void web_search_modes_resolve_to_themselves(CodexWebSearch mode, string value)
    {
        Assert.Equal(mode, CodexCliConfig.ResolveWebSearch(mode, null));
        Assert.Equal(value, CodexCliConfig.WebSearchValue(mode));
    }

    [Fact]
    public void an_invalid_web_search_mode_throws_pythons_message()
    {
        var ex = Assert.Throws<ArgumentException>(() => CodexCliConfig.ResolveWebSearch((CodexWebSearch)42, null));
        Assert.StartsWith("web_search must be one of 'live', 'cached', or 'disabled'.", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new CodexCliOptions { WebSearch = (CodexWebSearch)42 }.Validate());
    }

    [Fact]
    public void the_obsolete_disallowed_tools_web_search_resolves_to_disabled()
    {
#pragma warning disable CS0618
        var options = new CodexCliOptions { DisallowedTools = ["web_search"] };
#pragma warning restore CS0618

        options.Validate();
        Assert.Equal(["web_search"], options.DeprecatedDisallowedTools);
        Assert.Equal(CodexWebSearch.Disabled, CodexCliConfig.ResolveWebSearch(options.WebSearch, options.DeprecatedDisallowedTools));
    }

    [Fact]
    public void deprecated_disallowed_tools_reject_unknown_tools_sorted()
    {
#pragma warning disable CS0618
        var options = new CodexCliOptions { DisallowedTools = ["web_search", "python", "bash", "bash"] };
#pragma warning restore CS0618

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal("Unsupported Codex disallowed_tools value(s): bash, python", ex.Message);
    }

    [Fact]
    public void cli_overrides_format_values_for_cli()
    {
        Assert.Equal(
            [KeyValuePair.Create("web_search", "\"cached\""), KeyValuePair.Create("features.goals", "false")],
            CodexCliConfig.CliOverrides(CodexWebSearch.Cached, goals: false, autoReview: null));
    }

    [Fact]
    public void config_options_auto_review_off_by_default()
    {
        var keys = CodexCliConfig.ConfigOptions(CodexWebSearch.Live, true, null).Select(pair => pair.Key).ToList();

        Assert.DoesNotContain("approvals_reviewer", keys);
        Assert.DoesNotContain("approval_policy", keys);
        Assert.DoesNotContain("sandbox_mode", keys);
    }

    [Fact]
    public void config_options_auto_review_enabled()
    {
        var config = CodexCliConfig.ConfigOptions(CodexWebSearch.Live, true, new CodexAutoReview());

        Assert.Equal(
            ["web_search", "features.goals", "approval_policy", "sandbox_mode", "approvals_reviewer", "features.guardian_approval"],
            config.Select(pair => pair.Key));
        Assert.Equal(true, config[^1].Value);
        var toml = Toml.Write(config);
        Assert.Contains("approvals_reviewer = \"auto_review\"", toml, StringComparison.Ordinal);
        Assert.Contains("approval_policy = \"on-request\"", toml, StringComparison.Ordinal);
    }

    [Fact]
    public void config_options_auto_review_policy_table()
    {
        var config = CodexCliConfig.ConfigOptions(CodexWebSearch.Live, true, new CodexAutoReview { Policy = "Never allow curl.\nAllow pip." });

        Assert.Equal("auto_review", config[^1].Key);
        var toml = Toml.Write(config);
        Assert.Contains("[auto_review]", toml, StringComparison.Ordinal);
        Assert.Contains("policy = \"Never allow curl.\\nAllow pip.\"", toml, StringComparison.Ordinal);
    }

    [Fact]
    public void cli_overrides_auto_review()
    {
        var overrides = CodexCliConfig.CliOverrides(CodexWebSearch.Live, true, new CodexAutoReview { Policy = "Never allow curl." }).ToDictionary();

        Assert.Equal("\"on-request\"", overrides["approval_policy"]);
        Assert.Equal("\"workspace-write\"", overrides["sandbox_mode"]);
        Assert.Equal("\"auto_review\"", overrides["approvals_reviewer"]);
        Assert.Equal("true", overrides["features.guardian_approval"]);
        Assert.DoesNotContain(overrides.Keys, key => key.StartsWith("auto_review", StringComparison.Ordinal));
        Assert.DoesNotContain("approvals_reviewer", CodexCliConfig.CliOverrides(CodexWebSearch.Live, true, null).Select(pair => pair.Key));
    }

    [Fact]
    public void auto_review_model_aliases_pass_through_without_a_guardian_model()
    {
        var existing = new Dictionary<string, Model> { ["alias"] = NewModel("other") };

        Assert.Same(existing, CodexCliConfig.AutoReviewAliases(new CodexAutoReview(), existing));
        Assert.Same(existing, CodexCliConfig.AutoReviewAliases(null, existing));
        Assert.Null(CodexCliConfig.AutoReviewAliases(new CodexAutoReview(), null));
    }

    [Fact]
    public void auto_review_model_aliases_bind_the_guardian_slug_over_a_caller_alias()
    {
        var guardian = NewModel("guardian");
        var other = NewModel("other");
        var existing = new Dictionary<string, Model> { ["alias"] = other, [CodexCliConfig.GuardianModelSlug] = NewModel("caller") };

        var aliases = CodexCliConfig.AutoReviewAliases(new CodexAutoReview { Model = guardian }, existing)!;

        Assert.Equal("codex-auto-review", CodexCliConfig.GuardianModelSlug);
        Assert.Equal(2, aliases.Count);
        Assert.Same(other, aliases["alias"]);
        Assert.Same(guardian, aliases["codex-auto-review"]);
        Assert.Same(guardian, CodexCliConfig.AutoReviewAliases(new CodexAutoReview { Model = guardian }, null)!["codex-auto-review"]);
    }

    [Fact]
    public void check_auto_review_version()
    {
        CodexCliConfig.CheckAutoReviewVersion("0.137.0");
        CodexCliConfig.CheckAutoReviewVersion("0.145.0");
        CodexCliConfig.CheckAutoReviewVersion("1.0.0");
        CodexCliConfig.CheckAutoReviewVersion(null);

        Assert.Contains("0.137.0", Assert.Throws<InvalidOperationException>(() => CodexCliConfig.CheckAutoReviewVersion("0.136.0")).Message, StringComparison.Ordinal);
        Assert.Contains("0.137.0", Assert.Throws<InvalidOperationException>(() => CodexCliConfig.CheckAutoReviewVersion("0.99.0")).Message, StringComparison.Ordinal);
        Assert.Equal(
            "auto_review requires Codex CLI >= 0.137.0 (found 0.136.9). Pass version='latest' (or an explicit newer version) to codex_cli().",
            Assert.Throws<InvalidOperationException>(() => CodexCliConfig.CheckAutoReviewVersion("0.136.9")).Message);
    }

    [Fact]
    public void golden_default_config_toml()
    {
        Assert.Equal(GoldenDefault, CodexCliConfig.BuildToml(CodexWebSearch.Live, true, null, [], [], BaseUrl));
    }

    [Fact]
    public void golden_auto_review_config_toml_with_static_and_bridged_mcp_servers()
    {
        var fs = new McpServerConfigStdio("fs", "npx")
        {
            Args = ["-y", "@modelcontextprotocol/server-filesystem", "/workspace"],
            Env = new Dictionary<string, string> { ["ROOT"] = "/workspace" },
        };
        var secrets = new McpServerConfigHttp("http", "secrets", $"{BaseUrl}/mcp/secrets", new Dictionary<string, string> { ["Authorization"] = "Bearer tok-123" });

        var toml = CodexCliConfig.BuildToml(CodexWebSearch.Cached, false, new CodexAutoReview { Policy = "Never allow curl.\nAllow pip." }, [fs], [secrets], BaseUrl);

        Assert.Equal(GoldenAutoReview, toml);
        Assert.DoesNotContain("tok-123", toml, StringComparison.Ordinal);
        var parsed = MiniToml.Parse(toml);
        Assert.Equal("Never allow curl.\nAllow pip.", parsed["auto_review"]["policy"]!.GetValue<string>());
        Assert.Equal("/workspace", parsed["mcp_servers.fs"]["env"]!["ROOT"]!.GetValue<string>());
    }

    [Fact]
    public void a_later_mcp_server_with_the_same_name_replaces_the_earlier_in_place()
    {
        var toml = CodexCliConfig.BuildToml(
            CodexWebSearch.Live,
            true,
            null,
            [new McpServerConfigStdio("secrets", "old-server"), new McpServerConfigStdio("fs", "npx")],
            [new McpServerConfigHttp("http", "secrets", $"{BaseUrl}/mcp/secrets")],
            BaseUrl);

        var parsed = MiniToml.Parse(toml);
        Assert.Equal($"{BaseUrl}/mcp/secrets", parsed["mcp_servers.secrets"]["url"]!.GetValue<string>());
        Assert.False(parsed["mcp_servers.secrets"].ContainsKey("command"));
        Assert.True(toml.IndexOf("[mcp_servers.secrets]", StringComparison.Ordinal) < toml.IndexOf("[mcp_servers.fs]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("sandbox")]
    [InlineData("stable")]
    [InlineData("latest")]
    [InlineData("0.154.0")]
    [InlineData("0.155.0-alpha.3")]
    public void validate_accepts_version_keywords_and_releases(string version)
    {
        new CodexCliOptions { Version = version }.Validate();
    }

    [Fact]
    public void validate_rejects_a_bad_version_and_a_negative_port()
    {
        Assert.Throws<ArgumentException>(new CodexCliOptions { Version = "v0.154.0" }.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(new CodexCliOptions { Port = -1 }.Validate);
    }
}
