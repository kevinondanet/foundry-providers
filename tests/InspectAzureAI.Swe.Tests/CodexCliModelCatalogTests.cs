using System.Text.Json.Nodes;
using InspectAzureAI.Swe.CodexCli;

namespace InspectAzureAI.Swe.Tests;

/// <summary>
/// Port of inspect_swe <c>tests/test_codex_model_catalog.py</c> against the string-based
/// <see cref="CodexCliModelCatalog.ResolveSlug"/> (the <c>IModelApi</c> adapter cases belong to the agent slice), plus
/// the verbatim reason texts and the bundled snapshot.
/// </summary>
public class CodexCliModelCatalogTests
{
    /// <summary>Python's <c>CATALOG</c>: real catalogs mark sub-5.4 slugs <c>supports_search_tool</c>, reflecting Codex's backend.</summary>
    private static JsonObject Catalog() => JsonNode.Parse("""
        {
          "models": [
            { "slug": "gpt-5.5", "priority": 0, "apply_patch_tool_type": "freeform", "supports_search_tool": true },
            { "slug": "gpt-5.4", "priority": 2, "apply_patch_tool_type": "freeform", "supports_search_tool": true },
            { "slug": "gpt-5.4-mini", "priority": 4, "apply_patch_tool_type": "freeform", "supports_search_tool": true },
            { "slug": "gpt-5.3-codex", "priority": 6, "apply_patch_tool_type": "freeform", "supports_search_tool": true },
            { "slug": "gpt-5.2", "priority": 10, "apply_patch_tool_type": "freeform", "supports_search_tool": true },
            { "slug": "gpt-5.0-lite", "priority": 20, "apply_patch_tool_type": "freeform", "supports_search_tool": false }
          ]
        }
        """)!.AsObject();

    private static string Slug(string modelName, bool openAi = true, bool useCatalog = true, string? modelConfig = null, bool isLatest = false) =>
        CodexCliModelCatalog.ResolveSlug(modelName, openAi, useCatalog ? Catalog() : null, modelConfig, isLatest).Slug;

    [Fact]
    public void latest_openai_slug_prefers_highest_priority_non_mini()
    {
        Assert.Equal("gpt-5.5", CodexCliModelCatalog.LatestSlug(Catalog()));
    }

    [Fact]
    public void latest_openai_slug_none_when_empty()
    {
        Assert.Null(CodexCliModelCatalog.LatestSlug(null));
        Assert.Null(CodexCliModelCatalog.LatestSlug(new JsonObject { ["models"] = new JsonArray() }));
        Assert.Null(CodexCliModelCatalog.LatestSlug(new JsonObject()));
    }

    [Fact]
    public void latest_openai_slug_falls_back_to_mini_when_only_option()
    {
        var catalog = JsonNode.Parse("""{"models": [{"slug": "gpt-5.4-mini", "priority": 4}]}""")!.AsObject();

        Assert.Equal("gpt-5.4-mini", CodexCliModelCatalog.LatestSlug(catalog));
    }

