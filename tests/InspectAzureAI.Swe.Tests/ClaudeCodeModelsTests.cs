using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Swe.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// Port of inspect_swe <c>tests/test_claude_code_model.py</c> and <c>test_claude_code_effort.py</c>: presented
/// identities, per-role models, aliases and host-side effort.
/// </summary>
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
    public void unset_roles_inherit_the_presented_name()
    {
        var models = ClaudeCodeModels.Resolve(Served(), "claude-opus-4-1", sonnetModel: Served("mid"));

        Assert.Equal("claude-opus-4-1", models.Opus);
        Assert.Equal("mid", models.Sonnet);
        Assert.Equal("claude-opus-4-1", models.Haiku);
        Assert.Equal("claude-opus-4-1", models.Subagent);
    }

    [Fact]
    public void set_roles_register_their_own_name_and_alias_in_role_order()
    {
        var served = Served();
        var opus = Served("big");
        var sonnet = Served("mid");
        var haiku = Served("small");
        var subagent = Served("helper");

        var models = ClaudeCodeModels.Resolve(served, opusModel: opus, sonnetModel: sonnet, haikuModel: haiku, subagentModel: subagent);

        Assert.Equal(("big", "mid", "small", "helper"), (models.Opus, models.Sonnet, models.Haiku, models.Subagent));
        Assert.Equal(["model", "big", "mid", "small", "helper"], models.Aliases.Keys);
        Assert.Same(served, models.Aliases["model"]);
        Assert.Same(opus, models.Aliases["big"]);
        Assert.Same(sonnet, models.Aliases["mid"]);
        Assert.Same(haiku, models.Aliases["small"]);
        Assert.Same(subagent, models.Aliases["helper"]);
    }

    [Fact]
    public void a_role_sharing_the_presented_name_replaces_its_alias()
    {
        var served = Served();
        var haiku = Served("model");

        var models = ClaudeCodeModels.Resolve(served, haikuModel: haiku);

        Assert.Same(haiku, Assert.Single(models.Aliases).Value);
        Assert.Same(served, models.Served);
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
    public void caller_model_aliases_override_role_aliases()
    {
        var replacement = Served("replacement");

        var models = ClaudeCodeModels.Resolve(Served(), subagentModel: Served("helper"), modelAliases: new Dictionary<string, Model> { ["helper"] = replacement });

        Assert.Equal("helper", models.Subagent);
        Assert.Same(replacement, models.Aliases["helper"]);
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
    public void effort_applies_to_the_served_model_only()
    {
        var opus = Served("big");

        var models = ClaudeCodeModels.Resolve(Served(), effort: "high", opusModel: opus);

        Assert.Equal("high", models.Served.Config.ReasoningEffort);
        Assert.Same(opus, models.Aliases["big"]);
        Assert.Null(models.Aliases["big"].Config.ReasoningEffort);
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
