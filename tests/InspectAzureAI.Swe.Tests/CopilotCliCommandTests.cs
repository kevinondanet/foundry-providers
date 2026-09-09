using InspectAzureAI.Eval.Agents;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.CopilotCli;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

// The static entry point shares its name with its namespace, which shadows it from sibling namespaces.
using CopilotCliAgents = InspectAzureAI.Swe.CopilotCli.CopilotCli;

/// <summary>The argv of the Copilot CLI agent (the flags the 1.0.83 probe verified), its prompt prefix and the shared launch wrapper.</summary>
public class CopilotCliCommandTests
{
    private const string Cwd = "/workspace";

    [Fact]
    public void base_flags_default_to_yolo_json_output_and_the_hygiene_flags()
    {
        var flags = CopilotCliCommand.BaseFlags("inspect", new CopilotCliOptions(), Cwd);

        Assert.Equal(
            [
                "--output-format", "json", "--model", "inspect", "--no-auto-update", "--no-ask-user", "--disable-builtin-mcps", "--yolo",
                "--log-level", "error", "--log-dir", "/workspace/.copilot/logs",
            ],
            flags);
    }

    [Fact]
    public void base_flags_carry_effort_allow_list_denies_agent_plugins_dirs_and_usage_file()
    {
        var options = new CopilotCliOptions
        {
            Effort = "high",
            Permission = CopilotCliPermission.AllowList,
            AllowedTools = ["shell", "write"],
            DeniedTools = ["fetch"],
            CustomAgent = "hve-implementer",
            PluginDirs = ["/opt/hve", "/opt/other"],
            AddDirs = ["/data"],
            NoCustomInstructions = true,
            DisableBuiltinMcps = false,
            NoAskUser = false,
            UsageOutputFile = "/workspace/.copilot/usage.json",
        };

        var flags = CopilotCliCommand.BaseFlags("gpt-5", options, Cwd + "/");

        Assert.Equal(
            [
                "--output-format", "json", "--model", "gpt-5", "--effort", "high", "--no-auto-update", "--no-custom-instructions",
                "--allow-all-paths", "--allow-tool=shell", "--allow-tool=write", "--deny-tool=fetch", "--agent", "hve-implementer",
                "--plugin-dir", "/opt/hve", "--plugin-dir", "/opt/other", "--add-dir", "/data", "--usage-output-file", "/workspace/.copilot/usage.json",
                "--log-level", "error", "--log-dir", "/workspace/.copilot/logs",
            ],
            flags);
        Assert.DoesNotContain("--yolo", flags);
        Assert.DoesNotContain("--no-ask-user", flags);
        Assert.DoesNotContain("--disable-builtin-mcps", flags);
    }

    [Fact]
    public void argv_starts_a_session_or_resumes_it_with_the_prompt_after_dash_p()
    {
        var flags = new[] { "--model", "m" };

        var fresh = CopilotCliCommand.Build("/bin/copilot", "sid", isResume: false, flags, "do it");
        var resumed = CopilotCliCommand.Build("/bin/copilot", "sid", isResume: true, flags, "again");

        Assert.Equal(["/bin/copilot", "-p", "do it", "--session-id", "sid", "--model", "m"], fresh);
        Assert.Equal(["/bin/copilot", "-p", "again", "--resume=sid", "--model", "m"], resumed);
    }

    [Fact]
    public void system_texts_are_prepended_to_the_first_prompt_only()
    {
        var messages = new ChatMessage[] { new ChatMessageSystem("One"), new ChatMessageUser("q"), new ChatMessageSystem("Two") };
        var texts = CopilotCliCommand.SystemTexts(messages, "Agent");

        Assert.Equal(["One", "Two", "Agent"], texts);
        Assert.Equal(["One", "Two"], CopilotCliCommand.SystemTexts(messages, null));
        Assert.Equal("One\n\nTwo\n\nAgent\n\nFix it", CopilotCliCommand.Prompt(texts, "Fix it", isResume: false));
        Assert.Equal("Fix it", CopilotCliCommand.Prompt(texts, "Fix it", isResume: true));
        Assert.Equal("Fix it", CopilotCliCommand.Prompt([], "Fix it", isResume: false));
    }

