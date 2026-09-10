using InspectAzureAI.HveDemo;
using InspectAzureAI.HveDemo.Components;

namespace InspectAzureAI.HveDemo.Tests;

/// <summary>The two axes of the matrix: <see cref="HveVariant"/> and how <c>Program.Options</c> parses <c>--harness</c>, <c>--framework</c> and the deprecated <c>--solver</c>.</summary>
public sealed class HveOptionsTests
{
    [Theory]
    [InlineData("gpt-5.4-mini")]                    // bare deployment: Foundry, route by name
    [InlineData("openai/azure/gpt-5.6-sol")]        // Foundry deployment named explicitly
    [InlineData("anthropic/azure/claude-sonnet-4-6")]
    [InlineData("openai/gpt-5.6-sol")]              // direct api.openai.com
    [InlineData("anthropic/claude-sonnet-4-6")]     // direct api.anthropic.com
    public void model_names_reach_the_router_unchanged(string model)
    {
        // The demo hands --model to Models.Create, which owns provider prefixes, so nothing here may
        // rewrite or reject a name: a prefixed model is how the direct providers are reached at all.
        Assert.Equal(model, Program.Options.Parse(["--fake", "--model", model]).Model);
    }

    [Theory]
    [InlineData("models")]
    [InlineData("anthropic")]
    [InlineData("responses")]
    public void foundry_routes_are_carried_through(string route)
    {
        Assert.Equal(route, Program.Options.Parse(["--fake", "--route", route]).Route);
    }

    [Fact]
    public void variant_parses_and_labels_the_four_cells()
    {
        var original = HveVariant.Parse("Copilot", "HVE");
        Assert.Equal("copilot+hve", original.Label);
        Assert.True(original.IsCopilot);
        Assert.True(original.UsesFramework);

        var baseline = HveVariant.Parse("generic", "none");
        Assert.False(baseline.IsCopilot);
        Assert.False(baseline.UsesFramework);

        var pluginOnly = HveVariant.Parse("generic", "hve");
        Assert.True(pluginOnly.UsesFramework);
        Assert.False(pluginOnly.IsCopilot);

        var cliOnly = HveVariant.Parse("copilot", "none");
        Assert.True(cliOnly.IsCopilot);
        Assert.False(cliOnly.UsesFramework);

        Assert.Equal(new HveVariant("copilot", "hve"), HveVariant.Default);
    }

    [Fact]
    public void variant_rejects_unknown_names_with_the_flag_messages()
    {
        Assert.StartsWith("--harness expects", Assert.Throws<ArgumentException>(() => HveVariant.Parse("swarm", "hve")).Message);
        Assert.StartsWith("--framework expects", Assert.Throws<ArgumentException>(() => HveVariant.Parse("copilot", "all")).Message);
        Assert.StartsWith("--solver expects", Assert.Throws<ArgumentException>(() => HveVariant.FromSolverAlias("swarm")).Message);
    }

    [Fact]
    public void solver_alias_maps_to_the_cells_and_is_reported()
    {
        var copilot = Program.Options.Parse(["--fake", "--solver", "copilot"]);
        Assert.Equal("copilot", copilot.Harness);
        Assert.Equal("hve", copilot.Framework);
        Assert.Equal("copilot", copilot.SolverAlias);

        var basic = Program.Options.Parse(["--fake", "--solver", "basic"]);
        Assert.Equal("generic", basic.Harness);
        Assert.Equal("none", basic.Framework);
        Assert.Equal("basic", basic.SolverAlias);

        Assert.StartsWith("--solver expects", Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--fake", "--solver", "swarm"])).Message);
    }

    [Fact]
    public void later_harness_or_framework_flags_win_over_the_alias()
    {
        var mixed = Program.Options.Parse(["--fake", "--solver", "basic", "--framework", "hve"]);
        Assert.Equal("generic", mixed.Harness);
        Assert.Equal("hve", mixed.Framework);
        Assert.Equal("basic", mixed.SolverAlias);

        // Last wins in the other direction too: the alias sets both axes over an earlier flag.
        var overridden = Program.Options.Parse(["--fake", "--harness", "generic", "--solver", "copilot"]);
        Assert.Equal("copilot", overridden.Harness);
        Assert.Equal("hve", overridden.Framework);
    }

    [Fact]
    public void defaults_are_copilot_and_hve()
    {
        var options = Program.Options.Parse(["--fake"]);
        Assert.Equal("copilot", options.Harness);
        Assert.Equal("hve", options.Framework);
        Assert.Null(options.SolverAlias);
        Assert.Equal(HveVariant.Default, options.Variant);
    }

    [Fact]
    public void plugin_dir_needs_the_hve_framework()
    {
        var error = Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--fake", "--framework", "none", "--plugin-dir", "/x"]));
        Assert.Contains("--plugin-dir needs --framework hve", error.Message);
        Assert.Equal("/x", Program.Options.Parse(["--fake", "--plugin-dir", "/x"]).PluginDir);

        // The deprecated --solver basic took a --plugin-dir before the split (it only suppressed the plugin copy), so an old
        // command line still parses; RunAsync's deprecation note says the directory is ignored.
        var alias = Program.Options.Parse(["--fake", "--solver", "basic", "--plugin-dir", "/x"]);
        Assert.Equal("/x", alias.PluginDir);
        Assert.Equal("none", alias.Framework);
        Assert.Equal("basic", alias.SolverAlias);

        // An explicit --framework none after the alias is a deliberate choice, and is rejected again.
        Assert.Throws<ArgumentException>(() => Program.Options.Parse(["--fake", "--solver", "basic", "--framework", "none", "--plugin-dir", "/x"]));
    }

    [Fact]
    public void variant_property_validates_a_hand_built_record()
    {
        var options = new Program.Options(false, true, "suite", "swarm", "hve", null, null, null, "fake", null, null, null, "logs", "auto", null, false, true);
        Assert.Throws<ArgumentException>(() => options.Variant);
    }
}