    [Fact]
    public void explicit_override_is_verbatim()
    {
        var result = CodexCliModelCatalog.ResolveSlug("claude-sonnet-4-0", openAi: false, Catalog(), modelConfig: "gpt-5.4", isLatest: false);

        Assert.Equal("gpt-5.4", result.Slug);
        Assert.Contains("override", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void openai_model_present_in_catalog_uses_real_name()
    {
        var result = CodexCliModelCatalog.ResolveSlug("gpt-5.5-preview", openAi: true, Catalog(), modelConfig: null, isLatest: false);

        Assert.Equal("gpt-5.5-preview", result.Slug);
        Assert.Contains("matches Codex catalog", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void pre_tool_search_models_pass_through_to_generic_fallback()
    {
        var result = CodexCliModelCatalog.ResolveSlug("gpt-5", openAi: true, Catalog(), modelConfig: null, isLatest: false);

        Assert.Equal("gpt-5", result.Slug);
        Assert.Contains("predates tool_search", result.Reason, StringComparison.Ordinal);
        Assert.Equal("gpt-5.1", Slug("gpt-5.1"));
        Assert.Equal("gpt-5.1-codex", Slug("gpt-5.1-codex"));
        Assert.Equal("gpt-5-codex", Slug("gpt-5-codex"));
        Assert.Equal("gpt-4.1", Slug("gpt-4.1"));
        Assert.Equal("o3", Slug("o3"));
    }

    [Theory]
    [InlineData("gpt-5.2")]
    [InlineData("gpt-5.3-codex")]
    public void catalog_present_sub_boundary_model_forces_generic_fallback(string name)
    {
        var result = CodexCliModelCatalog.ResolveSlug(name, openAi: true, Catalog(), modelConfig: null, isLatest: false);

        Assert.Equal(CodexCliModelCatalog.GenericFallbackSlug, result.Slug);
        Assert.Contains("tool_search", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Catalog()["models"]!.AsArray(),
            model => CodexCliModelCatalog.GenericFallbackSlug.StartsWith(model!["slug"]!.GetValue<string>(), StringComparison.Ordinal));
    }

    [Fact]
    public void catalog_present_entry_without_search_stays_native()
    {
        Assert.Equal("gpt-5.0-lite", Slug("gpt-5.0-lite"));
    }

    [Fact]
    public void catalog_boundary_models_use_native_tools()
    {
        Assert.Equal("gpt-5.4", Slug("gpt-5.4"));
        Assert.Equal("gpt-5.5", Slug("gpt-5.5"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void tool_search_capable_absent_from_catalog_aliases_to_latest(bool isLatest)
    {
        Assert.Equal("gpt-5.5", Slug("gpt-5.6", isLatest: isLatest));
        Assert.Equal("gpt-5.5", Slug("gpt-6", isLatest: isLatest));
    }

    [Fact]
    public void latest_codename_aliases_to_latest()
    {
        Assert.Equal("gpt-5.5", Slug("frontier-x", isLatest: true));
    }

    [Fact]
    public void non_openai_model_passes_through_for_generic_fallback()
    {
        Assert.Equal("claude-sonnet-4-0", Slug("claude-sonnet-4-0", openAi: false));
    }

    [Fact]
    public void openai_model_with_no_catalog_passes_through()
    {
        Assert.Equal("gpt-5.1-codex", Slug("gpt-5.1-codex", useCatalog: false));
        Assert.Equal("frontier-x", Slug("frontier-x", useCatalog: false, isLatest: true));
    }

    [Theory]
    [InlineData("gpt-5.4", true, false, "gpt-5.4", "openai 'gpt-5.4' matches Codex catalog → native prompt/tools (apply_patch=freeform, tool_search=yes)")]
    [InlineData("gpt-5.0-lite", true, false, "gpt-5.0-lite", "openai 'gpt-5.0-lite' matches Codex catalog → native prompt/tools (apply_patch=freeform, tool_search=no)")]
    [InlineData("gpt-5.2", true, false, "inspect-generic", "openai 'gpt-5.2' is in the Codex catalog but predates tool_search support (gpt-5.4+) → forcing generic prompt via 'inspect-generic' (no apply_patch, no tool_search) to avoid a tool_search 400")]
    [InlineData("gpt-5.6", true, false, "gpt-5.5", "openai 'gpt-5.6' absent from catalog but supports tool_search (gpt-5.4+) → aliased to latest 'gpt-5.5' (apply_patch=freeform, tool_search=yes)")]
    [InlineData("otter", true, true, "gpt-5.5", "openai 'otter' absent from catalog but is a latest/codename model (likely pre-deployment) → aliased to latest 'gpt-5.5' (apply_patch=freeform, tool_search=yes)")]
    [InlineData("o3", true, false, "o3", "openai 'o3' predates tool_search support (gpt-5.4+) → Codex generic prompt (no apply_patch, no tool_search)")]
    [InlineData("claude-sonnet-4-6", false, false, "claude-sonnet-4-6", "non-openai model 'claude-sonnet-4-6' → Codex generic prompt (no apply_patch)")]
    public void reasons_are_python_verbatim(string name, bool openAi, bool isLatest, string slug, string reason)
    {
        Assert.Equal(new CodexModelResolution(slug, reason), CodexCliModelCatalog.ResolveSlug(name, openAi, Catalog(), null, isLatest));
    }

    [Fact]
    public void missing_catalog_reason_is_python_verbatim()
    {
        Assert.Equal(
            new CodexModelResolution("gpt-6", "Codex catalog unavailable → deferring 'gpt-6' to Codex's bundled catalog"),
            CodexCliModelCatalog.ResolveSlug("gpt-6", true, null, null, false));
    }

    [Theory]
    [InlineData("gpt-5.4", true)]
    [InlineData("gpt-5.4-mini", true)]
    [InlineData("gpt-5.10", true)]
    [InlineData("gpt-6", true)]
    [InlineData("gpt-6-astra", true)]
    [InlineData("gpt-5", false)]
    [InlineData("gpt-5.3-codex", false)]
    [InlineData("gpt-4.1", false)]
    [InlineData("o3", false)]
    [InlineData("GPT-5.4", false)]
    [InlineData("my-gpt-5.4", false)]
    public void supports_tool_search_is_gpt_5_4_and_later(string name, bool expected)
    {
        Assert.Equal(expected, CodexCliModelCatalog.SupportsToolSearch(name));
    }

    [Fact]
    public void malformed_entries_are_ignored_and_priority_defaults()
    {
        var catalog = JsonNode.Parse("""{"models": [{"slug": 5, "priority": 0}, {"priority": 0}, "gpt-5.9", {"slug": "gpt-5.7"}, {"slug": "gpt-5.8", "priority": 999999}]}""")!.AsObject();

        Assert.Equal("gpt-5.8", CodexCliModelCatalog.LatestSlug(catalog));
        Assert.Null(CodexCliModelCatalog.LatestSlug(new JsonObject { ["models"] = "not a list" }));
    }

    [Fact]
    public void bundled_catalog_is_the_python_snapshot_and_a_fresh_copy_each_read()
    {
        var bundled = CodexCliModelCatalog.Bundled;

        Assert.Equal(
            ["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5", "gpt-5.4", "gpt-5.4-mini", "gpt-5.2", "codex-auto-review"],
            bundled["models"]!.AsArray().Select(model => model!["slug"]!.GetValue<string>()));
        Assert.Equal("gpt-5.6-sol", CodexCliModelCatalog.LatestSlug(bundled));
        Assert.Equal("gpt-5.6-sol", CodexCliModelCatalog.ResolveSlug("gpt-6", true, bundled, null, false).Slug);
        Assert.Equal("gpt-5.4-mini", CodexCliModelCatalog.ResolveSlug("gpt-5.4-mini", true, bundled, null, false).Slug);

        bundled["models"]!.AsArray().Clear();
        Assert.Equal(8, CodexCliModelCatalog.Bundled["models"]!.AsArray().Count);
    }
}