    [Fact]
    public void launch_wrapper_closes_stdin_before_exec_and_home_is_prepared_with_argv()
    {
        Assert.Equal(["bash", "-c", "exec 0</dev/null; \"$@\"", "bash", "/bin/copilot", "-p", "hi"], CopilotCliCommand.Launch(["/bin/copilot", "-p", "hi"]));
        Assert.Equal(["mkdir", "-p", "/workspace/.copilot/logs"], CopilotCliCommand.PrepareHomeCommand(Cwd));
    }
}

/// <summary>The option defaults and the validation of the combinations the CLI would reject at launch.</summary>
public class CopilotCliOptionsTests
{
    [Fact]
    public void defaults_match_the_contract()
    {
        var options = new CopilotCliOptions();

        Assert.Equal("copilot_cli", options.Name);
        Assert.Equal("GitHub Copilot CLI agent", options.Description);
        Assert.Equal("inspect", options.Model);
        Assert.Equal("auto", options.Version);
        Assert.Equal("1.0.83", CopilotCliOptions.DefaultVersion);
        Assert.Equal("https://github.com/github/copilot-cli/releases/download", CopilotCliOptions.DefaultReleaseBaseUrl);
        Assert.Equal(CopilotCliProvider.OpenAI, options.Provider);
        Assert.Equal(CopilotCliPermission.Yolo, options.Permission);
        Assert.True(options.DisableBuiltinMcps);
        Assert.True(options.NoAskUser);
        Assert.False(options.NoCustomInstructions);
        Assert.False(options.Otel);
        Assert.Equal(3, options.RetryRefusals);
        Assert.Equal(3, options.RetryUncaughtErrors);
        Assert.Equal(new AgentAttempts(), options.Attempts);
        Assert.Equal(0, options.Port);
        options.Validate();
    }

    [Fact]
    public void unknown_effort_empty_model_and_an_empty_allow_list_are_rejected()
    {
        var effort = Assert.Throws<ArgumentException>(() => new CopilotCliOptions { Effort = "extreme" }.Validate());
        var model = Assert.Throws<ArgumentException>(() => new CopilotCliOptions { Model = " " }.Validate());
        var allow = Assert.Throws<ArgumentException>(() => new CopilotCliOptions { Permission = CopilotCliPermission.AllowList }.Validate());

        Assert.Equal("effort must be one of 'none', 'minimal', 'low', 'medium', 'high', 'xhigh', or 'max'.", effort.Message);
        Assert.Contains("model", model.Message);
        Assert.Contains("AllowList", allow.Message);
        foreach (var level in CopilotCliOptions.EffortLevels)
        {
            new CopilotCliOptions { Effort = level }.Validate();
        }

        new CopilotCliOptions { Permission = CopilotCliPermission.AllowList, AllowedTools = ["shell"] }.Validate();

        // An undefined permission (a cast) would otherwise launch with --allow-all-paths and no tool at all.
        var permission = Assert.Throws<ArgumentException>(() => new CopilotCliOptions { Permission = (CopilotCliPermission)7, AllowedTools = ["shell"] }.Validate());
        Assert.Equal("permission must be Yolo or AllowList.", permission.Message);
        Assert.Throws<ArgumentException>(() => new CopilotCliOptions { Provider = (CopilotCliProvider)7 }.Validate());
    }

    [Fact]
    public void agent_definition_carries_name_description_and_a_per_instance_session()
    {
        var def = CopilotCliAgents.Agent(new CopilotCliOptions { Name = "cp", Description = "desc" });
        var first = new CopilotCliAgent(new CopilotCliOptions());
        var second = new CopilotCliAgent(new CopilotCliOptions());

        Assert.Equal("cp", def.Name);
        Assert.Equal("desc", def.Description);
        Assert.NotNull(def.Execute);
        Assert.True(Guid.TryParse(first.SessionId, out _));
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.EndsWith(Path.Combine(".cache", "inspect-azureai", "copilot-cli-downloads"), first.Binary.CacheDir);
        Assert.Equal(CopilotCliOptions.DefaultReleaseBaseUrl, first.Binary.ReleaseBaseUrl);
    }
}

/// <summary>The BYOK environment of the Copilot CLI subprocess (wire facts of the 1.0.83 probe) and the caller override.</summary>
public class CopilotCliEnvTests
{
    private static CopilotCliModels Models() => CopilotCliModels.Resolve(new Model(new ScriptedModelApi([], "model")));

    [Fact]
    public void openai_provider_points_at_the_bridge_v1_root_with_the_token_as_api_key()
    {
        var env = CopilotCliEnv.Build("http://host.docker.internal:13337", "tok-abc", Models(), "/workspace");

        Assert.Equal(
            ["COPILOT_PROVIDER_TYPE", "COPILOT_PROVIDER_BASE_URL", "COPILOT_PROVIDER_API_KEY", "COPILOT_MODEL", "COPILOT_OFFLINE", "COPILOT_AUTO_UPDATE", "COPILOT_HOME", "COPILOT_ALLOW_ALL"],
            env.Keys);
        Assert.Equal("openai", env["COPILOT_PROVIDER_TYPE"]);
        Assert.Equal("http://host.docker.internal:13337/v1", env["COPILOT_PROVIDER_BASE_URL"]);
        Assert.Equal("tok-abc", env["COPILOT_PROVIDER_API_KEY"]);
        Assert.Equal("inspect", env["COPILOT_MODEL"]);
        Assert.Equal("true", env["COPILOT_OFFLINE"]);
        Assert.Equal("false", env["COPILOT_AUTO_UPDATE"]);
        Assert.Equal("/workspace/.copilot", env["COPILOT_HOME"]);
        Assert.Equal("true", env["COPILOT_ALLOW_ALL"]);
    }

    [Fact]
    public void anthropic_provider_uses_the_bare_bridge_root_and_an_allow_list_drops_allow_all()
    {
        var env = CopilotCliEnv.Build("http://127.0.0.1:4321/", "tok", Models(), "/srv/app/", CopilotCliProvider.Anthropic, CopilotCliPermission.AllowList);

        Assert.Equal("anthropic", env["COPILOT_PROVIDER_TYPE"]);
        Assert.Equal("http://127.0.0.1:4321", env["COPILOT_PROVIDER_BASE_URL"]);
        Assert.Equal("/srv/app/.copilot", env["COPILOT_HOME"]);
        Assert.False(env.ContainsKey("COPILOT_ALLOW_ALL"));
    }

    [Fact]
    public void otel_adds_the_file_exporter_trio_under_the_home()
    {
        var env = CopilotCliEnv.Build("http://127.0.0.1:1", "tok", Models(), "/w", otel: true);

        Assert.Equal("true", env["COPILOT_OTEL_ENABLED"]);
        Assert.Equal("file", env["COPILOT_OTEL_EXPORTER_TYPE"]);
        Assert.Equal("/w/.copilot/otel.jsonl", env["COPILOT_OTEL_FILE_EXPORTER_PATH"]);
        Assert.False(CopilotCliEnv.Build("http://127.0.0.1:1", "tok", Models(), "/w").ContainsKey("COPILOT_OTEL_ENABLED"));
    }

    [Fact]
    public void caller_env_overrides_defaults_and_adds_new_keys()
    {
        var env = CopilotCliEnv.Build("http://127.0.0.1:1", "tok", Models(), "/w", env: new Dictionary<string, string> { ["COPILOT_OFFLINE"] = "false", ["CUSTOM"] = "x" });

        Assert.Equal("false", env["COPILOT_OFFLINE"]);
        Assert.Equal("x", env["CUSTOM"]);
        Assert.Equal("CUSTOM", env.Keys.Last());
    }

    [Fact]
    public void a_caller_copilot_home_moves_the_log_dir_the_home_preparation_and_the_otel_file()
    {
        var caller = new Dictionary<string, string> { ["COPILOT_HOME"] = "/state/copilot" };
        var options = new CopilotCliOptions { Env = caller, Otel = true };

        var env = CopilotCliEnv.Build("http://127.0.0.1:1", "tok", Models(), "/w", otel: true, env: caller);
        var flags = CopilotCliCommand.BaseFlags("inspect", options, "/w");

        Assert.Equal("/state/copilot", env["COPILOT_HOME"]);
        Assert.Equal("/state/copilot/otel.jsonl", env["COPILOT_OTEL_FILE_EXPORTER_PATH"]);
        Assert.Equal("/state/copilot/logs", flags[flags.ToList().IndexOf("--log-dir") + 1]);
        Assert.Equal(["mkdir", "-p", "/state/copilot/logs"], CopilotCliCommand.PrepareHomeCommand("/w", caller));
        Assert.Equal("/w/.copilot", CopilotCliEnv.ResolveHome("/w", new Dictionary<string, string> { ["COPILOT_HOME"] = "" }));
    }

    [Fact]
    public void presented_model_config_flows_into_copilot_model()
    {
        var models = CopilotCliModels.Resolve(new Model(new ScriptedModelApi([], "served")), "inspect", "claude-sonnet-4");

        Assert.Equal("claude-sonnet-4", CopilotCliEnv.Build("http://127.0.0.1:1", "tok", models, "/w")["COPILOT_MODEL"]);
    }
}

/// <summary>The presented-versus-served model split of the Copilot CLI agent and the host-side effort.</summary>
public class CopilotCliModelsTests
{
    private static Model Served(string name = "model") => new(new ScriptedModelApi([], name));

    [Fact]
    public void defaults_present_inspect_as_an_alias_of_the_served_model()
    {
        var served = Served();

        var models = CopilotCliModels.Resolve(served);

        Assert.Equal("inspect", models.Presented);
        Assert.Same(served, Assert.Single(models.Aliases).Value);
        Assert.Same(served, models.Aliases["inspect"]);
        Assert.Same(served, models.Served);
    }

    [Fact]
    public void model_config_overrides_the_presented_identity_and_keeps_the_model_alias()
    {
        var served = Served();

        var models = CopilotCliModels.Resolve(served, "inspect", "gpt-5");

        Assert.Equal("gpt-5", models.Presented);
        Assert.Equal(["gpt-5", "inspect"], models.Aliases.Keys);
        Assert.Same(served, models.Aliases["gpt-5"]);
        Assert.Same(served, models.Aliases["inspect"]);
    }

    [Fact]
    public void effort_sets_reasoning_effort_on_a_copy_of_the_served_model()
    {
        var served = new Model(new ScriptedModelApi([], "model"), new GenerateConfig { Temperature = 0.2, ReasoningEffort = "low" }) { Role = "grader", AdaptiveConnections = InspectAzureAI.Eval.Concurrency.AdaptiveConnections.Disabled };

        var models = CopilotCliModels.Resolve(served, effort: "xhigh");

        Assert.Equal("xhigh", models.Served.Config.ReasoningEffort);
        Assert.Equal("grader", models.Served.Role);
        Assert.Same(InspectAzureAI.Eval.Concurrency.AdaptiveConnections.Disabled, models.Served.AdaptiveConnections);
        Assert.Equal(0.2, models.Served.Config.Temperature);
        Assert.NotSame(served, models.Served);
        Assert.Same(served.Api, models.Served.Api);
        Assert.Equal("low", served.Config.ReasoningEffort);
        Assert.Same(models.Served, models.Aliases["inspect"]);
        Assert.Same(served, CopilotCliModels.Resolve(served).Served);
    }
}
